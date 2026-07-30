using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenCoWork.Abstractions;
using OpenCoWork.Automations;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Sessions;
using OpenCoWork.Core.State;
using OpenCoWork.Core.Workspaces;
using Xunit;

namespace OpenCoWork.IntegrationTests;

public sealed class AutomationDispatchTests
{
    [Fact]
    public async Task Project_read_only_dispatch_creates_one_unattended_thread_and_turn()
    {
        await using var fixture = await Fixture.CreateAsync();
        var run = await fixture.SeedRunAsync(
            AutomationWorkspaceMode.Project,
            workspaceWrite: false,
            allowDirtyOrigin: false);
        var dispatcher = fixture.CreateDispatcher();

        Assert.True(await dispatcher.DispatchNextAsync(
            run.RunId,
            "test-worker",
            TestContext.Current.CancellationToken));
        Assert.True(await dispatcher.DispatchNextAsync(
            run.RunId,
            "test-worker",
            TestContext.Current.CancellationToken));
        Assert.False(await dispatcher.DispatchNextAsync(
            run.RunId,
            "test-worker",
            TestContext.Current.CancellationToken));

        var state = await fixture.ReadRunAsync(run.RunId);
        Assert.Equal(AutomationRunStatus.Running, state.Status);
        Assert.NotNull(state.ThreadId);
        Assert.Null(state.WorktreeId);
        Assert.Null(state.ProjectWriterLeaseId);
        Assert.Equal(2, await fixture.CountCompletedIntentsAsync(run.RunId));
        Assert.Equal(1, await fixture.CountTurnsAsync(state.ThreadId!.Value));
        Assert.Null(await fixture.Prepared.ReadAsync(
            run.PreparedTurnId,
            TestContext.Current.CancellationToken));

        var thread = await fixture.Sessions.GetThreadAsync(
            state.ThreadId.Value,
            TestContext.Current.CancellationToken);
        Assert.Equal(fixture.Workspace.Root, thread.Value!.ExecutionWorkspace!.WorkspaceRoot);
        Assert.Equal(CoWorkWorkspaceMode.Project, thread.Value.ExecutionWorkspace.Mode);
        Assert.Equal(run.RunId, thread.Value.AutomationProvenance!.AutomationRunId);
        Assert.Equal("sample", thread.Value.AutomationProvenance.AutomationId);
        Assert.Null(thread.Value.CoWorkProvenance);
        var projected = await new SessionProjection(fixture.Workspace.Store)
            .ReadThreadSnapshotAsync(
                state.ThreadId.Value,
                TestContext.Current.CancellationToken);
        Assert.Equal(run.RunId, projected!.AutomationProvenance!.AutomationRunId);
    }

    [Fact]
    public async Task Project_writer_dispatch_acquires_the_shared_writer_lease()
    {
        await using var fixture = await Fixture.CreateAsync();
        var run = await fixture.SeedRunAsync(
            AutomationWorkspaceMode.Project,
            workspaceWrite: true,
            allowDirtyOrigin: false);
        var dispatcher = fixture.CreateDispatcher();

        Assert.True(await dispatcher.DispatchNextAsync(
            run.RunId,
            "writer",
            TestContext.Current.CancellationToken));

        var state = await fixture.ReadRunAsync(run.RunId);
        Assert.NotNull(state.ThreadId);
        Assert.NotNull(state.ProjectWriterLeaseId);
        var sameLease = await fixture.WriterLeases.TryAcquireAsync(
            new ProjectWriterLeaseOwner(
                ProjectWriterLeaseOwnerKind.AutomationRun,
                run.RunId),
            TestContext.Current.CancellationToken);
        Assert.Equal(state.ProjectWriterLeaseId, sameLease!.LeaseId);
    }

