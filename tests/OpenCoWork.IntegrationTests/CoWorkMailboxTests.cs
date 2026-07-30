using System.Data.Common;
using System.Text;
using OpenCoWork.Abstractions;
using OpenCoWork.Teams;
using Xunit;

namespace OpenCoWork.IntegrationTests;

public sealed class CoWorkMailboxTests
{
    [Fact]
    public async Task Six_message_kinds_are_durable_bounded_and_acknowledged()
    {
        await using var workspace = await CoWorkTestWorkspace.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        var setup = await MissionTestData.CreateAsync(
            workspace,
            CoWorkWorkspaceMode.Project,
            10_000,
            ("leader", CoWorkMemberRole.Leader, Array.Empty<string>()),
            ("member", CoWorkMemberRole.Member, Array.Empty<string>()));
        await workspace.Service.ReconcilePendingAsync(token);
        var mission = await MissionTestData.GetMissionAsync(
            workspace,
            setup.Mission.MissionId,
            token);
        var leader = Actor(setup, "leader", CoWorkActorKind.Leader);
        var member = Actor(setup, "member", CoWorkActorKind.Member);
        var messages = new List<MailboxMessageSnapshot>();

        foreach (var kind in Enum.GetValues<CoWorkMailboxKind>())
        {
            var sent = await workspace.Service.SendMailboxMessageAsync(
                new SendMailboxMessageRequest(
                    Command(member, mission.Revision),
                    mission.MissionId,
                    setup.Members["leader"].MemberId,
                    kind,
                    $"message-{kind}"),
                token);
            Assert.True(sent.IsSuccess);
            messages.Add(sent.Value!);
        }

        var listed = await workspace.Service.ListMailboxMessagesAsync(
            new ListMailboxMessagesRequest(leader, mission.MissionId),
            token);
        Assert.True(listed.IsSuccess);
        Assert.Equal(6, listed.Value!.Items.Count);
        Assert.Equal(
            Enum.GetValues<CoWorkMailboxKind>().Order(),
            listed.Value.Items.Select(message => message.Kind).Order());

        var acknowledgement = new MailboxMessageCommandRequest(
            Command(leader, mission.Revision),
            messages[0].MessageId);
        var acknowledged = await workspace.Service.AcknowledgeMailboxMessageAsync(
            acknowledgement,
            token);
        var replay = await workspace.Service.AcknowledgeMailboxMessageAsync(
            acknowledgement,
            token);
        Assert.Equal(CoWorkMailboxStatus.Acknowledged, acknowledged.Value!.Status);
        Assert.Equivalent(acknowledged.Value, replay.Value, strict: true);

        var oversized = await workspace.Service.SendMailboxMessageAsync(
            new SendMailboxMessageRequest(
                Command(member, mission.Revision),
                mission.MissionId,
                setup.Members["leader"].MemberId,
                CoWorkMailboxKind.Info,
                new string('x', CoWorkRuntimeLimits.MaximumMailboxMessageBytes + 1)),
            token);
        Assert.Equal(CoWorkErrorCodes.InvalidState, oversized.Error?.Code);

        var memberToMember = await workspace.Service.SendMailboxMessageAsync(
            new SendMailboxMessageRequest(
                Command(member, mission.Revision),
                mission.MissionId,
                setup.Members["member"].MemberId,
                CoWorkMailboxKind.Info,
                "not allowed"),
            token);
        Assert.Equal(CoWorkErrorCodes.PermissionDenied, memberToMember.Error?.Code);
    }

    [Fact]
    public async Task Delivery_is_at_least_once_dead_letters_after_five_attempts_and_can_retry()
    {
        var failDelivery = true;
        await using var workspace = await CoWorkTestWorkspace.CreateAsync(
            dispatchFaultInjector: point =>
            {
                if (failDelivery && point == CoWorkDispatchFaultPoint.BeforeDeliverMessage)
                {
                    throw new IOException("transient");
                }
            });
        var token = TestContext.Current.CancellationToken;
        var setup = await MissionTestData.CreateAsync(
            workspace,
            CoWorkWorkspaceMode.Project,
            10_000,
            ("leader", CoWorkMemberRole.Leader, Array.Empty<string>()),
            ("member", CoWorkMemberRole.Member, Array.Empty<string>()));
        await workspace.Service.ReconcilePendingAsync(token);
        var mission = await MissionTestData.GetMissionAsync(
            workspace,
            setup.Mission.MissionId,
            token);
        var member = Actor(setup, "member", CoWorkActorKind.Member);
        var leader = Actor(setup, "leader", CoWorkActorKind.Leader);

        var sent = await workspace.Service.SendMailboxMessageAsync(
            new SendMailboxMessageRequest(
                Command(member, mission.Revision),
                mission.MissionId,
                setup.Members["leader"].MemberId,
                CoWorkMailboxKind.Request,
                "retry me"),
            token);
        Assert.True(sent.IsSuccess);

        var deadLettered = await workspace.Service.ListMailboxMessagesAsync(
            new ListMailboxMessagesRequest(
                leader,
                mission.MissionId,
                CoWorkMailboxStatus.DeadLettered),
            token);
        var message = Assert.Single(deadLettered.Value!.Items);
        Assert.Equal(CoWorkRuntimeLimits.DispatchAttempts, message.Attempt);
        Assert.Equal(CoWorkErrorCodes.RetryExhausted, message.ErrorCode);

        failDelivery = false;
        var retried = await workspace.Service.RetryMailboxMessageAsync(
            new MailboxMessageCommandRequest(
                Command(leader, mission.Revision),
                message.MessageId),
            token);
        Assert.True(retried.IsSuccess);
        Assert.Equal(CoWorkMailboxStatus.Delivered, retried.Value!.Status);
        Assert.Equal(1, retried.Value.Attempt);

        var deliveries = await CountAsync(
            workspace.Store,
            """
            SELECT count(*)
            FROM session_idempotency
            WHERE idempotency_key = (
                SELECT intent_id
                FROM cowork_dispatch_intents
                WHERE entity_kind = 'mailboxMessage' AND entity_id = $messageId
                ORDER BY created_utc DESC
                LIMIT 1);
            """,
            token,
            ("$messageId", message.MessageId));
        Assert.Equal(1, deliveries);
    }

    private static CoWorkActorContext Actor(
        MissionTestData.Setup setup,
        string alias,
        CoWorkActorKind kind) =>
        new(
            kind,
            $"thread-{alias}",
            MissionId: setup.Mission.MissionId,
            MemberId: setup.Members[alias].MemberId);

    private static CoWorkCommandContext Command(
        CoWorkActorContext actor,
        long revision) =>
        new(Guid.CreateVersion7(), actor, revision);

    private static ValueTask<long> CountAsync(
        IWorkspaceStateStore store,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters) =>
        store.ReadAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                foreach (var (name, value) in parameters)
                {
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = name;
                    parameter.Value = value is Guid guid ? guid.ToString("D") : value ?? DBNull.Value;
                    command.Parameters.Add(parameter);
                }

                return Convert.ToInt64(
                    await command.ExecuteScalarAsync(token),
                    System.Globalization.CultureInfo.InvariantCulture);
            },
            cancellationToken);
}
