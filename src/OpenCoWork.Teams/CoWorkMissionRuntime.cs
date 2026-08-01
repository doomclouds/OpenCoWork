using System.Data.Common;
using System.Text.Json;
using OpenCoWork.Abstractions;

namespace OpenCoWork.Teams;

public sealed partial class CoWorkService
{
    private async Task<DispatchIntentSnapshot?> MaterializeMissionIntentAsync(
        DispatchIntentSnapshot intent,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(intent.EntityKind, "mission", StringComparison.Ordinal))
        {
            return intent;
        }

        return await _store.WriteAsync(
            async (connection, transaction, token) =>
            {
                var mission = await LoadMissionAsync(connection, intent.EntityId, token)
                              ?? throw NotFound("Mission dispatch target was not found.");
                var existing = await ReadOptionalGuidAsync(
                    connection,
                    """
                    SELECT agent_run_id
                    FROM agent_runs
                    WHERE mission_id = $missionId
                      AND run_kind = 'leaderPlanning'
                    ORDER BY attempt, agent_run_id
                    LIMIT 1;
                    """,
                    token,
                    ("$missionId", mission.MissionId));
                if (existing is not null)
                {
                    await BindIntentToRunAsync(
                        connection,
                        transaction,
                        intent.DispatchIntentId,
                        existing.Value,
                        token);
                    return await LoadDispatchIntentAsync(
                        connection,
                        intent.DispatchIntentId,
                        token);
                }

                if (mission.Status is CoWorkMissionStatus.Completed or
                    CoWorkMissionStatus.Failed or
                    CoWorkMissionStatus.Cancelled)
                {
                    await CompleteIntentInTransactionAsync(
                        connection,
                        transaction,
                        intent.DispatchIntentId,
                        token);
                    return null;
                }

                if (!await HasRunCapacityAsync(
                        connection,
                        transaction,
                        mission.MissionId,
                        token))
                {
                    await ReleaseIntentInTransactionAsync(
                        connection,
                        transaction,
                        intent.DispatchIntentId,
                        token);
                    return null;
                }

                var leader = mission.Members.Single(member =>
                    member.Role == CoWorkMemberRole.Leader);
                var budget = await ReadMissionBudgetAsync(
                                 connection,
                                 transaction,
                                 mission.MissionId,
                                 token)
                             ?? throw InvalidState("Mission Budget Scope is missing.");
                var reservation = EstimateReservation(mission.Objective);
                if (!await TryReserveBudgetAsync(
                        connection,
                        transaction,
                        budget.ScopeId,
                        reservation,
                        token))
                {
                    await DeadLetterIntentInTransactionAsync(
                        connection,
                        transaction,
                        intent.DispatchIntentId,
                        CoWorkErrorCodes.BudgetExceeded,
                        "Mission Budget is exhausted.",
                        token);
                    return null;
                }

                var runId = Guid.CreateVersion7(_timeProvider.GetUtcNow());
                var workspace = new ExecutionWorkspaceDescriptor(
                    CoWorkWorkspaceMode.Project,
                    _workspace!.WorkspaceRoot,
                    Path.Combine(
                        _workspace.MissionsRoot,
                        mission.MissionId.ToString("D"),
                        runId.ToString("D"),
                        "scratchpad"),
                    WorktreeId: null,
                    WorktreeRoot: null,
                    BaseCommitSha: mission.BaseCommitSha);
                await InsertMissionAgentRunAsync(
                    connection,
                    transaction,
                    runId,
                    mission,
                    task: null,
                    leader,
                    CoWorkAgentRunKind.LeaderPlanning,
                    attempt: 1,
                    workspace,
                    CoWorkWorkspaceAccess.ReadOnly,
                    reservation,
                    token);
                await BindIntentToRunAsync(
                    connection,
                    transaction,
                    intent.DispatchIntentId,
                    runId,
                    token);
                return await LoadDispatchIntentAsync(
                    connection,
                    intent.DispatchIntentId,
                    token);
            },
            cancellationToken);
    }

    private async Task<int> PrepareMissionRunsAsync(
        CancellationToken cancellationToken) =>
        await _store.WriteAsync(
            async (connection, transaction, token) =>
            {
                var missionIds = await ReadGuidsAsync(
                    connection,
                    """
                    SELECT mission_id
                    FROM missions
                    WHERE status = 'active'
                    ORDER BY created_utc, mission_id;
                    """,
                    token);
                var prepared = 0;
                foreach (var missionId in missionIds)
                {
                    var mission = await LoadMissionAsync(connection, missionId, token);
                    if (mission is null)
                    {
                        continue;
                    }

                    if (mission.LeaderThreadId is null)
                    {
                        continue;
                    }

                    foreach (var task in mission.Tasks.Where(task =>
                                 task.Status is CoWorkTaskStatus.Pending or
                                     CoWorkTaskStatus.WaitingDependencies or
                                     CoWorkTaskStatus.Ready))
                    {
                        var ready = task.DependsOn.All(alias =>
                            mission.Tasks.Single(candidate =>
                                    string.Equals(
                                        candidate.Alias,
                                        alias,
                                        StringComparison.OrdinalIgnoreCase))
                                .Status == CoWorkTaskStatus.Completed);
                        var desired = ready
                            ? CoWorkTaskStatus.Ready
                            : CoWorkTaskStatus.WaitingDependencies;
                        if (task.Status != desired)
                        {
                            await ExecuteSqlAsync(
                                connection,
                                transaction,
                                """
                                UPDATE mission_tasks
                                SET status = $status,
                                    revision = revision + 1,
                                    updated_utc = $now
                                WHERE mission_task_id = $taskId;
                                """,
                                token,
                                ("$status", EnumText(desired)),
                                ("$now", UtcNowMilliseconds()),
                                ("$taskId", task.TaskId));
                        }
                    }

                    mission = await LoadMissionAsync(connection, missionId, token);
                    if (mission is null)
                    {
                        continue;
                    }
                    if (CanAwaitLeaderReview(mission))
                    {
                        await ExecuteSqlAsync(
                            connection,
                            transaction,
                            """
                            UPDATE missions
                            SET status = 'awaitingLeaderReview',
                                revision = revision + 1,
                                updated_utc = $now
                            WHERE mission_id = $missionId
                              AND status = 'active';
                            """,
                            token,
                            ("$now", UtcNowMilliseconds()),
                            ("$missionId", missionId));
                        continue;
                    }

                    foreach (var task in mission.Tasks
                                 .Where(task => task.Status == CoWorkTaskStatus.Ready)
                                 .OrderBy(task => task.CreatedAt)
                                 .ThenBy(task => task.TaskId))
                    {
                        if (!await HasRunCapacityAsync(
                                connection,
                                transaction,
                                missionId,
                                token))
                        {
                            break;
                        }

                        if (await ScalarAsync<long>(
                                connection,
                                transaction,
                                """
                                SELECT count(*)
                                FROM agent_runs
                                WHERE mission_id = $missionId
                                  AND member_id = $memberId
                                  AND status IN ('pending', 'starting', 'running');
                                """,
                                token,
                                ("$missionId", missionId),
                                ("$memberId", task.AssignedMemberId)) != 0)
                        {
                            continue;
                        }

                        var member = mission.Members.Single(candidate =>
                            candidate.MemberId == task.AssignedMemberId);
                        var access = InferWorkspaceAccess(member.Profile);
                        if (mission.WorkspaceMode == CoWorkWorkspaceMode.Project &&
                            access == CoWorkWorkspaceAccess.ReadWrite &&
                            await ScalarAsync<long>(
                                connection,
                                transaction,
                                """
                                SELECT count(*)
                                FROM agent_runs
                                WHERE workspace_mode = 'project'
                                  AND workspace_access = 'readWrite'
                                  AND status IN ('pending', 'starting', 'running');
                                """,
                                token) != 0)
                        {
                            continue;
                        }

                        var budget = await ReadMissionBudgetAsync(
                                         connection,
                                         transaction,
                                         missionId,
                                         token)
                                     ?? throw InvalidState(
                                         "Mission Budget Scope is missing.");
                        var reservation = EstimateReservation(
                            task.Objective + "\n" + task.Instructions);
                        if (!await TryReserveBudgetAsync(
                                connection,
                                transaction,
                                budget.ScopeId,
                                reservation,
                                token))
                        {
                            break;
                        }

                        var runId = Guid.CreateVersion7(_timeProvider.GetUtcNow());
                        var workspace = new ExecutionWorkspaceDescriptor(
                            mission.WorkspaceMode,
                            _workspace!.WorkspaceRoot,
                            Path.Combine(
                                _workspace.MissionsRoot,
                                missionId.ToString("D"),
                                runId.ToString("D"),
                                "scratchpad"),
                            WorktreeId: null,
                            WorktreeRoot: null,
                            BaseCommitSha: mission.BaseCommitSha);
                        var attempt = task.CurrentAttempt + 1;
                        await InsertMissionAgentRunAsync(
                            connection,
                            transaction,
                            runId,
                            mission,
                            task,
                            member,
                            CoWorkAgentRunKind.MissionTask,
                            attempt,
                            workspace,
                            access,
                            reservation,
                            token);
                        var now = UtcNowMilliseconds();
                        await ExecuteSqlAsync(
                            connection,
                            transaction,
                            """
                            UPDATE mission_tasks
                            SET status = 'running',
                                attempt_count = $attempt,
                                blocker = NULL,
                                error_code = NULL,
                                revision = revision + 1,
                                updated_utc = $now
                            WHERE mission_task_id = $taskId
                              AND status = 'ready';
                            """,
                            token,
                            ("$attempt", attempt),
                            ("$now", now),
                            ("$taskId", task.TaskId));
                        await InsertDispatchIntentAsync(
                            connection,
                            transaction,
                            CoWorkDispatchKind.CreateThread,
                            "agentRun",
                            runId,
                            commandId: null,
                            now,
                            token);
                        prepared++;
                    }
                }

                var awaitingMissionIds = await ReadGuidsAsync(
                    connection,
                    """
                    SELECT mission_id
                    FROM missions
                    WHERE status = 'awaitingLeaderReview'
                    ORDER BY created_utc, mission_id;
                    """,
                    token);
                foreach (var missionId in awaitingMissionIds)
                {
                    var mission = await LoadMissionAsync(connection, missionId, token);
                    if (mission?.LeaderThreadId is null)
                    {
                        continue;
                    }

                    var leader = mission.Members.Single(member =>
                        member.Role == CoWorkMemberRole.Leader);
                    if (await ScalarAsync<long>(
                            connection,
                            transaction,
                            """
                            SELECT count(*) FROM agent_runs
                            WHERE mission_id = $missionId
                              AND member_id = $memberId
                              AND status IN ('pending', 'starting', 'running');
                            """,
                            token,
                            ("$missionId", missionId),
                            ("$memberId", leader.MemberId)) != 0 ||
                        await ScalarAsync<long>(
                            connection,
                            transaction,
                            """
                            SELECT count(*) FROM agent_runs
                            WHERE mission_id = $missionId
                              AND run_kind = 'leaderSynthesis';
                            """,
                            token,
                            ("$missionId", missionId)) != 0 ||
                        !await HasRunCapacityAsync(
                            connection,
                            transaction,
                            missionId,
                            token))
                    {
                        continue;
                    }

                    var budget = await ReadMissionBudgetAsync(
                                     connection,
                                     transaction,
                                     missionId,
                                     token)
                                 ?? throw InvalidState(
                                     "Mission Budget Scope is missing.");
                    var reservation = EstimateReservation(mission.Objective);
                    if (!await TryReserveBudgetAsync(
                            connection,
                            transaction,
                            budget.ScopeId,
                            reservation,
                            token))
                    {
                        continue;
                    }

                    var runId = Guid.CreateVersion7(_timeProvider.GetUtcNow());
                    var workspace = new ExecutionWorkspaceDescriptor(
                        CoWorkWorkspaceMode.Project,
                        _workspace!.WorkspaceRoot,
                        Path.Combine(
                            _workspace.MissionsRoot,
                            missionId.ToString("D"),
                            runId.ToString("D"),
                            "scratchpad"),
                        WorktreeId: null,
                        WorktreeRoot: null,
                        BaseCommitSha: mission.BaseCommitSha);
                    await InsertMissionAgentRunAsync(
                        connection,
                        transaction,
                        runId,
                        mission,
                        task: null,
                        leader,
                        CoWorkAgentRunKind.LeaderSynthesis,
                        attempt: 1,
                        workspace,
                        CoWorkWorkspaceAccess.ReadOnly,
                        reservation,
                        token);
                    var now = UtcNowMilliseconds();
                    await ExecuteSqlAsync(
                        connection,
                        transaction,
                        """
                        UPDATE agent_runs
                        SET thread_id = $threadId,
                            status = 'starting',
                            updated_utc = $now
                        WHERE agent_run_id = $runId;
                        """,
                        token,
                        ("$threadId", mission.LeaderThreadId.Value),
                        ("$now", now),
                        ("$runId", runId));
                    await InsertDispatchIntentAsync(
                        connection,
                        transaction,
                        CoWorkDispatchKind.SynthesizeMission,
                        "agentRun",
                        runId,
                        commandId: null,
                        now,
                        token);
                    prepared++;
                }

                if (prepared != 0)
                {
                    WakeReconciler();
                }

                return prepared;
            },
            cancellationToken);

    private async Task<string> BuildMissionRunInputAsync(
        AgentRunSnapshot run,
        CancellationToken cancellationToken) =>
        await _store.ReadAsync(
            async (connection, token) =>
            {
                var mission = await LoadMissionAsync(
                                  connection,
                                  run.MissionId!.Value,
                                  token)
                              ?? throw NotFound("Mission was not found.");
                if (run.Kind == CoWorkAgentRunKind.LeaderPlanning)
                {
                    var members = string.Join(
                        "\n",
                        mission.Members
                            .OrderBy(member => member.Order)
                            .Select(member =>
                                $"- {member.Alias}: {member.Role}; {member.Description}"));
                    return
                        $"Plan Mission {mission.MissionId:D}.\n" +
                        $"Objective: {mission.Objective}\nMembers:\n{members}\n" +
                        "Use only CoWork orchestration tools to create the initial DAG.";
                }

                if (run.Kind == CoWorkAgentRunKind.LeaderSynthesis)
                {
                    var members = string.Join(
                        "\n",
                        mission.Members
                            .OrderBy(member => member.Order)
                            .ThenBy(member => member.MemberId)
                            .Select(member =>
                                $"- {member.Alias}: {member.Role}; {member.Description}"));
                    var tasks = string.Join(
                        "\n",
                        mission.Tasks
                            .OrderBy(task => task.CreatedAt)
                            .ThenBy(task => task.TaskId)
                            .Select(task =>
                                $"- {task.Alias}: required={task.Required}; " +
                                $"status={task.Status}; summary={task.OutputSummary ?? task.BlockedReason ?? "(none)"}"));
                    var artifacts = await ReadMissionArtifactMetadataAsync(
                        connection,
                        mission.MissionId,
                        token);
                    return
                        $"Synthesize Mission {mission.MissionId:D}.\n" +
                        $"Objective: {mission.Objective}\n" +
                        $"Frozen responsibilities:\n{members}\n" +
                        $"Task outcomes:\n{tasks}\n" +
                        $"Artifact metadata:\n{artifacts}\n" +
                        "Return the final Mission summary and provenance. " +
                        "Do not reconstruct full member Thread history.";
                }

                var task = mission.Tasks.Single(candidate =>
                    candidate.TaskId == run.TaskId);
                return
                    $"Mission: {mission.Objective}\n" +
                    $"Task {task.Alias}: {task.Objective}\n" +
                    $"Instructions: {task.Instructions}";
            },
            cancellationToken);

    private static async ValueTask<string> ReadMissionArtifactMetadataAsync(
        DbConnection connection,
        Guid missionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT display_name, media_type, status, sha256, size_bytes
            FROM cowork_files
            WHERE mission_id = $missionId AND kind = 'artifact'
            ORDER BY created_utc, cowork_file_id;
            """;
        AddParameter(command, "$missionId", missionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var artifacts = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            artifacts.Add(
                $"- {reader.GetString(0)}: {reader.GetString(1)}; " +
                $"status={reader.GetString(2)}; sha256={reader.GetString(3)}; " +
                $"bytes={reader.GetInt64(4)}");
        }

        return artifacts.Count == 0 ? "(none)" : string.Join("\n", artifacts);
    }

    private static bool CanAwaitLeaderReview(MissionSnapshot mission) =>
        mission.Tasks.Count != 0 &&
        mission.Tasks.Where(task => task.Required)
            .All(task => task.Status == CoWorkTaskStatus.Completed) &&
        mission.Tasks.All(task =>
            task.Status is CoWorkTaskStatus.Completed or
                CoWorkTaskStatus.Failed or
                CoWorkTaskStatus.Cancelled) &&
        mission.Tasks.All(task =>
            task.Status is not CoWorkTaskStatus.Review and
                not CoWorkTaskStatus.Blocked);

    private async ValueTask<bool> HasRunCapacityAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid missionId,
        CancellationToken cancellationToken)
    {
        var global = await ScalarAsync<long>(
            connection,
            transaction,
            """
            SELECT count(*)
            FROM agent_runs
            WHERE status IN ('pending', 'starting', 'running');
            """,
            cancellationToken);
        var mission = await ScalarAsync<long>(
            connection,
            transaction,
            """
            SELECT count(*)
            FROM agent_runs
            WHERE mission_id = $missionId
              AND status IN ('pending', 'starting', 'running');
            """,
            cancellationToken,
            ("$missionId", missionId));
        return global < _config.MaxConcurrentAgentRuns &&
               mission < _config.MaxConcurrentAgentRunsPerMission;
    }

    private static async ValueTask<bool> TryReserveBudgetAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid scopeId,
        long reservation,
        CancellationToken cancellationToken) =>
        await ExecuteSqlAsync(
            connection,
            transaction,
            """
            UPDATE cowork_budget_scopes
            SET reserved_tokens = reserved_tokens + $reservation,
                revision = revision + 1
            WHERE scope_id = $scopeId
              AND limit_tokens - reserved_tokens - used_tokens >= $reservation;
            """,
            cancellationToken,
            ("$reservation", reservation),
            ("$scopeId", scopeId)) == 1;

    private static async ValueTask<MissionBudget?> ReadMissionBudgetAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid missionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT scope_id
            FROM cowork_budget_scopes
            WHERE owner_kind = 'mission'
              AND owner_id = $missionId;
            """;
        AddParameter(command, "$missionId", missionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new MissionBudget(Guid.Parse(reader.GetString(0)))
            : null;
    }

    private async ValueTask InsertMissionAgentRunAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid runId,
        MissionSnapshot mission,
        MissionTaskSnapshot? task,
        MissionMemberSnapshot member,
        CoWorkAgentRunKind kind,
        int attempt,
        ExecutionWorkspaceDescriptor workspace,
        CoWorkWorkspaceAccess access,
        long reservation,
        CancellationToken cancellationToken) =>
        _ = await ExecuteSqlAsync(
            connection,
            transaction,
            """
            INSERT INTO agent_runs (
                agent_run_id, mission_id, mission_task_id, member_id,
                thread_id, parent_agent_run_id, parent_thread_id,
                run_kind, status, profile_snapshot_json,
                workspace_mode, workspace_access, workspace_json,
                budget_limit_tokens, budget_reserved_tokens, budget_used_tokens,
                correlation_id,
                attempt, lease_owner, lease_expires_utc,
                error_code, diagnostic, created_utc, updated_utc, completed_utc)
            VALUES (
                $runId, $missionId, $taskId, $memberId,
                NULL, NULL, $parentThreadId,
                $kind, 'pending', $profile,
                $workspaceMode, $workspaceAccess, $workspace,
                $budgetLimit, $reservation, 0,
                (SELECT correlation_id FROM turns
                 WHERE thread_id = $parentThreadId AND correlation_id IS NOT NULL
                 ORDER BY created_utc DESC, turn_id DESC LIMIT 1),
                $attempt, NULL, NULL,
                NULL, NULL, $now, $now, NULL);
            """,
            cancellationToken,
            ("$runId", runId),
            ("$missionId", mission.MissionId),
            ("$taskId", task?.TaskId),
            ("$memberId", member.MemberId),
            ("$parentThreadId", mission.LeaderThreadId ?? mission.OriginThreadId),
            ("$kind", EnumText(kind)),
            ("$profile", JsonSerializer.Serialize(member.Profile, JsonOptions)),
            ("$workspaceMode", EnumText(workspace.Mode)),
            ("$workspaceAccess", EnumText(access)),
            ("$workspace", JsonSerializer.Serialize(workspace, JsonOptions)),
            ("$budgetLimit", mission.TokenBudget),
            ("$reservation", reservation),
            ("$attempt", attempt),
            ("$now", UtcNowMilliseconds()));

    private async ValueTask<Guid[]> ReadGuidsAsync(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await ReadGuidsAsync(command, cancellationToken);
    }

    private async ValueTask BindIntentToRunAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid intentId,
        Guid runId,
        CancellationToken cancellationToken) =>
        _ = await ExecuteSqlAsync(
            connection,
            transaction,
            """
            UPDATE cowork_dispatch_intents
            SET entity_kind = 'agentRun',
                entity_id = $runId,
                updated_utc = $now
            WHERE intent_id = $intentId;
            """,
            cancellationToken,
            ("$runId", runId),
            ("$now", UtcNowMilliseconds()),
            ("$intentId", intentId));

    private async ValueTask CompleteIntentInTransactionAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid intentId,
        CancellationToken cancellationToken) =>
        _ = await ExecuteSqlAsync(
            connection,
            transaction,
            """
            UPDATE cowork_dispatch_intents
            SET status = 'completed',
                lease_owner = NULL,
                lease_expires_utc = NULL,
                updated_utc = $now
            WHERE intent_id = $intentId;
            """,
            cancellationToken,
            ("$now", UtcNowMilliseconds()),
            ("$intentId", intentId));

    private async ValueTask ReleaseIntentInTransactionAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid intentId,
        CancellationToken cancellationToken) =>
        _ = await ExecuteSqlAsync(
            connection,
            transaction,
            """
            UPDATE cowork_dispatch_intents
            SET status = 'pending',
                attempt_count = CASE
                    WHEN attempt_count > 0 THEN attempt_count - 1
                    ELSE 0
                END,
                lease_owner = NULL,
                lease_expires_utc = NULL,
                updated_utc = $now
            WHERE intent_id = $intentId;
            """,
            cancellationToken,
            ("$now", UtcNowMilliseconds()),
            ("$intentId", intentId));

    private async ValueTask DeadLetterIntentInTransactionAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid intentId,
        string errorCode,
        string diagnostic,
        CancellationToken cancellationToken) =>
        _ = await ExecuteSqlAsync(
            connection,
            transaction,
            """
            UPDATE cowork_dispatch_intents
            SET status = 'deadLettered',
                lease_owner = NULL,
                lease_expires_utc = NULL,
                error_code = $errorCode,
                diagnostic = $diagnostic,
                updated_utc = $now
            WHERE intent_id = $intentId;
            """,
            cancellationToken,
            ("$errorCode", errorCode),
            ("$diagnostic", diagnostic),
            ("$now", UtcNowMilliseconds()),
            ("$intentId", intentId));

    private sealed record MissionBudget(Guid ScopeId);
}
