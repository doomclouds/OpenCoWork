using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.State;

namespace OpenCoWork.Core.Operations;

internal sealed class OperationsTraceCollector
{
    private static readonly HashSet<string> SpanNames =
    [
        OpenCoWorkTelemetry.GatewayReceive,
        OpenCoWorkTelemetry.GatewayDispatch,
        OpenCoWorkTelemetry.SessionTurn,
        OpenCoWorkTelemetry.ProviderResponses,
        OpenCoWorkTelemetry.ToolInvoke,
        OpenCoWorkTelemetry.AutomationRun,
        OpenCoWorkTelemetry.CoWorkAgentRun,
        OpenCoWorkTelemetry.GatewayOutboxSend,
    ];

    private static readonly HashSet<string> SafeTags =
    [
        OpenCoWorkTelemetry.ProviderIdTag,
        OpenCoWorkTelemetry.ModelIdTag,
        OpenCoWorkTelemetry.PurposeTag,
        OpenCoWorkTelemetry.ToolIdTag,
    ];

    private readonly StateRuntime _state;
    private readonly int _capacity;
    private readonly Func<CancellationToken, ValueTask>? _beforePersist;
    private ActivityListener? _listener;
    private Channel<PendingSpan>? _channel;
    private CancellationTokenSource? _stopping;
    private Task? _worker;
    private long _droppedCount;

    public OperationsTraceCollector(StateRuntime state)
        : this(state, 1024, beforePersist: null)
    {
    }

