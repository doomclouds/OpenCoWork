using System.Data.Common;
using OpenCoWork.Abstractions;
using OpenCoWork.Teams;
using Xunit;

namespace OpenCoWork.IntegrationTests;

public sealed class CoWorkDispatchFaultTests
{
    [Fact]
    public async Task Replayed_spawn_has_one_completed_dispatch_chain()
    {
        await using var workspace = await CoWorkTestWorkspace.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = (await workspace.Service.UpsertAgentProfileAsync(
            new UpsertAgentProfileRequest(
                Command(),
                null,
                "dispatch",
                "",
                "Be deterministic.",
                "fake",
                "fake-model",
                [],
                []),
            cancellationToken)).Value!;
        var request = new SpawnSubAgentRequest(
            Command(),
            workspace.OriginThreadId,
            profile.ProfileId,
            "Exercise dispatch.",
            4_000,
            CoWorkWorkspaceMode.Project);

        var first = await workspace.Service.SpawnSubAgentAsync(request, cancellationToken);
        var replay = await workspace.Service.SpawnSubAgentAsync(request, cancellationToken);

        Assert.True(first.IsSuccess);
        Assert.Equivalent(first.Value, replay.Value, strict: true);
        var intents = await workspace.Store.ReadAsync(
            (connection, token) => ReadIntentCountsAsync(
                connection,
                first.Value!.AgentRunId,
                token),
            cancellationToken);
        Assert.Equal(2, intents.Total);
        Assert.Equal(2, intents.Completed);
        Assert.Equal(0, intents.DeadLettered);
        Assert.Equal(2, intents.TotalAttempts);
    }

    [Theory]
    [InlineData((int)CoWorkDispatchFaultPoint.BeforeCreateThread)]
    [InlineData((int)CoWorkDispatchFaultPoint.AfterCreateThread)]
    [InlineData((int)CoWorkDispatchFaultPoint.BeforeSubmitTurn)]
    [InlineData((int)CoWorkDispatchFaultPoint.AfterSubmitTurn)]
    public async Task Expired_lease_replays_unknown_session_outcome_once(
        int faultValue)
    {
        var faultPoint = (CoWorkDispatchFaultPoint)faultValue;
        var time = new MutableTimeProvider(
            new DateTimeOffset(2026, 7, 30, 0, 0, 0, TimeSpan.Zero));
        var injected = false;
        await using var workspace = await CoWorkTestWorkspace.CreateAsync(
            timeProvider: time,
            dispatchFaultInjector: point =>
            {
                if (!injected && point == faultPoint)
                {
                    injected = true;
                    throw new CoWorkDispatchCrashException(point);
                }
            });
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = (await workspace.Service.UpsertAgentProfileAsync(
            new UpsertAgentProfileRequest(
                Command(),
                null,
                $"fault-{faultPoint}",
                "",
                "Be deterministic.",
                "fake",
                "fake-model",
                [],
                []),
            cancellationToken)).Value!;
        var request = new SpawnSubAgentRequest(
            Command(),
            workspace.OriginThreadId,
            profile.ProfileId,
            "Recover exactly once.",
            4_000,
            CoWorkWorkspaceMode.Project);

        await Assert.ThrowsAsync<CoWorkDispatchCrashException>(
            () => workspace.Service.SpawnSubAgentAsync(request, cancellationToken));
        time.Advance(CoWorkRuntimeLimits.DispatchLease + TimeSpan.FromSeconds(1));

        var recovered = await workspace.ReplaceService()
            .SpawnSubAgentAsync(request, cancellationToken);

        Assert.True(recovered.IsSuccess, recovered.Error?.ToString());
        var counts = await workspace.Store.ReadAsync(
            (connection, token) => ReadRecoveryCountsAsync(
                connection,
                recovered.Value!.AgentRunId,
                token),
            cancellationToken);
        Assert.Equal(2, counts.Intents);
        Assert.Equal(2, counts.CompletedIntents);
        Assert.Equal(3, counts.IntentAttempts);
        Assert.Equal(1, counts.Threads);
        Assert.Equal(1, counts.Turns);
    }

