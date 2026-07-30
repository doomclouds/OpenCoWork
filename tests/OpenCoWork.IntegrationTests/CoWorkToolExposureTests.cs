using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenCoWork.Abstractions;
using OpenCoWork.App;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Tools;
using Xunit;

namespace OpenCoWork.IntegrationTests;

public sealed class CoWorkToolExposureTests
{
    [Fact]
    public void Deferred_tools_are_narrowed_by_persisted_cowork_role()
    {
        var workspace = Directory.CreateTempSubdirectory("ocw-cowork-tools-");
        try
        {
            using var host = OpenCoWorkCompositionRoot.Build(
                [],
                workspace.FullName);
            var runtime = host.Services.GetRequiredService<ToolRuntime>();
            var config = new ToolsConfig();
            var direct = runtime.BuildSnapshot(
                AgentMode.Agent,
                config,
                Thread());

            Assert.Equal(
                [
                    "subagent.cancel",
                    "subagent.followup",
                    "subagent.list",
                    "subagent.send",
                    "subagent.spawn",
                ],
                CoWorkNames(direct));
            Assert.Equal(
                [
                    "artifact.manage",
                    "mailbox.manage",
                    "mission.manage",
                    "mission.review",
                    "mission.task",
                ],
                CoWorkNames(runtime.BuildSnapshot(
                    AgentMode.Agent,
                    config,
                    Thread(CoWorkAgentRunKind.LeaderPlanning))));
            Assert.Equal(
                [
                    "artifact.manage",
                    "mailbox.manage",
                    "mission.task",
                ],
                CoWorkNames(runtime.BuildSnapshot(
                    AgentMode.Agent,
                    config,
                    Thread(CoWorkAgentRunKind.MissionTask))));
            Assert.All(
                host.Services
                    .GetRequiredService<ToolRegistrationContribution>()
                    .Registrations,
                registration =>
                {
                    Assert.Equal(ToolExposure.Deferred, registration.Exposure);
                    Assert.Equal(
                        (ToolInvocationAudience)0,
                        registration.Audience & ToolInvocationAudience.Host);
                });
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Contextual_binding_rejects_a_member_using_a_leader_tool()
    {
        var workspace = Directory.CreateTempSubdirectory("ocw-cowork-tools-");
        try
        {
            using var host = OpenCoWorkCompositionRoot.Build(
                [],
                workspace.FullName);
            var runtime = host.Services.GetRequiredService<ToolRuntime>();
            var thread = Thread(CoWorkAgentRunKind.MissionTask);
            var snapshot = runtime.BuildSnapshot(
                AgentMode.Agent,
                new ToolsConfig(),
                thread);
            var contribution = host.Services
                .GetRequiredService<ToolRegistrationContribution>();
            var binding = Assert.Single(contribution.Bindings, item =>
                item.Id.Value == "cowork.mission.review");
            using var arguments = JsonDocument.Parse("{}");
            var result = await binding.ContextualExecutor!(
                new ToolInvocationContext(
                    thread.ThreadId,
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    0,
                    "call-1",
                    "mission__review",
                    arguments.RootElement,
                    new string('0', 64),
                    SensitiveInputDetected: false,
                    snapshot,
                    CoWorkProvenance: thread.CoWorkProvenance),
                TestContext.Current.CancellationToken);

            Assert.Equal(CoWorkErrorCodes.PermissionDenied, result.Error?.Code);
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }

    private static IReadOnlyList<string> CoWorkNames(
        EffectiveToolSnapshot snapshot) =>
        snapshot.Registrations
            .Where(item => item.Definition.Id.SourceId == "opencowork.teams")
            .Select(item =>
                $"{item.Definition.Name.Namespace}.{item.Definition.Name.Name}")
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static ThreadSnapshot Thread(CoWorkAgentRunKind? kind = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new ThreadSnapshot(
            Guid.NewGuid(),
            "test",
            ThreadStatus.Active,
            ThreadAvailability.Available,
            HistoryMode.Server,
            0,
            activeTurnId: null,
            [],
            now,
            now,
            SessionProjectionState.Ready,
            diagnostic: null,
            coWorkProvenance: kind is { } runKind
                ? new CoWorkThreadProvenance(
                    Guid.NewGuid(),
                    runKind,
                    Guid.NewGuid(),
                    Guid.NewGuid(),
                    Guid.NewGuid())
                : null);
    }
}
