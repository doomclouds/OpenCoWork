using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Workspaces;
using Xunit;

namespace OpenCoWork.IntegrationTests;

public sealed class CoWorkArtifactTests
{
    [Fact]
    public async Task Publish_hashes_deduplicates_promotes_and_detects_tampering()
    {
        await using var workspace = await CoWorkTestWorkspace.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        var (mission, leader, run) = await CoWorkFileTestData.CreateLeaderRunAsync(
            workspace,
            token);
        const string content = "artifact-content";
        var sourcePath = Path.Combine(run.Workspace.WorkspaceRoot, "artifact.txt");
        await File.WriteAllTextAsync(sourcePath, content, token);

        var first = await PublishAsync(
            workspace,
            mission,
            leader,
            run,
            "artifact.txt",
            token);
        var second = await PublishAsync(
            workspace,
            mission,
            leader,
            run,
            "artifact.txt",
            token);
        Assert.True(first.IsSuccess);
        Assert.Equal(first.Value!.ArtifactId, second.Value!.ArtifactId);
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))
                .ToLowerInvariant(),
            first.Value.Sha256);

        var promoted = await workspace.Service.PromoteArtifactAsync(
            new PromoteArtifactRequest(
                CoWorkFileTestData.Command(leader, mission.Revision),
                first.Value.ArtifactId),
            token);
        Assert.Equal(CoWorkArtifactVisibility.Origin, promoted.Value!.Visibility);

        var storedPath = CoWorkFileTestData.ArtifactPath(workspace, mission, first.Value);
        await File.WriteAllTextAsync(storedPath, "tampered", token);
        var unavailable = await workspace.Service.GetArtifactAsync(
            new GetArtifactRequest(leader, first.Value.ArtifactId),
            token);
        Assert.Equal(CoWorkErrorCodes.ArtifactUnavailable, unavailable.Error?.Code);

        File.Delete(storedPath);
        var listed = await workspace.Service.ListArtifactsAsync(
            new ListArtifactsRequest(leader, mission.MissionId),
            token);
        Assert.Equal(CoWorkArtifactStatus.Unavailable, Assert.Single(listed.Value!.Items).Status);
    }

    [Fact]
    public async Task Publish_accepts_64_mib_and_enforces_512_mib_mission_limit()
    {
        await using var workspace = await CoWorkTestWorkspace.CreateAsync();
        var token = TestContext.Current.CancellationToken;
        var (mission, leader, run) = await CoWorkFileTestData.CreateLeaderRunAsync(
            workspace,
            token);
        var sourcePath = Path.Combine(run.Workspace.WorkspaceRoot, "limit.bin");
        await using (var source = new FileStream(
                         sourcePath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None))
        {
            source.SetLength(CoWorkRuntimeLimits.MaximumArtifactBytes);
        }

        await CoWorkFileTestData.SeedOwnedBytesAsync(
            workspace,
            mission,
            run,
            7 * CoWorkRuntimeLimits.MaximumArtifactBytes,
            token);
        var accepted = await PublishAsync(
            workspace,
            mission,
            leader,
            run,
            "limit.bin",
            token);
        Assert.True(accepted.IsSuccess);
        Assert.Equal(CoWorkRuntimeLimits.MaximumArtifactBytes, accepted.Value!.Bytes);

        await File.WriteAllTextAsync(
            Path.Combine(run.Workspace.WorkspaceRoot, "overflow.txt"),
            "x",
            token);
        var rejected = await PublishAsync(
            workspace,
            mission,
            leader,
            run,
            "overflow.txt",
            token);
        Assert.Equal(CoWorkErrorCodes.InvalidState, rejected.Error?.Code);
    }

    private static Task<CoWorkResult<ArtifactSnapshot>> PublishAsync(
        CoWorkTestWorkspace workspace,
        MissionSnapshot mission,
        CoWorkActorContext actor,
        CoWorkFileTestData.Run run,
        string path,
        CancellationToken cancellationToken) =>
        workspace.Service.PublishArtifactAsync(
            new PublishArtifactRequest(
                CoWorkFileTestData.Command(actor, mission.Revision),
                mission.MissionId,
                run.AgentRunId,
                CoWorkFileArea.Workspace,
                path,
                Path.GetFileName(path),
                "application/octet-stream"),
            cancellationToken);
}