    [Fact]
    public async Task Transient_dispatch_is_dead_lettered_after_five_attempts()
    {
        await using var workspace = await CoWorkTestWorkspace.CreateAsync(
            dispatchFaultInjector: point =>
            {
                if (point == CoWorkDispatchFaultPoint.BeforeCreateThread)
                {
                    throw new IOException("transient");
                }
            });
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = (await workspace.Service.UpsertAgentProfileAsync(
            new UpsertAgentProfileRequest(
                Command(),
                null,
                "dead-letter",
                "",
                "Be deterministic.",
                "fake",
                "fake-model",
                [],
                []),
            cancellationToken)).Value!;

        var result = await workspace.Service.SpawnSubAgentAsync(
            new SpawnSubAgentRequest(
                Command(),
                workspace.OriginThreadId,
                profile.ProfileId,
                "Exhaust retries.",
                4_000,
                CoWorkWorkspaceMode.Project),
            cancellationToken);

        Assert.Equal(CoWorkErrorCodes.RetryExhausted, result.Error?.Code);
        var deadLetter = await workspace.Store.ReadAsync(
            (connection, token) => ReadDeadLetterAsync(connection, token),
            cancellationToken);
        Assert.Equal(5, deadLetter.Attempts);
        Assert.Equal(CoWorkErrorCodes.RetryExhausted, deadLetter.ErrorCode);
    }

    private static CoWorkCommandContext Command() =>
        new(Guid.CreateVersion7(), CoWorkTestWorkspace.Host, ExpectedRevision: null);

    private static async ValueTask<IntentCounts> ReadIntentCountsAsync(
        DbConnection connection,
        Guid agentRunId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT count(*),
                   sum(CASE WHEN status = 'completed' THEN 1 ELSE 0 END),
                   sum(CASE WHEN status = 'deadLettered' THEN 1 ELSE 0 END),
                   sum(attempt_count)
            FROM cowork_dispatch_intents
            WHERE entity_kind = 'agentRun'
              AND entity_id = $agentRunId;
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$agentRunId";
        parameter.Value = agentRunId.ToString();
        command.Parameters.Add(parameter);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        return new IntentCounts(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3));
    }

    private static async ValueTask<RecoveryCounts> ReadRecoveryCountsAsync(
        DbConnection connection,
        Guid agentRunId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                (SELECT count(*)
                 FROM cowork_dispatch_intents
                 WHERE entity_kind = 'agentRun'
                   AND entity_id = $agentRunId),
                (SELECT count(*)
                 FROM cowork_dispatch_intents
                 WHERE entity_kind = 'agentRun'
                   AND entity_id = $agentRunId
                   AND status = 'completed'),
                (SELECT sum(attempt_count)
                 FROM cowork_dispatch_intents
                 WHERE entity_kind = 'agentRun'
                   AND entity_id = $agentRunId),
                (SELECT count(*)
                 FROM threads
                 WHERE cowork_provenance_json LIKE '%' || $agentRunId || '%'),
                (SELECT count(*)
                 FROM turns
                 WHERE thread_id = (
                     SELECT thread_id
                     FROM agent_runs
                     WHERE agent_run_id = $agentRunId));
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$agentRunId";
        parameter.Value = agentRunId.ToString();
        command.Parameters.Add(parameter);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        return new RecoveryCounts(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4));
    }

    private static async ValueTask<DeadLetter> ReadDeadLetterAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT attempt_count, error_code
            FROM cowork_dispatch_intents
            WHERE status = 'deadLettered'
            ORDER BY created_utc
            LIMIT 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        return new DeadLetter(reader.GetInt32(0), reader.GetString(1));
    }

    private sealed record IntentCounts(
        long Total,
        long Completed,
        long DeadLettered,
        long TotalAttempts);

    private sealed record RecoveryCounts(
        long Intents,
        long CompletedIntents,
        long IntentAttempts,
        long Threads,
        long Turns);

    private sealed record DeadLetter(int Attempts, string ErrorCode);

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan value) => _utcNow += value;
    }
}
