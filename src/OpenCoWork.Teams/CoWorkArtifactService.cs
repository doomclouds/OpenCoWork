using System.Data.Common;
using System.Security.Cryptography;
using OpenCoWork.Abstractions;

namespace OpenCoWork.Teams;

public sealed partial class CoWorkService
{
    private int _artifactRecoveryCompleted;

    public async Task<CoWorkResult<CoWorkPage<ArtifactSnapshot>>> ListArtifactsAsync(
        ListArtifactsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PageSize is < 1 or > 1000 ||
            !TryReadOffset(request.Cursor, out var offset))
        {
            return await FailureAsync<CoWorkPage<ArtifactSnapshot>>(
                CoWorkErrorCodes.InvalidState,
                "Page size or cursor is invalid.",
                cancellationToken);
        }

        var mission = await ReadMissionSnapshotAsync(
            request.MissionId,
            cancellationToken);
        if (mission is null)
        {
            return await FailureAsync<CoWorkPage<ArtifactSnapshot>>(
                CoWorkErrorCodes.NotFound,
                "Mission was not found.",
                cancellationToken);
        }

        if (!CanViewMission(mission, request.Actor))
        {
            return await FailureAsync<CoWorkPage<ArtifactSnapshot>>(
                CoWorkErrorCodes.PermissionDenied,
                "Actor cannot view this Mission's Artifacts.",
                cancellationToken);
        }

