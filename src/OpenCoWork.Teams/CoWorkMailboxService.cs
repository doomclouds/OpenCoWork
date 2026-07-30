using System.Data.Common;
using System.Text;
using OpenCoWork.Abstractions;

namespace OpenCoWork.Teams;

public sealed partial class CoWorkService
{
    public async Task<CoWorkResult<CoWorkPage<MailboxMessageSnapshot>>>
        ListMailboxMessagesAsync(
            ListMailboxMessagesRequest request,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PageSize is < 1 or > 1000 ||
            !TryReadOffset(request.Cursor, out var offset))
        {
            return await FailureAsync<CoWorkPage<MailboxMessageSnapshot>>(
                CoWorkErrorCodes.InvalidState,
                "Page size or cursor is invalid.",
                cancellationToken);
        }

        var mission = await ReadMissionSnapshotAsync(
            request.MissionId,
            cancellationToken);
        if (mission is null)
        {
            return await FailureAsync<CoWorkPage<MailboxMessageSnapshot>>(
                CoWorkErrorCodes.NotFound,
                "Mission was not found.",
                cancellationToken);
        }

        if (!CanViewMission(mission, request.Actor))
        {
            return await FailureAsync<CoWorkPage<MailboxMessageSnapshot>>(
                CoWorkErrorCodes.PermissionDenied,
                "Actor cannot view this Mission Mailbox.",
                cancellationToken);
        }

