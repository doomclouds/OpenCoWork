using System.Collections.Concurrent;
using System.Data.Common;
using System.Reflection;
using Microsoft.Data.Sqlite;
using OpenCoWork.Abstractions;
using OpenCoWork.Automations;
using OpenCoWork.Core.Gateway;
using OpenCoWork.Core.State;
using OpenCoWork.Core.Workspaces;
using OpenCoWork.Teams;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class GatewayInboundTests
{
    [Fact]
    public async Task Duplicate_and_conflicting_messages_are_decided_before_session_dispatch()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var files = await InboundWorkspace.CreateAsync(cancellationToken);
        var request = Request("message-1", "conversation-1", "body-a", "hello");

        var first = await files.Service.AcceptAsync(request, cancellationToken);
        var duplicate = await files.Service.AcceptAsync(request, cancellationToken);
        var conflict = await Assert.ThrowsAsync<ChannelServiceException>(() =>
            files.Service.AcceptAsync(
                    request with { BodySha256 = new string('b', 64) },
                    cancellationToken)
                .AsTask());

        Assert.False(first.Duplicate);
        Assert.True(duplicate.Duplicate);
        Assert.Equal(first.ReceiptId, duplicate.ReceiptId);
        Assert.Equal(first.CorrelationId, duplicate.CorrelationId);
        Assert.Equal(ChannelErrorCodes.IdempotencyConflict, conflict.Code);
        Assert.Equal(1L, await files.ScalarAsync<long>(
            "SELECT count(*) FROM channel_inbound_messages;",
            cancellationToken));
        Assert.Equal(0, files.Sessions.UniqueThreadCount);
        Assert.Equal(0, files.Sessions.UniqueEnqueueCount);

        Assert.Equal(1, await files.Service.DispatchPendingAsync(
            files.RuntimeInstanceId,
            maxConcurrency: 4,
            cancellationToken));
        Assert.Equal(0, await files.Service.DispatchPendingAsync(
            files.RuntimeInstanceId,
            maxConcurrency: 4,
            cancellationToken));
        Assert.Equal(1, files.Sessions.UniqueThreadCount);
        Assert.Equal(1, files.Sessions.UniqueEnqueueCount);
        Assert.Equal("delivered", await files.ScalarAsync<string>(
            "SELECT status FROM channel_inbound_messages;",
            cancellationToken));
    }

    [Theory]
    [InlineData((int)GatewayInboundFaultPoint.ThreadCreated)]
    [InlineData((int)GatewayInboundFaultPoint.MappingCommitted)]
    [InlineData((int)GatewayInboundFaultPoint.QueueCommitted)]
    [InlineData((int)GatewayInboundFaultPoint.DeliveredCommitted)]
    public async Task Every_dispatch_crash_window_replays_without_duplicate_local_work(
        int faultPointValue)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var files = await InboundWorkspace.CreateAsync(cancellationToken);
        var injected = false;
        files.ReplaceService(point =>
        {
            if (!injected && point == (GatewayInboundFaultPoint)faultPointValue)
            {
                injected = true;
                throw new InjectedCrashException();
            }
        });
        await files.Service.AcceptAsync(
            Request("message-1", "conversation-1", "body-a", "hello"),
            cancellationToken);

        await files.Service.DispatchPendingAsync(
            files.RuntimeInstanceId,
            maxConcurrency: 1,
            cancellationToken);
        files.Advance(TimeSpan.FromMinutes(11));
        await files.Service.DispatchPendingAsync(
            files.RuntimeInstanceId,
            maxConcurrency: 1,
            cancellationToken);

        Assert.True(injected);
        Assert.Equal(1, files.Sessions.UniqueThreadCount);
        Assert.Equal(1, files.Sessions.UniqueEnqueueCount);
        Assert.Equal("delivered", await files.ScalarAsync<string>(
            "SELECT status FROM channel_inbound_messages;",
            cancellationToken));
        Assert.NotNull(await files.ScalarAsync<object>(
            "SELECT session_expected_sequence FROM channel_inbound_messages;",
            cancellationToken));
        Assert.NotNull(await files.ScalarAsync<object>(
            "SELECT session_queue_item_id FROM channel_inbound_messages;",
            cancellationToken));
    }

    [Fact]
    public async Task Conversations_dispatch_in_parallel_while_each_partition_stays_ordered()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var files = await InboundWorkspace.CreateAsync(cancellationToken);
        for (var index = 0; index < 100; index++)
        {
            await files.Service.AcceptAsync(
                Request(
                    $"same-{index:D3}",
                    "same-conversation",
                    $"body-{index:D3}",
                    $"same-{index:D3}"),
                cancellationToken);
        }

        for (var index = 0; index < 32; index++)
        {
            await files.Service.AcceptAsync(
                Request(
                    $"parallel-{index:D2}",
                    $"conversation-{index:D2}",
                    $"parallel-body-{index:D2}",
                    $"parallel-{index:D2}"),
                cancellationToken);
        }

        files.Sessions.RequireConcurrentEnqueues = true;
        while (await files.Service.DispatchPendingAsync(
                   files.RuntimeInstanceId,
                   maxConcurrency: 32,
                   cancellationToken) > 0)
        {
        }

        var sameThread = files.Sessions.Threads.Single(pair =>
            pair.Value.DisplayName.Contains("same-conversation", StringComparison.Ordinal)).Key;
        Assert.Equal(
            Enumerable.Range(0, 100).Select(index =>
                ExpectedSessionText($"same-{index:D3}")),
            files.Sessions.TextByThread[sameThread]);
        Assert.True(files.Sessions.MaximumConcurrentEnqueues > 1);
        Assert.Equal(132L, await files.ScalarAsync<long>(
            "SELECT count(*) FROM channel_inbound_messages WHERE status = 'delivered';",
            cancellationToken));
    }

    [Fact]
    public async Task Concurrent_duplicates_commit_once_and_media_is_referenced_not_embedded()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var files = await InboundWorkspace.CreateAsync(cancellationToken);
        const string encoded = "c2VjcmV0LW1lZGlhLWJ5dGVz";
        var request = Request("message-1", "conversation-1", "body-a", "hello") with
        {
            Envelope = Request("message-1", "conversation-1", "body-a", "hello").Envelope with
            {
                Attachments = [new ChannelMediaInput("text/plain", "note.txt", encoded)],
            },
        };

        var receipts = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ =>
            files.Service.AcceptAsync(request, cancellationToken).AsTask()));
        Assert.Single(receipts, receipt => !receipt.Duplicate);
        Assert.Single(receipts.Select(receipt => receipt.ReceiptId).Distinct());
        Assert.Equal(1, await files.Service.DispatchPendingAsync(
            files.RuntimeInstanceId,
            4,
            cancellationToken));

        var submitted = Assert.Single(files.Sessions.TextByThread.Values).Single();
        Assert.Contains("[media: note.txt", submitted, StringComparison.Ordinal);
        Assert.DoesNotContain(encoded, submitted, StringComparison.Ordinal);
        Assert.DoesNotContain(encoded, await files.ScalarAsync<string>(
            "SELECT payload_json FROM channel_inbound_messages;",
            cancellationToken), StringComparison.Ordinal);
        Assert.Equal(1L, await files.ScalarAsync<long>(
            "SELECT count(*) FROM channel_media;",
            cancellationToken));
    }

    [Fact]
    public async Task Poison_partition_does_not_block_another_conversation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var files = await InboundWorkspace.CreateAsync(cancellationToken);
        files.Sessions.RejectedTexts.Add(ExpectedSessionText("poison"));
        await files.Service.AcceptAsync(
            Request("poison-1", "poison-conversation", "body-a", "poison"),
            cancellationToken);
        await files.Service.AcceptAsync(
            Request("good-1", "good-conversation", "body-b", "good"),
            cancellationToken);

        Assert.Equal(2, await files.Service.DispatchPendingAsync(
            files.RuntimeInstanceId,
            2,
            cancellationToken));
        Assert.Equal("deadLettered", await files.ScalarAsync<string>(
            "SELECT status FROM channel_inbound_messages WHERE external_message_id = 'poison-1';",
            cancellationToken));
        Assert.Equal("delivered", await files.ScalarAsync<string>(
            "SELECT status FROM channel_inbound_messages WHERE external_message_id = 'good-1';",
            cancellationToken));
    }

    [Fact]
    public async Task Retryable_infrastructure_failure_dead_letters_on_fifth_attempt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var files = await InboundWorkspace.CreateAsync(cancellationToken);
        var text = ExpectedSessionText("retryable");
        files.Sessions.RetryableTexts.Add(text);
        await files.Service.AcceptAsync(
            Request("retry-1", "retry-conversation", "body-a", "retryable"),
            cancellationToken);
        TimeSpan[] delays =
        [
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(2),
        ];

        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.Equal(1, await files.Service.DispatchPendingAsync(
                files.RuntimeInstanceId,
                1,
                cancellationToken));
            Assert.Equal(attempt == 4 ? "deadLettered" : "failed",
                await files.ScalarAsync<string>(
                    "SELECT status FROM channel_inbound_messages;",
                    cancellationToken));
            if (attempt < delays.Length)
            {
                files.Advance(delays[attempt]);
            }
        }

        Assert.Equal(5L, await files.ScalarAsync<long>(
            "SELECT attempt_count FROM channel_inbound_messages;",
            cancellationToken));
    }

    private static ChannelInboundRequest Request(
        string messageId,
        string conversationId,
        string bodySeed,
        string text) =>
        new(
            "build-bot",
            Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(bodySeed)))
                .ToLowerInvariant(),
            new ChannelInboundEnvelope(
                1,
                messageId,
                conversationId,
                DateTimeOffset.FromUnixTimeSeconds(1_786_070_400),
                text,
                []));

    private static string ExpectedSessionText(string text) =>
        $"[OpenCoWork external channel message]\n\n{text}";

    private sealed class InboundWorkspace : IAsyncDisposable
    {
        private readonly StateRuntime _state;
        private readonly GatewayMediaStore _media;
        private readonly FixedTimeProvider _timeProvider = new(
            DateTimeOffset.FromUnixTimeSeconds(1_786_070_400));

        private InboundWorkspace(
            string root,
            OpenCoWorkPaths paths,
            StateRuntime state,
            GatewaySessionProxy sessions)
        {
            Root = root;
            Paths = paths;
            _state = state;
            _media = new GatewayMediaStore(paths, state);
            Sessions = sessions;
            Service = CreateService();
        }

        public string Root { get; }
        public OpenCoWorkPaths Paths { get; }
        public GatewaySessionProxy Sessions { get; }
        public GatewayService Service { get; private set; }
        public Guid RuntimeInstanceId { get; } = Guid.CreateVersion7();

        public static async Task<InboundWorkspace> CreateAsync(
            CancellationToken cancellationToken)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"opencowork-gateway-inbound-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var paths = new OpenCoWorkPaths(root);
            var state = new StateRuntime(
                paths,
                TimeSpan.FromSeconds(2),
                [
                    .. GatewayStateMigrationContributors.Create(),
                    .. TeamsStateMigrationContributors.Create(),
                    .. AutomationsStateMigrationContributors.Create(),
                ]);
            await state.InitializeAsync(cancellationToken);
            await state.WriteAsync(
                async (connection, transaction, token) =>
                {
                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText =
                        """
                        INSERT INTO channels (
                            channel_id, kind, enabled, definition_sha256,
                            trust_status, runtime_status, revision, created_utc, updated_utc)
                        VALUES (
                            'build-bot', 'webhook', 1,
                            'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                            'trusted', 'ready', 1, 1, 1);
                        """;
                    await command.ExecuteNonQueryAsync(token);
                    return true;
                },
                cancellationToken);
            var sessionService = DispatchProxy.Create<ISessionService, GatewaySessionProxy>();
            var sessions = (GatewaySessionProxy)(object)sessionService;
            sessions.Service = sessionService;
            sessions.State = state;
            return new InboundWorkspace(root, paths, state, sessions);
        }

        public void ReplaceService(Action<GatewayInboundFaultPoint> faultInjector) =>
            Service = CreateService(faultInjector);

        public void Advance(TimeSpan duration) => _timeProvider.Advance(duration);

        public async Task<T> ScalarAsync<T>(string sql, CancellationToken cancellationToken)
        {
            await using var connection =
                await _state.OpenReadWriteConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            var value = await command.ExecuteScalarAsync(cancellationToken);
            return (T)Convert.ChangeType(
                value,
                typeof(T),
                System.Globalization.CultureInfo.InvariantCulture)!;
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
            return ValueTask.CompletedTask;
        }

        private GatewayService CreateService(
            Action<GatewayInboundFaultPoint>? faultInjector = null) =>
            new(
                _state,
                _media,
                Sessions.Service,
                _timeProvider,
                faultInjector);
    }

    private class GatewaySessionProxy : DispatchProxy
    {
        private readonly ConcurrentDictionary<Guid, ThreadSnapshot> _threads = [];
        private readonly ConcurrentDictionary<Guid, SessionCommandResult<ThreadSnapshot>>
            _creates = [];
        private readonly ConcurrentDictionary<Guid, SessionCommandResult<SubmittedTurnInputSnapshot>>
            _enqueues = [];
        private readonly ConcurrentDictionary<Guid, List<string>> _texts = [];
        private int _activeEnqueues;
        private int _maximumConcurrentEnqueues;
        private readonly TaskCompletionSource _concurrentEnqueues =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ISessionService Service { get; set; } = null!;
        public IWorkspaceStateStore State { get; set; } = null!;
        public IReadOnlyDictionary<Guid, ThreadSnapshot> Threads => _threads;
        public IReadOnlyDictionary<Guid, List<string>> TextByThread => _texts;
        public HashSet<string> RejectedTexts { get; } = new(StringComparer.Ordinal);
        public HashSet<string> RetryableTexts { get; } = new(StringComparer.Ordinal);
        public int UniqueThreadCount => _creates.Count;
        public int UniqueEnqueueCount => _enqueues.Count;
        public int MaximumConcurrentEnqueues => Volatile.Read(ref _maximumConcurrentEnqueues);
        public bool RequireConcurrentEnqueues { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name switch
            {
                nameof(ISessionService.CreateThreadAsync) =>
                    CreateAsync((CreateThreadRequest)args![0]!),
                nameof(ISessionService.GetThreadAsync) =>
                    GetAsync((Guid)args![0]!),
                nameof(ISessionService.EnqueueInputAsync) =>
                    EnqueueAsync((EnqueueInputRequest)args![0]!),
                _ => throw new NotSupportedException(targetMethod?.Name),
            };

        private async Task<SessionCommandResult<ThreadSnapshot>> CreateAsync(
            CreateThreadRequest request)
        {
            if (_creates.TryGetValue(request.IdempotencyKey, out var replay))
            {
                return replay;
            }

            var now = DateTimeOffset.FromUnixTimeSeconds(1_786_070_400);
            var thread = new ThreadSnapshot(
                Guid.CreateVersion7(),
                request.DisplayName ?? "New thread",
                ThreadStatus.Active,
                ThreadAvailability.Available,
                HistoryMode.Server,
                1,
                null,
                [],
                now,
                now,
                SessionProjectionState.Ready,
                null);
            await State.WriteAsync(
                async (connection, transaction, token) =>
                {
                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText =
                        """
                        INSERT INTO threads (
                            thread_id, display_name, display_name_search, status,
                            availability, history_mode, current_sequence,
                            last_applied_sequence, created_utc, updated_utc)
                        VALUES (
                            $threadId, $displayName, $displayName, 'active',
                            'available', 'server', 1, 1, $now, $now);
                        """;
                    var threadId = command.CreateParameter();
                    threadId.ParameterName = "$threadId";
                    threadId.Value = thread.ThreadId.ToString("D");
                    command.Parameters.Add(threadId);
                    var displayName = command.CreateParameter();
                    displayName.ParameterName = "$displayName";
                    displayName.Value = thread.DisplayName;
                    command.Parameters.Add(displayName);
                    var timestamp = command.CreateParameter();
                    timestamp.ParameterName = "$now";
                    timestamp.Value = now.ToUnixTimeMilliseconds();
                    command.Parameters.Add(timestamp);
                    await command.ExecuteNonQueryAsync(token);
                    return true;
                });
            var result = new SessionCommandResult<ThreadSnapshot>(
                SessionCommandStatus.Committed,
                thread,
                1,
                null,
                null);
            _threads[thread.ThreadId] = thread;
            _texts[thread.ThreadId] = [];
            _creates[request.IdempotencyKey] = result;
            return result;
        }

        private Task<SessionQueryResult<ThreadSnapshot>> GetAsync(Guid threadId) =>
            Task.FromResult(_threads.TryGetValue(threadId, out var thread)
                ? new SessionQueryResult<ThreadSnapshot>(thread, null)
                : new SessionQueryResult<ThreadSnapshot>(
                    null,
                    new SessionError(SessionErrorCodes.NotFound, "missing", false)));

        private async Task<SessionCommandResult<SubmittedTurnInputSnapshot>> EnqueueAsync(
            EnqueueInputRequest request)
        {
            if (_enqueues.TryGetValue(request.IdempotencyKey, out var replay))
            {
                return replay;
            }

            if (RejectedTexts.Contains(request.Text) || RetryableTexts.Contains(request.Text))
            {
                var rejected = new SessionCommandResult<SubmittedTurnInputSnapshot>(
                    SessionCommandStatus.Rejected,
                    null,
                    null,
                    null,
                    new SessionError(
                        SessionErrorCodes.InvalidState,
                        "rejected",
                        RetryableTexts.Contains(request.Text)));
                _enqueues[request.IdempotencyKey] = rejected;
                return rejected;
            }

            var active = Interlocked.Increment(ref _activeEnqueues);
            InterlockedExtensions.Max(ref _maximumConcurrentEnqueues, active);
            try
            {
                if (RequireConcurrentEnqueues)
                {
                    if (active > 1)
                    {
                        _concurrentEnqueues.TrySetResult();
                    }
                    await _concurrentEnqueues.Task.WaitAsync(
                        TimeSpan.FromSeconds(5),
                        TestContext.Current.CancellationToken);
                }
                var thread = _threads[request.ThreadId];
                Assert.Equal(thread.CurrentSequence, request.ExpectedSequence);
                Assert.NotNull(request.CorrelationId);
                var now = DateTimeOffset.FromUnixTimeSeconds(1_786_070_400);
                var queue = new QueuedTurnInputSnapshot(
                    Guid.CreateVersion7(),
                    request.ThreadId,
                    request.Text,
                    0,
                    now,
                    AgentMode.Agent,
                    request.CorrelationId);
                var result = new SessionCommandResult<SubmittedTurnInputSnapshot>(
                    SessionCommandStatus.Committed,
                    new SubmittedTurnInputSnapshot(queue, null),
                    thread.CurrentSequence + 1,
                    null,
                    null);
                if (_enqueues.TryAdd(request.IdempotencyKey, result))
                {
                    _texts[request.ThreadId].Add(request.Text);
                    _threads[request.ThreadId] = new ThreadSnapshot(
                        thread.ThreadId,
                        thread.DisplayName,
                        thread.Status,
                        thread.Availability,
                        thread.HistoryMode,
                        thread.CurrentSequence + 1,
                        null,
                        [],
                        thread.CreatedAt,
                        now,
                        thread.ProjectionState,
                        null);
                }

                return _enqueues[request.IdempotencyKey];
            }
            finally
            {
                Interlocked.Decrement(ref _activeEnqueues);
            }
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan duration) => now += duration;
    }

    private sealed class InjectedCrashException : Exception;
}

internal static class InterlockedExtensions
{
    public static void Max(ref int location, int value)
    {
        var current = Volatile.Read(ref location);
        while (current < value)
        {
            var observed = Interlocked.CompareExchange(ref location, value, current);
            if (observed == current)
            {
                return;
            }

            current = observed;
        }
    }
}