    [Fact]
    public async Task Shared_writer_lease_contention_requeues_without_spending_an_attempt()
    {
        await using var fixture = await Fixture.CreateAsync();
        var blocker = new ProjectWriterLeaseOwner(
            ProjectWriterLeaseOwnerKind.CoWorkAgentRun,
            Guid.CreateVersion7());
        var lease = await fixture.WriterLeases.TryAcquireAsync(
            blocker,
            TestContext.Current.CancellationToken);
        var run = await fixture.SeedRunAsync(
            AutomationWorkspaceMode.Project,
            workspaceWrite: true,
            allowDirtyOrigin: false);
        var dispatcher = fixture.CreateDispatcher();

        Assert.True(await dispatcher.DispatchNextAsync(
            run.RunId,
            "contended",
            TestContext.Current.CancellationToken));
        Assert.Null((await fixture.ReadRunAsync(run.RunId)).ThreadId);
        Assert.Equal(
            ("pending", 0L),
            await fixture.ReadActiveIntentAsync(run.RunId));

        Assert.True(await fixture.WriterLeases.ReleaseAsync(
            blocker,
            lease!.LeaseId,
            TestContext.Current.CancellationToken));
        Assert.True(await dispatcher.DispatchNextAsync(
            run.RunId,
            "contended",
            TestContext.Current.CancellationToken));
        Assert.NotNull((await fixture.ReadRunAsync(run.RunId)).ThreadId);
    }

    [Fact]
    public async Task Worktree_dispatch_freezes_base_and_uses_a_run_scoped_root()
    {
        await using var fixture = await Fixture.CreateAsync();
        var run = await fixture.SeedRunAsync(
            AutomationWorkspaceMode.Worktree,
            workspaceWrite: true,
            allowDirtyOrigin: true);
        var worktrees = new RecordingWorktrees(
            fixture.Descriptor.WorktreesRoot,
            new string('c', 40),
            isDirty: true);
        var dispatcher = fixture.CreateDispatcher(worktrees);

        Assert.True(await dispatcher.DispatchNextAsync(
            run.RunId,
            "worktree",
            TestContext.Current.CancellationToken));
        Assert.True(await dispatcher.DispatchNextAsync(
            run.RunId,
            "worktree",
            TestContext.Current.CancellationToken));
        Assert.True(await dispatcher.DispatchNextAsync(
            run.RunId,
            "worktree",
            TestContext.Current.CancellationToken));

        var state = await fixture.ReadRunAsync(run.RunId);
        Assert.Equal(new string('c', 40), state.BaseCommitSha);
        Assert.Null(state.ProjectWriterLeaseId);
        Assert.Equal(run.RunId, worktrees.Requests.Single().AgentRunId);
        Assert.True(worktrees.Requests.Single().AllowDirtyOrigin);
        Assert.Equal(new string('c', 40), worktrees.Requests.Single().BaseCommitSha);
        var thread = await fixture.Sessions.GetThreadAsync(
            state.ThreadId!.Value,
            TestContext.Current.CancellationToken);
        Assert.Equal(CoWorkWorkspaceMode.Worktree, thread.Value!.ExecutionWorkspace!.Mode);
        Assert.Equal(
            Path.Combine(fixture.Descriptor.WorktreesRoot, run.RunId.ToString("D")),
            thread.Value.ExecutionWorkspace.WorktreeRoot);
    }

    [Fact]
    public async Task AutomationRetentionTests_clean_worktree_cleanup_replays_after_archive_crash()
    {
        await using var fixture = await Fixture.CreateAsync();
        var run = await fixture.SeedRunAsync(
            AutomationWorkspaceMode.Worktree,
            workspaceWrite: true,
            allowDirtyOrigin: true);
        var worktrees = new RecordingWorktrees(
            fixture.Descriptor.WorktreesRoot,
            new string('c', 40),
            isDirty: false);
        var clock = new DispatchTimeProvider(DateTimeOffset.UtcNow);
        var dispatcher = fixture.CreateDispatcher(worktrees, clock);
        for (var step = 0; step < 3; step++)
        {
            Assert.True(await dispatcher.DispatchNextAsync(
                run.RunId,
                "retention",
                TestContext.Current.CancellationToken));
        }

        await fixture.SeedRetentionIntentAsync(run.RunId);
        var crashed = fixture.CreateDispatcher(
            worktrees,
            clock,
            point =>
            {
                if (point == AutomationDispatchFaultPoint.AfterThreadArchived)
                {
                    throw new InjectedCrash();
                }
            });
        await Assert.ThrowsAsync<InjectedCrash>(() => crashed.DispatchNextAsync(
            run.RunId,
            "retention-crash",
            TestContext.Current.CancellationToken));

        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.True(await dispatcher.DispatchNextAsync(
            run.RunId,
            "retention-replay",
            TestContext.Current.CancellationToken));
        Assert.True(await dispatcher.DispatchNextAsync(
            run.RunId,
            "retention-replay",
            TestContext.Current.CancellationToken));

        var state = await fixture.ReadRunAsync(run.RunId);
        var thread = await fixture.Sessions.GetThreadAsync(
            state.ThreadId!.Value,
            TestContext.Current.CancellationToken);
        Assert.Equal(ThreadStatus.Archived, thread.Value!.Status);
        Assert.Null(state.WorktreeId);
        Assert.Equal(1, worktrees.RemoveCount);
    }

