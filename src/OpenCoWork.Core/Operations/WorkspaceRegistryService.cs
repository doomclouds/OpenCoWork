using System.Text.Json;
using OpenCoWork.Abstractions;

namespace OpenCoWork.Core.Operations;

internal sealed class WorkspaceRegistryService : IWorkspaceRegistryService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _directory;
    private readonly string _path;
    private readonly string _lockPath;
    private readonly TimeProvider _timeProvider;

    public WorkspaceRegistryService(TimeProvider timeProvider)
        : this(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            timeProvider)
    {
    }

    internal WorkspaceRegistryService(string userRoot, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userRoot);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _directory = Path.Combine(Path.GetFullPath(userRoot), ".opencowork");
        _path = Path.Combine(_directory, "workspaces.json");
        _lockPath = Path.Combine(_directory, "workspaces.lock");
        _timeProvider = timeProvider;
    }

    public async Task<WorkspaceRegistration> UpsertAsync(
        Guid workspaceId,
        string workspaceRoot,
        string dataRoot,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        RequireVersionSeven(workspaceId, nameof(workspaceId));
        var normalizedWorkspace = RequireDirectory(workspaceRoot, nameof(workspaceRoot));
        var normalizedData = RequireDirectory(dataRoot, nameof(dataRoot));
        if (!IsClean(displayName, 256))
        {
            throw new ArgumentException("Workspace display name is invalid.", nameof(displayName));
        }

        Directory.CreateDirectory(_directory);
        RejectLink(_directory);
        await using var registryLock = await AcquireLockAsync(cancellationToken);
        var items = await ReadCoreAsync(strictRoot: true, cancellationToken);
        var now = _timeProvider.GetUtcNow();
        var existing = items.FirstOrDefault(item => item.WorkspaceId == workspaceId);
        var lastSeen = existing is null || now >= existing.LastSeenAtUtc
            ? now
            : existing.LastSeenAtUtc;
        var registration = new WorkspaceRegistration(
            workspaceId,
            normalizedWorkspace,
            normalizedData,
            displayName.Trim(),
            existing?.RegisteredAtUtc ?? now,
            lastSeen);
        items.RemoveAll(item => item.WorkspaceId == workspaceId);
        items.Add(registration);
        await WriteAsync(items, cancellationToken);
        return registration;
    }

    public async Task<IReadOnlyList<WorkspaceRegistration>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_directory))
        {
            return [];
        }

        RejectLink(_directory);
        var items = await ReadCoreAsync(strictRoot: false, cancellationToken);
        return items
            .GroupBy(item => item.WorkspaceId)
            .Select(group => group.OrderByDescending(item => item.LastSeenAtUtc).First())
            .OrderBy(item => item.DisplayName, StringComparer.Ordinal)
            .ThenBy(item => item.WorkspaceId)
            .ToArray();
    }

    private async Task<List<WorkspaceRegistration>> ReadCoreAsync(
        bool strictRoot,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return [];
        }

        RejectLink(_path);
        try
        {
            await using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var document = await JsonDocument.ParseAsync(
                stream,
                new JsonDocumentOptions { MaxDepth = 16 },
                cancellationToken);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("schemaVersion", out var version) ||
                version.ValueKind != JsonValueKind.Number ||
                version.GetInt32() != 1 ||
                !root.TryGetProperty("workspaces", out var workspaces) ||
                workspaces.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Workspace Registry root is invalid.");
            }

            var items = new List<WorkspaceRegistration>();
            foreach (var element in workspaces.EnumerateArray())
            {
                if (TryRead(element, out var item))
                {
                    items.Add(item);
                }
            }
            return items;
        }
        catch (Exception exception) when (
            !strictRoot && exception is JsonException or IOException or InvalidDataException)
        {
            return [];
        }
    }

    private async Task WriteAsync(
        IReadOnlyList<WorkspaceRegistration> items,
        CancellationToken cancellationToken)
    {
        var temporary = Path.Combine(_directory, $"workspaces-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    new RegistryDocument(1, items.OrderBy(item => item.WorkspaceId).ToArray()),
                    JsonOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    temporary,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private async Task<FileStream> AcquireLockAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                RejectLink(_lockPath);
                return new FileStream(
                    _lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.Asynchronous);
            }
            catch (IOException) when (attempt < 200)
            {
                await Task.Delay(25, cancellationToken);
            }
        }
    }

    private static bool TryRead(JsonElement element, out WorkspaceRegistration item)
    {
        item = null!;
        try
        {
            var workspaceId = element.GetProperty("workspaceId").GetGuid();
            var workspaceRoot = element.GetProperty("workspaceRoot").GetString();
            var dataRoot = element.GetProperty("dataRoot").GetString();
            var displayName = element.GetProperty("displayName").GetString();
            var registered = element.GetProperty("registeredAtUtc").GetDateTimeOffset();
            var lastSeen = element.GetProperty("lastSeenAtUtc").GetDateTimeOffset();
            if (workspaceId.Version != 7 ||
                !IsAbsolute(workspaceRoot) ||
                !IsAbsolute(dataRoot) ||
                !IsClean(displayName, 256) ||
                registered.Offset != TimeSpan.Zero ||
                lastSeen.Offset != TimeSpan.Zero ||
                lastSeen < registered)
            {
                return false;
            }

            item = new WorkspaceRegistration(
                workspaceId,
                Path.GetFullPath(workspaceRoot!),
                Path.GetFullPath(dataRoot!),
                displayName!,
                registered,
                lastSeen);
            return true;
        }
        catch (Exception exception) when (
            exception is KeyNotFoundException or InvalidOperationException or
                FormatException or ArgumentException)
        {
            return false;
        }
    }

    private static string RequireDirectory(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Registered directory does not exist: {fullPath}");
        }
        RejectLink(fullPath);
        return fullPath;
    }

    private static void RejectLink(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return;
        }
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Workspace Registry paths cannot be links or reparse points.");
        }
    }

    private static bool IsAbsolute(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path);

    private static bool IsClean(string? value, int maximumLength) =>
        value is { Length: > 0 } &&
        value.Length <= maximumLength &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !value.Any(char.IsControl);

    private static void RequireVersionSeven(Guid value, string parameterName)
    {
        if (value.Version != 7)
        {
            throw new ArgumentException("Workspace ID must be a UUIDv7.", parameterName);
        }
    }

    private sealed record RegistryDocument(
        int SchemaVersion,
        IReadOnlyList<WorkspaceRegistration> Workspaces);
}
