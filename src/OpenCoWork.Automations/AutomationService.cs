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
    TimeProvider timeProvider,
    AutomationControlPlane? controlPlane = null,
    ISessionService? sessions = null,
    IProjectWriterLeaseService? writerLeases = null) : IAutomationService
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

        return await StartRunCoreAsync(
            request,
            requestSha256,
            new RunTrigger(
                AutomationTriggerKind.Manual,
                $"manual:{request.CommandId:D}",
                null,
                null,
                null),
            cancellationToken);
    }

    internal async Task<AutomationResult<AutomationRunSnapshot>> StartScheduledRunAsync(
        string automationId,
        DateTimeOffset expectedNextOccurrenceUtc,
        CancellationToken cancellationToken)
    {
        var projection = await source.ReadAsync(automationId, cancellationToken);
        if (projection?.Schedule is not { } schedule ||
            projection.DefinitionVersion is null)
        {
            return await Failure<AutomationRunSnapshot>(
                AutomationErrorCodes.NotFound,
                "Automation Schedule was not found.",
                cancellationToken);
        }

        var now = timeProvider.GetUtcNow();
        var advance = AutomationScheduleCalculator.Advance(
            schedule.Cron,
            schedule.TimeZone,
            expectedNextOccurrenceUtc,
            now);
        if (advance.CoalescedOccurrenceUtc is not { } scheduledForUtc)
        {
            return Error<AutomationRunSnapshot>(
                projection.AutomationRevision,
                AutomationErrorCodes.Conflict,
                "Automation Schedule is not due.");
        }

        var idempotencyKey = AutomationScheduleCalculator.IdempotencyKey(
            automationId,
            projection.DefinitionVersion,
            scheduledForUtc);
        var runId = ScheduledRunId(scheduledForUtc, idempotencyKey);
        var requestSha256 = Hash($"cron:{idempotencyKey}");
        if (await ReadReceiptAsync(runId, cancellationToken) is { } receipt)
        {
            return ReceiptResult(receipt, requestSha256);
        }

        using var inputs = JsonDocument.Parse("{}");
        var request = new StartAutomationRunRequest(
            new AutomationActorContext(AutomationActorKind.Scheduler, "scheduler"),
            automationId,
            inputs.RootElement.Clone(),
            runId,
            projection.Revision);
        return await StartRunCoreAsync(
            request,
            requestSha256,
            new RunTrigger(
                AutomationTriggerKind.Cron,
                idempotencyKey,
                scheduledForUtc,
                expectedNextOccurrenceUtc,
                advance.NextOccurrenceUtc),
            cancellationToken);
    }

    private async Task<AutomationResult<AutomationRunSnapshot>> StartRunCoreAsync(
        StartAutomationRunRequest request,
        string requestSha256,
        RunTrigger trigger,
        CancellationToken cancellationToken)
    {
        if (!config.Enabled)
        {
            return await Failure<AutomationRunSnapshot>(
                AutomationErrorCodes.Unavailable,
                "Automations are disabled.",
                cancellationToken);
        }

        if (controlPlane is { IsAvailable: false })
        {
            return await Failure<AutomationRunSnapshot>(
                AutomationErrorCodes.Unavailable,
                "The Automation control plane is unavailable.",
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
            new AutomationTriggerContext(
                trigger.Kind == AutomationTriggerKind.Manual ? "manual" : "cron",
                trigger.ScheduledForUtc),
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
                    trigger,
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

    public async Task<AutomationResult<AutomationRunSnapshot>> CancelRunAsync(
        CancelAutomationRunRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!ValidHost(request.Actor) ||
            request.RunId.Version != 7 ||
            request.CommandId.Version != 7)
        {
            return await Failure<AutomationRunSnapshot>(
                AutomationErrorCodes.PermissionDenied,
                "Only a valid Host actor can cancel an Automation Run.",
                cancellationToken);
        }

        var requestSha256 = RequestSha256(request);
        if (await ReadReceiptAsync(request.CommandId, cancellationToken) is { } receipt)
        {
            return ReceiptResult(receipt, requestSha256);
        }

        var result = await store.WriteAsync(
            (connection, transaction, token) => SettleRunAsync(
                connection,
                transaction,
                request.RunId,
                request.CommandId,
                request.Actor,
                request.ExpectedRevision,
                expectedAttentionId: null,
                "cancelRun",
                "cancelled",
                errorCode: null,
                diagnostic: null,
                requestSha256,
                token),
            cancellationToken);
        if (!result.IsSuccess)
        {
            return result;
        }

        await CancelSessionTurnAsync(
            result.Value!,
            request.CommandId,
            CancellationToken.None);
        await ReleaseWriterLeaseAsync(request.RunId, CancellationToken.None);
        return result;
    }

    public async Task<AutomationResult<AutomationRunSnapshot>> ResolveAttentionAsync(
        ResolveAutomationAttentionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!ValidHost(request.Actor) ||
            request.RunId.Version != 7 ||
            request.AttentionId.Version != 7 ||
            request.CommandId.Version != 7)
        {
            return await Failure<AutomationRunSnapshot>(
                AutomationErrorCodes.PermissionDenied,
                "Only a valid Host actor can resolve Automation attention.",
                cancellationToken);
        }

        var requestSha256 = RequestSha256(request);
        if (await ReadReceiptAsync(request.CommandId, cancellationToken) is { } receipt)
        {
            return ReceiptResult(receipt, requestSha256);
        }

        var attention = await ReadAttentionContextAsync(
            request.RunId,
            cancellationToken);
        if (attention is null ||
            attention.Revision != request.ExpectedRevision ||
            attention.AttentionId != request.AttentionId)
        {
            return await Failure<AutomationRunSnapshot>(
                AutomationErrorCodes.Conflict,
                "Automation attention changed.",
                cancellationToken);
        }

        if (request.Resolution.Kind == AutomationAttentionResolutionKind.Cancel ||
            (attention.Kind == AutomationAttentionKind.OutcomeUnknown &&
             request.Resolution.Kind == AutomationAttentionResolutionKind.Fail))
        {
            var status = request.Resolution.Kind ==
                         AutomationAttentionResolutionKind.Cancel
                ? "cancelled"
                : "failed";
            var result = await store.WriteAsync(
                (connection, transaction, token) => SettleRunAsync(
                    connection,
                    transaction,
                    request.RunId,
                    request.CommandId,
                    request.Actor,
                    request.ExpectedRevision,
                    request.AttentionId,
                    "resolveAttention",
                    status,
                    status == "failed" ? AutomationErrorCodes.OutcomeUnknown : null,
                    status == "failed"
                        ? "Automation outcome was resolved as failed by the Host."
                        : null,
                    requestSha256,
                    token),
                cancellationToken);
            if (result.IsSuccess &&
                request.Resolution.Kind == AutomationAttentionResolutionKind.Cancel)
            {
                await CancelSessionTurnAsync(
                    result.Value!,
                    request.CommandId,
                    CancellationToken.None);
            }

            if (result.IsSuccess)
            {
                await ReleaseWriterLeaseAsync(
                    request.RunId,
                    CancellationToken.None);
            }

            return result;
        }

        if (!TryCreateInteractionResponse(
                attention.Kind,
                request.Resolution,
                out var response))
        {
            return Error<AutomationRunSnapshot>(
                await CurrentRevisionAsync(cancellationToken),
                AutomationErrorCodes.InvalidState,
                "The attention resolution does not match its kind.");
        }

        if (sessions is null ||
            attention.ThreadId is null ||
            attention.TurnId is null)
        {
            return Error<AutomationRunSnapshot>(
                await CurrentRevisionAsync(cancellationToken),
                AutomationErrorCodes.Unavailable,
                "The Automation Session is unavailable.",
                retryable: true);
        }

        var thread = await sessions.GetThreadAsync(
            attention.ThreadId.Value,
            cancellationToken);
        if (thread.Value is null)
        {
            return Error<AutomationRunSnapshot>(
                await CurrentRevisionAsync(cancellationToken),
                AutomationErrorCodes.Unavailable,
                "The Automation Session is unavailable.",
                retryable: true);
        }

        var resolved = await sessions.ResolveInteractionAsync(
            new ResolveInteractionRequest(
                attention.ThreadId.Value,
                attention.TurnId.Value,
                request.AttentionId,
                response!,
                request.CommandId,
                thread.Value.CurrentSequence),
            cancellationToken);
        if (resolved.Value is null)
        {
            return Error<AutomationRunSnapshot>(
                await CurrentRevisionAsync(cancellationToken),
                resolved.Error?.Code == SessionErrorCodes.NotFound
                    ? AutomationErrorCodes.NotFound
                    : AutomationErrorCodes.Conflict,
                $"The Automation Session rejected the attention resolution: " +
                $"{resolved.Error?.Code ?? "unknown"}.",
                resolved.Error?.IsRetryable ?? false);
        }

        return await store.WriteAsync(
            (connection, transaction, token) => CompleteResolutionAsync(
                connection,
                transaction,
                request,
                requestSha256,
                token),
            CancellationToken.None);
    }

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
        RunTrigger trigger,
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

        if (trigger.Kind == AutomationTriggerKind.Cron &&
            !await ScheduleMatchesAsync(
                connection,
                transaction,
                request.AutomationId,
                trigger.ExpectedNextOccurrenceUtc!.Value,
                cancellationToken))
        {
            return Error<AutomationRunSnapshot>(
                revision,
                AutomationErrorCodes.Conflict,
                "Automation Schedule changed before Run creation.");
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
                if (trigger.Kind == AutomationTriggerKind.Cron)
                {
                    await AdvanceScheduleAsync(
                        connection,
                        transaction,
                        request.AutomationId,
                        trigger,
                        coalesced: true,
                        cancellationToken);
                }

                return Error<AutomationRunSnapshot>(
                    revision,
                    AutomationErrorCodes.RunConflict,
                    "A nonterminal Automation Run already exists.",
                    retryable: true);
            }
        }

        if (await CountAsync(
                connection,
                transaction,
                """
                SELECT count(*)
                FROM automation_runs
                WHERE status IN ('pending', 'running');
                """,
                cancellationToken) >= config.MaxConcurrentRuns)
        {
            return Error<AutomationRunSnapshot>(
                revision,
                AutomationErrorCodes.RunConflict,
                "The Automation concurrency limit is active.",
                retryable: true);
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
                $runId, $automationId, $triggerKind,
                $idempotencyKey, $scheduledOccurrence, 'pending',
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
            ("$triggerKind",
                trigger.Kind == AutomationTriggerKind.Manual ? "manual" : "cron"),
            ("$idempotencyKey", trigger.IdempotencyKey),
            ("$scheduledOccurrence", trigger.ScheduledForUtc is null
                ? null
                : Milliseconds(trigger.ScheduledForUtc.Value)),
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
        if (trigger.Kind == AutomationTriggerKind.Cron)
        {
            await AdvanceScheduleAsync(
                connection,
                transaction,
                request.AutomationId,
                trigger,
                coalesced: false,
                cancellationToken);
        }

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
                trigger.Kind,
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
                $commandId, $actorKind, $actorId, 'startRun', $targetId,
                $requestSha, $result, $revision, $now);
            """,
            cancellationToken,
            ("$commandId", request.CommandId.ToString("D")),
            ("$actorKind",
                request.Actor.Kind == AutomationActorKind.Host ? "host" : "scheduler"),
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

    private static async ValueTask<bool> ScheduleMatchesAsync(
        DbConnection connection,
        DbTransaction transaction,
        string automationId,
        DateTimeOffset expectedNextOccurrenceUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT count(*)
            FROM automation_schedules
            WHERE automation_id = $id
              AND next_occurrence_utc = $expected;
            """;
        Add(command, "$id", automationId);
        Add(command, "$expected", Milliseconds(expectedNextOccurrenceUtc));
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture) == 1;
    }

    private async ValueTask AdvanceScheduleAsync(
        DbConnection connection,
        DbTransaction transaction,
        string automationId,
        RunTrigger trigger,
        bool coalesced,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await ExecuteAsync(
            connection,
            transaction,
            $"""
             UPDATE automation_schedules
             SET last_occurrence_utc = {(coalesced ? "last_occurrence_utc" : "$scheduled")},
                 coalesced_occurrence_utc = {(coalesced ? "$scheduled" : "NULL")},
                 next_occurrence_utc = $next,
                 revision = revision + 1,
                 updated_utc = $now
             WHERE automation_id = $id
               AND next_occurrence_utc = $expected;
             {(coalesced
                 ? """
                   UPDATE automation_state
                   SET automation_revision = automation_revision + 1,
                       updated_utc = $now
                   WHERE id = 1;
                   """
                 : string.Empty)}
             """,
            cancellationToken,
            ("$scheduled", Milliseconds(trigger.ScheduledForUtc!.Value)),
            ("$next", trigger.NextOccurrenceUtc is null
                ? null
                : Milliseconds(trigger.NextOccurrenceUtc.Value)),
            ("$now", Milliseconds(now)),
            ("$id", automationId),
            ("$expected", Milliseconds(trigger.ExpectedNextOccurrenceUtc!.Value)));
    }

    private async ValueTask<AutomationResult<AutomationRunSnapshot>> SettleRunAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid runId,
        Guid commandId,
        AutomationActorContext actor,
        long expectedRevision,
        Guid? expectedAttentionId,
        string commandKind,
        string status,
        string? errorCode,
        string? diagnostic,
        string requestSha256,
        CancellationToken cancellationToken)
    {
        var current = await ReadRunAsync(
            connection,
            transaction,
            runId,
            cancellationToken);
        var automationRevision = await ReadAutomationRevisionAsync(
            connection,
            transaction,
            cancellationToken);
        if (current is null)
        {
            return Error<AutomationRunSnapshot>(
                automationRevision,
                AutomationErrorCodes.NotFound,
                "Automation Run was not found.");
        }

        if (current.Summary.Revision != expectedRevision ||
            (expectedAttentionId is not null &&
             (current.Summary.Status != AutomationRunStatus.NeedsAttention ||
              current.AttentionId != expectedAttentionId)))
        {
            return Error<AutomationRunSnapshot>(
                automationRevision,
                AutomationErrorCodes.Conflict,
                "Automation Run changed.");
        }

        if (current.Summary.Status is
            AutomationRunStatus.Completed or
            AutomationRunStatus.Failed or
            AutomationRunStatus.Cancelled or
            AutomationRunStatus.TimedOut)
        {
            return Error<AutomationRunSnapshot>(
                automationRevision,
                AutomationErrorCodes.InvalidState,
                "Automation Run is already terminal.");
        }

        var now = timeProvider.GetUtcNow();
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE automation_runs
                SET status = $status,
                    attention_kind = NULL,
                    attention_deadline_utc = NULL,
                    error_code = $errorCode,
                    diagnostic = $diagnostic,
                    completed_utc = $now,
                    revision = revision + 1,
                    updated_utc = $now
                WHERE automation_run_id = $runId
                  AND revision = $expectedRevision
                  AND status IN ('pending', 'running', 'needsAttention');
                """;
            Add(update, "$status", status);
            Add(update, "$errorCode", errorCode);
            Add(update, "$diagnostic", diagnostic);
            Add(update, "$now", Milliseconds(now));
            Add(update, "$runId", runId.ToString("D"));
            Add(update, "$expectedRevision", expectedRevision);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                return Error<AutomationRunSnapshot>(
                    automationRevision,
                    AutomationErrorCodes.Conflict,
                    "Automation Run changed.");
            }
        }

        await IncrementAutomationRevisionAsync(
            connection,
            transaction,
            now,
            cancellationToken);
        automationRevision++;
        var snapshot = await ReadRunAsync(
            connection,
            transaction,
            runId,
            cancellationToken) ?? throw new InvalidDataException(
            "Automation Run disappeared after settlement.");
        await InsertReceiptAsync(
            connection,
            transaction,
            commandId,
            actor,
            commandKind,
            runId,
            requestSha256,
            snapshot,
            automationRevision,
            now,
            cancellationToken);
        return new AutomationResult<AutomationRunSnapshot>(
            snapshot,
            automationRevision,
            null);
    }

    private async ValueTask<AutomationResult<AutomationRunSnapshot>>
        CompleteResolutionAsync(
            DbConnection connection,
            DbTransaction transaction,
            ResolveAutomationAttentionRequest request,
            string requestSha256,
            CancellationToken cancellationToken)
    {
        var current = await ReadRunAsync(
            connection,
            transaction,
            request.RunId,
            cancellationToken);
        var automationRevision = await ReadAutomationRevisionAsync(
            connection,
            transaction,
            cancellationToken);
        if (current is null)
        {
            return Error<AutomationRunSnapshot>(
                automationRevision,
                AutomationErrorCodes.NotFound,
                "Automation Run was not found.");
        }

        var now = timeProvider.GetUtcNow();
        if (current.Summary.Status == AutomationRunStatus.NeedsAttention &&
            current.Summary.Revision == request.ExpectedRevision)
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE automation_runs
                SET status = 'running',
                    attention_kind = NULL,
                    attention_deadline_utc = NULL,
                    revision = revision + 1,
                    updated_utc = $now
                WHERE automation_run_id = $runId
                  AND status = 'needsAttention'
                  AND revision = $expectedRevision;
                """,
                cancellationToken,
                ("$now", Milliseconds(now)),
                ("$runId", request.RunId.ToString("D")),
                ("$expectedRevision", request.ExpectedRevision));
            await IncrementAutomationRevisionAsync(
                connection,
                transaction,
                now,
                cancellationToken);
            automationRevision++;
            current = await ReadRunAsync(
                connection,
                transaction,
                request.RunId,
                cancellationToken);
        }
        else if (current.Summary.Status == AutomationRunStatus.NeedsAttention)
        {
            return Error<AutomationRunSnapshot>(
                automationRevision,
                AutomationErrorCodes.Conflict,
                "Automation Run changed.");
        }

        await InsertReceiptAsync(
            connection,
            transaction,
            request.CommandId,
            request.Actor,
            "resolveAttention",
            request.RunId,
            requestSha256,
            current!,
            automationRevision,
            now,
            cancellationToken);
        return new AutomationResult<AutomationRunSnapshot>(
            current,
            automationRevision,
            null);
    }

    private Task<AttentionContext?> ReadAttentionContextAsync(
        Guid runId,
        CancellationToken cancellationToken) =>
        store.ReadAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT r.revision, r.attention_kind, r.thread_id,
                           t.active_turn_id, p.interaction_id, p.turn_id
                    FROM automation_runs r
                    LEFT JOIN threads t ON t.thread_id = r.thread_id
                    LEFT JOIN pending_interactions p
                      ON p.thread_id = r.thread_id AND p.status = 'pending'
                    WHERE r.automation_run_id = $runId
                      AND r.status = 'needsAttention'
                    ORDER BY p.created_utc DESC, p.interaction_id DESC
                    LIMIT 1;
                    """;
                Add(command, "$runId", runId.ToString("D"));
                await using var reader = await command.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token))
                {
                    return null;
                }

                var kind = AttentionKind(reader.GetString(1));
                var interactionId = GuidOrNull(reader, 4);
                return new AttentionContext(
                    reader.GetInt64(0),
                    kind,
                    kind == AutomationAttentionKind.OutcomeUnknown
                        ? DerivedId(runId, 0xa8)
                        : interactionId ?? Guid.Empty,
                    GuidOrNull(reader, 2),
                    GuidOrNull(reader, 5) ?? GuidOrNull(reader, 3));
            },
            cancellationToken).AsTask();

    private async Task CancelSessionTurnAsync(
        AutomationRunSnapshot run,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        if (sessions is null || run.ThreadId is null)
        {
            return;
        }

        var thread = await sessions.GetThreadAsync(run.ThreadId.Value, cancellationToken);
        if (thread.Value?.ActiveTurnId is not { } turnId)
        {
            return;
        }

        _ = await sessions.CancelTurnAsync(
            new CancelTurnRequest(
                run.ThreadId.Value,
                turnId,
                DerivedId(commandId, 0xc1),
                thread.Value.CurrentSequence),
            cancellationToken);
    }

    private async Task ReleaseWriterLeaseAsync(
        Guid runId,
        CancellationToken cancellationToken)
    {
        if (writerLeases is null)
        {
            return;
        }

        var leaseId = await store.ReadAsync<Guid?>(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT project_writer_lease_id
                    FROM automation_runs
                    WHERE automation_run_id = $runId;
                    """;
                Add(command, "$runId", runId.ToString("D"));
                var value = await command.ExecuteScalarAsync(token);
                return value is null or DBNull ? null : Guid.Parse((string)value);
            },
            cancellationToken);
        if (leaseId is null)
        {
            return;
        }

        _ = await writerLeases.ReleaseAsync(
            new ProjectWriterLeaseOwner(
                ProjectWriterLeaseOwnerKind.AutomationRun,
                runId),
            leaseId.Value,
            cancellationToken);
        await store.WriteAsync(
            async (connection, transaction, token) =>
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    UPDATE automation_runs
                    SET project_writer_lease_id = NULL,
                        project_writer_lease_expires_utc = NULL
                    WHERE automation_run_id = $runId
                      AND project_writer_lease_id = $leaseId;
                    """,
                    token,
                    ("$runId", runId.ToString("D")),
                    ("$leaseId", leaseId.Value.ToString("D")));
                return 0;
            },
            cancellationToken);
    }

    private static bool TryCreateInteractionResponse(
        AutomationAttentionKind kind,
        AutomationAttentionResolution resolution,
        out SessionItemContent? response)
    {
        response = (kind, resolution.Kind) switch
        {
            (AutomationAttentionKind.ApprovalRequired,
                AutomationAttentionResolutionKind.Approve) =>
                new ApprovalResponseContent(true, resolution.Text),
            (AutomationAttentionKind.ApprovalRequired,
                AutomationAttentionResolutionKind.Reject) =>
                new ApprovalResponseContent(false, resolution.Text),
            (AutomationAttentionKind.UserInputRequired,
                AutomationAttentionResolutionKind.ProvideInput)
                when !string.IsNullOrWhiteSpace(resolution.Text) =>
                new UserInputResponseContent(resolution.Text),
            _ => null,
        };
        return response is not null;
    }

    private async Task<long> CurrentRevisionAsync(
        CancellationToken cancellationToken) =>
        await store.ReadAsync(
            (connection, token) => ReadAutomationRevisionAsync(
                connection,
                transaction: null,
                token),
            cancellationToken);

    private static async ValueTask IncrementAutomationRevisionAsync(
        DbConnection connection,
        DbTransaction transaction,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
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

    private static ValueTask InsertReceiptAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid commandId,
        AutomationActorContext actor,
        string commandKind,
        Guid runId,
        string requestSha256,
        AutomationRunSnapshot snapshot,
        long revision,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO automation_command_receipts (
                command_id, actor_kind, actor_id, command_kind, target_id,
                request_sha256, result_json, revision, created_utc)
            VALUES (
                $commandId, 'host', $actorId, $commandKind, $targetId,
                $requestSha, $result, $revision, $now);
            """,
            cancellationToken,
            ("$commandId", commandId.ToString("D")),
            ("$actorId", actor.PrincipalId),
            ("$commandKind", commandKind),
            ("$targetId", runId.ToString("D")),
            ("$requestSha", requestSha256),
            ("$result", JsonSerializer.Serialize(snapshot, JsonOptions)),
            ("$revision", revision),
            ("$now", Milliseconds(now)));

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
                   permission_snapshot_json, capability_snapshot_json,
                   (
                       SELECT interaction_id
                       FROM pending_interactions
                       WHERE thread_id = automation_runs.thread_id
                         AND status = 'pending'
                       ORDER BY created_utc DESC, interaction_id DESC
                       LIMIT 1
                   )
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
        Guid? attentionId = summary.AttentionKind == AutomationAttentionKind.OutcomeUnknown
            ? DerivedId(summary.RunId, 0xa8)
            : reader.IsDBNull(20)
                ? null
                : Guid.Parse(reader.GetString(20));
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
            capabilities,
            attentionId);
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

    private static string RequestSha256(CancelAutomationRunRequest request) =>
        Hash(
            $"{request.Actor.Kind}\n{request.Actor.PrincipalId}\n" +
            $"{request.RunId:D}\n{request.ExpectedRevision}\ncancel");

    private static string RequestSha256(
        ResolveAutomationAttentionRequest request) =>
        Hash(
            $"{request.Actor.Kind}\n{request.Actor.PrincipalId}\n" +
            $"{request.RunId:D}\n{request.AttentionId:D}\n" +
            $"{request.Resolution.Kind}\n{Hash(request.Resolution.Text ?? string.Empty)}\n" +
            $"{request.ExpectedRevision}");

    private static bool ValidActor(AutomationActorContext? actor) =>
        actor is not null &&
        Enum.IsDefined(actor.Kind) &&
        !string.IsNullOrWhiteSpace(actor.PrincipalId);

    private static bool ValidHost(AutomationActorContext? actor) =>
        ValidActor(actor) && actor!.Kind == AutomationActorKind.Host;

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

    private static Guid ScheduledRunId(
        DateTimeOffset scheduledForUtc,
        string idempotencyKey)
    {
        var timestamp = scheduledForUtc.ToUnixTimeMilliseconds();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey))[..16];
        bytes[0] = (byte)(timestamp >> 40);
        bytes[1] = (byte)(timestamp >> 32);
        bytes[2] = (byte)(timestamp >> 24);
        bytes[3] = (byte)(timestamp >> 16);
        bytes[4] = (byte)(timestamp >> 8);
        bytes[5] = (byte)timestamp;
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x70);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes, bigEndian: true);
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

    private sealed record RunTrigger(
        AutomationTriggerKind Kind,
        string IdempotencyKey,
        DateTimeOffset? ScheduledForUtc,
        DateTimeOffset? ExpectedNextOccurrenceUtc,
        DateTimeOffset? NextOccurrenceUtc);

    private sealed record AttentionContext(
        long Revision,
        AutomationAttentionKind Kind,
        Guid AttentionId,
        Guid? ThreadId,
        Guid? TurnId);

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