    [Fact]
    public async Task AutomationRetentionTests_dirty_worktree_is_preserved()
    {
        await using var fixture = await Fixture.CreateAsync();
        var run = await fixture.SeedRunAsync(
            AutomationWorkspaceMode.Worktree,
            workspaceWrite: true,
            allowDirtyOrigin: true);
        var worktrees = new RecordingWorktrees(
            fixture.Descriptor.WorktreesRoot,
            new string('d', 40),
            isDirty: false,
            retainOnRemove: true);
        var dispatcher = fixture.CreateDispatcher(worktrees);
        for (var step = 0; step < 3; step++)
        {
            Assert.True(await dispatcher.DispatchNextAsync(
                run.RunId,
                "retention-dirty",
                TestContext.Current.CancellationToken));
        }

        await fixture.SeedRetentionIntentAsync(run.RunId);
        Assert.True(await dispatcher.DispatchNextAsync(
            run.RunId,
            "retention-dirty",
            TestContext.Current.CancellationToken));
        Assert.True(await dispatcher.DispatchNextAsync(
            run.RunId,
            "retention-dirty",
            TestContext.Current.CancellationToken));

        var state = await fixture.ReadRunAsync(run.RunId);
        Assert.NotNull(state.WorktreeId);
        Assert.Equal(
            CoWorkWorktreeStatus.RetainedDirty,
            (await worktrees.GetAsync(
                state.WorktreeId.Value,
                TestContext.Current.CancellationToken))!.Status);
    }

    [Fact]
    public async Task Dirty_origin_fails_closed_before_worktree_creation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var run = await fixture.SeedRunAsync(
            AutomationWorkspaceMode.Worktree,
            workspaceWrite: false,
            allowDirtyOrigin: false);
        var worktrees = new RecordingWorktrees(
            fixture.Descriptor.WorktreesRoot,
            new string('d', 40),
            isDirty: true);

        Assert.True(await fixture.CreateDispatcher(worktrees).DispatchNextAsync(
            run.RunId,
            "dirty",
            TestContext.Current.CancellationToken));

