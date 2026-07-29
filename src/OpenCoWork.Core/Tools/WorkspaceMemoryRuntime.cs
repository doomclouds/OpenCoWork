using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.State;
using OpenCoWork.Core.Workspaces;

namespace OpenCoWork.Core.Tools;

internal sealed class WorkspaceMemoryRuntime
{
    private const int MaximumBodyBytes = 64 * 1024;
    private const int MaximumSummaryBytes = 2 * 1024;
    private const int MaximumTitleLength = 256;
    private const int MaximumTags = 32;
    private const int MaximumTagLength = 64;
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly OpenCoWorkPaths _paths;
    private readonly StateRuntime _state;

    public WorkspaceMemoryRuntime(OpenCoWorkPaths paths, StateRuntime state)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _state = state ?? throw new ArgumentNullException(nameof(state));
    }

    public async ValueTask<ToolBindingResult> ListAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            var includeArchived = OptionalBoolean(
                arguments,
                "includeArchived",
                defaultValue: false);
            var limit = OptionalPositive(arguments, "limit", 50, 50);
            await using var connection =
                await _state.OpenReadOnlyConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT memory_id, current_version, title, summary, tags_json,
                       status, created_utc, updated_utc
                FROM workspace_memories
                WHERE $include_archived = 1 OR status = 'active'
                ORDER BY updated_utc DESC, memory_id
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue(
                "$include_archived",
                includeArchived ? 1 : 0);
            command.Parameters.AddWithValue("$limit", limit);
            return ToolBindingResult.Success(JsonSerializer.SerializeToElement(new
            {
                items = await ReadItemsAsync(command, cancellationToken),
            }));
        }
        catch (MemoryRuntimeException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
        catch (Exception exception) when (
            exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            return Failure(
                ToolErrorCodes.ExecutionFailed,
                "Workspace Memory list failed.");
        }
    }

    public async ValueTask<ToolBindingResult> SearchAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            var query = Normalize(RequiredString(arguments, "query"));
            if (query.Length is 0 or > 256)
            {
                throw Invalid();
            }

            var includeArchived = OptionalBoolean(
                arguments,
                "includeArchived",
                defaultValue: false);
            var limit = OptionalPositive(arguments, "limit", 50, 50);
            await using var connection =
                await _state.OpenReadOnlyConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT memory_id, current_version, title, summary, tags_json,
                       status, created_utc, updated_utc
                FROM workspace_memories
                WHERE ($include_archived = 1 OR status = 'active')
                  AND instr(normalized_search_text, $query) > 0
                ORDER BY updated_utc DESC, memory_id
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue(
                "$include_archived",
                includeArchived ? 1 : 0);
            command.Parameters.AddWithValue("$query", query);
            command.Parameters.AddWithValue("$limit", limit);
            return ToolBindingResult.Success(JsonSerializer.SerializeToElement(new
            {
                items = await ReadItemsAsync(command, cancellationToken),
            }));
        }
        catch (MemoryRuntimeException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
        catch (Exception exception) when (
            exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            return Failure(
                ToolErrorCodes.ExecutionFailed,
                "Workspace Memory search failed.");
        }
    }

    public async ValueTask<ToolBindingResult> ReadAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            var memoryId = RequiredGuid(arguments, "memoryId");
            var requestedVersion = OptionalPositive(
                arguments,
                "version",
                defaultValue: 0,
                maximum: int.MaxValue);
            await using var connection =
                await _state.OpenReadOnlyConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT m.memory_id, m.current_version, m.title, m.summary,
                       m.tags_json, m.status, m.created_utc, m.updated_utc,
                       v.version, v.content_sha256, v.content_length,
                       v.created_utc
                FROM workspace_memories AS m
                JOIN workspace_memory_versions AS v
                  ON v.memory_id = m.memory_id
                 AND v.version = CASE
                     WHEN $version = 0 THEN m.current_version
                     ELSE $version
                 END
                WHERE m.memory_id = $memory_id;
                """;
            command.Parameters.AddWithValue("$memory_id", memoryId.ToString("D"));
            command.Parameters.AddWithValue("$version", requestedVersion);
            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new MemoryRuntimeException(
                    ToolErrorCodes.NotFound,
                    "Workspace Memory was not found.");
            }

            var contentSha256 = reader.GetString(9);
            var content = await ReadBlobAsync(contentSha256, cancellationToken);
            if (content.Length != reader.GetInt64(10) ||
                !string.Equals(
                    Sha256(content),
                    contentSha256,
                    StringComparison.Ordinal))
            {
                throw new MemoryRuntimeException(
                    ToolErrorCodes.ExecutionFailed,
                    "Workspace Memory content integrity check failed.");
            }

            var body = StrictUtf8.GetString(content);
            return ToolBindingResult.Success(JsonSerializer.SerializeToElement(new
            {
                memoryId = reader.GetString(0),
                currentVersion = reader.GetInt32(1),
                title = reader.GetString(2),
                summary = reader.GetString(3),
                tags = ReadTags(reader.GetString(4)),
                status = reader.GetString(5),
                createdUtc = reader.GetInt64(6),
                updatedUtc = reader.GetInt64(7),
                version = reader.GetInt32(8),
                contentSha256,
                contentLength = reader.GetInt64(10),
                versionCreatedUtc = reader.GetInt64(11),
                body,
            }));
        }
        catch (DecoderFallbackException)
        {
            return Failure(
                ToolErrorCodes.ContentUnsupported,
                "Workspace Memory content is not valid UTF-8.");
        }
        catch (MemoryRuntimeException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
        catch (Exception exception) when (
            exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            return Failure(
                ToolErrorCodes.ExecutionFailed,
                "Workspace Memory read failed.");
        }
    }

    public async ValueTask<ToolBindingResult> WriteAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            var memoryId = RequiredGuid(arguments, "memoryId");
            var expectedVersion = RequiredNonNegative(arguments, "expectedVersion");
            var title = RequiredString(arguments, "title").Trim();
            var summary = RequiredString(arguments, "summary").Trim();
            var tags = RequiredTags(arguments);
            var body = RequiredString(arguments, "body");
            if (title.Length is 0 or > MaximumTitleLength)
            {
                throw Invalid();
            }

            var summaryBytes = StrictUtf8.GetByteCount(summary);
            var bodyBytes = StrictUtf8.GetBytes(body);
            if (summaryBytes > MaximumSummaryBytes ||
                bodyBytes.Length > MaximumBodyBytes)
            {
                return Failure(
                    ToolErrorCodes.InputTooLarge,
                    "Workspace Memory content exceeds the size limit.");
            }

            var contentSha256 = Sha256(bodyBytes);
            await StoreBlobAsync(contentSha256, bodyBytes, cancellationToken);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var nextVersion = 0;
            await _state.WriteCoordinator.ExecuteAsync(
                async (connection, transaction, token) =>
                {
                    var current = await ReadCurrentVersionAsync(
                        connection,
                        transaction,
                        memoryId,
                        token);
                    if (current != expectedVersion)
                    {
                        throw new MemoryRuntimeException(
                            WorkspaceMemoryErrorCodes.VersionConflict,
                            "Workspace Memory version does not match.");
                    }

                    nextVersion = checked(expectedVersion + 1);
                    if (current == 0)
                    {
                        await InsertMemoryAsync(
                            connection,
                            transaction,
                            memoryId,
                            nextVersion,
                            title,
                            summary,
                            tags,
                            now,
                            token);
                    }
                    else
                    {
                        await UpdateMemoryAsync(
                            connection,
                            transaction,
                            memoryId,
                            nextVersion,
                            title,
                            summary,
                            tags,
                            now,
                            token);
                    }

                    await InsertVersionAsync(
                        connection,
                        transaction,
                        memoryId,
                        nextVersion,
                        contentSha256,
                        bodyBytes.Length,
                        now,
                        token);
                },
                cancellationToken);
            return ToolBindingResult.Success(JsonSerializer.SerializeToElement(new
            {
                memoryId,
                version = nextVersion,
                contentSha256,
                contentLength = bodyBytes.Length,
            }));
        }
        catch (EncoderFallbackException)
        {
            return Failure(
                ToolErrorCodes.ContentUnsupported,
                "Workspace Memory content is not valid UTF-8 text.");
        }
        catch (MemoryRuntimeException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
        catch (OverflowException)
        {
            return Failure(
                WorkspaceMemoryErrorCodes.VersionConflict,
                "Workspace Memory version cannot advance.");
        }
        catch (Exception exception) when (
            exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            return Failure(
                ToolErrorCodes.ExecutionFailed,
                "Workspace Memory write failed.");
        }
    }

    public async ValueTask<ToolBindingResult> ArchiveAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            var memoryId = RequiredGuid(arguments, "memoryId");
            var expectedVersion = RequiredNonNegative(arguments, "expectedVersion");
            var archived = false;
            await _state.WriteCoordinator.ExecuteAsync(
                async (connection, transaction, token) =>
                {
                    var current = await ReadCurrentVersionAsync(
                        connection,
                        transaction,
                        memoryId,
                        token);
                    if (current == 0)
                    {
                        throw new MemoryRuntimeException(
                            ToolErrorCodes.NotFound,
                            "Workspace Memory was not found.");
                    }

                    if (current != expectedVersion)
                    {
                        throw new MemoryRuntimeException(
                            WorkspaceMemoryErrorCodes.VersionConflict,
                            "Workspace Memory version does not match.");
                    }

                    await using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText =
                        """
                        UPDATE workspace_memories
                        SET status = 'archived', updated_utc = $updated_utc
                        WHERE memory_id = $memory_id AND status <> 'archived';
                        """;
                    command.Parameters.AddWithValue(
                        "$memory_id",
                        memoryId.ToString("D"));
                    command.Parameters.AddWithValue(
                        "$updated_utc",
                        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    archived = await command.ExecuteNonQueryAsync(token) != 0;
                },
                cancellationToken);
            return ToolBindingResult.Success(JsonSerializer.SerializeToElement(new
            {
                memoryId,
                version = expectedVersion,
                archived,
            }));
        }
        catch (MemoryRuntimeException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
        catch (Exception exception) when (
            exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            return Failure(
                ToolErrorCodes.ExecutionFailed,
                "Workspace Memory archive failed.");
        }
    }

    internal async Task<IReadOnlyList<string>> FindOrphanBlobNamesAsync(
        CancellationToken cancellationToken = default)
    {
        var directory = ContentDirectory();
        if (!Directory.Exists(directory))
        {
            return [];
        }

        await using var connection =
            await _state.OpenReadOnlyConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT DISTINCT content_sha256 FROM workspace_memory_versions;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            referenced.Add(reader.GetString(0));
        }

        return Directory.EnumerateFiles(directory, "*.md")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name =>
                name is not null &&
                !referenced.Contains(name))
            .Order(StringComparer.Ordinal)
            .ToArray()!;
    }

    private async Task StoreBlobAsync(
        string contentSha256,
        byte[] content,
        CancellationToken cancellationToken)
    {
        var directory = ContentDirectory();
        Directory.CreateDirectory(directory);
        var destination = ContentPath(contentSha256);
        if (File.Exists(destination))
        {
            var existing = await ReadBlobAsync(contentSha256, cancellationToken);
            if (!string.Equals(
                    Sha256(existing),
                    contentSha256,
                    StringComparison.Ordinal))
            {
                throw new MemoryRuntimeException(
                    ToolErrorCodes.ExecutionFailed,
                    "Workspace Memory content integrity check failed.");
            }

            return;
        }

        var temporary = Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             new FileStreamOptions
                             {
                                 Mode = FileMode.CreateNew,
                                 Access = FileAccess.Write,
                                 Share = FileShare.None,
                                 Options = FileOptions.Asynchronous |
                                           FileOptions.WriteThrough,
                             }))
            {
                await stream.WriteAsync(content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temporary, destination);
            }
            catch (IOException) when (File.Exists(destination))
            {
                File.Delete(temporary);
            }
        }
        catch
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }

            throw;
        }
    }

    private static async Task<object[]> ReadItemsAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<object>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new
            {
                memoryId = reader.GetString(0),
                currentVersion = reader.GetInt32(1),
                title = reader.GetString(2),
                summary = reader.GetString(3),
                tags = ReadTags(reader.GetString(4)),
                status = reader.GetString(5),
                createdUtc = reader.GetInt64(6),
                updatedUtc = reader.GetInt64(7),
            });
        }

        return items.ToArray();
    }

    private static async Task<int> ReadCurrentVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid memoryId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT current_version
            FROM workspace_memories
            WHERE memory_id = $memory_id;
            """;
        command.Parameters.AddWithValue("$memory_id", memoryId.ToString("D"));
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? 0 : Convert.ToInt32(value);
    }

    private static async Task InsertMemoryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid memoryId,
        int version,
        string title,
        string summary,
        string[] tags,
        long now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO workspace_memories (
                memory_id, current_version, title, summary, tags_json, status,
                normalized_search_text, created_utc, updated_utc)
            VALUES (
                $memory_id, $version, $title, $summary, $tags_json, 'active',
                $search_text, $now, $now);
            """;
        BindMetadata(command, memoryId, version, title, summary, tags, now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateMemoryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid memoryId,
        int version,
        string title,
        string summary,
        string[] tags,
        long now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE workspace_memories
            SET current_version = $version,
                title = $title,
                summary = $summary,
                tags_json = $tags_json,
                normalized_search_text = $search_text,
                updated_utc = $now
            WHERE memory_id = $memory_id;
            """;
        BindMetadata(command, memoryId, version, title, summary, tags, now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid memoryId,
        int version,
        string contentSha256,
        int contentLength,
        long now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO workspace_memory_versions (
                memory_id, version, content_sha256, content_length, created_utc)
            VALUES (
                $memory_id, $version, $content_sha256, $content_length, $now);
            """;
        command.Parameters.AddWithValue("$memory_id", memoryId.ToString("D"));
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$content_sha256", contentSha256);
        command.Parameters.AddWithValue("$content_length", contentLength);
        command.Parameters.AddWithValue("$now", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void BindMetadata(
        SqliteCommand command,
        Guid memoryId,
        int version,
        string title,
        string summary,
        string[] tags,
        long now)
    {
        command.Parameters.AddWithValue("$memory_id", memoryId.ToString("D"));
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$summary", summary);
        command.Parameters.AddWithValue("$tags_json", JsonSerializer.Serialize(tags));
        command.Parameters.AddWithValue(
            "$search_text",
            Normalize(string.Join(' ', new[] { title, summary }.Concat(tags))));
        command.Parameters.AddWithValue("$now", now);
    }

    private string ContentDirectory() =>
        ResolveContained(
            _paths.WorkspaceRoot,
            Path.Combine(
                Path.GetRelativePath(
                    _paths.WorkspaceRoot,
                    _paths.RuntimeDirectory),
                "memory",
                "content"));

    private string ContentPath(string contentSha256) =>
        IsSha256(contentSha256)
            ? ResolveContained(ContentDirectory(), $"{contentSha256}.md")
            : throw new MemoryRuntimeException(
                ToolErrorCodes.ExecutionFailed,
                "Workspace Memory content identity is invalid.");

    private async Task<byte[]> ReadBlobAsync(
        string contentSha256,
        CancellationToken cancellationToken)
    {
        var path = ContentPath(contentSha256);
        var info = new FileInfo(path);
        if (!info.Exists ||
            info.Length > MaximumBodyBytes ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new MemoryRuntimeException(
                ToolErrorCodes.ExecutionFailed,
                "Workspace Memory content integrity check failed.");
        }

        return await File.ReadAllBytesAsync(path, cancellationToken);
    }

    private string ResolveContained(string root, string relativePath)
    {
        try
        {
            return WorkspacePathGuard.ResolveContained(
                root,
                Path.Combine(root, ".opencowork-memory-anchor"),
                relativePath).PhysicalPath;
        }
        catch (WorkspacePathEscapeException)
        {
            throw new MemoryRuntimeException(
                ToolErrorCodes.PathDenied,
                "Workspace Memory path is denied.");
        }
    }

    private static string[] RequiredTags(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty("tags", out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            throw Invalid();
        }

        var tags = value.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String
                ? item.GetString()?.Trim()
                : null)
            .ToArray();
        if (tags.Length > MaximumTags ||
            tags.Any(item =>
                string.IsNullOrEmpty(item) ||
                item.Length > MaximumTagLength))
        {
            throw Invalid();
        }

        return tags
            .Select(item => item!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] ReadTags(string json) =>
        JsonSerializer.Deserialize<string[]>(json) ??
        throw new MemoryRuntimeException(
            ToolErrorCodes.ExecutionFailed,
            "Workspace Memory tags are invalid.");

    private static Guid RequiredGuid(JsonElement arguments, string name)
    {
        var value = RequiredString(arguments, name);
        return Guid.TryParse(value, out var result) && result != Guid.Empty
            ? result
            : throw Invalid();
    }

    private static string RequiredString(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            throw Invalid();
        }

        return value.GetString()!;
    }

    private static int RequiredNonNegative(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty(name, out var value) ||
            !value.TryGetInt32(out var result) ||
            result < 0)
        {
            throw Invalid();
        }

        return result;
    }

    private static int OptionalPositive(
        JsonElement arguments,
        string name,
        int defaultValue,
        int maximum)
    {
        if (!arguments.TryGetProperty(name, out var value))
        {
            return defaultValue;
        }

        if (!value.TryGetInt32(out var result) ||
            result <= 0 ||
            result > maximum)
        {
            throw Invalid();
        }

        return result;
    }

    private static bool OptionalBoolean(
        JsonElement arguments,
        string name,
        bool defaultValue)
    {
        if (!arguments.TryGetProperty(name, out var value))
        {
            return defaultValue;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Invalid(),
        };
    }

    private static string Normalize(string value) =>
        string.Join(
                ' ',
                value.Normalize(NormalizationForm.FormKC)
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool IsSha256(string value) =>
        value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static ToolBindingResult Failure(string code, string message) =>
        ToolBindingResult.Failure(new SessionError(
            code,
            message,
            IsRetryable: false));

    private static MemoryRuntimeException Invalid() =>
        new(ToolErrorCodes.InputInvalid, "Workspace Memory arguments are invalid.");

    private sealed class MemoryRuntimeException(string code, string message)
        : Exception(message)
    {
        public string Code { get; } = code;
    }
}
