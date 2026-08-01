using System.Data.Common;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using OpenCoWork.Abstractions;
using OpenCoWork.Automations;
using OpenCoWork.Core.Capabilities;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Gateway;
using OpenCoWork.Core.Logging;
using OpenCoWork.Core.Operations;
using OpenCoWork.Core.State;
using OpenCoWork.Core.Workspaces;
using OpenCoWork.Teams;
using Xunit;

namespace OpenCoWork.IntegrationTests;

public sealed class GatewayOperationsLoadTests(ITestOutputHelper output)
{
    private const int PageSize = 100;

    [Fact]
    public async Task Fixed_load_pages_gateway_and_operations_without_loss()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await LoadFixture.CreateAsync(cancellationToken);
        var process = Process.GetCurrentProcess();
        var handlesBefore = process.HandleCount;
        var memoryBefore = GC.GetTotalMemory(forceFullCollection: true);

        var seed = Stopwatch.StartNew();
        await fixture.SeedAsync(cancellationToken);
        seed.Stop();

        var page = Stopwatch.StartNew();
        var channels = await fixture.Channels.ListChannelsAsync(
            new ChannelListQuery(PageSize: PageSize),
            cancellationToken);
        var inbound = await ReadInboundAsync(fixture.Channels, cancellationToken);
        var outbox = await ReadOutboxAsync(fixture.Channels, cancellationToken);
        var traces = await ReadTracesAsync(fixture.Operations, cancellationToken);
        var proposals = await ReadProposalsAsync(fixture.Insights, cancellationToken);
        var trace = await fixture.Operations.GetTraceAsync(
            "00000000000000000000000000000000",
            cancellationToken);
        var usage = await fixture.Operations.QueryUsageAsync(
            new UsageQuery(
                fixture.Now.AddHours(-1),
                fixture.Now.AddHours(1),
                OperationsTimeBucket.Hour),
            cancellationToken);
        page.Stop();

        Assert.Equal(8, channels.Items.Count);
        Assert.Equal(25_600, inbound.Count);
        Assert.Equal(25_600, inbound.Distinct().Count());
        Assert.Equal(10_000, outbox.Count);
        Assert.Equal(10_000, outbox.Select(item => item.OutboxMessageId).Distinct().Count());
        Assert.Equal(1_000, outbox.Count(item => item.Status == ChannelOutboxStatus.Failed));
        Assert.Equal(100, outbox.Count(item => item.Status == ChannelOutboxStatus.DeadLettered));
        Assert.Equal(1_000, traces.Count);
        Assert.Equal(1_000, traces.Distinct().Count());
        Assert.Equal(100, trace.Count);
        Assert.Equal(1_000, proposals.Count);
        Assert.Equal(1_000, proposals.Distinct().Count());
        var aggregate = Assert.Single(usage);
        Assert.Equal(10_000, aggregate.PromptTokens);
        Assert.Equal(10_000, aggregate.CompletionTokens);
        Assert.Equal(20_000, aggregate.TotalTokens);

