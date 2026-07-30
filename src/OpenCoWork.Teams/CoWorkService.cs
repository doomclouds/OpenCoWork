using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCoWork.Abstractions;

namespace OpenCoWork.Teams;

public sealed partial class CoWorkService : ICoWorkService
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };

    private readonly IWorkspaceStateStore _store;
    private readonly ISensitiveDataService _sensitiveData;
    private readonly CoWorkConfig _config;
    private readonly TimeProvider _timeProvider;

    public CoWorkService(
        IWorkspaceStateStore store,
        ISensitiveDataService sensitiveData,
        CoWorkConfig config,
        TimeProvider timeProvider)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _sensitiveData = sensitiveData ?? throw new ArgumentNullException(nameof(sensitiveData));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public Task<CoWorkResult<CoWorkPage<AgentProfileSnapshot>>> ListAgentProfilesAsync(
        ListAgentProfilesRequest request,
        CancellationToken cancellationToken = default) =>
        ReadHostPageAsync(
            request.Actor,
            request.PageSize,
            request.Cursor,
            LoadProfileIdsAsync,
            LoadProfileAsync,
            cancellationToken);

    public Task<CoWorkResult<AgentProfileSnapshot>> GetAgentProfileAsync(
        GetAgentProfileRequest request,
        CancellationToken cancellationToken = default) =>
        ReadHostAsync(
            request.Actor,
            (connection, token) => LoadProfileAsync(
                connection,
                request.ProfileId,
                token),
            cancellationToken);

    public Task<CoWorkResult<AgentProfileSnapshot>> UpsertAgentProfileAsync(
        UpsertAgentProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!IsHost(request.Command.Actor))
        {
            return FailureAsync<AgentProfileSnapshot>(
                CoWorkErrorCodes.PermissionDenied,
                "Only Host can manage Agent Profiles.",
                cancellationToken);
        }

        if (ContainsSensitiveData(
                request.Name,
                request.Description,
                request.Instructions,
                request.ProviderId,
                request.ModelId,
                request.SkillAllowlist,
                request.ToolAllowlist))
        {
            return FailureAsync<AgentProfileSnapshot>(
                CoWorkErrorCodes.SecretDetected,
                "Agent Profile contains sensitive data.",
                cancellationToken);
        }

        return ExecuteCommandAsync(
            request,
            request.Command,
            "upsertAgentProfile",
            request.ProfileId?.ToString(),
            async (connection, transaction, token) =>
            {
                var name = RequiredText(request.Name, "Profile name");
                var normalizedName = Normalize(name);
                var now = UtcNowMilliseconds();
                var profileId = request.ProfileId ?? Guid.CreateVersion7(_timeProvider.GetUtcNow());
                var existing = await LoadProfileAsync(
                    connection,
                    profileId,
                    token);
                if (existing is null)
                {
                    if (request.Command.ExpectedRevision is not null)
                    {
                        throw Conflict("New Agent Profile cannot have an expected revision.");
                    }

                    await ExecuteSqlAsync(
                        connection,
                        transaction,
                        """
                        INSERT INTO agent_profiles (
                            agent_profile_id, name, normalized_name, description,
                            instructions, model_json, tools_json, permission_json,
                            enabled, revision, created_utc, updated_utc)
                        VALUES (
                            $id, $name, $normalizedName, $description,
                            $instructions, $modelJson, $toolsJson, '{}',
                            1, 1, $now, $now);
                        """,
                        token,
                        ("$id", profileId),
                        ("$name", name),
                        ("$normalizedName", normalizedName),
                        ("$description", request.Description.Trim()),
                        ("$instructions", request.Instructions),
                        ("$modelJson", JsonSerializer.Serialize(
                            new ModelSelection(request.ProviderId, request.ModelId),
                            JsonOptions)),
                        ("$toolsJson", JsonSerializer.Serialize(
                            new ToolSelection(
                                NormalizeList(request.SkillAllowlist),
                                NormalizeList(request.ToolAllowlist)),
                            JsonOptions)),
                        ("$now", now));
                }
                else
                {
                    RequireRevision(request.Command.ExpectedRevision, existing.Revision);
                    if (await ScalarAsync<long>(
                            connection,
                            transaction,
                            """
                            SELECT count(*)
                            FROM team_members
                            WHERE agent_profile_id = $id;
                            """,
                            token,
                            ("$id", profileId)) != 0)
                    {
                        throw Conflict(
                            "Referenced Agent Profiles can only change Enabled state.");
                    }

                    await ExecuteSqlAsync(
                        connection,
                        transaction,
                        """
                        UPDATE agent_profiles
                        SET name = $name,
                            normalized_name = $normalizedName,
                            description = $description,
                            instructions = $instructions,
                            model_json = $modelJson,
                            tools_json = $toolsJson,
                            revision = revision + 1,
                            updated_utc = $now
                        WHERE agent_profile_id = $id;
                        """,
                        token,
                        ("$id", profileId),
                        ("$name", name),
                        ("$normalizedName", normalizedName),
                        ("$description", request.Description.Trim()),
                        ("$instructions", request.Instructions),
                        ("$modelJson", JsonSerializer.Serialize(
                            new ModelSelection(request.ProviderId, request.ModelId),
                            JsonOptions)),
                        ("$toolsJson", JsonSerializer.Serialize(
                            new ToolSelection(
                                NormalizeList(request.SkillAllowlist),
                                NormalizeList(request.ToolAllowlist)),
                            JsonOptions)),
                        ("$now", now));
                }

                return (await LoadProfileAsync(connection, profileId, token))!;
            },
            cancellationToken);
    }

    public Task<CoWorkResult<AgentProfileSnapshot>> SetAgentProfileEnabledAsync(
        SetAgentProfileEnabledRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!IsHost(request.Command.Actor))
        {
            return FailureAsync<AgentProfileSnapshot>(
                CoWorkErrorCodes.PermissionDenied,
                "Only Host can manage Agent Profiles.",
                cancellationToken);
        }

        return ExecuteCommandAsync(
            request,
            request.Command,
            "setAgentProfileEnabled",
            request.ProfileId.ToString(),
            async (connection, transaction, token) =>
            {
                var profile = await LoadProfileAsync(connection, request.ProfileId, token)
                    ?? throw NotFound("Agent Profile was not found.");
                RequireRevision(request.Command.ExpectedRevision, profile.Revision);
                await ExecuteSqlAsync(
                    connection,
                    transaction,
                    """
                    UPDATE agent_profiles
                    SET enabled = $enabled,
                        revision = revision + 1,
                        updated_utc = $now
                    WHERE agent_profile_id = $id;
                    """,
                    token,
                    ("$id", request.ProfileId),
                    ("$enabled", request.Enabled ? 1 : 0),
                    ("$now", UtcNowMilliseconds()));
                return (await LoadProfileAsync(connection, request.ProfileId, token))!;
            },
            cancellationToken);
    }

    public Task<CoWorkResult<CoWorkPage<TeamSnapshot>>> ListTeamsAsync(
        ListTeamsRequest request,
        CancellationToken cancellationToken = default) =>
        ReadHostPageAsync(
            request.Actor,
            request.PageSize,
            request.Cursor,
            LoadTeamIdsAsync,
            LoadTeamAsync,
            cancellationToken);

    public Task<CoWorkResult<TeamSnapshot>> GetTeamAsync(
        GetTeamRequest request,
        CancellationToken cancellationToken = default) =>
        ReadHostAsync(
            request.Actor,
            (connection, token) => LoadTeamAsync(
                connection,
                request.TeamId,
                token),
            cancellationToken);

    public Task<CoWorkResult<TeamSnapshot>> UpsertTeamAsync(
        UpsertTeamRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!IsHost(request.Command.Actor))
        {
            return FailureAsync<TeamSnapshot>(
                CoWorkErrorCodes.PermissionDenied,
                "Only Host can manage Teams.",
                cancellationToken);
        }

        if (ContainsSensitiveData(
                request.Name,
                request.Description,
                request.Members.SelectMany(member =>
                    new[] { member.Alias, member.Description })))
        {
            return FailureAsync<TeamSnapshot>(
                CoWorkErrorCodes.SecretDetected,
                "Team contains sensitive data.",
                cancellationToken);
        }

        return ExecuteCommandAsync(
            request,
            request.Command,
            "upsertTeam",
            request.TeamId?.ToString(),
            async (connection, transaction, token) =>
            {
                ValidateMembers(request.Members);
                foreach (var member in request.Members)
                {
                    var profile = await LoadProfileAsync(
                                      connection,
                                      member.ProfileId,
                                      token)
                                  ?? throw NotFound(
                                      $"Agent Profile '{member.ProfileId}' was not found.");
                    if (!profile.Enabled)
                    {
                        throw InvalidState(
                            $"Agent Profile '{profile.Name}' is disabled.");
                    }
                }

                var name = RequiredText(request.Name, "Team name");
                var teamId = request.TeamId ?? Guid.CreateVersion7(_timeProvider.GetUtcNow());
                var existing = await LoadTeamAsync(
                    connection,
                    teamId,
                    token);
                var now = UtcNowMilliseconds();
                if (existing is null)
                {
                    if (request.Command.ExpectedRevision is not null)
                    {
                        throw Conflict("New Team cannot have an expected revision.");
                    }

                    await ExecuteSqlAsync(
                        connection,
                        transaction,
                        """
                        INSERT INTO teams (
                            team_id, name, normalized_name, description,
                            enabled, revision, created_utc, updated_utc)
                        VALUES (
                            $id, $name, $normalizedName, $description,
                            1, 1, $now, $now);
                        """,
                        token,
                        ("$id", teamId),
                        ("$name", name),
                        ("$normalizedName", Normalize(name)),
                        ("$description", request.Description.Trim()),
                        ("$now", now));
                }
                else
                {
                    RequireRevision(request.Command.ExpectedRevision, existing.Revision);
                    if (await ScalarAsync<long>(
                            connection,
                            transaction,
                            "SELECT count(*) FROM missions WHERE team_id = $id;",
                            token,
                            ("$id", teamId)) != 0)
                    {
                        throw Conflict("Referenced Teams can only change Enabled state.");
                    }

                    await ExecuteSqlAsync(
                        connection,
                        transaction,
                        """
                        UPDATE teams
                        SET name = $name,
                            normalized_name = $normalizedName,
                            description = $description,
                            revision = revision + 1,
                            updated_utc = $now
                        WHERE team_id = $id;
                        DELETE FROM team_members WHERE team_id = $id;
                        """,
                        token,
                        ("$id", teamId),
                        ("$name", name),
                        ("$normalizedName", Normalize(name)),
                        ("$description", request.Description.Trim()),
                        ("$now", now));
                }

                for (var index = 0; index < request.Members.Count; index++)
                {
                    var member = request.Members[index];
                    await ExecuteSqlAsync(
                        connection,
                        transaction,
                        """
                        INSERT INTO team_members (
                            team_member_id, team_id, agent_profile_id,
                            alias, normalized_alias, role, description, ordinal)
                        VALUES (
                            $memberId, $teamId, $profileId,
                            $alias, $normalizedAlias, $role, $description, $ordinal);
                        """,
                        token,
                        ("$memberId", member.MemberId ??
                            Guid.CreateVersion7(_timeProvider.GetUtcNow())),
                        ("$teamId", teamId),
                        ("$profileId", member.ProfileId),
                        ("$alias", RequiredText(member.Alias, "Member alias")),
                        ("$normalizedAlias", Normalize(member.Alias)),
                        ("$role", EnumText(member.Role)),
                        ("$description", member.Description.Trim()),
                        ("$ordinal", index));
                }

                return (await LoadTeamAsync(connection, teamId, token))!;
            },
            cancellationToken);
    }

    public Task<CoWorkResult<TeamSnapshot>> SetTeamEnabledAsync(
        SetTeamEnabledRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!IsHost(request.Command.Actor))
        {
            return FailureAsync<TeamSnapshot>(
                CoWorkErrorCodes.PermissionDenied,
                "Only Host can manage Teams.",
                cancellationToken);
        }

        return ExecuteCommandAsync(
            request,
            request.Command,
            "setTeamEnabled",
            request.TeamId.ToString(),
            async (connection, transaction, token) =>
            {
                var team = await LoadTeamAsync(connection, request.TeamId, token)
                    ?? throw NotFound("Team was not found.");
                RequireRevision(request.Command.ExpectedRevision, team.Revision);
                await ExecuteSqlAsync(
                    connection,
                    transaction,
                    """
                    UPDATE teams
                    SET enabled = $enabled,
                        revision = revision + 1,
                        updated_utc = $now
                    WHERE team_id = $id;
                    """,
                    token,
                    ("$id", request.TeamId),
                    ("$enabled", request.Enabled ? 1 : 0),
                    ("$now", UtcNowMilliseconds()));
                return (await LoadTeamAsync(connection, request.TeamId, token))!;
            },
            cancellationToken);
    }

    private async Task<CoWorkResult<T>> ExecuteCommandAsync<T>(
        object request,
        CoWorkCommandContext command,
        string commandKind,
        string? targetId,
        Func<DbConnection, DbTransaction, CancellationToken, ValueTask<T>> mutation,
        CancellationToken cancellationToken)
    {
        if (command.CommandId == Guid.Empty ||
            string.IsNullOrWhiteSpace(command.Actor.PrincipalId))
        {
            return await FailureAsync<T>(
                CoWorkErrorCodes.InvalidState,
                "Command and Actor identities are required.",
                cancellationToken);
        }

        var requestHash = Convert.ToHexString(
                SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions)))
            .ToLowerInvariant();
        try
        {
            return await _store.WriteAsync(
                async (connection, transaction, token) =>
                {
                    await using (var receipt = connection.CreateCommand())
                    {
                        receipt.Transaction = transaction;
                        receipt.CommandText =
                            """
                            SELECT request_sha256, result_json, revision
                            FROM cowork_command_receipts
                            WHERE command_id = $commandId;
                            """;
                        AddParameter(receipt, "$commandId", command.CommandId);
                        await using var reader =
                            await receipt.ExecuteReaderAsync(token);
                        if (await reader.ReadAsync(token))
                        {
                            if (!string.Equals(
                                    reader.GetString(0),
                                    requestHash,
                                    StringComparison.Ordinal))
                            {
                                throw Conflict(
                                    "Command ID was already used with a different request.");
                            }

                            return Success(
                                JsonSerializer.Deserialize<T>(
                                    reader.GetString(1),
                                    JsonOptions)
                                ?? throw InvalidState(
                                    "Stored command result is invalid."),
                                reader.GetInt64(2));
                        }
                    }

                    var value = await mutation(connection, transaction, token);
                    var revision = await IncrementGlobalRevisionAsync(
                        connection,
                        transaction,
                        token);
                    await ExecuteSqlAsync(
                        connection,
                        transaction,
                        """
                        INSERT INTO cowork_command_receipts (
                            command_id, actor_id, command_kind, target_id,
                            request_sha256, result_json, revision, created_utc)
                        VALUES (
                            $commandId, $actorId, $commandKind, $targetId,
                            $requestHash, $resultJson, $revision, $createdUtc);
                        """,
                        token,
                        ("$commandId", command.CommandId),
                        ("$actorId", command.Actor.PrincipalId),
                        ("$commandKind", commandKind),
                        ("$targetId", targetId),
                        ("$requestHash", requestHash),
                        ("$resultJson", JsonSerializer.Serialize(value, JsonOptions)),
                        ("$revision", revision),
                        ("$createdUtc", UtcNowMilliseconds()));
                    return Success(value, revision);
                },
                cancellationToken);
        }
        catch (CoWorkDomainException exception)
        {
            return await FailureAsync<T>(
                exception.Code,
                exception.Message,
                cancellationToken);
        }
        catch (DbException exception)
        {
            return await FailureAsync<T>(
                CoWorkErrorCodes.Conflict,
                $"State constraint rejected the command: {exception.GetType().Name}.",
                cancellationToken);
        }
    }

    private async Task<CoWorkResult<T>> ReadAsync<T>(
        CoWorkActorContext actor,
        Func<DbConnection, CancellationToken, ValueTask<T?>> read,
        CancellationToken cancellationToken)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(actor.PrincipalId))
        {
            return await FailureAsync<T>(
                CoWorkErrorCodes.PermissionDenied,
                "Actor identity is required.",
                cancellationToken);
        }

        var value = await _store.ReadAsync(read, cancellationToken);
        return value is null
            ? await FailureAsync<T>(
                CoWorkErrorCodes.NotFound,
                "Entity was not found.",
                cancellationToken)
            : Success(value, await ReadGlobalRevisionAsync(cancellationToken));
    }

    private Task<CoWorkResult<T>> ReadHostAsync<T>(
        CoWorkActorContext actor,
        Func<DbConnection, CancellationToken, ValueTask<T?>> read,
        CancellationToken cancellationToken)
        where T : class =>
        IsHost(actor)
            ? ReadAsync(actor, read, cancellationToken)
            : FailureAsync<T>(
                CoWorkErrorCodes.PermissionDenied,
                "Only Host can read this resource.",
                cancellationToken);

    private async Task<CoWorkResult<CoWorkPage<T>>> ReadPageAsync<T>(
        CoWorkActorContext actor,
        int pageSize,
        string? cursor,
        Func<DbConnection, int, int, CancellationToken, ValueTask<Guid[]>> loadIds,
        Func<DbConnection, Guid, CancellationToken, ValueTask<T?>> load,
        CancellationToken cancellationToken)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(actor.PrincipalId) ||
            pageSize is < 1 or > 1000 ||
            !TryReadOffset(cursor, out var offset))
        {
            return await FailureAsync<CoWorkPage<T>>(
                CoWorkErrorCodes.InvalidState,
                "Actor, page size, or cursor is invalid.",
                cancellationToken);
        }

        var page = await _store.ReadAsync(
            async (connection, token) =>
            {
                var ids = await loadIds(connection, pageSize + 1, offset, token);
                var items = new List<T>(Math.Min(pageSize, ids.Length));
                foreach (var id in ids.Take(pageSize))
                {
                    items.Add((await load(connection, id, token))!);
                }

                return new CoWorkPage<T>(
                    items,
                    ids.Length > pageSize
                        ? (offset + pageSize).ToString(
                            System.Globalization.CultureInfo.InvariantCulture)
                        : null);
            },
            cancellationToken);
        return Success(page, await ReadGlobalRevisionAsync(cancellationToken));
    }

    private Task<CoWorkResult<CoWorkPage<T>>> ReadHostPageAsync<T>(
        CoWorkActorContext actor,
        int pageSize,
        string? cursor,
        Func<DbConnection, int, int, CancellationToken, ValueTask<Guid[]>> loadIds,
        Func<DbConnection, Guid, CancellationToken, ValueTask<T?>> load,
        CancellationToken cancellationToken)
        where T : class =>
        IsHost(actor)
            ? ReadPageAsync(
                actor,
                pageSize,
                cursor,
                loadIds,
                load,
                cancellationToken)
            : FailureAsync<CoWorkPage<T>>(
                CoWorkErrorCodes.PermissionDenied,
                "Only Host can read this resource.",
                cancellationToken);

    private async Task<CoWorkResult<T>> FailureAsync<T>(
        string code,
        string message,
        CancellationToken cancellationToken) =>
        new(
            default,
            await ReadGlobalRevisionAsync(cancellationToken),
            new CoWorkError(code, message));

    private async ValueTask<long> ReadGlobalRevisionAsync(
        CancellationToken cancellationToken) =>
        await _store.ReadAsync(
            (connection, token) => ScalarAsync<long>(
                connection,
                transaction: null,
                "SELECT current_revision FROM cowork_state WHERE id = 1;",
                token),
            cancellationToken);

    private async ValueTask<long> IncrementGlobalRevisionAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        var now = UtcNowMilliseconds();
        await ExecuteSqlAsync(
            connection,
            transaction,
            """
            UPDATE cowork_state
            SET current_revision = current_revision + 1,
                updated_utc = $now
            WHERE id = 1;
            """,
            cancellationToken,
            ("$now", now));
        return await ScalarAsync<long>(
            connection,
            transaction,
            "SELECT current_revision FROM cowork_state WHERE id = 1;",
            cancellationToken);
    }

    private async ValueTask<Guid[]> LoadProfileIdsAsync(
        DbConnection connection,
        int limit,
        int offset,
        CancellationToken cancellationToken) =>
        await ReadIdsAsync(
            connection,
            "SELECT agent_profile_id FROM agent_profiles ORDER BY created_utc, agent_profile_id LIMIT $limit OFFSET $offset;",
            limit,
            offset,
            cancellationToken);

    private static async ValueTask<AgentProfileSnapshot?> LoadProfileAsync(
        DbConnection connection,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT name, description, instructions, model_json, tools_json,
                   enabled, revision, created_utc, updated_utc
            FROM agent_profiles
            WHERE agent_profile_id = $id;
            """;
        AddParameter(command, "$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var model = JsonSerializer.Deserialize<ModelSelection>(
                        reader.GetString(3),
                        JsonOptions)
                    ?? throw InvalidState("Agent Profile model snapshot is invalid.");
        var tools = JsonSerializer.Deserialize<ToolSelection>(
                        reader.GetString(4),
                        JsonOptions)
                    ?? throw InvalidState("Agent Profile tool snapshot is invalid.");
        return new AgentProfileSnapshot(
            id,
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            model.ProviderId,
            model.ModelId,
            tools.SkillAllowlist,
            tools.ToolAllowlist,
            reader.GetInt64(5) != 0,
            reader.GetInt64(6),
            FromUnixMilliseconds(reader.GetInt64(7)),
            FromUnixMilliseconds(reader.GetInt64(8)));
    }

    private async ValueTask<Guid[]> LoadTeamIdsAsync(
        DbConnection connection,
        int limit,
        int offset,
        CancellationToken cancellationToken) =>
        await ReadIdsAsync(
            connection,
            "SELECT team_id FROM teams ORDER BY created_utc, team_id LIMIT $limit OFFSET $offset;",
            limit,
            offset,
            cancellationToken);

    private static async ValueTask<TeamSnapshot?> LoadTeamAsync(
        DbConnection connection,
        Guid id,
        CancellationToken cancellationToken)
    {
        string name;
        string description;
        bool enabled;
        long revision;
        long createdUtc;
        long updatedUtc;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT name, description, enabled, revision, created_utc, updated_utc
                FROM teams
                WHERE team_id = $id;
                """;
            AddParameter(command, "$id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            name = reader.GetString(0);
            description = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
            enabled = reader.GetInt64(2) != 0;
            revision = reader.GetInt64(3);
            createdUtc = reader.GetInt64(4);
            updatedUtc = reader.GetInt64(5);
        }

        var members = new List<TeamMemberSnapshot>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT team_member_id, alias, agent_profile_id, role, description, ordinal
                FROM team_members
                WHERE team_id = $id
                ORDER BY ordinal, team_member_id;
                """;
            AddParameter(command, "$id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                members.Add(new TeamMemberSnapshot(
                    Guid.Parse(reader.GetString(0)),
                    reader.GetString(1),
                    Guid.Parse(reader.GetString(2)),
                    ParseEnum<CoWorkMemberRole>(reader.GetString(3)),
                    reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    reader.GetInt32(5)));
            }
        }

        return new TeamSnapshot(
            id,
            name,
            description,
            members,
            enabled,
            revision,
            FromUnixMilliseconds(createdUtc),
            FromUnixMilliseconds(updatedUtc));
    }

    private static void ValidateMembers(IReadOnlyList<TeamMemberInput> members)
    {
        if (members.Count is < 1 or > CoWorkRuntimeLimits.MaximumMissionMembers)
        {
            throw InvalidState(
                $"Team must contain 1 to {CoWorkRuntimeLimits.MaximumMissionMembers} members.");
        }

        if (members.Count(member => member.Role == CoWorkMemberRole.Leader) != 1)
        {
            throw InvalidState("Team must contain exactly one Leader.");
        }

        if (members.Select(member => Normalize(member.Alias))
            .Distinct(StringComparer.Ordinal)
            .Count() != members.Count)
        {
            throw Conflict("Team member aliases must be unique.");
        }

        if (members.Select(member => member.ProfileId).Distinct().Count() != members.Count)
        {
            throw Conflict("An Agent Profile can only appear once in a Team.");
        }
    }

    private bool ContainsSensitiveData(params object[] values)
    {
        foreach (var value in values)
        {
            switch (value)
            {
                case string text when _sensitiveData.ContainsSensitiveData(text):
                    return true;
                case IEnumerable<string> texts
                    when texts.Any(_sensitiveData.ContainsSensitiveData):
                    return true;
            }
        }

        return false;
    }

    private long UtcNowMilliseconds() =>
        _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    private static CoWorkResult<T> Success<T>(T value, long revision) =>
        new(value, revision, null);

    private static bool IsHost(CoWorkActorContext actor) =>
        actor.Kind == CoWorkActorKind.Host &&
        !string.IsNullOrWhiteSpace(actor.PrincipalId);

    private static string RequiredText(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw InvalidState($"{name} is required.");
        }

        return value.Trim();
    }

    private static string Normalize(string value) =>
        RequiredText(value, "Value").ToUpperInvariant();

    private static string[] NormalizeList(IEnumerable<string> values) =>
        values.Select(value => RequiredText(value, "Allowlist value"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string EnumText<T>(T value)
        where T : struct, Enum =>
        JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    private static T ParseEnum<T>(string value)
        where T : struct, Enum =>
        Enum.Parse<T>(value, ignoreCase: true);

    private static void RequireRevision(long? expected, long actual)
    {
        if (expected != actual)
        {
            throw Conflict(
                $"Revision conflict; expected {expected?.ToString() ?? "<none>"}, actual {actual}.");
        }
    }

    private static bool TryReadOffset(string? cursor, out int offset)
    {
        if (cursor is null)
        {
            offset = 0;
            return true;
        }

        return int.TryParse(
                   cursor,
                   System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out offset) &&
               offset >= 0;
    }

    private static async ValueTask<Guid[]> ReadIdsAsync(
        DbConnection connection,
        string sql,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddParameter(command, "$limit", limit);
        AddParameter(command, "$offset", offset);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ids = new List<Guid>();
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(Guid.Parse(reader.GetString(0)));
        }

        return ids.ToArray();
    }

    private static async ValueTask<int> ExecuteSqlAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            AddParameter(command, parameter.Name, parameter.Value);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask<T> ScalarAsync<T>(
        DbConnection connection,
        DbTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            AddParameter(command, parameter.Name, parameter.Value);
        }

        var value = await command.ExecuteScalarAsync(cancellationToken)
            ?? throw InvalidState("State scalar query returned null.");
        return (T)Convert.ChangeType(
            value,
            typeof(T),
            System.Globalization.CultureInfo.InvariantCulture)!;
    }

    private static void AddParameter(
        DbCommand command,
        string name,
        object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value switch
        {
            null => DBNull.Value,
            Guid id => id.ToString(),
            bool flag => flag ? 1 : 0,
            _ => value,
        };
        command.Parameters.Add(parameter);
    }

    private static DateTimeOffset FromUnixMilliseconds(long value) =>
        DateTimeOffset.FromUnixTimeMilliseconds(value);

    private static CoWorkDomainException NotFound(string message) =>
        new(CoWorkErrorCodes.NotFound, message);

    private static CoWorkDomainException Conflict(string message) =>
        new(CoWorkErrorCodes.Conflict, message);

    private static CoWorkDomainException InvalidState(string message) =>
        new(CoWorkErrorCodes.InvalidState, message);

    private static CoWorkDomainException PermissionDenied(string message) =>
        new(CoWorkErrorCodes.PermissionDenied, message);

    private sealed record ModelSelection(string ProviderId, string ModelId);

    private sealed record ToolSelection(
        IReadOnlyList<string> SkillAllowlist,
        IReadOnlyList<string> ToolAllowlist);
}

internal sealed class CoWorkDomainException(string code, string message)
    : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