        var page = await _store.ReadAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT mailbox_message_id
                    FROM mailbox_messages
                    WHERE scope = 'mission'
                      AND mission_id = $missionId
                      AND ($status IS NULL OR status = $status)
                      AND ($memberId IS NULL OR
                           sender_member_id = $memberId OR
                           recipient_member_id = $memberId)
                    ORDER BY created_utc, mailbox_message_id
                    LIMIT $limit OFFSET $offset;
                    """;
                AddParameter(command, "$missionId", request.MissionId);
                AddParameter(
                    command,
                    "$status",
                    request.Status is null ? null : EnumText(request.Status.Value));
                AddParameter(
                    command,
                    "$memberId",
                    IsHost(request.Actor) ||
                    request.Actor.Kind == CoWorkActorKind.Leader
                        ? null
                        : request.Actor.MemberId);
                AddParameter(command, "$limit", request.PageSize + 1);
                AddParameter(command, "$offset", offset);
                var ids = new List<Guid>(request.PageSize + 1);
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    ids.Add(Guid.Parse(reader.GetString(0)));
                }

                await reader.DisposeAsync();
                var messages = new List<MailboxMessageSnapshot>(request.PageSize);
                foreach (var id in ids.Take(request.PageSize))
                {
                    messages.Add((await LoadMailboxMessageAsync(connection, id, token))!);
                }

                return new CoWorkPage<MailboxMessageSnapshot>(
                    messages,
                    ids.Count > request.PageSize
                        ? (offset + request.PageSize).ToString(
                            System.Globalization.CultureInfo.InvariantCulture)
                        : null);
            },
            cancellationToken);
        return Success(page, await ReadGlobalRevisionAsync(cancellationToken));
    }

    public async Task<CoWorkResult<MailboxMessageSnapshot>> SendMailboxMessageAsync(
        SendMailboxMessageRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = request.Body.Trim();
        if (body.Length == 0 ||
            Encoding.UTF8.GetByteCount(body) > _config.MaximumMailboxMessageBytes)
        {
            return await FailureAsync<MailboxMessageSnapshot>(
                CoWorkErrorCodes.InvalidState,
                "Mailbox message is empty or exceeds the configured limit.",
                cancellationToken);
        }

        if (ContainsSensitiveData(body))
        {
            return await FailureAsync<MailboxMessageSnapshot>(
                CoWorkErrorCodes.SecretDetected,
                "Mailbox message contains sensitive data.",
                cancellationToken);
        }

        var result = await ExecuteCommandAsync(
            request,
            request.Command,
            "sendMailboxMessage",
            request.MissionId.ToString(),
            async (connection, transaction, token) =>
            {
                var mission = await LoadMissionAsync(connection, request.MissionId, token)
                              ?? throw NotFound("Mission was not found.");
                RequireRevision(request.Command.ExpectedRevision, mission.Revision);
                var sender = RequireMailboxActor(mission, request.Command.Actor);
                var recipient = mission.Members.SingleOrDefault(member =>
                                    member.MemberId == request.RecipientId)
                                ?? throw NotFound("Mailbox recipient was not found.");
                if (sender.MemberId == recipient.MemberId ||
                    sender.Role == recipient.Role)
                {
                    throw PermissionDenied(
                        "Mission Mailbox only allows Leader and Member direct messages.");
                }

                if (request.TaskId is { } taskId &&
                    mission.Tasks.All(task => task.TaskId != taskId))
                {
                    throw NotFound("Referenced Mission Task was not found.");
                }

                if (request.ArtifactId is { } artifactId &&
                    await ScalarAsync<long>(
                        connection,
                        transaction,
                        """
                        SELECT count(*)
                        FROM cowork_files
                        WHERE cowork_file_id = $artifactId
                          AND mission_id = $missionId
                          AND kind = 'artifact';
                        """,
                        token,
                        ("$artifactId", artifactId),
                        ("$missionId", mission.MissionId)) == 0)
                {
                    throw NotFound("Referenced Artifact was not found.");
                }

                var messageId = Guid.CreateVersion7(_timeProvider.GetUtcNow());
                var now = UtcNowMilliseconds();
                await ExecuteSqlAsync(
                    connection,
                    transaction,
                    """
                    INSERT INTO mailbox_messages (
                        mailbox_message_id, mission_id, scope,
                        sender_member_id, recipient_member_id,
                        sender_thread_id, recipient_thread_id,
                        mission_task_id, artifact_id, message_kind,
                        content, content_length, status, attempt_count,
                        lease_owner, lease_expires_utc, error_code, diagnostic,
                        created_utc, delivered_utc, acknowledged_utc)
                    VALUES (
                        $id, $missionId, 'mission',
                        $senderId, $recipientId,
                        NULL, NULL,
                        $taskId, $artifactId, $kind,
                        $content, $length, 'pending', 0,
                        NULL, NULL, NULL, NULL,
                        $now, NULL, NULL);
                    """,
                    token,
                    ("$id", messageId),
                    ("$missionId", mission.MissionId),
                    ("$senderId", sender.MemberId),
                    ("$recipientId", recipient.MemberId),
                    ("$taskId", request.TaskId),
                    ("$artifactId", request.ArtifactId),
                    ("$kind", EnumText(request.Kind)),
                    ("$content", body),
                    ("$length", Encoding.UTF8.GetByteCount(body)),
                    ("$now", now));
                await InsertDispatchIntentAsync(
                    connection,
                    transaction,
                    CoWorkDispatchKind.DeliverMessage,
                    "mailboxMessage",
                    messageId,
                    request.Command.CommandId,
                    now,
                    token);
                return (await LoadMailboxMessageAsync(connection, messageId, token))!;
            },
            cancellationToken);
        if (result.IsSuccess)
        {
            WakeReconciler();
            await ReconcilePendingAsync(cancellationToken);
        }

        return result;
    }

    public async Task<CoWorkResult<MailboxMessageSnapshot>>
        AcknowledgeMailboxMessageAsync(
            MailboxMessageCommandRequest request,
            CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await ExecuteCommandAsync(
            request,
            request.Command,
            "acknowledgeMailboxMessage",
            request.MessageId.ToString(),
            async (connection, transaction, token) =>
            {
                var message = await LoadMailboxMessageAsync(
                                  connection,
                                  request.MessageId,
                                  token)
                              ?? throw NotFound("Mailbox message was not found.");
                if (message.Scope != CoWorkMailboxScope.Mission ||
                    message.MissionId is not { } missionId)
                {
                    throw NotFound("Mission Mailbox message was not found.");
                }

                var mission = await LoadMissionAsync(connection, missionId, token)
                              ?? throw NotFound("Mission was not found.");
                RequireRevision(request.Command.ExpectedRevision, mission.Revision);
                var actor = RequireMailboxActor(mission, request.Command.Actor);
                if (actor.MemberId != message.RecipientId)
                {
                    throw PermissionDenied("Only the recipient can acknowledge this message.");
                }

                if (message.Status == CoWorkMailboxStatus.Acknowledged)
                {
                    return message;
                }

                if (message.Status != CoWorkMailboxStatus.Delivered)
                {
                    throw InvalidState("Only delivered messages can be acknowledged.");
                }

                await ExecuteSqlAsync(
                    connection,
                    transaction,
                    """
                    UPDATE mailbox_messages
                    SET status = 'acknowledged',
                        acknowledged_utc = $now,
                        error_code = NULL,
                        diagnostic = NULL
                    WHERE mailbox_message_id = $id;
                    """,
                    token,
                    ("$now", UtcNowMilliseconds()),
                    ("$id", message.MessageId));
                return (await LoadMailboxMessageAsync(connection, message.MessageId, token))!;
            },
            cancellationToken);
    }

    public async Task<CoWorkResult<MailboxMessageSnapshot>> RetryMailboxMessageAsync(
        MailboxMessageCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await ExecuteCommandAsync(
            request,
            request.Command,
            "retryMailboxMessage",
            request.MessageId.ToString(),
            async (connection, transaction, token) =>
            {
                var message = await LoadMailboxMessageAsync(
                                  connection,
                                  request.MessageId,
                                  token)
                              ?? throw NotFound("Mailbox message was not found.");
                if (message.Scope != CoWorkMailboxScope.Mission ||
                    message.MissionId is not { } missionId)
                {
                    throw NotFound("Mission Mailbox message was not found.");
                }

                var mission = await LoadMissionAsync(connection, missionId, token)
                              ?? throw NotFound("Mission was not found.");
                RequireRevision(request.Command.ExpectedRevision, mission.Revision);
                RequireMissionManager(mission, request.Command.Actor);
                if (message.Status != CoWorkMailboxStatus.DeadLettered)
                {
                    throw InvalidState("Only dead-lettered messages can be retried.");
                }

                var now = UtcNowMilliseconds();
                await ExecuteSqlAsync(
                    connection,
                    transaction,
                    """
                    UPDATE mailbox_messages
                    SET status = 'pending',
                        attempt_count = 0,
                        error_code = NULL,
                        diagnostic = NULL,
                        delivered_utc = NULL,
                        acknowledged_utc = NULL
                    WHERE mailbox_message_id = $messageId;