        var state = await fixture.ReadRunAsync(run.RunId);
        Assert.Equal(AutomationRunStatus.Failed, state.Status);
        Assert.Equal(AutomationErrorCodes.WorktreeDirty, state.ErrorCode);
        Assert.Empty(worktrees.Requests);
    }

    [Fact]
    public async Task Escaping_worktree_path_fails_closed()
    {
        await using var fixture = await Fixture.CreateAsync();
        var run = await fixture.SeedRunAsync(
            AutomationWorkspaceMode.Worktree,
            workspaceWrite: false,
            allowDirtyOrigin: false);
        var worktrees = new RecordingWorktrees(
            fixture.Descriptor.WorktreesRoot,
            new string('e', 40),
            isDirty: false,
            escapeRoot: true);

        Assert.True(await fixture.CreateDispatcher(worktrees).DispatchNextAsync(
            run.RunId,
            "escape",
            TestContext.Current.CancellationToken));

        var state = await fixture.ReadRunAsync(run.RunId);
        Assert.Equal(AutomationRunStatus.Failed, state.Status);
        Assert.Equal(AutomationErrorCodes.PathEscape, state.ErrorCode);
    }

    [Fact]
    public async Task Crash_after_thread_creation_replays_the_same_thread()
    {
        await using var fixture = await Fixture.CreateAsync();
        var run = await fixture.SeedRunAsync(
            AutomationWorkspaceMode.Project,
            workspaceWrite: false,
            allowDirtyOrigin: false);
        var time = new DispatchTimeProvider(DateTimeOffset.UtcNow);

        await Assert.ThrowsAsync<InjectedCrash>(() =>
            fixture.CreateDispatcher(
                    timeProvider: time,
                    faultInjector: point =>
                    {
                        if (point == AutomationDispatchFaultPoint.AfterThreadCreated)
                        {
                            throw new InjectedCrash();
                        }
                    })
                .DispatchNextAsync(
                    run.RunId,
                    "crash-thread",
                    TestContext.Current.CancellationToken));

        time.Advance(TimeSpan.FromMinutes(3));
        var recovered = fixture.CreateDispatcher(timeProvider: time);
        Assert.True(await recovered.DispatchNextAsync(
            run.RunId,
            "crash-thread",
            TestContext.Current.CancellationToken));
        Assert.True(await recovered.DispatchNextAsync(
            run.RunId,
            "crash-thread",
            TestContext.Current.CancellationToken));
        Assert.Equal(1, await fixture.CountThreadsAsync());
        Assert.Equal(
            1,
            await fixture.CountTurnsAsync(
                (await fixture.ReadRunAsync(run.RunId)).ThreadId!.Value));
    }

    [Theory]
    [InlineData(AutomationDispatchFaultPoint.BeforeTurnSubmitted, 0)]
    [InlineData(AutomationDispatchFaultPoint.AfterTurnSubmitted, 1)]
    public async Task Turn_submission_crashes_recover_without_duplicates(
        AutomationDispatchFaultPoint faultPoint,
        int turnsBeforeRecovery)
    {
        await using var fixture = await Fixture.CreateAsync();
        var run = await fixture.SeedRunAsync(
            AutomationWorkspaceMode.Project,
            workspaceWrite: false,
            allowDirtyOrigin: false);
        var time = new DispatchTimeProvider(DateTimeOffset.UtcNow);
        var crashing = fixture.CreateDispatcher(
            timeProvider: time,
            faultInjector: point =>
            {
                if (point == faultPoint)
                {
                    throw new InjectedCrash();
                }
            });
        Assert.True(await crashing.DispatchNextAsync(
            run.RunId,
            "crash-turn",
            TestContext.Current.CancellationToken));
        var threadId = (await fixture.ReadRunAsync(run.RunId)).ThreadId!.Value;

        await Assert.ThrowsAsync<InjectedCrash>(() =>
            crashing.DispatchNextAsync(
                run.RunId,
                "crash-turn",
                TestContext.Current.CancellationToken));
        Assert.Equal(turnsBeforeRecovery, await fixture.CountTurnsAsync(threadId));

        time.Advance(TimeSpan.FromMinutes(3));
        Assert.True(await fixture.CreateDispatcher(timeProvider: time).DispatchNextAsync(
            run.RunId,
            "crash-turn",
            TestContext.Current.CancellationToken));
        Assert.Equal(1, await fixture.CountTurnsAsync(threadId));
        Assert.Null(await fixture.Prepared.ReadAsync(
            run.PreparedTurnId,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Missing_prepared_turn_deadletters_after_five_attempts()
    {
        await using var fixture = await Fixture.CreateAsync();
        var run = await fixture.SeedRunAsync(
            AutomationWorkspaceMode.Project,
            workspaceWrite: false,
            allowDirtyOrigin: false);
        var dispatcher = fixture.CreateDispatcher();
        Assert.True(await dispatcher.DispatchNextAsync(
            run.RunId,
            "retry",
            TestContext.Current.CancellationToken));
        Assert.True(await fixture.Prepared.DeleteAsync(
            run.PreparedTurnId,
            run.RequestSha256,
            TestContext.Current.CancellationToken));

        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.True(await dispatcher.DispatchNextAsync(
                run.RunId,
                "retry",
                TestContext.Current.CancellationToken));
        }

        var state = await fixture.ReadRunAsync(run.RunId);
        Assert.Equal(AutomationRunStatus.Failed, state.Status);
        Assert.Equal(AutomationErrorCodes.RetryExhausted, state.ErrorCode);
        Assert.Equal(
            ("deadLettered", 5L),
            await fixture.ReadLatestIntentAsync(run.RunId));
    }

    private sealed record SeededRun(
        Guid RunId,
        Guid PreparedTurnId,
        string RequestSha256);

    private sealed record StoredRun(
        AutomationRunStatus Status,
        Guid? ThreadId,
        Guid? WorktreeId,
        Guid? ProjectWriterLeaseId,
        string? BaseCommitSha,
        string? ErrorCode);

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            AutomationSourceTestWorkspace workspace,
            PreparedAutomationTurnStore prepared,
            SessionService sessions,
            WorkspaceRuntimeDescriptor descriptor,
            ProjectWriterLeaseService writerLeases)
        {
            Workspace = workspace;
            Prepared = prepared;
            Sessions = sessions;
            Descriptor = descriptor;
            WriterLeases = writerLeases;
        }

        public AutomationSourceTestWorkspace Workspace { get; }

        public PreparedAutomationTurnStore Prepared { get; }

        public SessionService Sessions { get; }

        public WorkspaceRuntimeDescriptor Descriptor { get; }

        public ProjectWriterLeaseService WriterLeases { get; }

        public static async Task<Fixture> CreateAsync()
        {
            var workspace = await AutomationSourceTestWorkspace.CreateAsync();
            var paths = new OpenCoWorkPaths(workspace.Root);
            var descriptor = new WorkspaceRuntimeDescriptor(
                paths.WorkspaceRoot,
                paths.OpenCoWorkDirectory,
                paths.RuntimeDirectory,
                paths.TeamsRuntimeDirectory,
                paths.MissionsDirectory,
                paths.SubAgentsDirectory,
                paths.WorktreesDirectory);
            var prepared = new PreparedAutomationTurnStore(
                paths,
                new NoSensitiveDataService());
            var sessions = new SessionService(
                workspace.Store,
                new ThreadJournal(paths),
                new SessionProjection(workspace.Store),
                new SessionConfig(),
                executor: new CompletionExecutor(),
                executorKind: "test",
                paths: paths);
            return new Fixture(
                workspace,
                prepared,
                sessions,
                descriptor,
                new ProjectWriterLeaseService(workspace.Store, TimeProvider.System));
        }

        public AutomationDispatcher CreateDispatcher(
            IManagedWorktreeService? worktrees = null,
            TimeProvider? timeProvider = null,
            Action<AutomationDispatchFaultPoint>? faultInjector = null) =>
            new(
                Workspace.Store,
                Prepared,
                Sessions,
                worktrees ??
                new ManagedWorktreeService(new OpenCoWorkPaths(Workspace.Root)),
                WriterLeases,
                Descriptor,
                timeProvider ?? TimeProvider.System,
                faultInjector);

        public async Task<SeededRun> SeedRunAsync(
            AutomationWorkspaceMode mode,
            bool workspaceWrite,
            bool allowDirtyOrigin)
        {
            var runId = Guid.CreateVersion7();
            var preparedTurnId = Guid.CreateVersion7();
            var requestSha = Hash($"request:{runId:D}");
            const string prompt = "Perform the unattended sample task.";
            var promptSha = Hash(prompt);
            await Prepared.PrepareAsync(
                new AutomationPreparedTurnSnapshot(
                    preparedTurnId,
                    requestSha,
                    prompt,
                    promptSha,
                    DateTimeOffset.UtcNow),
                TestContext.Current.CancellationToken);
            var effects = workspaceWrite
                ? new[]
                {
                    new AutomationEffectPermissionSnapshot(
                        "workspaceWrite",
                        ToolAuthorityDecision.Allow),
                }
                : new[]
                {
                    new AutomationEffectPermissionSnapshot(
                        "workspaceRead",
                        ToolAuthorityDecision.Allow),
                };
            var permissions = new AutomationPermissionSnapshot(
                "trust",
                1,
                [],
                [],
                [],
                effects);
            var definition = JsonSerializer.Serialize(new
            {
                id = "sample",
                displayName = "Sample",
                workspace = new
                {
                    mode = mode == AutomationWorkspaceMode.Project
                        ? "project"
                        : "worktree",
                    allowDirtyOrigin,
                },
            });
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await Workspace.Store.WriteAsync(
                async (connection, transaction, cancellationToken) =>
                {
                    await ExecuteAsync(
                        connection,
                        transaction,
                        """
                        INSERT INTO automation_definitions (
                            automation_id, source_relative_path, source_status,
                            source_sha256, definition_version, display_name, enabled,
                            definition_json, diagnostics_json, has_schedule, revision,
                            created_utc, updated_utc, missing_utc)
                        VALUES (
                            'sample', 'sample.yaml', 'ready',
                            $sha, $sha, 'Sample', 1,
                            $definition, '[]', 0, 1,
                            $now, $now, NULL);

                        INSERT INTO automation_runs (
                            automation_run_id, automation_id, trigger_kind,
                            trigger_idempotency_key, scheduled_occurrence_utc, status,
                            definition_snapshot_json, inputs_sha256,
                            rendered_prompt_sha256, prepared_turn_id,
                            workspace_mode, workspace_access, provider_id, model_id,
                            permission_snapshot_json, capability_snapshot_json,
                            run_deadline_utc, attention_kind, attention_deadline_utc,
                            thread_id, worktree_id, base_commit_sha,
                            project_writer_lease_id, project_writer_lease_expires_utc,
                            safe_summary, error_code, diagnostic, revision,
                            created_utc, started_utc, updated_utc, completed_utc)
                        VALUES (
                            $runId, 'sample', 'manual', $trigger, NULL, 'pending',
                            $definition, $inputsSha, $promptSha, $preparedTurnId,
                            $mode, $access, 'provider-a', 'model-a',
                            $permissions, '[]',
                            $deadline, NULL, NULL,
                            NULL, NULL, NULL,
                            NULL, NULL,
                            NULL, NULL, NULL, 1,
                            $now, NULL, $now, NULL);

                        INSERT INTO automation_dispatch_intents (
                            intent_id, idempotency_key, dispatch_kind,
                            entity_kind, entity_id, status, attempt_count,
                            lease_owner, lease_expires_utc, error_code, diagnostic,
                            created_utc, updated_utc)
                        VALUES (
                            $intentId, $intentKey, $dispatchKind,
                            'automationRun', $runId, 'pending', 0,
                            NULL, NULL, NULL, NULL,
                            $now, $now);

                        INSERT INTO automation_command_receipts (
                            command_id, actor_kind, actor_id, command_kind, target_id,
                            request_sha256, result_json, revision, created_utc)
                        VALUES (
                            $runId, 'host', 'test', 'startRun', $runId,
                            $requestSha, '{}', 1, $now);
                        """,
                        cancellationToken,
                        ("$sha", new string('a', 64)),
                        ("$definition", definition),
                        ("$now", now),
                        ("$runId", runId.ToString("D")),
                        ("$trigger", $"manual:{runId:D}"),
                        ("$inputsSha", new string('b', 64)),
                        ("$promptSha", promptSha),
                        ("$preparedTurnId", preparedTurnId.ToString("D")),
                        ("$mode", mode == AutomationWorkspaceMode.Project
                            ? "project"
                            : "worktree"),
                        ("$access", workspaceWrite ? "readWrite" : "readOnly"),
                        ("$permissions", JsonSerializer.Serialize(permissions)),
                        ("$deadline", now + (long)TimeSpan.FromHours(1).TotalMilliseconds),
                        ("$intentId", Guid.CreateVersion7().ToString("D")),
                        ("$intentKey", $"automation-run:{runId:D}:dispatch"),
                        ("$dispatchKind", mode == AutomationWorkspaceMode.Project
                            ? "createThread"
                            : "createWorktree"),
                        ("$requestSha", requestSha));
                    return 0;
                },
                TestContext.Current.CancellationToken);
            return new SeededRun(runId, preparedTurnId, requestSha);
        }

        public Task<StoredRun> ReadRunAsync(Guid runId) =>
            Workspace.Store.ReadAsync(
                async (connection, cancellationToken) =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText =
                        """
                        SELECT status, thread_id, worktree_id,
                               project_writer_lease_id, base_commit_sha, error_code
                        FROM automation_runs
                        WHERE automation_run_id = $runId;
                        """;
                    Add(command, "$runId", runId.ToString("D"));
                    await using var reader =
                        await command.ExecuteReaderAsync(cancellationToken);
                    Assert.True(await reader.ReadAsync(cancellationToken));
                    return new StoredRun(
                        reader.GetString(0) switch
                        {
                            "pending" => AutomationRunStatus.Pending,
                            "running" => AutomationRunStatus.Running,
                            _ => AutomationRunStatus.Failed,
                        },
                        reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)),
                        reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                        reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)),
                        reader.IsDBNull(4) ? null : reader.GetString(4),
                        reader.IsDBNull(5) ? null : reader.GetString(5));
                },
                TestContext.Current.CancellationToken).AsTask();

        public Task<(string Status, long AttemptCount)> ReadActiveIntentAsync(Guid runId) =>
            ReadIntentAsync(
                """
                SELECT status, attempt_count
                FROM automation_dispatch_intents
                WHERE entity_id = $id AND status <> 'completed'
                ORDER BY created_utc DESC, intent_id DESC
                LIMIT 1;
                """,
                runId);

        public Task<(string Status, long AttemptCount)> ReadLatestIntentAsync(Guid runId) =>
            ReadIntentAsync(
                """
                SELECT status, attempt_count
                FROM automation_dispatch_intents
                WHERE entity_id = $id
                ORDER BY created_utc DESC, intent_id DESC
                LIMIT 1;
                """,
                runId);

        public Task<long> CountThreadsAsync() =>
            Workspace.Store.ReadAsync(
                async (connection, cancellationToken) =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT count(*) FROM threads;";
                    return Convert.ToInt64(
                        await command.ExecuteScalarAsync(cancellationToken));
                },
                TestContext.Current.CancellationToken).AsTask();

        public Task<long> CountCompletedIntentsAsync(Guid runId) =>
            ScalarAsync(
                """
                SELECT count(*)
                FROM automation_dispatch_intents
                WHERE entity_id = $id AND status = 'completed';
                """,
                runId);

        public Task<long> CountTurnsAsync(Guid threadId) =>
            ScalarAsync(
                "SELECT count(*) FROM turns WHERE thread_id = $id;",
                threadId);

        public async Task SeedRetentionIntentAsync(Guid runId)
        {
            var run = await ReadRunAsync(runId);
            for (var attempt = 0; attempt < 100; attempt++)
            {
                var thread = await Sessions.GetThreadAsync(
                    run.ThreadId!.Value,
                    TestContext.Current.CancellationToken);
                if (thread.Value!.ActiveTurnId is null)
                {
                    break;
                }

                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            await Workspace.Store.WriteAsync(
                async (connection, transaction, cancellationToken) =>
                {
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    await ExecuteAsync(
                        connection,
                        transaction,
                        """
                        UPDATE automation_runs
                        SET status = 'completed',
                            completed_utc = $now,
                            updated_utc = $now,
                            revision = revision + 1
                        WHERE automation_run_id = $runId;
                        INSERT INTO automation_dispatch_intents (
                            intent_id, idempotency_key, dispatch_kind,
                            entity_kind, entity_id, status, attempt_count,
                            lease_owner, lease_expires_utc, error_code, diagnostic,
                            created_utc, updated_utc)
                        VALUES (
                            $intentId, $key, 'archiveThread',
                            'automationRun', $runId, 'pending', 0,
                            NULL, NULL, NULL, NULL,
                            $now, $now);
                        """,
                        cancellationToken,
                        ("$runId", runId.ToString("D")),
                        ("$intentId", Guid.CreateVersion7().ToString("D")),
                        ("$key", $"automation-run:{runId:D}:archiveThread"),
                        ("$now", now));
                    return 0;
                },
                TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync() => await Workspace.DisposeAsync();

        private Task<long> ScalarAsync(string sql, Guid id) =>
            Workspace.Store.ReadAsync(
                async (connection, cancellationToken) =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = sql;
                    Add(command, "$id", id.ToString("D"));
                    return Convert.ToInt64(
                        await command.ExecuteScalarAsync(cancellationToken));
                },
                TestContext.Current.CancellationToken).AsTask();

        private Task<(string Status, long AttemptCount)> ReadIntentAsync(
            string sql,
            Guid runId) =>
            Workspace.Store.ReadAsync(
                async (connection, cancellationToken) =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = sql;
                    Add(command, "$id", runId.ToString("D"));
                    await using var reader =
                        await command.ExecuteReaderAsync(cancellationToken);
                    Assert.True(await reader.ReadAsync(cancellationToken));
                    return (reader.GetString(0), reader.GetInt64(1));
                },
                TestContext.Current.CancellationToken).AsTask();

        private sealed class CompletionExecutor : ISessionExecutor
        {
            public ValueTask ExecuteAsync(
                AgentSession context,
                ISessionExecutionSink sink,
                CancellationToken cancellationToken) =>
                sink.EmitAsync(new CompleteTurnIntent(), cancellationToken);
        }
    }

    private sealed class RecordingWorktrees(
        string worktreesRoot,
        string baseCommitSha,
        bool isDirty,
        bool escapeRoot = false,
        bool retainOnRemove = false) : IManagedWorktreeService
    {
        private readonly Dictionary<Guid, ManagedWorktreeDescriptor> _items = [];

        public List<ManagedWorktreeCreateRequest> Requests { get; } = [];

        public int RemoveCount { get; private set; }

        public ValueTask<ManagedWorktreeOriginSnapshot> InspectOriginAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                new ManagedWorktreeOriginSnapshot(baseCommitSha, isDirty));

        public ValueTask<ManagedWorktreeDescriptor> CreateAsync(
            Guid agentRunId,
            CancellationToken cancellationToken = default) =>
            CreateAsync(
                new ManagedWorktreeCreateRequest(agentRunId, baseCommitSha),
                cancellationToken);

        public ValueTask<ManagedWorktreeDescriptor> CreateAsync(
            ManagedWorktreeCreateRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var existing = _items.Values.SingleOrDefault(item =>
                string.Equals(
                    Path.GetFileName(item.WorktreeRoot),
                    request.AgentRunId.ToString("D"),
                    StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                return ValueTask.FromResult(existing);
            }

            var root = escapeRoot
                ? Path.Combine(
                    Path.GetTempPath(),
                    "escaped-automations",
                    request.AgentRunId.ToString("D"))
                : Path.Combine(worktreesRoot, request.AgentRunId.ToString("D"));
            var descriptor = new ManagedWorktreeDescriptor(
                Guid.CreateVersion7(),
                root,
                request.BaseCommitSha,
                CoWorkWorktreeStatus.Ready,
                IsDirty: false);
            _items[descriptor.WorktreeId] = descriptor;
            return ValueTask.FromResult(descriptor);
        }

        public ValueTask<ManagedWorktreeDescriptor?> GetAsync(
            Guid worktreeId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_items.GetValueOrDefault(worktreeId));

        public ValueTask<ManagedWorktreeDescriptor> RemoveAsync(
            Guid worktreeId,
            CancellationToken cancellationToken = default)
        {
            RemoveCount++;
            var result = _items[worktreeId] with
            {
                Status = retainOnRemove
                    ? CoWorkWorktreeStatus.RetainedDirty
                    : CoWorkWorktreeStatus.Removed,
                IsDirty = retainOnRemove,
            };
            _items[worktreeId] = result;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class DispatchTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan duration) => now += duration;
    }

    private sealed class InjectedCrash : Exception;

    private static async Task ExecuteAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            Add(command, parameter.Name, parameter.Value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}
