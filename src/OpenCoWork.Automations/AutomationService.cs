using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenCoWork.Abstractions;

namespace OpenCoWork.Automations;

internal sealed class AutomationService(
    IWorkspaceStateStore store,
    AutomationSourceRuntime source,
    AutomationDefinitionLoader loader,
    AutomationTemplateRenderer renderer,
    IAutomationPreparedTurnStore preparedTurns,
    IAutomationRuntimeSnapshotProvider runtime,
    AutomationsConfig config,
    TimeProvider timeProvider) : IAutomationService
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public Task<AutomationResult<AutomationPage<AutomationDefinitionSummary>>>
        ListDefinitionsAsync(
            ListAutomationDefinitionsRequest request,
            CancellationToken cancellationToken = default)
    {
        if (!ValidActor(request.Actor) ||
            !TryPage(request.PageSize, request.Cursor, "d", out var after, out _))
        {
            return InvalidCursor<AutomationPage<AutomationDefinitionSummary>>(
                cancellationToken);
        }

        return store.ReadAsync(
            async (connection, token) =>
            {
                var revision = await ReadAutomationRevisionAsync(connection, null, token);
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT automation_id, display_name, enabled, source_status,
                           definition_version, has_schedule, revision
                    FROM automation_definitions
                    WHERE source_status <> 'missing' AND automation_id > $after
                    ORDER BY automation_id
                    LIMIT $limit;
                    """;
                Add(command, "$after", after);
                Add(command, "$limit", request.PageSize + 1);
                await using var reader = await command.ExecuteReaderAsync(token);
                var items = new List<AutomationDefinitionSummary>();
                while (await reader.ReadAsync(token))
                {
                    items.Add(new AutomationDefinitionSummary(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetInt64(2) != 0,
                        DefinitionStatus(reader.GetString(3)),
                        reader.IsDBNull(4) ? null : reader.GetString(4),
                        reader.GetInt64(5) != 0,
                        reader.GetInt64(6)));
                }

                return SuccessPage(items, request.PageSize, revision, "d", item =>
                    item.AutomationId);
            },
            cancellationToken).AsTask();
    }

    public async Task<AutomationResult<AutomationDefinitionSnapshot>>
        GetDefinitionAsync(
            GetAutomationDefinitionRequest request,
            CancellationToken cancellationToken = default)
    {
        if (!ValidActor(request.Actor) ||
            string.IsNullOrWhiteSpace(request.AutomationId))
        {
            return await Failure<AutomationDefinitionSnapshot>(
                AutomationErrorCodes.NotFound,
                "Automation Definition was not found.",
                cancellationToken);
        }

        var row = await store.ReadAsync(
            (connection, token) => ReadDefinitionAsync(
                connection,
                request.AutomationId,
                token),
            cancellationToken);
        if (row is null)
        {
            return await Failure<AutomationDefinitionSnapshot>(
                AutomationErrorCodes.NotFound,
                "Automation Definition was not found.",
                cancellationToken);
        }

        var trust = await runtime.GetWorkspaceTrustAsync(cancellationToken);
        var summary = new AutomationDefinitionSummary(
            row.AutomationId,
            row.DisplayName,
            row.Enabled,
            row.Status,
            row.DefinitionVersion,
            row.HasSchedule,
            row.Revision);
        return new AutomationResult<AutomationDefinitionSnapshot>(
            new AutomationDefinitionSnapshot(
                summary,
                row.SourceRelativePath,
                row.DefinitionJson is null
                    ? null
                    : JsonDocument.Parse(row.DefinitionJson).RootElement.Clone(),
                row.Diagnostics,
                new AutomationDefinitionActivationSnapshot(
                    config.Enabled,
                    trust.IsTrusted,
                    row.Enabled,
                    trust.Source)),
            row.AutomationRevision,
            null);
    }

    public Task<AutomationResult<AutomationPage<AutomationScheduleSnapshot>>>
        ListSchedulesAsync(
            ListAutomationSchedulesRequest request,
            CancellationToken cancellationToken = default)
    {
        if (!ValidActor(request.Actor) ||
            !TryPage(request.PageSize, request.Cursor, "s", out var after, out _))
        {
            return InvalidCursor<AutomationPage<AutomationScheduleSnapshot>>(
                cancellationToken);
        }

        return store.ReadAsync(
            async (connection, token) =>
            {
                var revision = await ReadAutomationRevisionAsync(connection, null, token);
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT automation_id, cron, time_zone, next_occurrence_utc,
                           last_occurrence_utc, coalesced_occurrence_utc, revision
                    FROM automation_schedules
                    WHERE automation_id > $after
                    ORDER BY automation_id
                    LIMIT $limit;
                    """;
                Add(command, "$after", after);
                Add(command, "$limit", request.PageSize + 1);
                await using var reader = await command.ExecuteReaderAsync(token);
                var items = new List<AutomationScheduleSnapshot>();
                while (await reader.ReadAsync(token))
                {
                    items.Add(ReadSchedule(reader));
                }

                return SuccessPage(items, request.PageSize, revision, "s", item =>
                    item.AutomationId);
            },
            cancellationToken).AsTask();
    }

    public async Task<AutomationResult<AutomationScheduleSnapshot>> GetScheduleAsync(
        GetAutomationScheduleRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!ValidActor(request.Actor))
        {
            return await Failure<AutomationScheduleSnapshot>(
                AutomationErrorCodes.NotFound,
                "Automation Schedule was not found.",
                cancellationToken);
        }

        return await store.ReadAsync(
            async (connection, token) =>
            {
                var revision = await ReadAutomationRevisionAsync(connection, null, token);
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT automation_id, cron, time_zone, next_occurrence_utc,
                           last_occurrence_utc, coalesced_occurrence_utc, revision
                    FROM automation_schedules
                    WHERE automation_id = $id;
                    """;
                Add(command, "$id", request.AutomationId);
                await using var reader = await command.ExecuteReaderAsync(token);
                return await reader.ReadAsync(token)
                    ? new AutomationResult<AutomationScheduleSnapshot>(
                        ReadSchedule(reader),
                        revision,
                        null)
                    : Error<AutomationScheduleSnapshot>(
                        revision,
                        AutomationErrorCodes.NotFound,
                        "Automation Schedule was not found.");
            },
            cancellationToken);
    }

    public async Task<AutomationResult<AutomationRunSnapshot>> StartRunAsync(
        StartAutomationRunRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!ValidActor(request.Actor) ||
            request.Actor.Kind != AutomationActorKind.Host ||
            request.CommandId.Version != 7)
        {
            return await Failure<AutomationRunSnapshot>(
                AutomationErrorCodes.PermissionDenied,
                "Only a valid Host actor can start an Automation Run.",
                cancellationToken);
        }

        string requestSha256;
        try
        {
            requestSha256 = RequestSha256(request);
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidOperationException or ArgumentException)
        {
            return await Failure<AutomationRunSnapshot>(
                AutomationErrorCodes.InputInvalid,
                "Automation inputs are invalid.",
                cancellationToken);
        }

        if (await ReadReceiptAsync(request.CommandId, cancellationToken) is { } receipt)
        {
            return ReceiptResult(receipt, requestSha256);
        }

        if (!config.Enabled)
        {
            return await Failure<AutomationRunSnapshot>(
                AutomationErrorCodes.Unavailable,
                "Automations are disabled.",
                cancellationToken);
        }

        var projection = await source.ReadAsync(request.AutomationId, cancellationToken);
        if (projection is null)
        {
            return await Failure<AutomationRunSnapshot>(
                AutomationErrorCodes.NotFound,
                "Automation Definition was not found.",
                cancellationToken);
        }

        if (projection.Status != AutomationDefinitionSourceStatus.Ready ||
            projection.DefinitionJson is null ||
            projection.DefinitionVersion is null)
        {
            return Error<AutomationRunSnapshot>(
                projection.AutomationRevision,
                AutomationErrorCodes.DefinitionInvalid,
                "Automation Definition is not runnable.");
        }

        if (projection.Revision != request.ExpectedRevision)
        {
            return Error<AutomationRunSnapshot>(
                projection.AutomationRevision,
                AutomationErrorCodes.Conflict,
                "Automation Definition revision changed.");
        }

        if (!projection.Enabled)
        {
            return Error<AutomationRunSnapshot>(
                projection.AutomationRevision,
                AutomationErrorCodes.Unavailable,
                "Automation Definition is disabled.");
        }

        AutomationDefinitionCandidate definition;
        try
        {
            using var document = JsonDocument.Parse(projection.DefinitionJson);
            definition = loader.Hydrate(
                document.RootElement,
                projection.DefinitionVersion);
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidDataException)
        {
            return Error<AutomationRunSnapshot>(
                projection.AutomationRevision,
                AutomationErrorCodes.DefinitionInvalid,
                "Automation Definition snapshot is invalid.");
        }

        var runId = request.CommandId;
        var preparedTurnId = PreparedTurnId(request.CommandId);
        var rendered = await renderer.RenderAsync(
            definition,
            runId,
            request.Inputs,
            new AutomationTriggerContext("manual", null),
            cancellationToken);
        if (!rendered.IsValid)
        {
            var diagnostic = rendered.Diagnostics[0].Code;
            return Error<AutomationRunSnapshot>(
                projection.AutomationRevision,
                diagnostic switch
                {
                    AutomationDefinitionDiagnosticCodes.InvalidInputs =>
                        AutomationErrorCodes.InputInvalid,
                    AutomationDefinitionDiagnosticCodes.SecretDetected =>
                        AutomationErrorCodes.SecretDetected,
                    _ => AutomationErrorCodes.DefinitionInvalid,
                },
                "Automation inputs or Prompt are invalid.");
        }

        var captured = await runtime.CaptureAsync(
            new AutomationRuntimeSnapshotRequest(
                definition.Allow.Plugins,
                definition.Allow.Skills,
                definition.Allow.Tools,
                definition.Allow.Effects),
            cancellationToken);
        if (!captured.IsSuccess)
        {
            return new AutomationResult<AutomationRunSnapshot>(
                null,
                projection.AutomationRevision,
                captured.Error);
        }

        var canonicalInputs = CanonicalJson.Write(rendered.Inputs!.Value);
        var inputSha256 = Hash(canonicalInputs);
        var promptSha256 = Hash(rendered.Prompt!);
        var prepared = new AutomationPreparedTurnSnapshot(
            preparedTurnId,
            requestSha256,
            rendered.Prompt!,
            promptSha256,
            timeProvider.GetUtcNow());
        AutomationPreparedTurnWriteResult staged;
        try
        {
            staged = await preparedTurns.PrepareAsync(prepared, cancellationToken);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Error<AutomationRunSnapshot>(
                projection.AutomationRevision,
                AutomationErrorCodes.Unavailable,
                "Automation Prepared Turn could not be persisted.",
                retryable: true);
        }

        if (staged.IsConflict)
        {
            return Error<AutomationRunSnapshot>(
                projection.AutomationRevision,
                AutomationErrorCodes.Conflict,
                "Automation Prepared Turn conflicts with the command.");
        }

        AutomationResult<AutomationRunSnapshot> result;
        try
        {
            result = await store.WriteAsync(
                (connection, transaction, token) => CreateRunAsync(
                    connection,
                    transaction,
                    request,
                    requestSha256,
                    definition,
                    inputSha256,
                    promptSha256,
                    preparedTurnId,
                    captured.Value!,
                    token),
                cancellationToken);
        }
        catch
        {
            _ = await preparedTurns.DeleteAsync(
                preparedTurnId,
                requestSha256,
                CancellationToken.None);
            throw;
        }

        if (!result.IsSuccess && !result.IsReplay)
        {
            _ = await preparedTurns.DeleteAsync(
                preparedTurnId,
                requestSha256,
                CancellationToken.None);
        }

        return result;
    }

    public Task<AutomationResult<AutomationPage<AutomationRunSummary>>> ListRunsAsync(
        ListAutomationRunsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!ValidActor(request.Actor) ||
            !TryPage(
                request.PageSize,
                request.Cursor,
                "r",
                out _,
                out var runCursor))
        {
            return InvalidCursor<AutomationPage<AutomationRunSummary>>(
                cancellationToken);
        }

        return store.ReadAsync(
            async (connection, token) =>
            {
                var revision = await ReadAutomationRevisionAsync(connection, null, token);
                await using var command = connection.CreateCommand();
                var filters = new List<string>();
                if (request.AutomationId is not null)
                {
                    filters.Add("automation_id = $automationId");
                    Add(command, "$automationId", request.AutomationId);
                }

                if (request.Status is not null)
                {
                    filters.Add("status = $status");
                    Add(command, "$status", RunStatus(request.Status.Value));
                }

                if (runCursor is not null)
                {
                    filters.Add(
                        "(created_utc < $created OR " +
                        "(created_utc = $created AND automation_run_id < $runId))");
                    Add(command, "$created", runCursor.Value.CreatedUtc);
                    Add(command, "$runId", runCursor.Value.RunId.ToString("D"));
                }

                command.CommandText =
                    $"""
                     SELECT automation_run_id, automation_id, trigger_kind, status,
                            attention_kind, created_utc, started_utc, completed_utc,
                            revision
                     FROM automation_runs
                     {(filters.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", filters))}
                     ORDER BY created_utc DESC, automation_run_id DESC
                     LIMIT $limit;
                     """;
                Add(command, "$limit", request.PageSize + 1);
                await using var reader = await command.ExecuteReaderAsync(token);
                var items = new List<AutomationRunSummary>();
                while (await reader.ReadAsync(token))
                {
                    items.Add(ReadRunSummary(reader));
                }

                var more = items.Count > request.PageSize;
                var page = items.Take(request.PageSize).ToArray();
                var cursor = more
                    ? Encode(
                        $"r:{Milliseconds(page[^1].CreatedAtUtc)}:" +
                        $"{page[^1].RunId:D}")
                    : null;
                return new AutomationResult<AutomationPage<AutomationRunSummary>>(
                    new AutomationPage<AutomationRunSummary>(page, cursor),
                    revision,
                    null);
            },
            cancellationToken).AsTask();
    }

    public async Task<AutomationResult<AutomationRunSnapshot>> GetRunAsync(
        GetAutomationRunRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!ValidActor(request.Actor) || request.RunId.Version != 7)
        {
            return await Failure<AutomationRunSnapshot>(
                AutomationErrorCodes.NotFound,
                "Automation Run was not found.",
                cancellationToken);
        }

        return await store.ReadAsync(
            async (connection, token) =>
            {
                var revision = await ReadAutomationRevisionAsync(connection, null, token);
                var run = await ReadRunAsync(connection, null, request.RunId, token);
                return run is null
                    ? Error<AutomationRunSnapshot>(
                        revision,
                        AutomationErrorCodes.NotFound,
                        "Automation Run was not found.")
                    : new AutomationResult<AutomationRunSnapshot>(
                        run,
                        revision,
                        null);
            },
            cancellationToken);
    }

    public Task<AutomationResult<AutomationRunSnapshot>> CancelRunAsync(
        CancelAutomationRunRequest request,
        CancellationToken cancellationToken = default) =>
        Failure<AutomationRunSnapshot>(
            AutomationErrorCodes.Unavailable,
            "Automation cancellation is not available yet.",
            cancellationToken);

    public Task<AutomationResult<AutomationRunSnapshot>> ResolveAttentionAsync(
        ResolveAutomationAttentionRequest request,
        CancellationToken cancellationToken = default) =>
        Failure<AutomationRunSnapshot>(
            AutomationErrorCodes.Unavailable,
            "Automation attention resolution is not available yet.",
            cancellationToken);

    internal static Guid PreparedTurnId(Guid commandId) =>
        DerivedId(commandId, 0xa5);

    private async ValueTask<AutomationResult<AutomationRunSnapshot>> CreateRunAsync(
        DbConnection connection,
        DbTransaction transaction,
        StartAutomationRunRequest request,
        string requestSha256,
        AutomationDefinitionCandidate definition,
        string inputSha256,
        string promptSha256,
        Guid preparedTurnId,
        AutomationRuntimeSnapshot captured,
        CancellationToken cancellationToken)
    {
        if (await ReadReceiptAsync(
                connection,
                transaction,
                request.CommandId,
                cancellationToken) is { } existing)
        {
            return ReceiptResult(existing, requestSha256);
        }

        var current = await ReadDefinitionForStartAsync(
            connection,
            transaction,
            request.AutomationId,
            cancellationToken);
        var revision = await ReadAutomationRevisionAsync(
            connection,
            transaction,
            cancellationToken);
        if (current is null ||
            current.Value.Revision != request.ExpectedRevision ||
            current.Value.Status != "ready" ||
            !current.Value.Enabled ||
            !string.Equals(
                current.Value.DefinitionVersion,
                definition.DefinitionVersion,
                StringComparison.Ordinal))
        {
            return Error<AutomationRunSnapshot>(
                revision,
                AutomationErrorCodes.Conflict,
                "Automation Definition changed before Run creation.");
        }

        if (await CountAsync(
                connection,
                transaction,
                """
                SELECT count(*)
                FROM automation_runs
                WHERE status IN ('pending', 'running', 'needsAttention');
                """,
                cancellationToken) >= config.MaxConcurrentRuns)
        {
            return Error<AutomationRunSnapshot>(
                revision,
                AutomationErrorCodes.RunConflict,
                "The Automation concurrency limit is active.",
                retryable: true);
        }

        await using (var active = connection.CreateCommand())
        {
            active.Transaction = transaction;
            active.CommandText =
                """
                SELECT count(*)
                FROM automation_runs
                WHERE automation_id = $id
                  AND status IN ('pending', 'running', 'needsAttention');
                """;
            Add(active, "$id", request.AutomationId);
            if (Convert.ToInt64(
                    await active.ExecuteScalarAsync(cancellationToken),
                    CultureInfo.InvariantCulture) != 0)
            {
                return Error<AutomationRunSnapshot>(
                    revision,
                    AutomationErrorCodes.RunConflict,
                    "A nonterminal Automation Run already exists.",
                    retryable: true);
            }
        }

        var now = timeProvider.GetUtcNow();
        var runDeadline = now + Min(definition.RunTimeout, config.MaximumRunTimeout);
        var workspaceMode = definition.Workspace.Mode == AutomationWorkspaceMode.Project
            ? "project"
            : "worktree";
        var workspaceAccess = captured.Permissions.Effects.Any(effect =>
            effect.Effect == "workspaceWrite")
            ? "readWrite"
            : "readOnly";
        var runId = request.CommandId;
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO automation_runs (
                automation_run_id, automation_id, trigger_kind,
                trigger_idempotency_key, scheduled_occurrence_utc, status,
                definition_snapshot_json, inputs_sha256, rendered_prompt_sha256,
                prepared_turn_id, workspace_mode, workspace_access,
                provider_id, model_id, permission_snapshot_json,
                capability_snapshot_json, run_deadline_utc,
                attention_kind, attention_deadline_utc, thread_id, worktree_id,
                base_commit_sha, project_writer_lease_id,
                project_writer_lease_expires_utc, safe_summary, error_code,
                diagnostic, revision, created_utc, started_utc, updated_utc,
                completed_utc)
            VALUES (
                $runId, $automationId, 'manual',
                $idempotencyKey, NULL, 'pending',
                $definition, $inputsSha, $promptSha,
                $preparedTurnId, $workspaceMode, $workspaceAccess,
                $providerId, $modelId, $permissions,
                $capabilities, $runDeadline,
                NULL, NULL, NULL, NULL,
                NULL, NULL,
                NULL, NULL, NULL,
                NULL, 1, $now, NULL, $now,
                NULL);
            """,
            cancellationToken,
            ("$runId", runId.ToString("D")),
            ("$automationId", request.AutomationId),
            ("$idempotencyKey", $"manual:{runId:D}"),
            ("$definition", definition.CanonicalDefinition.GetRawText()),
            ("$inputsSha", inputSha256),
            ("$promptSha", promptSha256),
            ("$preparedTurnId", preparedTurnId.ToString("D")),
            ("$workspaceMode", workspaceMode),
            ("$workspaceAccess", workspaceAccess),
            ("$providerId", captured.ProviderId),
            ("$modelId", captured.ModelId),
            ("$permissions", JsonSerializer.Serialize(
                captured.Permissions,
                JsonOptions)),
            ("$capabilities", JsonSerializer.Serialize(
                captured.Capabilities,
                JsonOptions)),
            ("$runDeadline", Milliseconds(runDeadline)),
            ("$now", Milliseconds(now)));

        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO automation_dispatch_intents (
                intent_id, idempotency_key, dispatch_kind, entity_kind, entity_id,
                status, attempt_count, lease_owner, lease_expires_utc,
                error_code, diagnostic, created_utc, updated_utc)
            VALUES (
                $intentId, $idempotencyKey, $dispatchKind, 'automationRun', $runId,
                'pending', 0, NULL, NULL,
                NULL, NULL, $now, $now);
            """,
            cancellationToken,
            ("$intentId", DerivedId(request.CommandId, 0x5a).ToString("D")),
            ("$idempotencyKey", $"automation-run:{runId:D}:dispatch"),
            ("$dispatchKind",
                definition.Workspace.Mode == AutomationWorkspaceMode.Worktree
                    ? "createWorktree"
                    : "createThread"),
            ("$runId", runId.ToString("D")),
            ("$now", Milliseconds(now)));
        await ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE automation_state
            SET automation_revision = automation_revision + 1,
                updated_utc = $now
            WHERE id = 1;
            """,
            cancellationToken,
            ("$now", Milliseconds(now)));
        revision++;

        var snapshot = new AutomationRunSnapshot(
            new AutomationRunSummary(
                runId,
                request.AutomationId,
                AutomationTriggerKind.Manual,
                AutomationRunStatus.Pending,
                null,
                now,
                null,
                null,
                Revision: 1),
            null,
            null,
            null,
            AutomationResourceAvailability.Missing,
            null,
            AutomationResourceAvailability.Missing,
            runDeadline,
            null,
            captured.ProviderId,
            captured.ModelId,
            captured.Permissions,
            captured.Capabilities);
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO automation_command_receipts (
                command_id, actor_kind, actor_id, command_kind, target_id,
                request_sha256, result_json, revision, created_utc)
            VALUES (
                $commandId, 'host', $actorId, 'startRun', $targetId,
                $requestSha, $result, $revision, $now);
            """,
            cancellationToken,
            ("$commandId", request.CommandId.ToString("D")),
            ("$actorId", request.Actor.PrincipalId),
            ("$targetId", runId.ToString("D")),
            ("$requestSha", requestSha256),
            ("$result", JsonSerializer.Serialize(snapshot, JsonOptions)),
            ("$revision", revision),
            ("$now", Milliseconds(now)));
        return new AutomationResult<AutomationRunSnapshot>(
            snapshot,
            revision,
            null);
    }

    private Task<Receipt?> ReadReceiptAsync(
        Guid commandId,
        CancellationToken cancellationToken) =>
        store.ReadAsync(
            (connection, token) =>
                ReadReceiptAsync(connection, null, commandId, token),
            cancellationToken).AsTask();

    private static async ValueTask<Receipt?> ReadReceiptAsync(
        DbConnection connection,
        DbTransaction? transaction,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT request_sha256, result_json, revision
            FROM automation_command_receipts
            WHERE command_id = $commandId;
            """;
        Add(command, "$commandId", commandId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new Receipt(
                reader.GetString(0),
                JsonSerializer.Deserialize<AutomationRunSnapshot>(
                    reader.GetString(1),
                    JsonOptions) ?? throw new InvalidDataException(
                    "Automation command receipt is invalid."),
                reader.GetInt64(2))
            : null;
    }

    private static AutomationResult<AutomationRunSnapshot> ReceiptResult(
        Receipt receipt,
        string requestSha256) =>
        string.Equals(
            receipt.RequestSha256,
            requestSha256,
            StringComparison.Ordinal)
            ? new AutomationResult<AutomationRunSnapshot>(
                receipt.Snapshot,
                receipt.Revision,
                null,
                IsReplay: true)
            : Error<AutomationRunSnapshot>(
                receipt.Revision,
                AutomationErrorCodes.Conflict,
                "Command ID was already used for a different request.");

    private static async ValueTask<AutomationRunSnapshot?> ReadRunAsync(
        DbConnection connection,
        DbTransaction? transaction,
        Guid runId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT automation_run_id, automation_id, trigger_kind, status,
                   attention_kind, created_utc, started_utc, completed_utc, revision,
                   safe_summary, error_code, diagnostic, thread_id, worktree_id,
                   run_deadline_utc, attention_deadline_utc, provider_id, model_id,
                   permission_snapshot_json, capability_snapshot_json
            FROM automation_runs
            WHERE automation_run_id = $runId;
            """;
        Add(command, "$runId", runId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var summary = ReadRunSummary(reader);
        var threadId = GuidOrNull(reader, 12);
        var worktreeId = GuidOrNull(reader, 13);
        var permissions = JsonSerializer.Deserialize<AutomationPermissionSnapshot>(
                              reader.GetString(18),
                              JsonOptions)
                          ?? throw new InvalidDataException(
                              "Automation permission snapshot is invalid.");
        var capabilities = JsonSerializer.Deserialize<AutomationCapabilitySnapshot[]>(
                               reader.GetString(19),
                               JsonOptions)
                           ?? throw new InvalidDataException(
                               "Automation capability snapshot is invalid.");
        var errorCode = reader.IsDBNull(10) ? null : reader.GetString(10);
        return new AutomationRunSnapshot(
            summary,
            reader.IsDBNull(9) ? null : reader.GetString(9),
            errorCode is null
                ? null
                : new AutomationError(
                    errorCode,
                    reader.IsDBNull(11)
                        ? "Automation Run failed."
                        : reader.GetString(11)),
            threadId,
            threadId is null
                ? AutomationResourceAvailability.Missing
                : AutomationResourceAvailability.Available,
            worktreeId,
            worktreeId is null
                ? AutomationResourceAvailability.Missing
                : AutomationResourceAvailability.Available,
            Instant(reader.GetInt64(14)),
            InstantOrNull(reader, 15),
            reader.GetString(16),
            reader.GetString(17),
            permissions,
            capabilities);
    }

    private static AutomationRunSummary ReadRunSummary(DbDataReader reader) =>
        new(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            TriggerKind(reader.GetString(2)),
            RunStatus(reader.GetString(3)),
            reader.IsDBNull(4) ? null : AttentionKind(reader.GetString(4)),
            Instant(reader.GetInt64(5)),
            InstantOrNull(reader, 6),
            InstantOrNull(reader, 7),
            reader.GetInt64(8));

    private static async ValueTask<DefinitionProjection?> ReadDefinitionAsync(
        DbConnection connection,
        string automationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT d.automation_id, d.source_relative_path, d.source_status,
                   d.definition_version, d.display_name, d.enabled,
                   d.definition_json, d.diagnostics_json, d.has_schedule,
                   d.revision, s.automation_revision
            FROM automation_definitions AS d
            CROSS JOIN automation_state AS s
            WHERE d.automation_id = $id AND d.source_status <> 'missing' AND s.id = 1;
            """;
        Add(command, "$id", automationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new DefinitionProjection(
                reader.GetString(0),
                reader.GetString(1),
                DefinitionStatus(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5) != 0,
                reader.IsDBNull(6) ? null : reader.GetString(6),
                JsonSerializer.Deserialize<OpenCoWorkDiagnostic[]>(
                    reader.GetString(7),
                    JsonOptions) ?? [],
                reader.GetInt64(8) != 0,
                reader.GetInt64(9),
                reader.GetInt64(10))
            : null;
    }

    private static async ValueTask<(
        long Revision,
        string Status,
        bool Enabled,
        string? DefinitionVersion)?> ReadDefinitionForStartAsync(
        DbConnection connection,
        DbTransaction transaction,
        string automationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT revision, source_status, enabled, definition_version
            FROM automation_definitions
            WHERE automation_id = $id;
            """;
        Add(command, "$id", automationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt64(2) != 0,
                reader.IsDBNull(3) ? null : reader.GetString(3))
            : null;
    }

    private static AutomationScheduleSnapshot ReadSchedule(DbDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            InstantOrNull(reader, 3),
            InstantOrNull(reader, 4),
            InstantOrNull(reader, 5),
            reader.GetInt64(6));

    private async Task<AutomationResult<T>> Failure<T>(
        string code,
        string message,
        CancellationToken cancellationToken,
        bool retryable = false)
    {
        var revision = await store.ReadAsync(
            (connection, token) =>
                ReadAutomationRevisionAsync(connection, null, token),
            cancellationToken);
        return Error<T>(revision, code, message, retryable);
    }

    private Task<AutomationResult<T>> InvalidCursor<T>(
        CancellationToken cancellationToken) =>
        Failure<T>(
            AutomationErrorCodes.InvalidCursor,
            "Automation cursor or page size is invalid.",
            cancellationToken);

    private static AutomationResult<AutomationPage<T>> SuccessPage<T>(
        List<T> items,
        int pageSize,
        long revision,
        string prefix,
        Func<T, string> key)
    {
        var more = items.Count > pageSize;
        var page = items.Take(pageSize).ToArray();
        return new AutomationResult<AutomationPage<T>>(
            new AutomationPage<T>(
                page,
                more ? Encode($"{prefix}:{key(page[^1])}") : null),
            revision,
            null);
    }

    private static bool TryPage(
        int pageSize,
        string? cursor,
        string prefix,
        out string after,
        out (long CreatedUtc, Guid RunId)? runCursor)
    {
        after = string.Empty;
        runCursor = null;
        if (pageSize is < 1 or > AutomationRuntimeLimits.MaximumPageSize)
        {
            return false;
        }

        if (cursor is null)
        {
            return true;
        }

        string decoded;
        try
        {
            decoded = Decode(cursor);
        }
        catch (FormatException)
        {
            return false;
        }

        if (prefix == "r")
        {
            var parts = decoded.Split(':');
            if (parts.Length != 3 ||
                parts[0] != prefix ||
                !long.TryParse(
                    parts[1],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var created) ||
                !Guid.TryParseExact(parts[2], "D", out var runId) ||
                runId.Version != 7)
            {
                return false;
            }

            runCursor = (created, runId);
            return true;
        }

        var expected = prefix + ":";
        if (!decoded.StartsWith(expected, StringComparison.Ordinal) ||
            decoded.Length == expected.Length)
        {
            return false;
        }

        after = decoded[expected.Length..];
        return true;
    }

    private static string RequestSha256(StartAutomationRunRequest request)
    {
        var inputSha = Hash(CanonicalJson.Write(request.Inputs));
        return Hash(
            $"{request.Actor.Kind}\n{request.Actor.PrincipalId}\n" +
            $"{request.AutomationId}\n{request.ExpectedRevision}\n{inputSha}");
    }

    private static bool ValidActor(AutomationActorContext? actor) =>
        actor is not null &&
        Enum.IsDefined(actor.Kind) &&
        !string.IsNullOrWhiteSpace(actor.PrincipalId);

    private static async ValueTask<long> ReadAutomationRevisionAsync(
        DbConnection connection,
        DbTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT automation_revision FROM automation_state WHERE id = 1;";
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

    private static async ValueTask<long> CountAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
    }

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
        foreach (var (name, value) in parameters)
        {
            Add(command, name, value);
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

    private static AutomationResult<T> Error<T>(
        long revision,
        string code,
        string message,
        bool retryable = false) =>
        new(default, revision, new AutomationError(code, message, retryable));

    private static AutomationDefinitionSourceStatus DefinitionStatus(string value) =>
        value switch
        {
            "ready" => AutomationDefinitionSourceStatus.Ready,
            "faulted" => AutomationDefinitionSourceStatus.Faulted,
            "missing" => AutomationDefinitionSourceStatus.Missing,
            _ => throw new InvalidDataException(
                "Automation Definition status is invalid."),
        };

    private static AutomationTriggerKind TriggerKind(string value) => value switch
    {
        "manual" => AutomationTriggerKind.Manual,
        "cron" => AutomationTriggerKind.Cron,
        _ => throw new InvalidDataException("Automation Trigger kind is invalid."),
    };

    private static AutomationRunStatus RunStatus(string value) => value switch
    {
        "pending" => AutomationRunStatus.Pending,
        "running" => AutomationRunStatus.Running,
        "needsAttention" => AutomationRunStatus.NeedsAttention,
        "completed" => AutomationRunStatus.Completed,
        "failed" => AutomationRunStatus.Failed,
        "cancelled" => AutomationRunStatus.Cancelled,
        "timedOut" => AutomationRunStatus.TimedOut,
        _ => throw new InvalidDataException("Automation Run status is invalid."),
    };

    private static string RunStatus(AutomationRunStatus value) => value switch
    {
        AutomationRunStatus.Pending => "pending",
        AutomationRunStatus.Running => "running",
        AutomationRunStatus.NeedsAttention => "needsAttention",
        AutomationRunStatus.Completed => "completed",
        AutomationRunStatus.Failed => "failed",
        AutomationRunStatus.Cancelled => "cancelled",
        AutomationRunStatus.TimedOut => "timedOut",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static AutomationAttentionKind AttentionKind(string value) => value switch
    {
        "approvalRequired" => AutomationAttentionKind.ApprovalRequired,
        "userInputRequired" => AutomationAttentionKind.UserInputRequired,
        "outcomeUnknown" => AutomationAttentionKind.OutcomeUnknown,
        _ => throw new InvalidDataException("Automation attention kind is invalid."),
    };

    private static Guid? GuidOrNull(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Guid.Parse(reader.GetString(ordinal));

    private static DateTimeOffset? InstantOrNull(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Instant(reader.GetInt64(ordinal));

    private static DateTimeOffset Instant(long milliseconds) =>
        DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);

    private static long Milliseconds(DateTimeOffset value) =>
        value.ToUnixTimeMilliseconds();

    private static TimeSpan Min(TimeSpan first, TimeSpan second) =>
        first <= second ? first : second;

    private static string Hash(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static string Hash(string value) =>
        Hash(Encoding.UTF8.GetBytes(value));

    private static Guid DerivedId(Guid sourceId, byte discriminator)
    {
        if (sourceId.Version != 7)
        {
            throw new ArgumentException("Source ID must be UUIDv7.", nameof(sourceId));
        }

        var bytes = sourceId.ToByteArray();
        bytes[^1] ^= discriminator;
        return new Guid(bytes);
    }

    private static string Encode(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string Decode(string value)
    {
        var encoded = value.Replace('-', '+').Replace('_', '/');
        encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
    }

    private sealed record Receipt(
        string RequestSha256,
        AutomationRunSnapshot Snapshot,
        long Revision);

    private sealed record DefinitionProjection(
        string AutomationId,
        string SourceRelativePath,
        AutomationDefinitionSourceStatus Status,
        string? DefinitionVersion,
        string DisplayName,
        bool Enabled,
        string? DefinitionJson,
        IReadOnlyList<OpenCoWorkDiagnostic> Diagnostics,
        bool HasSchedule,
        long Revision,
        long AutomationRevision);
}
