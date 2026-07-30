using System.Data.Common;
using System.Text.Json;
using OpenCoWork.Abstractions;

namespace OpenCoWork.Teams;

public sealed partial class CoWorkService
{
    public Task<CoWorkResult<MissionSnapshot>> CreateMissionAsync(
        CreateMissionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!IsHost(request.Command.Actor))
        {
            return FailureAsync<MissionSnapshot>(
                CoWorkErrorCodes.PermissionDenied,
                "Only Host can create Missions.",
                cancellationToken);
        }

        if (ContainsSensitiveData(request.Objective))
        {
            return FailureAsync<MissionSnapshot>(
                CoWorkErrorCodes.SecretDetected,
                "Mission objective contains sensitive data.",
                cancellationToken);
        }

        if (request.TokenBudget <= 0)
        {
            return FailureAsync<MissionSnapshot>(
                CoWorkErrorCodes.BudgetExceeded,
                "Mission Token Budget must be positive.",
                cancellationToken);
        }

        return ExecuteCommandAsync(
            request,
            request.Command,
            "createMission",
            targetId: null,
            async (connection, transaction, token) =>
            {
                if (request.Command.ExpectedRevision is not null)
                {
                    throw Conflict("New Mission cannot have an expected revision.");
                }

                if (await ScalarAsync<long>(
                        connection,
                        transaction,
                        "SELECT count(*) FROM threads WHERE thread_id = $id;",
                        token,
                        ("$id", request.OriginThreadId)) == 0)
                {
                    throw NotFound("Origin Thread was not found.");
                }

                var team = await LoadTeamAsync(connection, request.TeamId, token)
                    ?? throw NotFound("Team was not found.");
                if (!team.Enabled)
                {
                    throw InvalidState("Team is disabled.");
                }

                var missionId = Guid.CreateVersion7(_timeProvider.GetUtcNow());
                var missionMembers = new List<MissionMemberSnapshot>(team.Members.Count);
                foreach (var teamMember in team.Members)
                {
                    var profile = await LoadProfileAsync(
                                      connection,
                                      teamMember.ProfileId,
                                      token)
                                  ?? throw NotFound("Team Agent Profile was not found.");
                    if (!profile.Enabled)
                    {
                        throw InvalidState(
                            $"Agent Profile '{profile.Name}' is disabled.");
                    }

                    missionMembers.Add(new MissionMemberSnapshot(
                        Guid.CreateVersion7(_timeProvider.GetUtcNow()),
                        teamMember.Alias,
                        teamMember.Role,
                        teamMember.Description,
                        profile,
                        teamMember.Order));
                }

                var objective = RequiredText(request.Objective, "Mission objective");
                var now = UtcNowMilliseconds();
                var originDeliveryId = $"mission:{missionId:N}:origin";
                await ExecuteSqlAsync(
                    connection,
                    transaction,
                    """
                    INSERT INTO missions (
                        mission_id, origin_thread_id, origin_delivery_id, team_id,
                        objective, status, workspace_mode, planning_team_revision,
                        team_snapshot_json, base_commit_sha, budget_limit_tokens,
                        revision, created_utc, updated_utc)
                    VALUES (
                        $id, $originThreadId, $originDeliveryId, $teamId,
                        $objective, 'planning', $workspaceMode, $teamRevision,
                        $teamSnapshotJson, NULL, $tokenBudget,
                        1, $now, $now);
                    """,
                    token,
                    ("$id", missionId),
                    ("$originThreadId", request.OriginThreadId),
                    ("$originDeliveryId", originDeliveryId),
                    ("$teamId", request.TeamId),
                    ("$objective", objective),
                    ("$workspaceMode", EnumText(request.WorkspaceMode)),
                    ("$teamRevision", team.Revision),
                    ("$teamSnapshotJson", JsonSerializer.Serialize(
                        missionMembers,
                        JsonOptions)),
                    ("$tokenBudget", request.TokenBudget),
                    ("$now", now));
                foreach (var member in missionMembers)
                {
                    await ExecuteSqlAsync(
                        connection,
                        transaction,
                        """
                        INSERT INTO mission_members (
                            mission_member_id, mission_id, agent_profile_id,
                            alias, normalized_alias, role, description, ordinal,
                            profile_snapshot_json)
                        VALUES (
                            $memberId, $missionId, $profileId,
                            $alias, $normalizedAlias, $role, $description, $ordinal,
                            $profileSnapshotJson);
                        """,
                        token,
                        ("$memberId", member.MemberId),
                        ("$missionId", missionId),
                        ("$profileId", member.Profile.ProfileId),
                        ("$alias", member.Alias),
                        ("$normalizedAlias", Normalize(member.Alias)),
                        ("$role", EnumText(member.Role)),
                        ("$description", member.Description),
                        ("$ordinal", member.Order),
                        ("$profileSnapshotJson", JsonSerializer.Serialize(
                            member.Profile,
                            JsonOptions)));
                }

                var intentId = Guid.CreateVersion7(_timeProvider.GetUtcNow());
                await ExecuteSqlAsync(
                    connection,
                    transaction,
                    """
                    INSERT INTO cowork_dispatch_intents (
                        intent_id, idempotency_key, command_id,
                        dispatch_kind, entity_kind, entity_id, status,
                        attempt_count, created_utc, updated_utc)
                    VALUES (
                        $intentId, $idempotencyKey, $commandId,
                        'createThread', 'mission', $missionId, 'pending',
                        0, $now, $now);
                    """,
                    token,
                    ("$intentId", intentId),
                    ("$idempotencyKey", $"mission:{missionId:N}:leader-planning"),
                    ("$commandId", request.Command.CommandId),
                    ("$missionId", missionId),
                    ("$now", now));
                return (await LoadMissionAsync(connection, missionId, token))!;
            },
            cancellationToken);
    }

    public Task<CoWorkResult<CoWorkPage<MissionSnapshot>>> ListMissionsAsync(
        ListMissionsRequest request,
        CancellationToken cancellationToken = default) =>
        IsHost(request.Actor)
            ? ReadMissionPageAsync(request, cancellationToken)
            : FailureAsync<CoWorkPage<MissionSnapshot>>(
                CoWorkErrorCodes.PermissionDenied,
                "Only Host can list Missions.",
                cancellationToken);

    public async Task<CoWorkResult<MissionSnapshot>> GetMissionAsync(
        GetMissionRequest request,
        CancellationToken cancellationToken = default)
    {
        return await ReadAsync(
            request.Actor,
            async (connection, token) =>
            {
                var mission = await LoadMissionAsync(
                    connection,
                    request.MissionId,
                    token);
                return mission is not null && CanViewMission(mission, request.Actor)
                    ? mission
                    : null;
            },
            cancellationToken);
    }

    public Task<CoWorkResult<MissionSnapshot>> ActivateMissionAsync(
        MissionCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!CanManageMission(request.Command.Actor, request.MissionId))
        {
            return FailureAsync<MissionSnapshot>(
                CoWorkErrorCodes.PermissionDenied,
                "Actor cannot activate this Mission.",
                cancellationToken);
        }

        return ExecuteCommandAsync(
            request,
            request.Command,
            "activateMission",
            request.MissionId.ToString(),
            async (connection, transaction, token) =>
            {
                var mission = await LoadMissionAsync(
                                  connection,
                                  request.MissionId,
                                  token)
                              ?? throw NotFound("Mission was not found.");
                RequireMissionManager(mission, request.Command.Actor);
                RequireRevision(request.Command.ExpectedRevision, mission.Revision);
                if (!CoWorkStateMachine.CanTransition(
                        mission.Status,
                        CoWorkMissionStatus.Active))
                {
                    throw InvalidState("Mission is not in Planning state.");
                }

                if (mission.Tasks.Count == 0)
                {
                    throw InvalidState("Mission requires at least one Task.");
                }

                ValidateDag(mission.Tasks);
                var team = await LoadTeamAsync(connection, mission.TeamId, token)
                    ?? throw NotFound("Mission Team was not found.");
                if (!team.Enabled || team.Revision != mission.PlanningTeamRevision)
                {
                    throw Conflict("Mission Team changed after planning began.");
                }

                foreach (var member in mission.Members)
                {
                    var current = await LoadProfileAsync(
                                      connection,
                                      member.Profile.ProfileId,
                                      token)
                                  ?? throw NotFound(
                                      "Mission Agent Profile was not found.");
                    if (!current.Enabled ||
                        current.Revision != member.Profile.Revision)
                    {
                        throw Conflict(
                            $"Agent Profile '{member.Profile.Name}' changed during planning.");
                    }
                }

                var now = UtcNowMilliseconds();
                await ExecuteSqlAsync(
                    connection,
                    transaction,
                    """
                    UPDATE mission_tasks
                    SET status = CASE
                            WHEN json_array_length(dependency_ids_json) = 0
                                THEN 'ready'
                            ELSE 'waitingDependencies'
                        END,
                        revision = revision + 1,
                        updated_utc = $now
                    WHERE mission_id = $missionId
                      AND status = 'pending';
                    UPDATE missions
                    SET status = 'active',
                        revision = revision + 1,
                        updated_utc = $now
                    WHERE mission_id = $missionId;
                    """,
                    token,
                    ("$missionId", request.MissionId),
                    ("$now", now));
                await ExecuteSqlAsync(
                    connection,
                    transaction,
                    """
                    INSERT INTO cowork_budget_scopes (
                        scope_id, owner_kind, owner_id, limit_tokens,
                        reserved_tokens, used_tokens, revision)
                    VALUES (
                        $scopeId, 'mission', $missionId, $limit,
                        0, 0, 0);
                    """,
                    token,
                    ("$scopeId", Guid.CreateVersion7(_timeProvider.GetUtcNow())),
                    ("$missionId", request.MissionId),
                    ("$limit", mission.TokenBudget));
                return (await LoadMissionAsync(
                    connection,
                    request.MissionId,
                    token))!;
            },
            cancellationToken);
    }

    public Task<CoWorkResult<MissionSnapshot>> CancelMissionAsync(
        MissionCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!IsHost(request.Command.Actor))
        {
            return FailureAsync<MissionSnapshot>(
                CoWorkErrorCodes.PermissionDenied,
                "Only Host can cancel Missions.",
                cancellationToken);
        }

        return ExecuteCommandAsync(
            request,
            request.Command,
            "cancelMission",
            request.MissionId.ToString(),
            async (connection, transaction, token) =>
            {
                var mission = await LoadMissionAsync(
                                  connection,
                                  request.MissionId,
                                  token)
                              ?? throw NotFound("Mission was not found.");
                RequireRevision(request.Command.ExpectedRevision, mission.Revision);
                if (!CoWorkStateMachine.CanTransition(
                        mission.Status,
                        CoWorkMissionStatus.Cancelled))
                {
                    throw InvalidState("Mission cannot be cancelled from its current state.");
                }

                var now = UtcNowMilliseconds();
                await ExecuteSqlAsync(
                    connection,
                    transaction,
                    """
                    UPDATE missions
                    SET status = 'cancelled',
                        revision = revision + 1,
                        updated_utc = $now,
                        completed_utc = $now
                    WHERE mission_id = $id;
                    UPDATE mission_tasks
                    SET status = 'cancelled',
                        revision = revision + 1,
                        updated_utc = $now
                    WHERE mission_id = $id
                      AND status NOT IN ('completed', 'failed', 'cancelled');
                    """,
                    token,
                    ("$id", request.MissionId),
                    ("$now", now));
                return (await LoadMissionAsync(
                    connection,
                    request.MissionId,
                    token))!;
            },
            cancellationToken);
    }

    public Task<CoWorkResult<MissionTaskSnapshot>> AddMissionTaskAsync(
        AddMissionTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!CanManageMission(request.Command.Actor, request.MissionId))
        {
            return FailureAsync<MissionTaskSnapshot>(
                CoWorkErrorCodes.PermissionDenied,
                "Actor cannot add Mission Tasks.",
                cancellationToken);
        }

        if (ContainsSensitiveData(
                request.Alias,
                request.Objective,
                request.Instructions))
        {
            return FailureAsync<MissionTaskSnapshot>(
                CoWorkErrorCodes.SecretDetected,
                "Mission Task contains sensitive data.",
                cancellationToken);
        }

        return ExecuteCommandAsync(
            request,
            request.Command,
            "addMissionTask",
            request.MissionId.ToString(),
            async (connection, transaction, token) =>
            {
                var mission = await LoadMissionAsync(
                                  connection,
                                  request.MissionId,
                                  token)
                              ?? throw NotFound("Mission was not found.");
                RequireMissionManager(mission, request.Command.Actor);
                RequireRevision(request.Command.ExpectedRevision, mission.Revision);
                RequirePlanning(mission);
                if (mission.Tasks.Count >= _config.MaximumTasksPerMission)
                {
                    throw InvalidState(
                        $"Mission Task limit {_config.MaximumTasksPerMission} was reached.");
                }

                RequireMissionMember(mission, request.AssignedMemberId);
                var alias = RequiredText(request.Alias, "Task alias");
                if (mission.Tasks.Any(task =>
                        Normalize(task.Alias) == Normalize(alias)))
                {
                    throw Conflict("Mission Task alias already exists.");
                }

                var dependsOn = ResolveDependencies(mission.Tasks, request.DependsOn);
                var now = UtcNowMilliseconds();
                var task = new MissionTaskSnapshot(
                    Guid.CreateVersion7(_timeProvider.GetUtcNow()),
                    mission.MissionId,
                    alias,
                    RequiredText(request.Objective, "Task objective"),
                    request.Instructions,
                    request.AssignedMemberId,
                    request.Required,
                    request.RequiresReview ?? request.Required,
                    dependsOn,
                    CoWorkTaskStatus.Pending,
                    null,
                    0,
                    null,
                    [],
                    1,
                    FromUnixMilliseconds(now),
                    FromUnixMilliseconds(now));
                ValidateDag([.. mission.Tasks, task]);
                await InsertTaskAsync(connection, transaction, task, token);
                await TouchMissionAsync(
                    connection,
                    transaction,
                    mission.MissionId,
                    now,
                    token);
                return (await LoadTaskAsync(connection, task.TaskId, token))!;
            },
            cancellationToken);
    }

    public Task<CoWorkResult<MissionTaskSnapshot>> UpdateMissionTaskAsync(
        UpdateMissionTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!CanManageMission(request.Command.Actor, request.MissionId))
        {
            return FailureAsync<MissionTaskSnapshot>(
                CoWorkErrorCodes.PermissionDenied,
                "Actor cannot update Mission Tasks.",
                cancellationToken);
        }

        if (ContainsSensitiveData(request.Objective, request.Instructions))
        {
            return FailureAsync<MissionTaskSnapshot>(
                CoWorkErrorCodes.SecretDetected,
                "Mission Task contains sensitive data.",
                cancellationToken);
        }

        return ExecuteCommandAsync(
            request,
            request.Command,
            "updateMissionTask",
            request.TaskId.ToString(),
            async (connection, transaction, token) =>
            {
                var mission = await RequireMissionForTaskMutationAsync(
                    connection,
                    request.MissionId,
                    request.Command.ExpectedRevision,
                    token);
                RequireMissionManager(mission, request.Command.Actor);
                RequirePlanning(mission);
                RequireMissionMember(mission, request.AssignedMemberId);
                var existing = mission.Tasks.SingleOrDefault(task =>
                                   task.TaskId == request.TaskId)
                               ?? throw NotFound("Mission Task was not found.");
                var dependsOn = ResolveDependencies(
                    mission.Tasks.Where(task => task.TaskId != request.TaskId).ToArray(),
                    request.DependsOn);
                var updated = existing with
                {
                    Objective = RequiredText(request.Objective, "Task objective"),
                    Instructions = request.Instructions,
                    AssignedMemberId = request.AssignedMemberId,
                    Required = request.Required,
                    RequiresReview = request.RequiresReview ?? request.Required,
                    DependsOn = dependsOn,
                    Revision = existing.Revision + 1,
                    UpdatedAt = _timeProvider.GetUtcNow(),
                };
                ValidateDag(
                    mission.Tasks.Select(task =>
                        task.TaskId == request.TaskId ? updated : task).ToArray());
                var now = UtcNowMilliseconds();
                await ExecuteSqlAsync(
                    connection,
                    transaction,
                    """
                    UPDATE mission_tasks
                    SET objective = $objective,
                        instructions = $instructions,
                        assigned_member_id = $memberId,
                        is_required = $required,
                        review_required = $reviewRequired,
                        dependency_ids_json = $dependsOn,
                        revision = revision + 1,
                        updated_utc = $now
                    WHERE mission_task_id = $taskId;
                    """,
                    token,
                    ("$taskId", request.TaskId),
                    ("$objective", updated.Objective),
                    ("$instructions", updated.Instructions),
                    ("$memberId", updated.AssignedMemberId),
                    ("$required", updated.Required ? 1 : 0),
                    ("$reviewRequired", updated.RequiresReview ? 1 : 0),
                    ("$dependsOn", JsonSerializer.Serialize(
                        updated.DependsOn,
                        JsonOptions)),
                    ("$now", now));
                await TouchMissionAsync(
                    connection,
                    transaction,
                    mission.MissionId,
                    now,
                    token);
                return (await LoadTaskAsync(connection, request.TaskId, token))!;
            },
            cancellationToken);
    }

    public Task<CoWorkResult<MissionTaskSnapshot>> RemoveMissionTaskAsync(
        MissionTaskCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!CanManageMission(request.Command.Actor, request.MissionId))
        {
            return FailureAsync<MissionTaskSnapshot>(
                CoWorkErrorCodes.PermissionDenied,
                "Actor cannot remove Mission Tasks.",
                cancellationToken);
        }

        return ExecuteCommandAsync(
            request,
            request.Command,
            "removeMissionTask",
            request.TaskId.ToString(),
            async (connection, transaction, token) =>
            {
                var mission = await RequireMissionForTaskMutationAsync(
                    connection,
                    request.MissionId,
                    request.Command.ExpectedRevision,
                    token);
                RequireMissionManager(mission, request.Command.Actor);
                RequirePlanning(mission);
                var task = mission.Tasks.SingleOrDefault(item =>
                               item.TaskId == request.TaskId)
                           ?? throw NotFound("Mission Task was not found.");
                if (mission.Tasks.Any(item =>
                        item.DependsOn.Any(alias =>
                            Normalize(alias) == Normalize(task.Alias))))
                {
                    throw InvalidState(
                        "Mission Task cannot be removed while another Task depends on it.");
                }

                var now = UtcNowMilliseconds();
                await ExecuteSqlAsync(
                    connection,
                    transaction,
                    "DELETE FROM mission_tasks WHERE mission_task_id = $id;",
                    token,
                    ("$id", request.TaskId));
                await TouchMissionAsync(
                    connection,
                    transaction,
                    mission.MissionId,
                    now,
                    token);
                return task;
            },
            cancellationToken);
    }

    public Task<CoWorkResult<MissionTaskSnapshot>> BlockMissionTaskAsync(
        BlockMissionTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        if (ContainsSensitiveData(request.Reason))
        {
            return FailureAsync<MissionTaskSnapshot>(
                CoWorkErrorCodes.SecretDetected,
                "Block reason contains sensitive data.",
                cancellationToken);
        }

        return TransitionTaskAsync(
            request,
            request.Command,
            request.MissionId,
            request.TaskId,
            "blockMissionTask",
            [CoWorkTaskStatus.Ready, CoWorkTaskStatus.Running],
            CoWorkTaskStatus.Blocked,
            request.Reason,
            memberAllowed: true,
            cancellationToken);
    }

    public Task<CoWorkResult<MissionTaskSnapshot>> UnblockMissionTaskAsync(
        MissionTaskCommandRequest request,
        CancellationToken cancellationToken = default) =>
        TransitionTaskAsync(
            request,
            request.Command,
            request.MissionId,
            request.TaskId,
            "unblockMissionTask",
            [CoWorkTaskStatus.Blocked],
            CoWorkTaskStatus.Ready,
            blockedReason: null,
            memberAllowed: true,
            cancellationToken);

    public Task<CoWorkResult<MissionTaskSnapshot>> RetryMissionTaskAsync(
        MissionTaskCommandRequest request,
        CancellationToken cancellationToken = default) =>
        TransitionTaskAsync(
            request,
            request.Command,
            request.MissionId,
            request.TaskId,
            "retryMissionTask",
            [CoWorkTaskStatus.Failed],
            CoWorkTaskStatus.Ready,
            blockedReason: null,
            memberAllowed: false,
            cancellationToken);

    public Task<CoWorkResult<MissionTaskSnapshot>> ReassignMissionTaskAsync(
        ReassignMissionTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!CanManageMission(request.Command.Actor, request.MissionId))
        {
            return FailureAsync<MissionTaskSnapshot>(
                CoWorkErrorCodes.PermissionDenied,
                "Actor cannot reassign Mission Tasks.",
                cancellationToken);
        }

        return ExecuteCommandAsync(
            request,
            request.Command,
            "reassignMissionTask",
            request.TaskId.ToString(),
            async (connection, transaction, token) =>
            {
                var mission = await RequireMissionForTaskMutationAsync(
                    connection,
                    request.MissionId,
                    request.Command.ExpectedRevision,
                    token);
                RequireMissionManager(mission, request.Command.Actor);
                RequireMissionMember(mission, request.MemberId);
                var task = mission.Tasks.SingleOrDefault(item =>
                               item.TaskId == request.TaskId)
                           ?? throw NotFound("Mission Task was not found.");
                if (task.Status == CoWorkTaskStatus.Running)
                {
                    throw InvalidState("Running Mission Task cannot be reassigned.");
                }

                var now = UtcNowMilliseconds();
                await ExecuteSqlAsync(
                    connection,
                    transaction,
                    """
                    UPDATE mission_tasks
                    SET assigned_member_id = $memberId,
                        revision = revision + 1,
                        updated_utc = $now
                    WHERE mission_task_id = $taskId;
                    """,
                    token,
                    ("$memberId", request.MemberId),
                    ("$taskId", request.TaskId),
                    ("$now", now));
                await TouchMissionAsync(
                    connection,
                    transaction,
                    mission.MissionId,
                    now,
                    token);
                return (await LoadTaskAsync(connection, request.TaskId, token))!;
            },
            cancellationToken);
    }

    public Task<CoWorkResult<MissionTaskSnapshot>> WaiveMissionTaskAsync(
        MissionTaskCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!CanManageMission(request.Command.Actor, request.MissionId))
        {
            return FailureAsync<MissionTaskSnapshot>(
                CoWorkErrorCodes.PermissionDenied,
                "Actor cannot waive Mission Tasks.",
                cancellationToken);
        }

        return ExecuteCommandAsync(
            request,
            request.Command,
            "waiveMissionTask",
            request.TaskId.ToString(),
            async (connection, transaction, token) =>
            {
                var mission = await RequireMissionForTaskMutationAsync(
                    connection,
                    request.MissionId,
                    request.Command.ExpectedRevision,
                    token);
                RequireMissionManager(mission, request.Command.Actor);
                var task = mission.Tasks.SingleOrDefault(item =>
                               item.TaskId == request.TaskId)
                           ?? throw NotFound("Mission Task was not found.");
                if (task.Required || task.Status != CoWorkTaskStatus.Failed)
                {
                    throw InvalidState("Only failed Optional Tasks can be waived.");
                }

                var now = UtcNowMilliseconds();
                await ExecuteSqlAsync(
                    connection,
                    transaction,
                    """
                    UPDATE mission_tasks
                    SET waived = 1,
                        status = 'completed',
                        revision = revision + 1,
                        updated_utc = $now,
                        completed_utc = $now
                    WHERE mission_task_id = $taskId;
                    """,
                    token,
                    ("$taskId", request.TaskId),
                    ("$now", now));
                await TouchMissionAsync(
                    connection,
                    transaction,
                    mission.MissionId,
                    now,
                    token);
                return (await LoadTaskAsync(connection, request.TaskId, token))!;
            },
            cancellationToken);
    }

    public Task<CoWorkResult<MissionTaskSnapshot>> ReviewMissionTaskAsync(
        ReviewMissionTaskRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Comment is not null && ContainsSensitiveData(request.Comment))
        {
            return FailureAsync<MissionTaskSnapshot>(
                CoWorkErrorCodes.SecretDetected,
                "Review comment contains sensitive data.",
                cancellationToken);
        }

        return TransitionTaskAsync(
            request,
            request.Command,
            request.MissionId,
            request.TaskId,
            "reviewMissionTask",
            [CoWorkTaskStatus.Review],
            request.Accepted
                ? CoWorkTaskStatus.Completed
                : CoWorkTaskStatus.Ready,
            request.Accepted ? null : request.Comment,
            memberAllowed: false,
            cancellationToken);
    }

    private async Task<CoWorkResult<CoWorkPage<MissionSnapshot>>> ReadMissionPageAsync(
        ListMissionsRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Actor.PrincipalId) ||
            request.PageSize is < 1 or > 1000 ||
            !TryReadOffset(request.Cursor, out var offset))
        {
            return await FailureAsync<CoWorkPage<MissionSnapshot>>(
                CoWorkErrorCodes.InvalidState,
                "Actor, page size, or cursor is invalid.",
                cancellationToken);
        }

        var page = await _store.ReadAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"""
                     SELECT mission_id
                     FROM missions
                     {(request.Status is null ? string.Empty : "WHERE status = $status")}
                     ORDER BY created_utc, mission_id
                     LIMIT $limit OFFSET $offset;
                     """;
                if (request.Status is not null)
                {
                    AddParameter(command, "$status", EnumText(request.Status.Value));
                }

                AddParameter(command, "$limit", request.PageSize + 1);
                AddParameter(command, "$offset", offset);
                await using var reader = await command.ExecuteReaderAsync(token);
                var ids = new List<Guid>();
                while (await reader.ReadAsync(token))
                {
                    ids.Add(Guid.Parse(reader.GetString(0)));
                }

                await reader.DisposeAsync();
                var items = new List<MissionSnapshot>();
                foreach (var id in ids.Take(request.PageSize))
                {
                    items.Add((await LoadMissionAsync(connection, id, token))!);
                }

                return new CoWorkPage<MissionSnapshot>(
                    items,
                    ids.Count > request.PageSize
                        ? (offset + request.PageSize).ToString(
                            System.Globalization.CultureInfo.InvariantCulture)
                        : null);
            },
            cancellationToken);
        return Success(page, await ReadGlobalRevisionAsync(cancellationToken));
    }

    private Task<CoWorkResult<MissionTaskSnapshot>> TransitionTaskAsync(
        object request,
        CoWorkCommandContext command,
        Guid missionId,
        Guid taskId,
        string commandKind,
        IReadOnlyCollection<CoWorkTaskStatus> allowedFrom,
        CoWorkTaskStatus to,
        string? blockedReason,
        bool memberAllowed,
        CancellationToken cancellationToken)
    {
        if (!CanManageMission(command.Actor, missionId) &&
            !(memberAllowed && IsMissionMember(command.Actor, missionId)))
        {
            return FailureAsync<MissionTaskSnapshot>(
                CoWorkErrorCodes.PermissionDenied,
                "Actor cannot change this Mission Task.",
                cancellationToken);
        }

        return ExecuteCommandAsync(
            request,
            command,
            commandKind,
            taskId.ToString(),
            async (connection, transaction, token) =>
            {
                var mission = await RequireMissionForTaskMutationAsync(
                    connection,
                    missionId,
                    command.ExpectedRevision,
                    token);
                var task = mission.Tasks.SingleOrDefault(item => item.TaskId == taskId)
                           ?? throw NotFound("Mission Task was not found.");
                RequireTaskActor(mission, task, command.Actor, memberAllowed);
                if (!allowedFrom.Contains(task.Status) ||
                    !CoWorkStateMachine.CanTransition(task.Status, to))
                {
                    throw InvalidState(
                        $"Mission Task cannot transition from {task.Status} to {to}.");
                }

                var now = UtcNowMilliseconds();
                await ExecuteSqlAsync(
                    connection,
                    transaction,
                    """
                    UPDATE mission_tasks
                    SET status = $status,
                        blocker = $blockedReason,
                        revision = revision + 1,
                        updated_utc = $now,
                        completed_utc = CASE
                            WHEN $status = 'completed' THEN $now
                            ELSE completed_utc
                        END
                    WHERE mission_task_id = $taskId;
                    """,
                    token,
                    ("$status", EnumText(to)),
                    ("$blockedReason", blockedReason),
                    ("$now", now),
                    ("$taskId", taskId));
                await TouchMissionAsync(
                    connection,
                    transaction,
                    mission.MissionId,
                    now,
                    token);
                return (await LoadTaskAsync(connection, taskId, token))!;
            },
            cancellationToken);
    }

    private async ValueTask<MissionSnapshot> RequireMissionForTaskMutationAsync(
        DbConnection connection,
        Guid missionId,
        long? expectedRevision,
        CancellationToken cancellationToken)
    {
        var mission = await LoadMissionAsync(connection, missionId, cancellationToken)
            ?? throw NotFound("Mission was not found.");
        RequireRevision(expectedRevision, mission.Revision);
        return mission;
    }

    private static void RequirePlanning(MissionSnapshot mission)
    {
        if (mission.Status != CoWorkMissionStatus.Planning)
        {
            throw InvalidState(
                "Mission Task dependency edges are immutable after activation.");
        }
    }

    private static void RequireMissionMember(
        MissionSnapshot mission,
        Guid memberId)
    {
        if (mission.Members.All(member => member.MemberId != memberId))
        {
            throw NotFound("Mission Member was not found.");
        }
    }

    private static string[] ResolveDependencies(
        IReadOnlyList<MissionTaskSnapshot> tasks,
        IReadOnlyList<string> requested)
    {
        var byAlias = tasks.ToDictionary(
            task => Normalize(task.Alias),
            task => task.Alias,
            StringComparer.Ordinal);
        var result = new List<string>(requested.Count);
        foreach (var dependency in requested)
        {
            if (!byAlias.TryGetValue(Normalize(dependency), out var alias))
            {
                throw new CoWorkDomainException(
                    CoWorkErrorCodes.InvalidDag,
                    $"Mission Task dependency '{dependency}' was not found.");
            }

            if (!result.Contains(alias, StringComparer.Ordinal))
            {
                result.Add(alias);
            }
        }

        return result.ToArray();
    }

    private static void ValidateDag(IReadOnlyList<MissionTaskSnapshot> tasks)
    {
        var byAlias = tasks.ToDictionary(
            task => Normalize(task.Alias),
            StringComparer.Ordinal);
        var states = new Dictionary<string, int>(StringComparer.Ordinal);

        void Visit(string alias)
        {
            states.TryGetValue(alias, out var state);
            if (state == 1)
            {
                throw new CoWorkDomainException(
                    CoWorkErrorCodes.InvalidDag,
                    "Mission Task graph contains a cycle.");
            }

            if (state == 2)
            {
                return;
            }

            states[alias] = 1;
            foreach (var dependency in byAlias[alias].DependsOn)
            {
                var normalized = Normalize(dependency);
                if (!byAlias.ContainsKey(normalized))
                {
                    throw new CoWorkDomainException(
                        CoWorkErrorCodes.InvalidDag,
                        $"Mission Task dependency '{dependency}' was not found.");
                }

                Visit(normalized);
            }

            states[alias] = 2;
        }

        foreach (var alias in byAlias.Keys.Order(StringComparer.Ordinal))
        {
            Visit(alias);
        }
    }

    private static async ValueTask InsertTaskAsync(
        DbConnection connection,
        DbTransaction transaction,
        MissionTaskSnapshot task,
        CancellationToken cancellationToken) =>
        _ = await ExecuteSqlAsync(
            connection,
            transaction,
            """
            INSERT INTO mission_tasks (
                mission_task_id, mission_id, assigned_member_id,
                alias, normalized_alias, objective, instructions,
                is_required, review_required, waived,
                dependency_ids_json, status, attempt_count,
                revision, created_utc, updated_utc)
            VALUES (
                $taskId, $missionId, $memberId,
                $alias, $normalizedAlias, $objective, $instructions,
                $required, $reviewRequired, 0,
                $dependsOn, $status, 0,
                1, $now, $now);
            """,
            cancellationToken,
            ("$taskId", task.TaskId),
            ("$missionId", task.MissionId),
            ("$memberId", task.AssignedMemberId),
            ("$alias", task.Alias),
            ("$normalizedAlias", Normalize(task.Alias)),
            ("$objective", task.Objective),
            ("$instructions", task.Instructions),
            ("$required", task.Required ? 1 : 0),
            ("$reviewRequired", task.RequiresReview ? 1 : 0),
            ("$dependsOn", JsonSerializer.Serialize(task.DependsOn, JsonOptions)),
            ("$status", EnumText(task.Status)),
            ("$now", task.CreatedAt.ToUnixTimeMilliseconds()));

    private static async ValueTask TouchMissionAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid missionId,
        long now,
        CancellationToken cancellationToken) =>
        _ = await ExecuteSqlAsync(
            connection,
            transaction,
            """
            UPDATE missions
            SET revision = revision + 1,
                updated_utc = $now
            WHERE mission_id = $id;
            """,
            cancellationToken,
            ("$id", missionId),
            ("$now", now));

    private static async ValueTask<MissionSnapshot?> LoadMissionAsync(
        DbConnection connection,
        Guid id,
        CancellationToken cancellationToken)
    {
        Guid originThreadId;
        Guid teamId;
        long planningTeamRevision;
        string objective;
        CoWorkMissionStatus status;
        CoWorkWorkspaceMode workspaceMode;
        string? baseCommitSha;
        long tokenBudget;
        Guid? leaderThreadId;
        string? finalSummary;
        string? originDeliveryId;
        long revision;
        long createdUtc;
        long updatedUtc;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT origin_thread_id, team_id, planning_team_revision,
                       objective, status, workspace_mode, base_commit_sha,
                       budget_limit_tokens, leader_thread_id, final_summary,
                       origin_delivery_id, revision, created_utc, updated_utc
                FROM missions
                WHERE mission_id = $id;
                """;
            AddParameter(command, "$id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            originThreadId = Guid.Parse(reader.GetString(0));
            teamId = Guid.Parse(reader.GetString(1));
            planningTeamRevision = reader.GetInt64(2);
            objective = reader.GetString(3);
            status = ParseEnum<CoWorkMissionStatus>(reader.GetString(4));
            workspaceMode = ParseEnum<CoWorkWorkspaceMode>(reader.GetString(5));
            baseCommitSha = reader.IsDBNull(6) ? null : reader.GetString(6);
            tokenBudget = reader.GetInt64(7);
            leaderThreadId = reader.IsDBNull(8)
                ? null
                : Guid.Parse(reader.GetString(8));
            finalSummary = reader.IsDBNull(9) ? null : reader.GetString(9);
            originDeliveryId = reader.IsDBNull(10) ? null : reader.GetString(10);
            revision = reader.GetInt64(11);
            createdUtc = reader.GetInt64(12);
            updatedUtc = reader.GetInt64(13);
        }

        var members = new List<MissionMemberSnapshot>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT mission_member_id, alias, role, description,
                       profile_snapshot_json, ordinal
                FROM mission_members
                WHERE mission_id = $id
                ORDER BY ordinal, mission_member_id;
                """;
            AddParameter(command, "$id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var profile = JsonSerializer.Deserialize<AgentProfileSnapshot>(
                                  reader.GetString(4),
                                  JsonOptions)
                              ?? throw InvalidState(
                                  "Mission Profile snapshot is invalid.");
                members.Add(new MissionMemberSnapshot(
                    Guid.Parse(reader.GetString(0)),
                    reader.GetString(1),
                    ParseEnum<CoWorkMemberRole>(reader.GetString(2)),
                    reader.GetString(3),
                    profile,
                    reader.GetInt32(5)));
            }
        }

        var taskIds = await ReadMissionTaskIdsAsync(
            connection,
            id,
            cancellationToken);
        var tasks = new List<MissionTaskSnapshot>(taskIds.Length);
        foreach (var taskId in taskIds)
        {
            tasks.Add((await LoadTaskAsync(connection, taskId, cancellationToken))!);
        }

        return new MissionSnapshot(
            id,
            originThreadId,
            teamId,
            planningTeamRevision,
            objective,
            status,
            workspaceMode,
            baseCommitSha,
            tokenBudget,
            leaderThreadId,
            members,
            tasks,
            finalSummary,
            originDeliveryId,
            revision,
            FromUnixMilliseconds(createdUtc),
            FromUnixMilliseconds(updatedUtc));
    }

    private static async ValueTask<Guid[]> ReadMissionTaskIdsAsync(
        DbConnection connection,
        Guid missionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT mission_task_id
            FROM mission_tasks
            WHERE mission_id = $id
            ORDER BY created_utc, mission_task_id;
            """;
        AddParameter(command, "$id", missionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ids = new List<Guid>();
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(Guid.Parse(reader.GetString(0)));
        }

        return ids.ToArray();
    }

    private static async ValueTask<MissionTaskSnapshot?> LoadTaskAsync(
        DbConnection connection,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT mission_id, alias, objective, instructions,
                   assigned_member_id, is_required, review_required,
                   dependency_ids_json, status, blocker, attempt_count,
                   output_summary, artifact_ids_json, revision,
                   created_utc, updated_utc
            FROM mission_tasks
            WHERE mission_task_id = $id;
            """;
        AddParameter(command, "$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new MissionTaskSnapshot(
            id,
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            Guid.Parse(reader.GetString(4)),
            reader.GetInt64(5) != 0,
            reader.GetInt64(6) != 0,
            JsonSerializer.Deserialize<string[]>(
                reader.GetString(7),
                JsonOptions) ?? [],
            ParseEnum<CoWorkTaskStatus>(reader.GetString(8)),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.GetInt32(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.IsDBNull(12)
                ? []
                : JsonSerializer.Deserialize<Guid[]>(
                    reader.GetString(12),
                    JsonOptions) ?? [],
            reader.GetInt64(13),
            FromUnixMilliseconds(reader.GetInt64(14)),
            FromUnixMilliseconds(reader.GetInt64(15)));
    }

    private bool CanManageMission(CoWorkActorContext actor, Guid missionId) =>
        IsHost(actor) ||
        actor.Kind == CoWorkActorKind.Leader &&
        actor.MissionId == missionId &&
        actor.MemberId is not null &&
        !string.IsNullOrWhiteSpace(actor.PrincipalId);

    private static bool IsMissionMember(
        CoWorkActorContext actor,
        Guid missionId) =>
        actor.Kind == CoWorkActorKind.Member &&
        actor.MissionId == missionId &&
        actor.MemberId is not null &&
        !string.IsNullOrWhiteSpace(actor.PrincipalId);

    private static bool CanViewMission(
        MissionSnapshot mission,
        CoWorkActorContext actor)
    {
        if (IsHost(actor))
        {
            return true;
        }

        var member = mission.Members.SingleOrDefault(candidate =>
            candidate.MemberId == actor.MemberId);
        return actor.MissionId == mission.MissionId &&
               member is not null &&
               (actor.Kind, member.Role) is
                   (CoWorkActorKind.Leader, CoWorkMemberRole.Leader) or
                   (CoWorkActorKind.Member, CoWorkMemberRole.Member);
    }

    private static void RequireMissionManager(
        MissionSnapshot mission,
        CoWorkActorContext actor)
    {
        if (IsHost(actor))
        {
            return;
        }

        var member = mission.Members.SingleOrDefault(candidate =>
            candidate.MemberId == actor.MemberId);
        if (actor.Kind != CoWorkActorKind.Leader ||
            actor.MissionId != mission.MissionId ||
            member?.Role != CoWorkMemberRole.Leader)
        {
            throw PermissionDenied("Actor cannot manage this Mission.");
        }
    }

    private static void RequireTaskActor(
        MissionSnapshot mission,
        MissionTaskSnapshot task,
        CoWorkActorContext actor,
        bool memberAllowed)
    {
        if (IsHost(actor))
        {
            return;
        }

        var member = mission.Members.SingleOrDefault(candidate =>
            candidate.MemberId == actor.MemberId);
        var isLeader = actor.Kind == CoWorkActorKind.Leader &&
                       member?.Role == CoWorkMemberRole.Leader;
        var isAssignedMember = memberAllowed &&
                               actor.Kind == CoWorkActorKind.Member &&
                               member?.Role == CoWorkMemberRole.Member &&
                               task.AssignedMemberId == member.MemberId;
        if (actor.MissionId == mission.MissionId &&
            (isLeader || isAssignedMember))
        {
            return;
        }

        throw PermissionDenied("Actor cannot change this Mission Task.");
    }
}
