using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Operations;
using OpenCoWork.Core.State;
using OpenCoWork.Core.Workspaces;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class OperationsRuntimeTests
{
    [Fact]
    public async Task Heartbeat_renews_stales_handles_clock_rollback_restarts_and_stops()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var files = await OperationsFiles.CreateAsync(cancellationToken);
        var clock = new ManualTimerTimeProvider(
            new DateTimeOffset(2026, 8, 1, 4, 0, 0, TimeSpan.Zero));
        var registry = new WorkspaceRegistryService(files.UserRoot, clock);
        var runtime = CreateRuntime(files, registry, clock);
        var query = new OperationsQueryService(files.State, clock);

        await runtime.StartAsync(cancellationToken);
        var first = await query.GetHeartbeatAsync(cancellationToken);
        Assert.NotNull(first);
        Assert.Equal(OperationsHealthStatus.Healthy, first.Status);
        Assert.Equal("gateway", first.PrimaryHost);
        Assert.True(first.SqliteWritable);

        clock.Advance(TimeSpan.FromSeconds(30));
        await WaitUntilAsync(
            async () => (await query.GetHeartbeatAsync(cancellationToken))!.Revision >
                        first.Revision,
            cancellationToken);
        var renewed = (await query.GetHeartbeatAsync(cancellationToken))!;

        clock.Advance(TimeSpan.FromMinutes(-5));
        await runtime.ObserveAsync(cancellationToken);
        var rolledBack = (await query.GetHeartbeatAsync(cancellationToken))!;
        Assert.True(rolledBack.ObservedAtUtc >= renewed.ObservedAtUtc);

        await runtime.StopAsync(cancellationToken);
        var stopped = (await query.GetHeartbeatAsync(cancellationToken))!;
        Assert.Equal(OperationsHealthStatus.Stopped, stopped.Status);
        Assert.NotNull(stopped.StoppedAtUtc);
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(
            stopped.Revision,
            (await query.GetHeartbeatAsync(cancellationToken))!.Revision);

        await files.State.WriteAsync(
            async (connection, transaction, token) =>
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    UPDATE workspace_heartbeat
                    SET status = 'healthy', stopped_utc = NULL,
                        expires_utc = $expired, revision = revision + 1;
                    """,
                    token,
                    ("$expired", clock.GetUtcNow().AddSeconds(-1).ToUnixTimeMilliseconds()));
                return true;
            },
            cancellationToken);
        Assert.Equal(
            OperationsHealthStatus.Stale,
            (await query.GetHeartbeatAsync(cancellationToken))!.Status);

        var restarted = CreateRuntime(files, registry, clock);
        await restarted.StartAsync(cancellationToken);
        Assert.NotEqual(
            runtime.RuntimeInstanceId,
            (await query.GetHeartbeatAsync(cancellationToken))!.RuntimeInstanceId);
        await restarted.StopAsync(cancellationToken);
    }

    [Fact]
    public async Task HubRegistry_is_atomic_concurrent_cwd_independent_and_isolates_bad_items()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var files = await OperationsFiles.CreateAsync(cancellationToken);
        var clock = new ManualTimerTimeProvider(
            new DateTimeOffset(2026, 8, 1, 5, 0, 0, TimeSpan.Zero));
        var registry = new WorkspaceRegistryService(files.UserRoot, clock);
        var workspaceId = await files.WorkspaceIdAsync(cancellationToken);

        await Task.WhenAll(Enumerable.Range(0, 16).Select(index =>
            registry.UpsertAsync(
                workspaceId,
                files.Root,
                files.Paths.OpenCoWorkDirectory,
                $"workspace-{index}",
                cancellationToken)));
        Assert.Single(await registry.ListAsync(cancellationToken));

        var registryPath = Path.Combine(
            files.UserRoot,
            ".opencowork",
            "workspaces.json");
        using (var document = JsonDocument.Parse(
                   await File.ReadAllTextAsync(registryPath, cancellationToken)))
        {
            var valid = document.RootElement.GetProperty("workspaces")[0].GetRawText();
            await File.WriteAllTextAsync(
                registryPath,
                $$"""{"schemaVersion":1,"workspaces":[{{valid}},{"workspaceId":"bad"}]}""",
                cancellationToken);
        }
        Assert.Single(await registry.ListAsync(cancellationToken));

        var hub = new HubService(registry, clock);
        var listed = await hub.ListWorkspacesAsync(cancellationToken);
        Assert.Single(listed);
        Assert.Equal(HubWorkspaceAvailability.Stale, listed[0].Availability);

        var dashboard = await hub.GetDashboardAsync(workspaceId, cancellationToken);
        Assert.NotNull(dashboard);
        Assert.Equal(workspaceId, dashboard.WorkspaceId);
        Assert.False(Directory.Exists(Path.Combine(files.Root, ".opencowork", "threads-created-by-hub")));
    }

    [Fact]
    public async Task Dashboard_and_WorkspaceInsight_are_read_only_deterministic_deduplicated_and_archivable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var files = await OperationsFiles.CreateAsync(cancellationToken);
        var clock = new ManualTimerTimeProvider(
            new DateTimeOffset(2026, 8, 1, 6, 0, 0, TimeSpan.Zero));
        await files.SeedSignalsAsync(clock.GetUtcNow(), cancellationToken);
        var query = new OperationsQueryService(files.State, clock);
        IWorkspaceInsightService insights = new WorkspaceInsightService(files.State, clock);

        var beforeThreads = await files.ScalarAsync<long>(
            "SELECT count(*) FROM threads;",
            cancellationToken);
        var firstRun = await insights.RunAsync(InsightRunTrigger.Manual, cancellationToken);
        var firstPage = await insights.ListAsync(1, cancellationToken: cancellationToken);
        Assert.Equal(InsightRunStatus.Completed, firstRun.Status);
        Assert.True(firstRun.ProposalCount >= 2);
        Assert.Single(firstPage.Items);
        Assert.NotNull(firstPage.NextCursor);
        Assert.All(firstPage.Items, proposal =>
        {
            Assert.DoesNotContain("secret", proposal.Evidence.GetRawText(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(ImprovementProposalStatus.Open, proposal.Status);
        });

        var tracked = firstPage.Items[0];
        var restartedInsights = new WorkspaceInsightService(files.State, clock);
        Assert.Equal(
            firstRun.InsightRunId,
            (await restartedInsights.RunAsync(
                InsightRunTrigger.Scheduled,
                cancellationToken)).InsightRunId);
        insights = restartedInsights;
        _ = await insights.RunAsync(InsightRunTrigger.Manual, cancellationToken);
        var deduplicated = await insights.GetAsync(tracked.ProposalId, cancellationToken);
        Assert.NotNull(deduplicated);
        Assert.True(deduplicated.Revision > tracked.Revision);
        var archived = await insights.ArchiveAsync(
            tracked.ProposalId,
            deduplicated.Revision,
            cancellationToken);
        Assert.Equal(ImprovementProposalStatus.Archived, archived.Status);

        _ = await insights.RunAsync(InsightRunTrigger.Manual, cancellationToken);
        Assert.Equal(
            ImprovementProposalStatus.Archived,
            (await insights.GetAsync(tracked.ProposalId, cancellationToken))!.Status);
        Assert.Equal(
            beforeThreads,
            await files.ScalarAsync<long>("SELECT count(*) FROM threads;", cancellationToken));

        var dashboard = await query.GetDashboardAsync(cancellationToken);
        Assert.Equal(1, dashboard.DeadLetterInbound);
        Assert.Equal(1, dashboard.DeadLetterOutbox);
        Assert.True(dashboard.TraceErrors >= 3);
        Assert.True(dashboard.OpenProposals >= 1);
    }

    private static OperationsRuntime CreateRuntime(
        OperationsFiles files,
        IWorkspaceRegistryService registry,
        TimeProvider clock) =>
        new(
            files.State,
            new OperationsTraceCollector(files.State),
            registry,
            files.Paths,
            clock,
            () => new OperationsRuntimeHealth(
                "gateway",
                "running",
                [new OperationsModuleHealth("session", "healthy")]));

    private static async Task WaitUntilAsync(
        Func<Task<bool>> predicate,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (await predicate())
            {
                return;
            }
            await Task.Delay(10, cancellationToken);
        }
        Assert.Fail("Condition was not observed.");
    }

    private sealed class OperationsFiles : IAsyncDisposable
    {
        private OperationsFiles(
            string root,
            string userRoot,
            OpenCoWorkPaths paths,
            StateRuntime state)
        {
            Root = root;
            UserRoot = userRoot;
            Paths = paths;
            State = state;
        }

        public string Root { get; }
        public string UserRoot { get; }
        public OpenCoWorkPaths Paths { get; }
        public StateRuntime State { get; }

        public static async Task<OperationsFiles> CreateAsync(
            CancellationToken cancellationToken)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"opencowork-operations-runtime-{Guid.NewGuid():N}");
            var userRoot = Path.Combine(root, "user");
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(userRoot);
            Directory.CreateDirectory(workspace);
            var paths = new OpenCoWorkPaths(workspace);
            var state = new StateRuntime(
                paths,
                TimeSpan.FromSeconds(2),
                GatewayStateMigrationContributors.Create());
            await state.InitializeAsync(cancellationToken);
            return new OperationsFiles(workspace, userRoot, paths, state);
        }

        public async Task<Guid> WorkspaceIdAsync(CancellationToken cancellationToken) =>
            Guid.Parse(await ScalarAsync<string>(
                "SELECT workspace_id FROM operations_state WHERE id = 1;",
                cancellationToken));

        public async Task SeedSignalsAsync(
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            var milliseconds = now.ToUnixTimeMilliseconds();
            var threadId = Guid.CreateVersion7().ToString("D");
            var turnId = Guid.CreateVersion7().ToString("D");
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
                        VALUES ($thread, 'operations-test', 'operations-test', 'active',
                                'available', 'server', 0, 0, $now, $now);
                        INSERT INTO turns (
                            turn_id, thread_id, status, created_utc, updated_utc,
                            completed_utc)
                        VALUES ($turn, $thread, 'completed', $now, $now, $now);
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
                            status, attempt_count, next_attempt_utc, error_code,
                            revision, created_utc, updated_utc)
                        VALUES ($inbound, 'alpha', 'm1', 'c1', 1, '{}',
                                'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
                                $createKey, $submitKey, $correlation,
                                'deadLettered', 5, $now, 'channel.deliveryFailed',
                                1, $now, $now);
                        INSERT INTO channel_outbox (
                            outbox_message_id, delivery_id, channel_id,
                            external_conversation_id, source_message_id,
                            thread_id, turn_id, correlation_id, partition_sequence,
                            envelope_json, body_sha256,
                            status, attempt_count, next_attempt_utc, error_code,
                            retry_idempotency_key, revision, created_utc, updated_utc)
                        VALUES ($outbox, $delivery, 'alpha', 'c1', 'm1',
                                $thread, $turn, $correlation, 1, '{}',
                                'cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc',
                                'deadLettered', 5, $now, 'channel.deliveryFailed',
                                $retryKey, 1, $now, $now);
                        """,
                        token,
                        ("$thread", threadId),
                        ("$turn", turnId),
                        ("$inbound", Guid.CreateVersion7().ToString("D")),
                        ("$createKey", Guid.CreateVersion7().ToString("D")),
                        ("$submitKey", Guid.CreateVersion7().ToString("D")),
                        ("$correlation", Guid.CreateVersion7().ToString("D")),
                        ("$outbox", Guid.CreateVersion7().ToString("D")),
                        ("$delivery", Guid.CreateVersion7().ToString("D")),
                        ("$retryKey", Guid.CreateVersion7().ToString("D")),
                        ("$now", milliseconds));
                    for (var index = 0; index < 3; index++)
                    {
                        await ExecuteAsync(
                            connection,
                            transaction,
                            """
                            INSERT INTO trace_spans (
                                trace_id, span_id, name, kind, status, duration_ms,
                                tags_json, error_code, started_utc, ended_utc)
                            VALUES ($trace, $span, 'gateway.dispatch', 'consumer', 'error',
                                    1, '{}', 'channel.deliveryFailed', $now, $now);
                            """,
                            token,
                            ("$trace", Guid.NewGuid().ToString("N")),
                            ("$span", Guid.NewGuid().ToString("N")[..16]),
                            ("$now", milliseconds));
                    }
                    return true;
                },
                cancellationToken);
        }

        public async Task<T> ScalarAsync<T>(
            string sql,
            CancellationToken cancellationToken)
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
            Directory.Delete(Directory.GetParent(Root)!.FullName, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

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