internal static class CoWorkFileTestData
{
    internal sealed record Run(
        Guid AgentRunId,
        Guid MemberId,
        ExecutionWorkspaceDescriptor Workspace);

    internal static CoWorkCommandContext Command(
        CoWorkActorContext actor,
        long revision) =>
        new(Guid.CreateVersion7(), actor, revision);

    internal static async Task<(MissionSnapshot Mission, CoWorkActorContext Leader, Run Run)>
        CreateLeaderRunAsync(
            CoWorkTestWorkspace workspace,
            CancellationToken cancellationToken)
    {
        var setup = await MissionTestData.CreateAsync(
            workspace,
            CoWorkWorkspaceMode.Project,
            20_000,
            ("leader", CoWorkMemberRole.Leader, Array.Empty<string>()),
            ("member", CoWorkMemberRole.Member, Array.Empty<string>()));
        await workspace.Service.ReconcilePendingAsync(cancellationToken);
        var mission = await MissionTestData.GetMissionAsync(
            workspace,
            setup.Mission.MissionId,
            cancellationToken);
        var member = setup.Members["leader"];
        var actor = new CoWorkActorContext(
            CoWorkActorKind.Leader,
            "leader-thread",
            MissionId: setup.Mission.MissionId,
            MemberId: member.MemberId);
        var run = await workspace.Store.ReadAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT agent_run_id, member_id, workspace_json
                    FROM agent_runs
                    WHERE mission_id = $missionId AND member_id = $memberId
                    ORDER BY created_utc
                    LIMIT 1;
                    """;
                Add(command, "$missionId", setup.Mission.MissionId);
                Add(command, "$memberId", member.MemberId);
                await using var reader = await command.ExecuteReaderAsync(token);
                Assert.True(await reader.ReadAsync(token));
                return new Run(
                    Guid.Parse(reader.GetString(0)),
                    Guid.Parse(reader.GetString(1)),
                    JsonSerializer.Deserialize<ExecutionWorkspaceDescriptor>(
                        reader.GetString(2),
                        new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true,
                            Converters =
                            {
                                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
                            },
                        })!);
            },
            cancellationToken);
        return (mission, actor, run);
    }

    internal static string ArtifactPath(
        CoWorkTestWorkspace workspace,
        MissionSnapshot mission,
        ArtifactSnapshot artifact)
    {
        var paths = new OpenCoWorkPaths(workspace.Root);
        return Path.Combine(
            paths.MissionsDirectory,
            mission.MissionId.ToString("D"),
            artifact.RelativePath);
    }

    internal static async ValueTask SeedOwnedBytesAsync(
        CoWorkTestWorkspace workspace,
        MissionSnapshot mission,
        Run run,
        long bytes,
        CancellationToken cancellationToken) =>
        _ = await workspace.Store.WriteAsync(
            async (connection, transaction, token) =>
            {
                var count = checked((int)(bytes / CoWorkRuntimeLimits.MaximumArtifactBytes));
                for (var index = 0; index < count; index++)
                {
                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText =
                        """
                        INSERT INTO cowork_files (
                            cowork_file_id, mission_id, agent_run_id, area, kind,
                            relative_path, sha256, size_bytes, media_type, display_name,
                            visibility, status, created_utc, updated_utc)
                        VALUES (
                            $id, $missionId, $runId, 'scratchpad', 'scratchpad',
                            $path, NULL, $bytes, NULL, NULL,
                            'private', 'available', 0, 0);
                        """;
                    Add(command, "$id", Guid.CreateVersion7());
                    Add(command, "$missionId", mission.MissionId);
                    Add(command, "$runId", run.AgentRunId);
                    Add(command, "$path", $"seed-{index}");
                    Add(command, "$bytes", CoWorkRuntimeLimits.MaximumArtifactBytes);
                    await command.ExecuteNonQueryAsync(token);
                }

                return 0;
            },
            cancellationToken);

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value is Guid guid ? guid.ToString("D") : value;
        command.Parameters.Add(parameter);
    }
}