                    UPDATE cowork_dispatch_intents
                    SET status = 'pending',
                        attempt_count = 0,
                        lease_owner = NULL,
                        lease_expires_utc = NULL,
                        error_code = NULL,
                        diagnostic = NULL,
                        updated_utc = $now
                    WHERE entity_kind = 'mailboxMessage'
                      AND entity_id = $messageId;
                    """,
                    token,
                    ("$messageId", message.MessageId),
                    ("$now", now));
                return (await LoadMailboxMessageAsync(connection, message.MessageId, token))!;
            },
            cancellationToken);
        if (!result.IsSuccess)
        {
            return result;
        }

        WakeReconciler();
        await ReconcilePendingAsync(cancellationToken);
        var current = await ReadMailboxMessageAsync(request.MessageId, cancellationToken);
        return result with { Value = current };
    }

    private static MissionMemberSnapshot RequireMailboxActor(
        MissionSnapshot mission,
        CoWorkActorContext actor)
    {
        var member = mission.Members.SingleOrDefault(candidate =>
            candidate.MemberId == actor.MemberId);
        var valid = actor.MissionId == mission.MissionId &&
                    !string.IsNullOrWhiteSpace(actor.PrincipalId) &&
                    (actor.Kind, member?.Role) is
                    (CoWorkActorKind.Leader, CoWorkMemberRole.Leader) or
                    (CoWorkActorKind.Member, CoWorkMemberRole.Member);
        return valid
            ? member!
            : throw PermissionDenied("Actor is not a Mission Mailbox participant.");
    }
}
