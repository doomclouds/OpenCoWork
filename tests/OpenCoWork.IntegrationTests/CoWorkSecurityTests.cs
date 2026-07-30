using OpenCoWork.Abstractions;
using Xunit;

namespace OpenCoWork.IntegrationTests;

public sealed class CoWorkSecurityTests
{
    [Fact]
    public async Task Artifact_publish_blocks_secrets_traversal_links_and_foreign_scratchpads()
    {
        const string secret = "artifact-secret-canary";
        await using var workspace = await CoWorkTestWorkspace.CreateAsync(secret: secret);
        var token = TestContext.Current.CancellationToken;
        var (mission, leader, run) = await CoWorkFileTestData.CreateLeaderRunAsync(
            workspace,
            token);

        var secretPath = Path.Combine(run.Workspace.WorkspaceRoot, "secret.txt");
        await File.WriteAllTextAsync(secretPath, secret, token);
        var secretResult = await PublishAsync(
            workspace,
            mission,
            leader,
            run,
            CoWorkFileArea.Workspace,
            "secret.txt",
            token);
        Assert.Equal(CoWorkErrorCodes.SecretDetected, secretResult.Error?.Code);

        var outsidePath = Path.Combine(workspace.Root, "..", $"outside-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(outsidePath, "outside", token);
        try
        {
            var traversal = await PublishAsync(
                workspace,
                mission,
                leader,
                run,
                CoWorkFileArea.Workspace,
                Path.GetRelativePath(run.Workspace.WorkspaceRoot, outsidePath),
                token);
            Assert.Equal(CoWorkErrorCodes.PathEscape, traversal.Error?.Code);
        }
        finally
        {
            File.Delete(outsidePath);
        }

        if (!OperatingSystem.IsWindows())
        {
            var target = Path.Combine(run.Workspace.WorkspaceRoot, "target.txt");
            var link = Path.Combine(run.Workspace.WorkspaceRoot, "link.txt");
            await File.WriteAllTextAsync(target, "safe", token);
            File.CreateSymbolicLink(link, target);
            var linked = await PublishAsync(
                workspace,
                mission,
                leader,
                run,
                CoWorkFileArea.Workspace,
                "link.txt",
                token);
            Assert.Equal(CoWorkErrorCodes.PathEscape, linked.Error?.Code);
        }

        Directory.CreateDirectory(run.Workspace.ScratchpadRoot);
        await File.WriteAllTextAsync(
            Path.Combine(run.Workspace.ScratchpadRoot, "private.txt"),
            "private",
            token);
        var foreignMember = mission.Members.Single(member =>
            member.Role == CoWorkMemberRole.Member);
        var foreignActor = new CoWorkActorContext(
            CoWorkActorKind.Member,
            "foreign-member",
            MissionId: mission.MissionId,
            MemberId: foreignMember.MemberId);
        var privateResult = await PublishAsync(
            workspace,
            mission,
            foreignActor,
            run,
            CoWorkFileArea.Scratchpad,
            "private.txt",
            token);
        Assert.Equal(CoWorkErrorCodes.PermissionDenied, privateResult.Error?.Code);
    }

    private static Task<CoWorkResult<ArtifactSnapshot>> PublishAsync(
        CoWorkTestWorkspace workspace,
        MissionSnapshot mission,
        CoWorkActorContext actor,
        CoWorkFileTestData.Run run,
        CoWorkFileArea area,
        string relativePath,
        CancellationToken cancellationToken) =>
        workspace.Service.PublishArtifactAsync(
            new PublishArtifactRequest(
                CoWorkFileTestData.Command(actor, mission.Revision),
                mission.MissionId,
                run.AgentRunId,
                area,
                relativePath,
                "artifact",
                "text/plain"),
            cancellationToken);
}
