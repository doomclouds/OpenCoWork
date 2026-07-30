using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Workspaces;

namespace OpenCoWork.Core.Sessions;

internal sealed class PreparedAutomationTurnStore(
    OpenCoWorkPaths paths,
    ISensitiveDataService sensitiveData) : IAutomationPreparedTurnStore
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<AutomationPreparedTurnWriteResult> PrepareAsync(
        AutomationPreparedTurnSnapshot preparedTurn,
        CancellationToken cancellationToken = default)
    {
        Validate(preparedTurn);
        if (sensitiveData.ContainsSensitiveData(preparedTurn.RenderedPrompt))
        {
            throw new InvalidDataException(
                "Prepared Automation Turn contains sensitive data.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var path = PathFor(preparedTurn.PreparedTurnId, createDirectory: true);
            if (File.Exists(path))
            {
                var existing = await ReadCoreAsync(path, cancellationToken);
                var replay =
                    existing.RequestSha256 == preparedTurn.RequestSha256 &&
                    existing.RenderedPromptSha256 ==
                    preparedTurn.RenderedPromptSha256 &&
                    existing.RenderedPrompt == preparedTurn.RenderedPrompt;
                return new AutomationPreparedTurnWriteResult(
                    replay ? existing : null,
                    IsReplay: replay,
                    IsConflict: !replay);
            }

            var document = new PreparedTurnDocument(
                SchemaVersion,
                preparedTurn.PreparedTurnId,
                preparedTurn.RequestSha256,
                preparedTurn.RenderedPrompt,
                preparedTurn.RenderedPromptSha256,
                preparedTurn.CreatedAtUtc);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
            var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
            GuardPath(temporaryPath);
            try
            {
                await using (var stream = new FileStream(
                                 temporaryPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.Read,
                                 bufferSize: 4096,
                                 FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(bytes, cancellationToken);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporaryPath, path);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }

            return new AutomationPreparedTurnWriteResult(
                preparedTurn,
                IsReplay: false,
                IsConflict: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AutomationPreparedTurnSnapshot?> ReadAsync(
        Guid preparedTurnId,
        CancellationToken cancellationToken = default)
    {
        RequireVersion7(preparedTurnId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var path = PathFor(preparedTurnId, createDirectory: false);
            return File.Exists(path)
                ? await ReadCoreAsync(path, cancellationToken)
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> DeleteAsync(
        Guid preparedTurnId,
        string requestSha256,
        CancellationToken cancellationToken = default)
    {
        RequireVersion7(preparedTurnId);
        RequireSha256(requestSha256, nameof(requestSha256));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var path = PathFor(preparedTurnId, createDirectory: false);
            if (!File.Exists(path))
            {
                return false;
            }

            var existing = await ReadCoreAsync(path, cancellationToken);
            if (!string.Equals(
                    existing.RequestSha256,
                    requestSha256,
                    StringComparison.Ordinal))
            {
                return false;
            }

            GuardPath(path);
            File.Delete(path);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AutomationPreparedTurnSnapshot> ReadCoreAsync(
        string path,
        CancellationToken cancellationToken)
    {
        GuardPath(path);
        var info = new FileInfo(path);
        if (info.Length > AutomationRuntimeLimits.MaximumRenderedPromptBytes + 4096)
        {
            throw new InvalidDataException("Prepared Automation Turn exceeds its size limit.");
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var document = JsonSerializer.Deserialize<PreparedTurnDocument>(bytes, JsonOptions)
                       ?? throw new InvalidDataException(
                           "Prepared Automation Turn is invalid.");
        if (document.SchemaVersion != SchemaVersion)
        {
            throw new InvalidDataException(
                "Prepared Automation Turn schema is unsupported.");
        }

        var snapshot = new AutomationPreparedTurnSnapshot(
            document.PreparedTurnId,
            document.RequestSha256,
            document.RenderedPrompt,
            document.RenderedPromptSha256,
            document.CreatedAtUtc);
        Validate(snapshot);
        if (sensitiveData.ContainsSensitiveData(snapshot.RenderedPrompt))
        {
            throw new InvalidDataException(
                "Prepared Automation Turn contains sensitive data.");
        }

        return snapshot;
    }

    private string PathFor(Guid preparedTurnId, bool createDirectory)
    {
        RequireVersion7(preparedTurnId);
        var directory = Path.Combine(paths.ThreadRecoveryDirectory, "prepared");
        GuardPath(directory);
        if (createDirectory)
        {
            Directory.CreateDirectory(directory);
            GuardPath(directory);
        }

        var path = Path.Combine(directory, $"{preparedTurnId:D}.json");
        GuardPath(path);
        return path;
    }

    private void GuardPath(string path)
    {
        var declaration = Path.Combine(
            paths.RuntimeDirectory,
            ".opencowork-prepared-turn-anchor");
        var relative = Path.GetRelativePath(paths.RuntimeDirectory, path);
        var resolved = WorkspacePathGuard.ResolveContained(
            paths.RuntimeDirectory,
            declaration,
            relative);
        WorkspacePathGuard.RevalidateForWrite(resolved);
    }

    private static void Validate(AutomationPreparedTurnSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        RequireVersion7(snapshot.PreparedTurnId);
        RequireSha256(snapshot.RequestSha256, nameof(snapshot.RequestSha256));
        RequireSha256(
            snapshot.RenderedPromptSha256,
            nameof(snapshot.RenderedPromptSha256));
        if (Encoding.UTF8.GetByteCount(snapshot.RenderedPrompt) >
            AutomationRuntimeLimits.MaximumRenderedPromptBytes ||
            snapshot.CreatedAtUtc.Offset != TimeSpan.Zero ||
            !string.Equals(
                Hash(snapshot.RenderedPrompt),
                snapshot.RenderedPromptSha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Prepared Automation Turn is invalid.");
        }
    }

    private static void RequireVersion7(Guid value)
    {
        if (value.Version != 7)
        {
            throw new ArgumentException("Prepared Turn ID must be UUIDv7.");
        }
    }

    private static void RequireSha256(string value, string parameterName)
    {
        if (value.Length != 64 ||
            value.Any(character =>
                character is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "SHA-256 must contain 64 lowercase hexadecimal characters.",
                parameterName);
        }
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record PreparedTurnDocument(
        int SchemaVersion,
        Guid PreparedTurnId,
        string RequestSha256,
        string RenderedPrompt,
        string RenderedPromptSha256,
        DateTimeOffset CreatedAtUtc);
}
