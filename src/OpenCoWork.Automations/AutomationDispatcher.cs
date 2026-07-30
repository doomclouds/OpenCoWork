using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenCoWork.Abstractions;

namespace OpenCoWork.Automations;

public enum AutomationDispatchFaultPoint
{
    BeforeWorktreeCreated,
    AfterWorktreeCreated,
    BeforeThreadCreated,
    AfterThreadCreated,
    BeforeTurnSubmitted,
    AfterTurnSubmitted,
}

public sealed class AutomationDispatcher(
    IWorkspaceStateStore store,
    IAutomationPreparedTurnStore preparedTurns,
    ISessionService sessions,
    IManagedWorktreeService worktrees,
    IProjectWriterLeaseService writerLeases,
    WorkspaceRuntimeDescriptor workspace,
    TimeProvider timeProvider,
    Action<AutomationDispatchFaultPoint>? faultInjector = null)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan IntentLeaseDuration = TimeSpan.FromMinutes(2);

    public async Task<bool> DispatchNextAsync(
        Guid automationRunId,
        string leaseOwner,
        CancellationToken cancellationToken = default)
    {
        if (automationRunId.Version != 7)
        {
            throw new ArgumentException(
                "Automation Run ID must be a UUIDv7.",
                nameof(automationRunId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        var intent = await ClaimAsync(automationRunId, leaseOwner, cancellationToken);
        if (intent is null)
        {
            return false;
        }

        try
        {
            switch (intent.Kind)
            {
                case "createWorktree":
                    await CreateWorktreeAsync(intent, cancellationToken);
                    break;
                case "createThread":
                    await CreateThreadAsync(intent, cancellationToken);
                    break;
                case "submitTurn":
                    await SubmitTurnAsync(intent, cancellationToken);
                    break;
                default:
                    await FailAsync(
                        intent,
                        AutomationErrorCodes.InvalidState,
                        "Automation dispatch kind is not executable.",
                        retryable: false,
                        cancellationToken);
                    break;
            }
        }
        catch (DispatchFailure exception)
        {
            await FailAsync(
                intent,
                exception.Code,
                exception.Message,
                exception.Retryable,
                cancellationToken);
        }

        return true;
    }

    private async Task CreateWorktreeAsync(
        ClaimedIntent intent,
        CancellationToken cancellationToken)
    {
        await RenewClaimAsync(intent, cancellationToken);
        var run = await ReadRunAsync(intent.RunId, cancellationToken);
        if (run.WorktreeId is not null)
        {
            await CompleteAsync(
                intent,
                nextKind: "createThread",
                runUpdateSql: string.Empty,
                [],
                cancellationToken);
            return;
        }

        ManagedWorktreeOriginSnapshot origin;
        try
        {
            origin = await worktrees.InspectOriginAsync(cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            throw Retryable(AutomationErrorCodes.Unavailable, exception.Message);
        }

        if (origin.IsDirty && !run.AllowDirtyOrigin)
        {
            throw Terminal(
                AutomationErrorCodes.WorktreeDirty,
                "Origin workspace is dirty.");
        }

        var baseCommitSha = run.BaseCommitSha ?? origin.BaseCommitSha;
        if (run.BaseCommitSha is null)
        {
            await PersistBaseCommitAsync(intent.RunId, baseCommitSha, cancellationToken);
        }

        ManagedWorktreeDescriptor created;
        try
        {
            faultInjector?.Invoke(AutomationDispatchFaultPoint.BeforeWorktreeCreated);
            created = await worktrees.CreateAsync(
                new ManagedWorktreeCreateRequest(
                    intent.RunId,
                    baseCommitSha,
                    run.AllowDirtyOrigin),
                cancellationToken);
        }
        catch (InvalidOperationException exception)
        {
            throw Retryable(AutomationErrorCodes.Unavailable, exception.Message);
        }

        ValidateWorktreePath(intent.RunId, created.WorktreeRoot);
        if (!string.Equals(
                created.BaseCommitSha,
                baseCommitSha,
                StringComparison.OrdinalIgnoreCase))
        {
            throw Terminal(
                AutomationErrorCodes.Conflict,
                "Managed Worktree Base SHA does not match the frozen Run.");
        }

        faultInjector?.Invoke(AutomationDispatchFaultPoint.AfterWorktreeCreated);
        await CompleteAsync(
            intent,
            nextKind: "createThread",
            """
            worktree_id = $worktreeId,
            base_commit_sha = $baseCommitSha,
            """,
            [
                ("$worktreeId", created.WorktreeId.ToString("D")),
                ("$baseCommitSha", baseCommitSha.ToLowerInvariant()),
            ],
            cancellationToken);
    }

    private async Task CreateThreadAsync(
        ClaimedIntent intent,
        CancellationToken cancellationToken)
    {
        await RenewClaimAsync(intent, cancellationToken);
        var run = await ReadRunAsync(intent.RunId, cancellationToken);
        if (run.ThreadId is not null)
        {
            await CompleteAsync(
                intent,
                nextKind: "submitTurn",
                runUpdateSql: string.Empty,
                [],
                cancellationToken);
            return;
        }

        if (run.WorkspaceMode == AutomationWorkspaceMode.Project &&
            run.WorkspaceAccess == CoWorkWorkspaceAccess.ReadWrite)
        {
            var lease = await EnsureWriterLeaseAsync(run, cancellationToken);
            if (lease is null)
            {
                await ReleaseClaimAsync(intent, cancellationToken);
                return;
            }

            run = run with
            {
                ProjectWriterLeaseId = lease.LeaseId,
                ProjectWriterLeaseExpiresAtUtc = lease.ExpiresAtUtc,
            };
        }

        var executionWorkspace = await ExecutionWorkspaceAsync(run, cancellationToken);
        faultInjector?.Invoke(AutomationDispatchFaultPoint.BeforeThreadCreated);
        var result = await sessions.CreateThreadAsync(
            new CreateThreadRequest(
                intent.IntentId,
                ExpectedSequence: 0,
                $"Automation: {run.DisplayName}",
                HistoryMode.Server,
                run.ProviderId,
                run.ModelId,
                AgentMode.Agent,
                executionWorkspace,
                CoWorkProvenance: null,
                new AutomationThreadProvenance(
                    run.RunId,
                    run.AutomationId,
                    run.Permissions,
                    run.Capabilities)),
            cancellationToken);
        if (result.Value is null)
        {
            throw SessionFailure(result.Error);
        }

        faultInjector?.Invoke(AutomationDispatchFaultPoint.AfterThreadCreated);
        await CompleteAsync(
            intent,
            nextKind: "submitTurn",
            """
            thread_id = $threadId,
            """,
            [("$threadId", result.Value.ThreadId.ToString("D"))],
            cancellationToken);
    }

    private async Task SubmitTurnAsync(
        ClaimedIntent intent,
        CancellationToken cancellationToken)
    {
        await RenewClaimAsync(intent, cancellationToken);
        var run = await ReadRunAsync(intent.RunId, cancellationToken);
        if (run.ThreadId is null)
        {
            throw Terminal(
                AutomationErrorCodes.InvalidState,
                "Automation Thread is missing.");
        }

        AutomationPreparedTurnSnapshot? prepared;
        try
        {
            prepared = await preparedTurns.ReadAsync(
                run.PreparedTurnId,
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or
                UnauthorizedAccessException)
        {
            throw Retryable(AutomationErrorCodes.Unavailable, exception.Message);
        }

        if (prepared is null ||
            prepared.PreparedTurnId != run.PreparedTurnId ||
            !string.Equals(
                prepared.RenderedPromptSha256,
                run.RenderedPromptSha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                Hash(prepared.RenderedPrompt),
                run.RenderedPromptSha256,
                StringComparison.Ordinal) ||
            run.RequestSha256 is null ||
            !string.Equals(
                prepared.RequestSha256,
                run.RequestSha256,
                StringComparison.Ordinal))
        {
            throw Retryable(
                AutomationErrorCodes.Unavailable,
                "Automation Prepared Turn is missing or invalid.");
        }

        var expectedSequence = await ExpectedSequenceAsync(
            intent,
            run.ThreadId.Value,
            cancellationToken);
        faultInjector?.Invoke(AutomationDispatchFaultPoint.BeforeTurnSubmitted);
        var result = await sessions.EnqueueInputAsync(
            new EnqueueInputRequest(
                run.ThreadId.Value,
                intent.IntentId,
                expectedSequence,
                prepared.RenderedPrompt,
                TurnAdmission.QueueIfBusy),
            cancellationToken);
        if (result.Value is null)
        {
            throw SessionFailure(result.Error);
        }

        faultInjector?.Invoke(AutomationDispatchFaultPoint.AfterTurnSubmitted);
        await CompleteTurnAsync(intent, cancellationToken);
        _ = await preparedTurns.DeleteAsync(
            run.PreparedTurnId,
            run.RequestSha256,
            CancellationToken.None);
    }

    private async Task<ClaimedIntent?> ClaimAsync(
        Guid runId,
        string leaseOwner,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        return await store.WriteAsync(
            async (connection, transaction, token) =>
            {
                await using var select = connection.CreateCommand();
                select.Transaction = transaction;
                select.CommandText =
                    """
                    SELECT intent_id, dispatch_kind, attempt_count
                    FROM automation_dispatch_intents
                    WHERE entity_kind = 'automationRun'
                      AND entity_id = $runId
                      AND attempt_count < 5
                      AND (
                          status = 'pending' OR
                          (status = 'leased' AND lease_expires_utc <= $now))
                    ORDER BY created_utc, intent_id
                    LIMIT 1;
                    """;
                Add(select, "$runId", runId.ToString("D"));
                Add(select, "$now", Milliseconds(now));
                await using var reader = await select.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token))
                {
                    return null;
                }

                var intentId = Guid.Parse(reader.GetString(0));
                var kind = reader.GetString(1);
                var attemptCount = reader.GetInt32(2) + 1;
                await reader.DisposeAsync();

                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText =
                    """
                    UPDATE automation_dispatch_intents
                    SET status = 'leased',
                        attempt_count = attempt_count + 1,
                        lease_owner = $owner,
                        lease_expires_utc = $expires,
                        error_code = NULL,
                        updated_utc = $now
                    WHERE intent_id = $intentId
                      AND attempt_count < 5
                      AND (
                          status = 'pending' OR
                          (status = 'leased' AND lease_expires_utc <= $now));
                    """;
                Add(update, "$owner", leaseOwner);
                Add(update, "$expires", Milliseconds(now + IntentLeaseDuration));
                Add(update, "$now", Milliseconds(now));
                Add(update, "$intentId", intentId.ToString("D"));
                return await update.ExecuteNonQueryAsync(token) == 1
                    ? new ClaimedIntent(
                        intentId,
                        runId,
                        kind,
                        attemptCount,
                        leaseOwner)
                    : null;
            },
            cancellationToken);
    }

    private async Task RenewClaimAsync(
        ClaimedIntent intent,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var renewed = await store.WriteAsync(
            async (connection, transaction, token) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    UPDATE automation_dispatch_intents
                    SET lease_expires_utc = $expires,
                        updated_utc = $now
                    WHERE intent_id = $intentId
                      AND status = 'leased'
                      AND lease_owner = $owner
                      AND lease_expires_utc > $now;
                    """;
                Add(command, "$expires", Milliseconds(now + IntentLeaseDuration));
                Add(command, "$now", Milliseconds(now));
                Add(command, "$intentId", intent.IntentId.ToString("D"));
                Add(command, "$owner", intent.LeaseOwner);
                return await command.ExecuteNonQueryAsync(token) == 1;
            },
            cancellationToken);
        if (!renewed)
        {
            throw Terminal(
                AutomationErrorCodes.LeaseLost,
                "Automation dispatch lease was lost.");
        }
    }

    private async Task<RunRow> ReadRunAsync(
        Guid runId,
        CancellationToken cancellationToken)
    {
        var run = await store.ReadAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT automation_id, definition_snapshot_json,
                           rendered_prompt_sha256, prepared_turn_id,
                           workspace_mode, workspace_access, provider_id, model_id,
                           permission_snapshot_json, capability_snapshot_json,
                           thread_id, worktree_id, base_commit_sha,
                           project_writer_lease_id,
                           project_writer_lease_expires_utc,
                           (
                               SELECT request_sha256
                               FROM automation_command_receipts
                               WHERE command_id = automation_runs.automation_run_id
                                 AND command_kind = 'startRun'
                           )
                    FROM automation_runs
                    WHERE automation_run_id = $runId
                      AND status IN ('pending', 'running');
                    """;
                Add(command, "$runId", runId.ToString("D"));
                await using var reader = await command.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token))
                {
                    return null;
                }

                using var definition = JsonDocument.Parse(reader.GetString(1));
                var root = definition.RootElement;
                var workspaceElement = root.GetProperty("workspace");
                return new RunRow(
                    runId,
                    reader.GetString(0),
                    root.TryGetProperty("displayName", out var displayName)
                        ? displayName.GetString() ?? reader.GetString(0)
                        : reader.GetString(0),
                    workspaceElement.TryGetProperty(
                        "allowDirtyOrigin",
                        out var allowDirty) &&
                    allowDirty.GetBoolean(),
                    reader.GetString(2),
                    Guid.Parse(reader.GetString(3)),
                    reader.GetString(4) == "project"
                        ? AutomationWorkspaceMode.Project
                        : AutomationWorkspaceMode.Worktree,
                    reader.GetString(5) == "readWrite"
                        ? CoWorkWorkspaceAccess.ReadWrite
                        : CoWorkWorkspaceAccess.ReadOnly,
                    reader.GetString(6),
                    reader.GetString(7),
                    JsonSerializer.Deserialize<AutomationPermissionSnapshot>(
                        reader.GetString(8),
                        JsonOptions)!,
                    JsonSerializer.Deserialize<AutomationCapabilitySnapshot[]>(
                        reader.GetString(9),
                        JsonOptions)!,
                    reader.IsDBNull(10) ? null : Guid.Parse(reader.GetString(10)),
                    reader.IsDBNull(11) ? null : Guid.Parse(reader.GetString(11)),
                    reader.IsDBNull(12) ? null : reader.GetString(12),
                    reader.IsDBNull(13) ? null : Guid.Parse(reader.GetString(13)),
                    reader.IsDBNull(14)
                        ? null
                        : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(14)),
                    reader.IsDBNull(15) ? null : reader.GetString(15));
            },
            cancellationToken);
        return run ?? throw Terminal(
            AutomationErrorCodes.InvalidState,
            "Automation Run is not dispatchable.");
    }

    private async Task<ProjectWriterLease?> EnsureWriterLeaseAsync(
        RunRow run,
        CancellationToken cancellationToken)
    {
        var owner = new ProjectWriterLeaseOwner(
            ProjectWriterLeaseOwnerKind.AutomationRun,
            run.RunId);
        var lease = run.ProjectWriterLeaseId is null
            ? await writerLeases.TryAcquireAsync(owner, cancellationToken)
            : await writerLeases.RenewAsync(
                owner,
                run.ProjectWriterLeaseId.Value,
                cancellationToken) ??
              await writerLeases.TryAcquireAsync(owner, cancellationToken);
        if (lease is null)
        {
            return null;
        }

        await store.WriteAsync(
            async (connection, transaction, token) =>
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    UPDATE automation_runs
                    SET project_writer_lease_id = $leaseId,
                        project_writer_lease_expires_utc = $expires,
                        revision = revision + 1,
                        updated_utc = $now
                    WHERE automation_run_id = $runId;
                    UPDATE automation_state
                    SET automation_revision = automation_revision + 1,
                        updated_utc = $now
                    WHERE id = 1;
                    """,
                    token,
                    ("$leaseId", lease.LeaseId.ToString("D")),
                    ("$expires", Milliseconds(lease.ExpiresAtUtc)),
                    ("$runId", run.RunId.ToString("D")),
                    ("$now", Milliseconds(timeProvider.GetUtcNow())));
                return 0;
            },
            cancellationToken);
        return lease;
    }

    private async Task<ExecutionWorkspaceDescriptor> ExecutionWorkspaceAsync(
        RunRow run,
        CancellationToken cancellationToken)
    {
        var scratchpad = Path.Combine(
            workspace.RuntimeRoot,
            "automations",
            "runs",
            run.RunId.ToString("D"),
            "scratchpad");
        if (run.WorkspaceMode == AutomationWorkspaceMode.Project)
        {
            return new ExecutionWorkspaceDescriptor(
                CoWorkWorkspaceMode.Project,
                workspace.WorkspaceRoot,
                scratchpad,
                null,
                null,
                null);
        }

        if (run.WorktreeId is null)
        {
            throw Terminal(
                AutomationErrorCodes.InvalidState,
                "Automation Worktree is missing.");
        }

        var worktree = await worktrees.GetAsync(
            run.WorktreeId.Value,
            cancellationToken) ??
            throw Retryable(
                AutomationErrorCodes.Unavailable,
                "Automation Worktree is unavailable.");
        ValidateWorktreePath(run.RunId, worktree.WorktreeRoot);
        return new ExecutionWorkspaceDescriptor(
            CoWorkWorkspaceMode.Worktree,
            worktree.WorktreeRoot,
            scratchpad,
            worktree.WorktreeId,
            worktree.WorktreeRoot,
            run.BaseCommitSha);
    }

    private async Task<long> ExpectedSequenceAsync(
        ClaimedIntent intent,
        Guid threadId,
        CancellationToken cancellationToken)
    {
        var diagnostic = await store.ReadAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT diagnostic
                    FROM automation_dispatch_intents
                    WHERE intent_id = $intentId;
                    """;
                Add(command, "$intentId", intent.IntentId.ToString("D"));
                var value = await command.ExecuteScalarAsync(token);
                return value is null or DBNull ? null : Convert.ToString(
                    value,
                    CultureInfo.InvariantCulture);
            },
            cancellationToken);
        const string prefix = "expectedSequence:";
        if (diagnostic?.StartsWith(prefix, StringComparison.Ordinal) == true &&
            long.TryParse(
                diagnostic.AsSpan(prefix.Length),
                CultureInfo.InvariantCulture,
                out var persisted))
        {
            return persisted;
        }

        var thread = await sessions.GetThreadAsync(threadId, cancellationToken);
        if (thread.Value is null)
        {
            throw SessionFailure(thread.Error);
        }

        var expected = thread.Value.CurrentSequence;
        await store.WriteAsync(
            async (connection, transaction, token) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    UPDATE automation_dispatch_intents
                    SET diagnostic = $diagnostic,
                        updated_utc = $now
                    WHERE intent_id = $intentId
                      AND status = 'leased'
                      AND lease_owner = $owner;
                    """;
                Add(command, "$diagnostic", $"{prefix}{expected}");
                Add(command, "$now", Milliseconds(timeProvider.GetUtcNow()));
                Add(command, "$intentId", intent.IntentId.ToString("D"));
                Add(command, "$owner", intent.LeaseOwner);
                await command.ExecuteNonQueryAsync(token);
                return 0;
            },
            cancellationToken);
        return expected;
    }

    private Task PersistBaseCommitAsync(
        Guid runId,
        string baseCommitSha,
        CancellationToken cancellationToken) =>
        store.WriteAsync(
            async (connection, transaction, token) =>
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    UPDATE automation_runs
                    SET base_commit_sha = $baseCommitSha,
                        revision = revision + 1,
                        updated_utc = $now
                    WHERE automation_run_id = $runId
                      AND base_commit_sha IS NULL;
                    UPDATE automation_state
                    SET automation_revision = automation_revision + 1,
                        updated_utc = $now
                    WHERE id = 1;
                    """,
                    token,
                    ("$baseCommitSha", baseCommitSha.ToLowerInvariant()),
                    ("$runId", runId.ToString("D")),
                    ("$now", Milliseconds(timeProvider.GetUtcNow())));
                return 0;
            },
            cancellationToken).AsTask();

    private Task CompleteAsync(
        ClaimedIntent intent,
        string nextKind,
        string runUpdateSql,
        IReadOnlyList<(string Name, object? Value)> parameters,
        CancellationToken cancellationToken) =>
        store.WriteAsync(
            async (connection, transaction, token) =>
            {
                var now = timeProvider.GetUtcNow();
                await using (var complete = connection.CreateCommand())
                {
                    complete.Transaction = transaction;
                    complete.CommandText =
                        """
                        UPDATE automation_dispatch_intents
                        SET status = 'completed',
                            lease_owner = NULL,
                            lease_expires_utc = NULL,
                            error_code = NULL,
                            updated_utc = $now
                        WHERE intent_id = $intentId
                          AND status = 'leased'
                          AND lease_owner = $owner;
                        """;
                    Add(complete, "$intentId", intent.IntentId.ToString("D"));
                    Add(complete, "$owner", intent.LeaseOwner);
                    Add(complete, "$now", Milliseconds(now));
                    if (await complete.ExecuteNonQueryAsync(token) != 1)
                    {
                        throw new InvalidOperationException(
                            "Automation dispatch lease was lost.");
                    }
                }

                await ExecuteAsync(
                    connection,
                    transaction,
                    $"""
                     UPDATE automation_runs
                     SET {runUpdateSql}
                         revision = revision + 1,
                         updated_utc = $now
                     WHERE automation_run_id = $runId;
                     INSERT INTO automation_dispatch_intents (
                         intent_id, idempotency_key, dispatch_kind,
                         entity_kind, entity_id, status, attempt_count,
                         lease_owner, lease_expires_utc, error_code, diagnostic,
                         created_utc, updated_utc)
                     VALUES (
                         $nextIntentId, $nextKey, $nextKind,
                         'automationRun', $runId, 'pending', 0,
                         NULL, NULL, NULL, NULL,
                         $now, $now)
                     ON CONFLICT(idempotency_key) DO NOTHING;
                     UPDATE automation_state
                     SET automation_revision = automation_revision + 1,
                         updated_utc = $now
                     WHERE id = 1;
                     """,
                    token,
                    parameters
                        .Concat(
                        [
                            ("$runId", intent.RunId.ToString("D")),
                            ("$now", Milliseconds(now)),
                            ("$nextIntentId", DerivedId(
                                intent.RunId,
                                nextKind == "createThread" ? (byte)0x31 : (byte)0x32)
                                .ToString("D")),
                            ("$nextKey", $"automation-run:{intent.RunId:D}:{nextKind}"),
                            ("$nextKind", nextKind),
                        ])
                        .ToArray());
                return 0;
            },
            cancellationToken).AsTask();

    private Task CompleteTurnAsync(
        ClaimedIntent intent,
        CancellationToken cancellationToken) =>
        store.WriteAsync(
            async (connection, transaction, token) =>
            {
                var now = timeProvider.GetUtcNow();
                await using (var complete = connection.CreateCommand())
                {
                    complete.Transaction = transaction;
                    complete.CommandText =
                        """
                        UPDATE automation_dispatch_intents
                        SET status = 'completed',
                            lease_owner = NULL,
                            lease_expires_utc = NULL,
                            error_code = NULL,
                            updated_utc = $now
                        WHERE intent_id = $intentId
                          AND status = 'leased'
                          AND lease_owner = $owner;
                        """;
                    Add(complete, "$intentId", intent.IntentId.ToString("D"));
                    Add(complete, "$owner", intent.LeaseOwner);
                    Add(complete, "$now", Milliseconds(now));
                    if (await complete.ExecuteNonQueryAsync(token) != 1)
                    {
                        throw new InvalidOperationException(
                            "Automation dispatch lease was lost.");
                    }
                }

                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    UPDATE automation_runs
                    SET status = 'running',
                        started_utc = COALESCE(started_utc, $now),
                        revision = revision + 1,
                        updated_utc = $now
                    WHERE automation_run_id = $runId;
                    UPDATE automation_state
                    SET automation_revision = automation_revision + 1,
                        updated_utc = $now
                    WHERE id = 1;
                    """,
                    token,
                    ("$runId", intent.RunId.ToString("D")),
                    ("$now", Milliseconds(now)));
                return 0;
            },
            cancellationToken).AsTask();

    private Task ReleaseClaimAsync(
        ClaimedIntent intent,
        CancellationToken cancellationToken) =>
        store.WriteAsync(
            async (connection, transaction, token) =>
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    UPDATE automation_dispatch_intents
                    SET status = 'pending',
                        attempt_count = attempt_count - 1,
                        lease_owner = NULL,
                        lease_expires_utc = NULL,
                        updated_utc = $now
                    WHERE intent_id = $intentId
                      AND status = 'leased'
                      AND lease_owner = $owner;
                    """,
                    token,
                    ("$intentId", intent.IntentId.ToString("D")),
                    ("$owner", intent.LeaseOwner),
                    ("$now", Milliseconds(timeProvider.GetUtcNow())));
                return 0;
            },
            cancellationToken).AsTask();

    private Task FailAsync(
        ClaimedIntent intent,
        string errorCode,
        string diagnostic,
        bool retryable,
        CancellationToken cancellationToken) =>
        store.WriteAsync(
            async (connection, transaction, token) =>
            {
                var exhausted = !retryable || intent.AttemptCount >= 5;
                var now = timeProvider.GetUtcNow();
                await ExecuteAsync(
                    connection,
                    transaction,
                    $"""
                     UPDATE automation_dispatch_intents
                     SET status = '{(exhausted ? "deadLettered" : "pending")}',
                         lease_owner = NULL,
                         lease_expires_utc = NULL,
                         error_code = $errorCode,
                         diagnostic = $diagnostic,
                         updated_utc = $now
                     WHERE intent_id = $intentId
                       AND status = 'leased'
                       AND lease_owner = $owner;
                     {(exhausted
                         ? """
                           UPDATE automation_runs
                           SET status = 'failed',
                               error_code = $terminalCode,
                               diagnostic = $diagnostic,
                               revision = revision + 1,
                               updated_utc = $now,
                               completed_utc = $now
                           WHERE automation_run_id = $runId;
                           UPDATE automation_state
                           SET automation_revision = automation_revision + 1,
                               updated_utc = $now
                           WHERE id = 1;
                           """
                         : string.Empty)}
                     """,
                    token,
                    ("$errorCode", errorCode),
                    ("$terminalCode", retryable
                        ? AutomationErrorCodes.RetryExhausted
                        : errorCode),
                    ("$diagnostic", diagnostic),
                    ("$intentId", intent.IntentId.ToString("D")),
                    ("$owner", intent.LeaseOwner),
                    ("$runId", intent.RunId.ToString("D")),
                    ("$now", Milliseconds(now)));
                return 0;
            },
            cancellationToken).AsTask();

    private void ValidateWorktreePath(Guid runId, string path)
    {
        var root = Path.GetFullPath(workspace.WorktreesRoot)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!candidate.StartsWith(root, comparison) ||
            !string.Equals(
                Path.GetFileName(candidate),
                runId.ToString("D"),
                StringComparison.OrdinalIgnoreCase))
        {
            throw Terminal(
                AutomationErrorCodes.PathEscape,
                "Managed Worktree path escapes the Automation root.");
        }
    }

    private static DispatchFailure SessionFailure(SessionError? error) =>
        new(
            error?.Code ?? AutomationErrorCodes.Unavailable,
            error?.Message ?? "Session operation failed.",
            error?.IsRetryable ?? true);

    private static DispatchFailure Retryable(string code, string message) =>
        new(code, message, retryable: true);

    private static DispatchFailure Terminal(string code, string message) =>
        new(code, message, retryable: false);

    private static Guid DerivedId(Guid source, byte marker)
    {
        Span<byte> bytes = stackalloc byte[16];
        source.TryWriteBytes(bytes);
        bytes[^1] ^= marker;
        return new Guid(bytes);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private static long Milliseconds(DateTimeOffset value) =>
        value.ToUnixTimeMilliseconds();

    private static async ValueTask ExecuteAsync(
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

    private sealed record ClaimedIntent(
        Guid IntentId,
        Guid RunId,
        string Kind,
        int AttemptCount,
        string LeaseOwner);

    private sealed record RunRow(
        Guid RunId,
        string AutomationId,
        string DisplayName,
        bool AllowDirtyOrigin,
        string RenderedPromptSha256,
        Guid PreparedTurnId,
        AutomationWorkspaceMode WorkspaceMode,
        CoWorkWorkspaceAccess WorkspaceAccess,
        string ProviderId,
        string ModelId,
        AutomationPermissionSnapshot Permissions,
        IReadOnlyList<AutomationCapabilitySnapshot> Capabilities,
        Guid? ThreadId,
        Guid? WorktreeId,
        string? BaseCommitSha,
        Guid? ProjectWriterLeaseId,
        DateTimeOffset? ProjectWriterLeaseExpiresAtUtc,
        string? RequestSha256);

    private sealed class DispatchFailure(
        string code,
        string message,
        bool retryable) : Exception(message)
    {
        public string Code { get; } = code;

        public bool Retryable { get; } = retryable;
    }
}