    internal OperationsTraceCollector(
        StateRuntime state,
        int capacity,
        Func<CancellationToken, ValueTask>? beforePersist)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _state = state;
        _capacity = capacity;
        _beforePersist = beforePersist;
    }

    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_listener is not null)
        {
            return Task.CompletedTask;
        }

        _stopping = new CancellationTokenSource();
        _channel = Channel.CreateBounded<PendingSpan>(new BoundedChannelOptions(_capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        _worker = PersistAsync(_channel.Reader, _stopping.Token);
        _listener = new ActivityListener
        {
            ShouldListenTo = source =>
                string.Equals(source.Name, OpenCoWorkTelemetry.SourceName, StringComparison.Ordinal),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllData,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) =>
                ActivitySamplingResult.AllData,
            ActivityStopped = OnActivityStopped,
        };
        ActivitySource.AddActivityListener(_listener);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var listener = Interlocked.Exchange(ref _listener, null);
        if (listener is null)
        {
            return;
        }

        listener.Dispose();
        _channel!.Writer.TryComplete();
        try
        {
            await _worker!.WaitAsync(cancellationToken);
        }
        finally
        {
            _stopping!.Cancel();
            _stopping.Dispose();
            _stopping = null;
            _channel = null;
            _worker = null;
        }
    }

    private void OnActivityStopped(Activity activity)
    {
        if (!SpanNames.Contains(activity.OperationName))
        {
            return;
        }

        if (_channel is null ||
            !TryCreatePending(activity, out var span) ||
            !_channel.Writer.TryWrite(span))
        {
            Interlocked.Increment(ref _droppedCount);
        }
    }

    private static bool TryCreatePending(Activity activity, out PendingSpan span)
    {
        var correlationId = ReadVersionSevenGuid(activity, OpenCoWorkTelemetry.CorrelationIdTag);
        var threadId = ReadGuid(activity, OpenCoWorkTelemetry.ThreadIdTag);
        var turnId = ReadGuid(activity, OpenCoWorkTelemetry.TurnIdTag);
        var automationRunId = ReadGuid(activity, OpenCoWorkTelemetry.AutomationRunIdTag);
        var agentRunId = ReadGuid(activity, OpenCoWorkTelemetry.AgentRunIdTag);
        var channelId = ReadSafeIdentifier(activity, OpenCoWorkTelemetry.ChannelIdTag);
        var errorCode = ReadSafeIdentifier(activity, OpenCoWorkTelemetry.ErrorCodeTag);
        var tags = activity.TagObjects
            .Where(item => SafeTags.Contains(item.Key))
            .Select(item => (item.Key, Value: Convert.ToString(
                item.Value,
                System.Globalization.CultureInfo.InvariantCulture)))
            .Where(item => IsSafeIdentifier(item.Value))
            .ToDictionary(item => item.Key, item => item.Value!, StringComparer.Ordinal);
        var started = new DateTimeOffset(activity.StartTimeUtc);
        var ended = started + activity.Duration;
        span = new PendingSpan(
            activity.TraceId.ToString(),
            activity.SpanId.ToString(),
            activity.ParentSpanId == default ? null : activity.ParentSpanId.ToString(),
            activity.OperationName,
            Kind(activity.Kind),
            Status(activity.Status),
            correlationId,
            threadId,
            turnId,
            automationRunId,
            agentRunId,
            channelId,
            Math.Max(0, activity.Duration.TotalMilliseconds),
            JsonSerializer.Serialize(tags),
            errorCode,
            started.ToUnixTimeMilliseconds(),
            ended.ToUnixTimeMilliseconds());
        return activity.TraceId != default && activity.SpanId != default;
    }

    private async Task PersistAsync(
        ChannelReader<PendingSpan> reader,
        CancellationToken cancellationToken)
    {
        await foreach (var span in reader.ReadAllAsync(cancellationToken))
        {
            try
            {
                if (_beforePersist is not null)
                {
                    await _beforePersist(cancellationToken);
                }

                await _state.WriteAsync(
                    async (connection, transaction, token) =>
                    {
                        await InsertAsync(connection, transaction, span, token);
                        return true;
                    },
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                Interlocked.Increment(ref _droppedCount);
            }
        }
    }

    private static async Task InsertAsync(
        DbConnection connection,
        DbTransaction transaction,
        PendingSpan span,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT OR IGNORE INTO trace_spans (
                trace_id, span_id, parent_span_id, name, kind, status,
                correlation_id, thread_id, turn_id, automation_run_id,
                agent_run_id, channel_id, duration_ms, tags_json, error_code,
                started_utc, ended_utc)
            VALUES (
                $traceId, $spanId, $parentSpanId, $name, $kind, $status,
                $correlationId, $threadId, $turnId, $automationRunId,
                $agentRunId, $channelId, $durationMs, $tags, $errorCode,
                $started, $ended);
            """;
        Add(command, "$traceId", span.TraceId);
        Add(command, "$spanId", span.SpanId);
        Add(command, "$parentSpanId", span.ParentSpanId);
        Add(command, "$name", span.Name);
        Add(command, "$kind", span.Kind);
        Add(command, "$status", span.Status);
        Add(command, "$correlationId", span.CorrelationId?.ToString("D"));
        Add(command, "$threadId", span.ThreadId?.ToString("D"));
        Add(command, "$turnId", span.TurnId?.ToString("D"));
        Add(command, "$automationRunId", span.AutomationRunId?.ToString("D"));
        Add(command, "$agentRunId", span.AgentRunId?.ToString("D"));
        Add(command, "$channelId", span.ChannelId);
        Add(command, "$durationMs", span.DurationMs);
        Add(command, "$tags", span.TagsJson);
        Add(command, "$errorCode", span.ErrorCode);
        Add(command, "$started", span.StartedUtc);
        Add(command, "$ended", span.EndedUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static Guid? ReadGuid(Activity activity, string name) =>
        Guid.TryParse(Convert.ToString(activity.GetTagItem(name)), out var value)
            ? value
            : null;

    private static Guid? ReadVersionSevenGuid(Activity activity, string name)
    {
        var value = ReadGuid(activity, name);
        return value is not null && value.Value.Version == 7 ? value : null;
    }

    private static string? ReadSafeIdentifier(Activity activity, string name)
    {
        var value = Convert.ToString(activity.GetTagItem(name));
        return IsSafeIdentifier(value) ? value : null;
    }

    private static bool IsSafeIdentifier(string? value) =>
        value is { Length: > 0 and <= 128 } &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');

    private static string Kind(ActivityKind kind) => kind switch
    {
        ActivityKind.Server => "server",
        ActivityKind.Client => "client",
        ActivityKind.Producer => "producer",
        ActivityKind.Consumer => "consumer",
        _ => "internal",
    };

    private static string Status(ActivityStatusCode status) => status switch
    {
        ActivityStatusCode.Ok => "ok",
        ActivityStatusCode.Error => "error",
        _ => "unset",
    };

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private sealed record PendingSpan(
        string TraceId,
        string SpanId,
        string? ParentSpanId,
        string Name,
        string Kind,
        string Status,
        Guid? CorrelationId,
        Guid? ThreadId,
        Guid? TurnId,
        Guid? AutomationRunId,
        Guid? AgentRunId,
        string? ChannelId,
        double DurationMs,
        string TagsJson,
        string? ErrorCode,
        long StartedUtc,
        long EndedUtc);
}
