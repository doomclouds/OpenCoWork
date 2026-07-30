using System.Data.Common;
using OpenCoWork.Abstractions;
using OpenCoWork.Teams;
using Xunit;

namespace OpenCoWork.IntegrationTests;

public sealed class CoWorkBudgetRaceTests
{
    [Fact]
    public async Task Sixteen_concurrent_runs_reserve_budget_and_the_next_run_is_rejected()
    {
        await using var workspace = await CoWorkTestWorkspace.CreateAsync(
            new CoWorkConfig
            {
                MaxConcurrentAgentRuns = 16,
                MaxConcurrentAgentRunsPerMission = 4,
            },
            completeAgentRuns: false);
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = (await workspace.Service.UpsertAgentProfileAsync(
            new UpsertAgentProfileRequest(
                Command(),
                null,
                "race",
                "",
                "Wait for input.",
                "fake",
                "fake-model",
                [],
                []),
            cancellationToken)).Value!;

        var starts = Enumerable.Range(0, 16)
            .Select(index => workspace.Service.SpawnSubAgentAsync(
                new SpawnSubAgentRequest(
                    Command(),
                    workspace.OriginThreadId,
                    profile.ProfileId,
                    $"Run {index}.",
                    2_000,
                    CoWorkWorkspaceMode.Project),
                cancellationToken))
            .ToArray();
        var results = await Task.WhenAll(starts);

        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Equal(16, results.Select(result => result.Value!.AgentRunId).Distinct().Count());

        var overflow = await workspace.Service.SpawnSubAgentAsync(
            new SpawnSubAgentRequest(
                Command(),
                workspace.OriginThreadId,
                profile.ProfileId,
                "Overflow.",
                2_000,
                CoWorkWorkspaceMode.Project),
            cancellationToken);
        Assert.Equal(CoWorkErrorCodes.ConcurrencyExceeded, overflow.Error?.Code);

        var scopes = await workspace.Store.ReadAsync(
            (connection, token) => ScalarAsync<long>(
                connection,
                """
                SELECT count(*)
                FROM cowork_budget_scopes
                WHERE reserved_tokens > 0
                  AND used_tokens = 0;
                """,
                token),
            cancellationToken);
        Assert.Equal(16, scopes);
    }

    [Fact]
    public async Task Followup_reuses_and_settles_the_root_budget_scope()
    {
        await using var workspace = await CoWorkTestWorkspace.CreateAsync();
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = (await workspace.Service.UpsertAgentProfileAsync(
            new UpsertAgentProfileRequest(
                Command(),
                null,
                "shared-budget",
                "",
                "Finish immediately.",
                "fake",
                "fake-model",
                [],
                []),
            cancellationToken)).Value!;
        var first = await workspace.Service.SpawnSubAgentAsync(
            new SpawnSubAgentRequest(
                Command(),
                workspace.OriginThreadId,
                profile.ProfileId,
                "First.",
                5_000,
                CoWorkWorkspaceMode.Project),
            cancellationToken);
        await WaitForIdleAsync(
            workspace,
            first.Value!.ThreadId,
            cancellationToken);
        var second = await workspace.Service.FollowUpSubAgentAsync(
            new FollowUpSubAgentRequest(
                Command(),
                first.Value!.ThreadId,
                "Second."),
            cancellationToken);

        Assert.True(second.IsSuccess, second.Error?.ToString());
        Assert.Equal(first.Value.BudgetScopeId, second.Value!.BudgetScopeId);
        await WaitForIdleAsync(
            workspace,
            first.Value.ThreadId,
            cancellationToken);
        var budget = await workspace.Store.ReadAsync(
            (connection, token) => ReadBudgetAsync(
                connection,
                first.Value.BudgetScopeId,
                first.Value.ThreadId,
                token),
            cancellationToken);
        Assert.Equal(0, budget.Reserved);
        Assert.Equal(budget.RunUsed, budget.Used);
        Assert.True(budget.Used > 0);
        Assert.True(budget.Used <= budget.Limit);
    }

    private static CoWorkCommandContext Command() =>
        new(Guid.CreateVersion7(), CoWorkTestWorkspace.Host, ExpectedRevision: null);

    private static async Task WaitForIdleAsync(
        CoWorkTestWorkspace workspace,
        Guid childThreadId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 1_000; attempt++)
        {
            var children = await workspace.Service.ListSubAgentChildrenAsync(
                new SubAgentQueryRequest(
                    CoWorkTestWorkspace.Host,
                    workspace.OriginThreadId,
                    childThreadId),
                cancellationToken);
            if (Assert.Single(children.Value!.Items).ActiveRun is null)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }

        throw new TimeoutException("Direct SubAgent did not become idle.");
    }

    private static async ValueTask<T> ScalarAsync<T>(
        DbConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidDataException("Scalar query returned null.");
        return (T)Convert.ChangeType(
            value,
            typeof(T),
            System.Globalization.CultureInfo.InvariantCulture)!;
    }

    private static async ValueTask<Budget> ReadBudgetAsync(
        DbConnection connection,
        Guid scopeId,
        Guid childThreadId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT limit_tokens, reserved_tokens, used_tokens,
                   (SELECT sum(budget_used_tokens)
                    FROM agent_runs
                    WHERE thread_id = $threadId)
            FROM cowork_budget_scopes
            WHERE scope_id = $scopeId;
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$scopeId";
        parameter.Value = scopeId.ToString();
        command.Parameters.Add(parameter);
        var threadParameter = command.CreateParameter();
        threadParameter.ParameterName = "$threadId";
        threadParameter.Value = childThreadId.ToString();
        command.Parameters.Add(threadParameter);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        Assert.True(await reader.ReadAsync(cancellationToken));
        return new Budget(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3));
    }

    private sealed record Budget(long Limit, long Reserved, long Used, long RunUsed);
}