        var handlesAfter = process.HandleCount;
        var memoryAfter = GC.GetTotalMemory(forceFullCollection: true);
        output.WriteLine(
            "channels=8; conversationsPerChannel=32; messagesPerConversation=100; " +
            "inbound=25600; outbox=10000; retryable=1000; deadLetter=100; " +
            "traceSpans=100000; usage=10000; proposals=1000; pageSize=100; " +
            "seedMs={0}; pageMs={1}; sqliteBusy=0; handlesBefore={2}; " +
            "handlesAfter={3}; memoryBefore={4}; memoryAfter={5}",
            seed.ElapsedMilliseconds,
            page.ElapsedMilliseconds,
            handlesBefore,
            handlesAfter,
            memoryBefore,
            memoryAfter);
    }

    private static async Task<List<Guid>> ReadInboundAsync(
        IChannelService channels,
        CancellationToken cancellationToken)
    {
        var result = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await channels.ListInboundAsync(
                new ChannelInboundQuery(PageSize: PageSize, Cursor: cursor),
                cancellationToken);
            result.AddRange(page.Items.Select(item => item.InboundMessageId));
            cursor = page.NextCursor;
        }
        while (cursor is not null);
        return result;
    }

    private static async Task<List<ChannelOutboxSummary>> ReadOutboxAsync(
        IChannelService channels,
        CancellationToken cancellationToken)
    {
        var result = new List<ChannelOutboxSummary>();
        string? cursor = null;
        do
        {
            var page = await channels.ListOutboxAsync(
                new ChannelOutboxQuery(PageSize: PageSize, Cursor: cursor),
                cancellationToken);
            result.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        while (cursor is not null);
        return result;
    }

    private static async Task<List<string>> ReadTracesAsync(
        IOperationsQueryService operations,
        CancellationToken cancellationToken)
    {
        var result = new List<string>();
        string? cursor = null;
        do
        {
            var page = await operations.ListTracesAsync(
                new TraceListQuery(PageSize: PageSize, Cursor: cursor),
                cancellationToken);
            result.AddRange(page.Items.Select(item => item.TraceId));
            cursor = page.NextCursor;
        }
        while (cursor is not null);
        return result;
    }

    private static async Task<List<Guid>> ReadProposalsAsync(
        IWorkspaceInsightService insights,
        CancellationToken cancellationToken)
    {
        var result = new List<Guid>();
        string? cursor = null;
        do
        {
            var page = await insights.ListAsync(PageSize, cursor, cancellationToken);
            result.AddRange(page.Items.Select(item => item.ProposalId));
            cursor = page.NextCursor;
        }
        while (cursor is not null);
        return result;
    }

    private sealed class LoadFixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly StateRuntime _state;

        private LoadFixture(string root, StateRuntime state, OpenCoWorkPaths paths)
        {
            _root = root;
            _state = state;
            Now = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
            var credentials = new ChannelCredentialService(
                new InMemoryOsSecretStore(),
                new SecretRedactor([]),
                paths);
            var reconciler = new GatewayReconciler(
                state,
                new GatewayConfig(),
                credentials,
                new NoopSender(),
                gateway: null,
                TimeProvider.System,
                faultInjector: null);
            Channels = new ChannelOperationsService(
                state,
                new GatewayMediaStore(paths, state),
                reconciler);
            Operations = new OperationsQueryService(state);
            Insights = new WorkspaceInsightService(state, TimeProvider.System);
        }

        public DateTimeOffset Now { get; }
        public IChannelService Channels { get; }
        public IOperationsQueryService Operations { get; }
        public IWorkspaceInsightService Insights { get; }

        public static async Task<LoadFixture> CreateAsync(
            CancellationToken cancellationToken)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"opencowork-gateway-load-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var paths = new OpenCoWorkPaths(root);
            var state = new StateRuntime(
                paths,
                TimeSpan.FromSeconds(10),
                [
                    .. GatewayStateMigrationContributors.Create(),
                    .. TeamsStateMigrationContributors.Create(),
                    .. AutomationsStateMigrationContributors.Create(),
                ]);
            await state.InitializeAsync(cancellationToken);
            return new LoadFixture(root, state, paths);
        }

        public Task SeedAsync(CancellationToken cancellationToken) =>
            _state.WriteAsync(
                async (connection, transaction, token) =>
                {
                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = SeedSql;
                    Add(command, "$now", Now.ToUnixTimeMilliseconds());
                    await command.ExecuteNonQueryAsync(token);
                    return 0;
                },
                cancellationToken).AsTask();

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(_root, recursive: true);
            return ValueTask.CompletedTask;
        }

        private static void Add(DbCommand command, string name, object value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
    }

    private sealed class NoopSender : IChannelSender
    {
        public ValueTask<ChannelSendResult> SendAsync(
            ChannelSendRequest request,
            ReadOnlyMemory<byte> secret,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ChannelSendResult(true, false));
    }

    private const string SeedSql =
        """
        INSERT INTO threads (
            thread_id, display_name, display_name_search, status, availability,
            history_mode, current_sequence, last_applied_sequence, created_utc, updated_utc)
        VALUES ('018f0000-1000-7000-8000-000000000001', 'load', 'load', 'active',
                'available', 'server', 10001, 10001, $now, $now);
        INSERT INTO turns (
            turn_id, thread_id, status, created_utc, updated_utc, completed_utc,
            effective_agent_mode, correlation_id)
        VALUES ('018f0000-1000-7000-8000-000000000002',
                '018f0000-1000-7000-8000-000000000001', 'completed', $now, $now, $now,
                'agent', '018f0000-1000-7000-8000-000000000003');
        INSERT INTO agent_invocations (
            invocation_id, thread_id, turn_id, snapshot_json, recorded_sequence, created_utc)
        VALUES ('018f0000-1000-7000-8000-000000000004',
                '018f0000-1000-7000-8000-000000000001',
                '018f0000-1000-7000-8000-000000000002',
                '{"providerId":"deepseek","modelId":"deepseek-v4-flash"}', 1, $now);

        WITH RECURSIVE seq(x) AS (VALUES(0) UNION ALL SELECT x + 1 FROM seq WHERE x < 7)
        INSERT INTO channels (
            channel_id, kind, enabled, definition_sha256, trust_status,
            runtime_status, revision, created_utc, updated_utc)
        SELECT printf('channel-%d', x), 'webhook', 1,
               'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
               'trusted', 'ready', 1, $now + x, $now + x FROM seq;

        WITH RECURSIVE seq(x) AS (VALUES(0) UNION ALL SELECT x + 1 FROM seq WHERE x < 25599)
        INSERT INTO channel_inbound_messages (
            inbound_message_id, channel_id, external_message_id,
            external_conversation_id, partition_sequence, payload_json, body_sha256,
            session_create_idempotency_key, session_submit_idempotency_key,
            correlation_id, status, attempt_count, next_attempt_utc, revision,
            created_utc, updated_utc, delivered_utc)
        SELECT printf('018f0000-0001-7000-8000-%012x', x),
               printf('channel-%d', x / 3200), printf('message-%d', x),
               printf('conversation-%d', (x / 100) % 32), (x % 100) + 1, '{}',
               'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
               printf('018f0000-0002-7000-8000-%012x', x),
               printf('018f0000-0003-7000-8000-%012x', x),
               printf('018f0000-0004-7000-8000-%012x', x),
               'delivered', 1, $now + x, 1, $now + x, $now + x, $now + x
        FROM seq;

        WITH RECURSIVE seq(x) AS (VALUES(0) UNION ALL SELECT x + 1 FROM seq WHERE x < 9999)
        INSERT INTO channel_outbox (
            outbox_message_id, delivery_id, channel_id, external_conversation_id,
            source_message_id, thread_id, turn_id, correlation_id, partition_sequence,
            envelope_json, body_sha256, status, attempt_count, next_attempt_utc,
            error_code, revision, created_utc, updated_utc, sent_utc)
        SELECT printf('018f0000-0010-7000-8000-%012x', x),
               printf('018f0000-0011-7000-8000-%012x', x),
               printf('channel-%d', x % 8), printf('conversation-%d', (x / 8) % 32),
               printf('source-%d', x), '018f0000-1000-7000-8000-000000000001',
               '018f0000-1000-7000-8000-000000000002',
               printf('018f0000-0012-7000-8000-%012x', x), (x / 256) + 1, '{}',
               'cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc',
               CASE WHEN x % 100 = 0 THEN 'deadLettered'
                    WHEN x % 10 = 1 THEN 'failed' ELSE 'sent' END,
               CASE WHEN x % 100 = 0 THEN 5 WHEN x % 10 = 1 THEN 1 ELSE 1 END,
               $now + x,
               CASE WHEN x % 100 = 0 THEN 'channel.deliveryFailed'
                    WHEN x % 10 = 1 THEN 'channel.unavailable' ELSE NULL END,
               1, $now + x, $now + x,
               CASE WHEN x % 100 <> 0 AND x % 10 <> 1 THEN $now + x ELSE NULL END
        FROM seq;

        WITH RECURSIVE seq(x) AS (VALUES(0) UNION ALL SELECT x + 1 FROM seq WHERE x < 99999)
        INSERT INTO trace_spans (
            trace_id, span_id, name, kind, status, duration_ms, tags_json,
            error_code, started_utc, ended_utc)
        SELECT printf('%032x', x / 100), printf('%016x', x), 'gateway.dispatch',
               'consumer', CASE WHEN x % 100 = 0 THEN 'error' ELSE 'ok' END,
               1, '{}', CASE WHEN x % 100 = 0 THEN 'channel.deliveryFailed' ELSE NULL END,
               $now + (x / 100), $now + (x / 100) + 1
        FROM seq;

        WITH RECURSIVE seq(x) AS (VALUES(0) UNION ALL SELECT x + 1 FROM seq WHERE x < 9999)
        INSERT INTO provider_usage (
            invocation_id, attempt_number, purpose, thread_id, turn_id,
            usage_json, recorded_sequence, created_utc)
        SELECT '018f0000-1000-7000-8000-000000000004', x + 1, 'response',
               '018f0000-1000-7000-8000-000000000001',
               '018f0000-1000-7000-8000-000000000002',
               '{"source":"provider","promptTokens":1,"cachedPromptTokens":0,"completionTokens":1,"reasoningCompletionTokens":0,"totalTokens":2}',
               x + 2, $now
        FROM seq;

        WITH RECURSIVE seq(x) AS (VALUES(0) UNION ALL SELECT x + 1 FROM seq WHERE x < 999)
        INSERT INTO improvement_proposals (
            proposal_id, fingerprint_sha256, proposal_type, severity, summary,
            evidence_json, status, revision, created_utc, updated_utc)
        SELECT printf('018f0000-0020-7000-8000-%012x', x), printf('%064x', x + 1),
               'reliability', 'warning', 'Load proposal', '{"count":1}',
               'open', 1, $now + x, $now + x
        FROM seq;

        UPDATE operations_state
        SET current_revision = 35600, updated_utc = $now + 25599 WHERE id = 1;
        """;
}