        var ids = await _store.ReadAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT cowork_file_id
                    FROM cowork_files
                    WHERE mission_id = $missionId AND kind = 'artifact'
                    ORDER BY created_utc, cowork_file_id
                    LIMIT $limit OFFSET $offset;
                    """;
                AddParameter(command, "$missionId", request.MissionId);
                AddParameter(command, "$limit", request.PageSize + 1);
                AddParameter(command, "$offset", offset);
                var values = new List<Guid>(request.PageSize + 1);
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    values.Add(Guid.Parse(reader.GetString(0)));
                }

                return values;
            },
            cancellationToken);
        var items = new List<ArtifactSnapshot>(Math.Min(request.PageSize, ids.Count));
        foreach (var id in ids.Take(request.PageSize))
        {
            var artifact = await ReadArtifactAsync(id, cancellationToken);
            if (artifact is not null)
            {
                items.Add(await RefreshArtifactAvailabilityAsync(
                    artifact,
                    cancellationToken));
            }
        }

        var page = new CoWorkPage<ArtifactSnapshot>(
            items,
            ids.Count > request.PageSize
                ? (offset + request.PageSize).ToString(
                    System.Globalization.CultureInfo.InvariantCulture)
                : null);
        return Success(page, await ReadGlobalRevisionAsync(cancellationToken));
    }

    public async Task<CoWorkResult<ArtifactSnapshot>> GetArtifactAsync(
        GetArtifactRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var artifact = await ReadArtifactAsync(request.ArtifactId, cancellationToken);
        if (artifact is null)
        {
            return await FailureAsync<ArtifactSnapshot>(
                CoWorkErrorCodes.NotFound,
                "Artifact was not found.",
                cancellationToken);
        }

        var mission = await ReadMissionSnapshotAsync(
            artifact.MissionId,
            cancellationToken);
        if (mission is null || !CanViewMission(mission, request.Actor))
        {
            return await FailureAsync<ArtifactSnapshot>(
                CoWorkErrorCodes.PermissionDenied,
                "Actor cannot view this Artifact.",
                cancellationToken);
        }

        artifact = await RefreshArtifactAvailabilityAsync(
            artifact,
            cancellationToken);
        return artifact.Status == CoWorkArtifactStatus.Available
            ? Success(artifact, await ReadGlobalRevisionAsync(cancellationToken))
            : await FailureAsync<ArtifactSnapshot>(
                CoWorkErrorCodes.ArtifactUnavailable,
                "Artifact content is unavailable.",
                cancellationToken);
    }

    public async Task<CoWorkResult<ArtifactSnapshot>> PublishArtifactAsync(
        PublishArtifactRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_workspace is null ||
            string.IsNullOrWhiteSpace(request.SourceRelativePath) ||
            string.IsNullOrWhiteSpace(request.DisplayName) ||
            string.IsNullOrWhiteSpace(request.MediaType))
        {
            return await FailureAsync<ArtifactSnapshot>(
                CoWorkErrorCodes.InvalidState,
                "Artifact runtime, source, display name, and media type are required.",
                cancellationToken);
        }

        if (ContainsSensitiveData(request.DisplayName, request.MediaType))
        {
            return await FailureAsync<ArtifactSnapshot>(
                CoWorkErrorCodes.SecretDetected,
                "Artifact metadata contains sensitive data.",
                cancellationToken);
        }

        var run = await ReadAgentRunAsync(request.AgentRunId, cancellationToken);
        if (run is null || run.MissionId != request.MissionId)
        {
            return await FailureAsync<ArtifactSnapshot>(
                CoWorkErrorCodes.NotFound,
                "Mission AgentRun was not found.",
                cancellationToken);
        }

        var mission = await ReadMissionSnapshotAsync(
            request.MissionId,
            cancellationToken);
        if (mission is null)
        {
            return await FailureAsync<ArtifactSnapshot>(
                CoWorkErrorCodes.NotFound,
                "Mission was not found.",
                cancellationToken);
        }

        try
        {
            RequireArtifactRunActor(mission, run, request.Command.Actor);
            var sourcePath = ResolveArtifactSource(run, request);
            var result = await ExecuteCommandAsync(
                request,
                request.Command,
                "publishArtifact",
                request.MissionId.ToString(),
                async (connection, transaction, token) =>
                {
                    RequireRevision(request.Command.ExpectedRevision, mission.Revision);
                    await using var source = new FileStream(
                        sourcePath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        81920,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    if (source.Length > CoWorkRuntimeLimits.MaximumArtifactBytes)
                    {
                        throw InvalidState("Artifact exceeds the 64 MiB limit.");
                    }

                    if (await _sensitiveData.ContainsSensitiveDataAsync(source, token))
                    {
                        throw new CoWorkDomainException(
                            CoWorkErrorCodes.SecretDetected,
                            "Artifact contains sensitive data.");
                    }

                    source.Position = 0;
                    var sha256 = Convert.ToHexString(
                            await SHA256.HashDataAsync(source, token))
                        .ToLowerInvariant();
                    var existing = await LoadArtifactByHashAsync(
                        connection,
                        mission.MissionId,
                        sha256,
                        token);
                    if (existing is not null)
                    {
                        return existing;
                    }

                    var ownedBytes = await ScalarAsync<long>(
                        connection,
                        transaction,
                        """
                        SELECT coalesce(sum(size_bytes), 0)
                        FROM cowork_files
                        WHERE mission_id = $missionId;
                        """,
                        token,
                        ("$missionId", mission.MissionId));
                    if (ownedBytes > CoWorkRuntimeLimits.MaximumOwnedFileBytes - source.Length)
                    {
                        throw InvalidState("Mission file storage exceeds the 512 MiB limit.");
                    }

                    var missionRoot = Path.Combine(
                        _workspace.MissionsRoot,
                        mission.MissionId.ToString("D"));
                    var artifactsRoot = Path.Combine(missionRoot, "artifacts");
                    Directory.CreateDirectory(artifactsRoot);
                    var target = Path.Combine(artifactsRoot, sha256);
                    if (File.Exists(target))
                    {
                        await EnsureArtifactFileMatchesAsync(
                            target,
                            sha256,
                            source.Length,
                            token);
                    }
                    else
                    {
                        var temporary = Path.Combine(
                            artifactsRoot,
                            $".{sha256}.{Guid.NewGuid():N}.tmp");
                        try
                        {
                            source.Position = 0;
                            await using (var destination = new FileStream(
                                             temporary,
                                             FileMode.CreateNew,
                                             FileAccess.Write,
                                             FileShare.None,
                                             81920,
                                             FileOptions.Asynchronous |
                                             FileOptions.SequentialScan |
                                             FileOptions.WriteThrough))
                            {
                                await source.CopyToAsync(destination, token);
                                await destination.FlushAsync(token);
                                destination.Flush(flushToDisk: true);
                            }

                            File.Move(temporary, target);
                        }
                        finally
                        {
                            if (File.Exists(temporary))
                            {
                                File.Delete(temporary);
                            }
                        }
                    }

                    var artifactId = Guid.CreateVersion7(_timeProvider.GetUtcNow());
                    var now = UtcNowMilliseconds();
                    await ExecuteSqlAsync(
                        connection,
                        transaction,
                        """
                        INSERT INTO cowork_files (
                            cowork_file_id, mission_id, agent_run_id,
                            area, kind, relative_path, sha256, size_bytes,
                            media_type, display_name, visibility, status,
                            created_utc, updated_utc)
                        VALUES (
                            $id, $missionId, $runId,
                            $area, 'artifact', $relativePath, $sha256, $bytes,
                            $mediaType, $displayName, 'mission', 'available',
                            $now, $now);
                        """,
                        token,
                        ("$id", artifactId),
                        ("$missionId", mission.MissionId),
                        ("$runId", run.AgentRunId),
                        ("$area", EnumText(request.SourceArea)),
                        ("$relativePath", Path.Combine("artifacts", sha256)),
                        ("$sha256", sha256),
                        ("$bytes", source.Length),
                        ("$mediaType", request.MediaType.Trim()),
                        ("$displayName", request.DisplayName.Trim()),
                        ("$now", now));
                    return (await LoadArtifactAsync(connection, artifactId, token))!;
                },
                cancellationToken);
            return result;
        }
        catch (CoWorkDomainException exception)
        {
            return await FailureAsync<ArtifactSnapshot>(
                exception.Code,
                exception.Message,
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or
                ArgumentException or NotSupportedException)
        {
            return await FailureAsync<ArtifactSnapshot>(
                CoWorkErrorCodes.PathEscape,
                "Artifact source path is outside its allowed root.",
                cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return await FailureAsync<ArtifactSnapshot>(
                CoWorkErrorCodes.NotFound,
                "Artifact source file was not found.",
                cancellationToken);
        }
        catch (DirectoryNotFoundException)
        {
            return await FailureAsync<ArtifactSnapshot>(
                CoWorkErrorCodes.NotFound,
                "Artifact source directory was not found.",
                cancellationToken);
        }
        catch (IOException)
        {
            return await FailureAsync<ArtifactSnapshot>(
                CoWorkErrorCodes.ArtifactUnavailable,
                "Artifact content could not be finalized safely.",
                cancellationToken);
        }
    }

    public async Task<CoWorkResult<ArtifactSnapshot>> PromoteArtifactAsync(
        PromoteArtifactRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await ExecuteCommandAsync(
            request,
            request.Command,
            "promoteArtifact",
            request.ArtifactId.ToString(),
            async (connection, transaction, token) =>
            {
                var artifact = await LoadArtifactAsync(
                                   connection,
                                   request.ArtifactId,
                                   token)
                               ?? throw NotFound("Artifact was not found.");
                var mission = await LoadMissionAsync(
                                  connection,
                                  artifact.MissionId,
                                  token)
                              ?? throw NotFound("Mission was not found.");
                RequireRevision(request.Command.ExpectedRevision, mission.Revision);
                RequireMissionManager(mission, request.Command.Actor);
                if (artifact.Status != CoWorkArtifactStatus.Available)
                {
                    throw new CoWorkDomainException(
                        CoWorkErrorCodes.ArtifactUnavailable,
                        "Unavailable Artifact cannot be promoted.");
                }

                await ExecuteSqlAsync(
                    connection,
                    transaction,
                    """
                    UPDATE cowork_files
                    SET visibility = 'origin',
                        updated_utc = $now
                    WHERE cowork_file_id = $id;
                    """,
                    token,
                    ("$now", UtcNowMilliseconds()),
                    ("$id", artifact.ArtifactId));
                return (await LoadArtifactAsync(connection, artifact.ArtifactId, token))!;
            },
            cancellationToken);
    }

    private static void RequireArtifactRunActor(
        MissionSnapshot mission,
        AgentRunSnapshot run,
        CoWorkActorContext actor)
    {
        if (IsHost(actor))
        {
            return;
        }

        var member = mission.Members.SingleOrDefault(candidate =>
            candidate.MemberId == actor.MemberId);
        var validRole = (actor.Kind, member?.Role) is
            (CoWorkActorKind.Leader, CoWorkMemberRole.Leader) or
            (CoWorkActorKind.Member, CoWorkMemberRole.Member);
        if (actor.MissionId != mission.MissionId ||
            run.MemberId != member?.MemberId ||
            !validRole)
        {
            throw PermissionDenied("Actor cannot publish from this AgentRun.");
        }
    }

    private static string ResolveArtifactSource(
        AgentRunSnapshot run,
        PublishArtifactRequest request)
    {
        if (Path.IsPathRooted(request.SourceRelativePath))
        {
            throw new CoWorkDomainException(
                CoWorkErrorCodes.PathEscape,
                "Artifact source path is outside its allowed root.");
        }

        var root = request.SourceArea switch
        {
            CoWorkFileArea.Workspace => run.ExecutionWorkspace.Mode switch
            {
                CoWorkWorkspaceMode.Project => run.ExecutionWorkspace.WorkspaceRoot,
                CoWorkWorkspaceMode.Worktree
                    when run.ExecutionWorkspace.WorktreeId is not null &&
                         !string.IsNullOrWhiteSpace(
                             run.ExecutionWorkspace.WorktreeRoot) =>
                    run.ExecutionWorkspace.WorktreeRoot,
                _ => throw InvalidState("AgentRun Workspace is invalid."),
            },
            CoWorkFileArea.Scratchpad => run.ExecutionWorkspace.ScratchpadRoot,
            _ => throw new ArgumentOutOfRangeException(nameof(request.SourceArea)),
        };
        var resolved = ResolveContainedPath(root, request.SourceRelativePath);
        RejectReparsePoints(root, resolved);
        if (!File.Exists(resolved))
        {
            throw new FileNotFoundException("Artifact source was not found.");
        }

        return resolved;
    }

    private static void RejectReparsePoints(string root, string path)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var relative = Path.GetRelativePath(fullRoot, Path.GetFullPath(path));
        var current = fullRoot;
        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                break;
            }

            FileSystemInfo info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : new FileInfo(current);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new CoWorkDomainException(
                    CoWorkErrorCodes.PathEscape,
                    "Artifact path contains a symbolic link or reparse point.");
            }
        }
    }

    private static string ResolveContainedPath(string root, string relativePath)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var resolved = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(relativePath, fullRoot));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(fullRoot, resolved, comparison) &&
            !resolved.StartsWith(
                fullRoot + Path.DirectorySeparatorChar,
                comparison))
        {
            throw new CoWorkDomainException(
                CoWorkErrorCodes.PathEscape,
                "Artifact path is outside its allowed root.");
        }

        return resolved;
    }

    private async Task<ArtifactSnapshot?> ReadArtifactAsync(
        Guid artifactId,
        CancellationToken cancellationToken) =>
        await _store.ReadAsync(
            (connection, token) => LoadArtifactAsync(connection, artifactId, token),
            cancellationToken);

    private async Task<MissionSnapshot?> ReadMissionSnapshotAsync(
        Guid missionId,
        CancellationToken cancellationToken) =>
        await _store.ReadAsync(
            (connection, token) => LoadMissionAsync(connection, missionId, token),
            cancellationToken);

    private static async ValueTask<ArtifactSnapshot?> LoadArtifactByHashAsync(
        DbConnection connection,
        Guid missionId,
        string sha256,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT cowork_file_id
            FROM cowork_files
            WHERE mission_id = $missionId
              AND kind = 'artifact'
              AND sha256 = $sha256;
            """;
        AddParameter(command, "$missionId", missionId);
        AddParameter(command, "$sha256", sha256);
        var id = await command.ExecuteScalarAsync(cancellationToken);
        return id is null
            ? null
            : await LoadArtifactAsync(
                connection,
                Guid.Parse(Convert.ToString(id)!),
                cancellationToken);
    }

    private static async ValueTask<ArtifactSnapshot?> LoadArtifactAsync(
        DbConnection connection,
        Guid artifactId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT mission_id, agent_run_id, relative_path, sha256, size_bytes,
                   media_type, display_name, visibility, status, created_utc
            FROM cowork_files
            WHERE cowork_file_id = $id AND kind = 'artifact';
            """;
        AddParameter(command, "$id", artifactId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ArtifactSnapshot(
                artifactId,
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetString(5),
                reader.GetString(6),
                ParseEnum<CoWorkArtifactVisibility>(reader.GetString(7)),
                ParseEnum<CoWorkArtifactStatus>(reader.GetString(8)),
                FromUnixMilliseconds(reader.GetInt64(9)))
            : null;
    }

    private async Task<ArtifactSnapshot> RefreshArtifactAvailabilityAsync(
        ArtifactSnapshot artifact,
        CancellationToken cancellationToken)
    {
        if (artifact.Status == CoWorkArtifactStatus.Unavailable || _workspace is null)
        {
            return artifact with { Status = CoWorkArtifactStatus.Unavailable };
        }

        try
        {
            var missionRoot = Path.Combine(
                _workspace.MissionsRoot,
                artifact.MissionId.ToString("D"));
            var path = ResolveContainedPath(missionRoot, artifact.RelativePath);
            RejectReparsePoints(missionRoot, path);
            await EnsureArtifactFileMatchesAsync(
                path,
                artifact.Sha256,
                artifact.Bytes,
                cancellationToken);
            return artifact;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            await MarkArtifactUnavailableAsync(artifact.ArtifactId, cancellationToken);
            return artifact with { Status = CoWorkArtifactStatus.Unavailable };
        }
    }

    private static async Task EnsureArtifactFileMatchesAsync(
        string path,
        string sha256,
        long bytes,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != bytes ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Artifact content is missing or changed.");
        }

        await using var source = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = Convert.ToHexString(
                await SHA256.HashDataAsync(source, cancellationToken))
            .ToLowerInvariant();
        if (!string.Equals(actual, sha256, StringComparison.Ordinal))
        {
            throw new IOException("Artifact digest does not match.");
        }
    }

    private async Task MarkArtifactUnavailableAsync(
        Guid artifactId,
        CancellationToken cancellationToken) =>
        await _store.WriteAsync(
            async (connection, transaction, token) =>
            {
                await ExecuteSqlAsync(
                    connection,
                    transaction,
                    """
                    UPDATE cowork_files
                    SET status = 'unavailable',
                        updated_utc = $now
                    WHERE cowork_file_id = $id AND kind = 'artifact';
                    """,
                    token,
                    ("$now", UtcNowMilliseconds()),
                    ("$id", artifactId));
                return 0;
            },
            cancellationToken);

    private async Task RecoverArtifactsOnceAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _artifactRecoveryCompleted, 1) != 0)
        {
            return;
        }

        var ids = await _store.ReadAsync(
            (connection, token) => ReadGuidsAsync(
                connection,
                """
                SELECT cowork_file_id
                FROM cowork_files
                WHERE kind = 'artifact' AND status = 'available';
                """,
                token),
            cancellationToken);
        foreach (var id in ids)
        {
            var artifact = await ReadArtifactAsync(id, cancellationToken);
            if (artifact is not null)
            {
                _ = await RefreshArtifactAvailabilityAsync(artifact, cancellationToken);
            }
        }
    }
}
