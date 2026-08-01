using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Operations;
using OpenCoWork.Core.State;
using OpenCoWork.Core.Workspaces;
using Xunit;

namespace OpenCoWork.Core.Tests;

[CollectionDefinition("Operations telemetry", DisableParallelization = true)]
public sealed class OperationsTelemetryCollection;

[Collection("Operations telemetry")]
public sealed class OperationsTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    [Fact]
    public async Task Trace_collector_is_nonblocking_safe_paged_and_restart_queryable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var files = await OperationsWorkspace.CreateAsync(cancellationToken);
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var firstWrite = 0;
        var collector = new OperationsTraceCollector(
            files.State,
            capacity: 1,
            beforePersist: async token =>
            {
                if (Interlocked.Exchange(ref firstWrite, 1) == 0)
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(token);
                }
            });
        await collector.StartAsync(cancellationToken);
        var correlationId = Guid.CreateVersion7();
        const string canary = "trace-secret-canary";
        using (var activity = OpenCoWorkTelemetry.StartActivity(
                   OpenCoWorkTelemetry.GatewayReceive,
                   System.Diagnostics.ActivityKind.Server,
                   correlationId,
                   channelId: "alpha"))
        {
            Assert.NotNull(activity);
            activity.SetTag(OpenCoWorkTelemetry.ProviderIdTag, "deepseek");
            activity.SetTag("request.body", canary);
            activity.SetTag("callback.url", "https://secret.example.test/path?q=token");
            activity.SetTag("absolute.path", files.Root);
            activity.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
        }
        await entered.Task.WaitAsync(cancellationToken);
        EmitTrace(correlationId, "beta");
        EmitTrace(correlationId, "gamma");
        release.TrySetResult();
        await collector.StopAsync(cancellationToken);

        Assert.Equal(1, collector.DroppedCount);
        IOperationsQueryService query = new OperationsQueryService(files.State);
        var first = await query.ListTracesAsync(
            new TraceListQuery(CorrelationId: correlationId, PageSize: 1),
            cancellationToken);
        var second = await query.ListTracesAsync(
            new TraceListQuery(
                CorrelationId: correlationId,
                PageSize: 1,
                Cursor: first.NextCursor),
            cancellationToken);
        Assert.Single(first.Items);
        Assert.Single(second.Items);
        Assert.NotNull(first.NextCursor);
        Assert.NotEqual(first.Items[0].TraceId, second.Items[0].TraceId);

        var spans = await new OperationsQueryService(files.State).GetTraceAsync(
            first.Items[0].TraceId,
            cancellationToken);
        var json = JsonSerializer.Serialize(spans, JsonOptions);
        Assert.DoesNotContain(canary, json, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", json, StringComparison.Ordinal);
        Assert.DoesNotContain(files.Root, json, StringComparison.Ordinal);
        Assert.All(spans, span => Assert.Equal(correlationId, span.CorrelationId));
    }

    [Fact]
    public async Task Usage_query_aggregates_existing_authority_by_channel_and_source()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var files = await OperationsWorkspace.CreateAsync(cancellationToken);
        var recordedAt = new DateTimeOffset(2026, 8, 1, 3, 15, 0, TimeSpan.Zero);
        await files.SeedUsageAsync(recordedAt, cancellationToken);
        IOperationsQueryService query = new OperationsQueryService(files.State);

        var usage = await query.QueryUsageAsync(
            new UsageQuery(
                recordedAt.AddHours(-1),
                recordedAt.AddHours(1),
                OperationsTimeBucket.Hour,
                ChannelId: "alpha"),
            cancellationToken);

        Assert.Collection(
            usage.OrderBy(item => item.Source),
            provider =>
            {
                Assert.Equal(ProviderUsageSource.Provider, provider.Source);
                Assert.Equal(10, provider.PromptTokens);
                Assert.Equal(4, provider.CachedPromptTokens);
                Assert.Equal(5, provider.CompletionTokens);
                Assert.Equal(2, provider.ReasoningCompletionTokens);
                Assert.Equal(15, provider.TotalTokens);
            },
            estimate =>
            {
                Assert.Equal(ProviderUsageSource.LocalEstimate, estimate.Source);
                Assert.Equal(12, estimate.PromptTokens);
                Assert.Equal(0, estimate.CachedPromptTokens);
                Assert.Equal(6, estimate.CompletionTokens);
                Assert.Equal(0, estimate.ReasoningCompletionTokens);
                Assert.Equal(18, estimate.TotalTokens);
            });
        Assert.All(usage, item =>
        {
            Assert.Equal("deepseek", item.ProviderId);
            Assert.Equal("deepseek-v4-flash", item.ModelId);
            Assert.Equal("alpha", item.ChannelId);
            Assert.Equal(
                new DateTimeOffset(2026, 8, 1, 3, 0, 0, TimeSpan.Zero),
                item.BucketStartUtc);
        });
        Assert.Equal(1L, await files.ScalarAsync<long>(
            "SELECT count(*) FROM sqlite_schema WHERE type = 'table' AND name LIKE '%usage%';",
            cancellationToken));
    }

    private static void EmitTrace(Guid correlationId, string channelId)
    {
        using var activity = OpenCoWorkTelemetry.StartActivity(
            OpenCoWorkTelemetry.GatewayDispatch,
            System.Diagnostics.ActivityKind.Consumer,
            correlationId,
            channelId: channelId);
        Assert.NotNull(activity);
        activity.SetStatus(System.Diagnostics.ActivityStatusCode.Ok);
    }

    private sealed class OperationsWorkspace : IAsyncDisposable
    {
        private OperationsWorkspace(string root, StateRuntime state)
        {
            Root = root;
            State = state;
        }

        public string Root { get; }
        public StateRuntime State { get; }

        public static async Task<OperationsWorkspace> CreateAsync(
            CancellationToken cancellationToken)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"opencowork-operations-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var state = new StateRuntime(
                new OpenCoWorkPaths(root),
                TimeSpan.FromSeconds(2),
                GatewayStateMigrationContributors.Create());
            await state.InitializeAsync(cancellationToken);
            return new OperationsWorkspace(root, state);
        }

        public async Task SeedUsageAsync(
            DateTimeOffset recordedAt,
            CancellationToken cancellationToken)
        {
            var threadId = Guid.CreateVersion7();
            var turnId = Guid.CreateVersion7();
            var invocationId = Guid.CreateVersion7();
            var correlationId = Guid.CreateVersion7();
            var inboundId = Guid.CreateVersion7();
            var now = recordedAt.ToUnixTimeMilliseconds();
            await State.WriteAsync(
                async (connection, transaction, token) =>
                {
                    await ExecuteAsync(
                        connection,
                        transaction,
                        """
                        INSERT INTO threads (
                            thread_id, display_name, display_name_search, status,
                            availability, history_mode, current_sequence,
                            last_applied_sequence, created_utc, updated_utc)
                        VALUES ($threadId, 'usage', 'usage', 'active', 'available',
                                'server', 3, 3, $now, $now);
                        INSERT INTO turns (
                            turn_id, thread_id, status, created_utc, updated_utc,
                            completed_utc, effective_agent_mode, correlation_id)
                        VALUES ($turnId, $threadId, 'completed', $now, $now, $now,
                                'agent', $correlationId);
                        INSERT INTO agent_invocations (
                            invocation_id, thread_id, turn_id, snapshot_json,
                            recorded_sequence, created_utc)
                        VALUES ($invocationId, $threadId, $turnId, $snapshot, 1, $now);
                        INSERT INTO channels (
                            channel_id, kind, enabled, definition_sha256,
                            trust_status, runtime_status, revision, created_utc, updated_utc)
                        VALUES ('alpha', 'webhook', 1,
                                'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                                'trusted', 'ready', 1, $now, $now);
                        INSERT INTO channel_inbound_messages (
                            inbound_message_id, channel_id, external_message_id,
                            external_conversation_id, partition_sequence, payload_json,
                            body_sha256, session_create_idempotency_key,
                            session_submit_idempotency_key, correlation_id,
                            thread_id, turn_id, status, attempt_count, next_attempt_utc,
                            revision, created_utc, updated_utc, delivered_utc)
                        VALUES ($inboundId, 'alpha', 'message-1', 'conversation-1', 1, '{}',
                                'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
                                $createKey, $submitKey, $correlationId, $threadId, $turnId,
                                'delivered', 1, $now, 1, $now, $now, $now);
                        """,
                        token,
                        ("$threadId", threadId.ToString("D")),
                        ("$turnId", turnId.ToString("D")),
                        ("$invocationId", invocationId.ToString("D")),
                        ("$correlationId", correlationId.ToString("D")),
                        ("$inboundId", inboundId.ToString("D")),
                        ("$createKey", Guid.CreateVersion7().ToString("D")),
                        ("$submitKey", Guid.CreateVersion7().ToString("D")),
                        ("$snapshot", "{\"providerId\":\"deepseek\",\"modelId\":\"deepseek-v4-flash\"}"),
                        ("$now", now));
                    await InsertUsageAsync(
                        connection,
                        transaction,
                        invocationId,
                        threadId,
                        turnId,
                        attempt: 1,
                        new ProviderUsageSnapshot(
                            invocationId,
                            1,
                            ProviderInvocationPurpose.Response,
                            10,
                            5,
                            15,
                            ProviderUsageSource.Provider,
                            IsEstimate: false,
                            CachedPromptTokens: 4,
                            ReasoningCompletionTokens: 2),
                        now,
                        token);
                    await InsertUsageAsync(
                        connection,
                        transaction,
                        invocationId,
                        threadId,
                        turnId,
                        attempt: 2,
                        new ProviderUsageSnapshot(
                            invocationId,
                            2,
                            ProviderInvocationPurpose.Response,
                            12,
                            6,
                            18,
                            ProviderUsageSource.LocalEstimate,
                            IsEstimate: true),
                        now,
                        token);
                    return true;
                },
                cancellationToken);
        }

        public async Task<T> ScalarAsync<T>(string sql, CancellationToken cancellationToken)
        {
            await using var connection =
                await State.OpenReadWriteConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return (T)Convert.ChangeType(
                await command.ExecuteScalarAsync(cancellationToken),
                typeof(T),
                System.Globalization.CultureInfo.InvariantCulture)!;
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
            return ValueTask.CompletedTask;
        }

        private static Task InsertUsageAsync(
            DbConnection connection,
            DbTransaction transaction,
            Guid invocationId,
            Guid threadId,
            Guid turnId,
            int attempt,
            ProviderUsageSnapshot usage,
            long now,
            CancellationToken cancellationToken) =>
            ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO provider_usage (
                    invocation_id, attempt_number, purpose, thread_id, turn_id,
                    usage_json, recorded_sequence, created_utc)
                VALUES ($invocationId, $attempt, 'response', $threadId, $turnId,
                        $usage, $sequence, $now);
                """,
                cancellationToken,
                ("$invocationId", invocationId.ToString("D")),
                ("$attempt", attempt),
                ("$threadId", threadId.ToString("D")),
                ("$turnId", turnId.ToString("D")),
                ("$usage", JsonSerializer.Serialize(usage, JsonOptions)),
                ("$sequence", attempt + 1),
                ("$now", now));

        private static async Task ExecuteAsync(
            DbConnection connection,
            DbTransaction transaction,
            string sql,
            CancellationToken cancellationToken,
            params (string Name, object Value)[] parameters)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = value;
                command.Parameters.Add(parameter);
            }
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
