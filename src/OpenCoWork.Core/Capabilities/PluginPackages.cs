using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenCoWork.Core.Workspaces;

namespace OpenCoWork.Core.Capabilities;

internal static class PluginErrorCodes
{
    public const string PackageInvalid = "plugin.packageInvalid";
    public const string ArtifactDigestMismatch = "plugin.artifactDigestMismatch";
    public const string StoreDigestMismatch = "plugin.storeDigestMismatch";
    public const string ManifestInvalid = "plugin.manifestInvalid";
    public const string LoadFailed = "plugin.loadFailed";
    public const string UnloadFailed = "plugin.unloadFailed";
}

internal sealed class PluginPackageException(
    string code,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
}

internal sealed record PluginEntryPoint(string Assembly, string Type);

internal sealed record PluginContributions(
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> Providers,
    IReadOnlyList<string> AuthProfiles,
    IReadOnlyList<string> McpServers,
    IReadOnlyList<string> LspServers,
    IReadOnlyList<string> Tools,
    IReadOnlyList<string> Hooks)
{
    public IEnumerable<string> All =>
        Skills.Concat(Providers)
            .Concat(AuthProfiles)
            .Concat(McpServers)
            .Concat(LspServers)
            .Concat(Tools)
            .Concat(Hooks);
}

internal sealed record PluginManifest(
    string Id,
    string Version,
    string DisplayName,
    PluginEntryPoint? EntryPoint,
    PluginContributions Contributions);

internal sealed record StoredPluginPackage(
    PluginManifest Manifest,
    string ContentSha256,
    string PackageDirectory);

internal sealed partial class PluginPackageStore : IDisposable
{
    private const long MaximumArchiveBytes = 50L * 1024 * 1024;
    private const long MaximumExpandedBytes = 200L * 1024 * 1024;
    private const long MaximumFileBytes = 64L * 1024 * 1024;
    private const int MaximumFileCount = 4096;
    private const int MaximumManifestBytes = 1024 * 1024;
    private const int UnixFileTypeMask = 0xF000;
    private const int UnixDirectory = 0x4000;
    private const int UnixRegularFile = 0x8000;
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly CapabilityPersistencePaths _paths;

    public PluginPackageStore(
        CapabilityPersistencePaths paths,
        HttpClient? httpClient = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    public async Task<StoredPluginPackage> StoreLocalAsync(
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var fullPath = Path.GetFullPath(archivePath);
        if (!File.Exists(fullPath) ||
            new FileInfo(fullPath).Length > MaximumArchiveBytes)
        {
            throw InvalidPackage();
        }

        return await StoreArchiveAsync(fullPath, cancellationToken);
    }

    public async Task<StoredPluginPackage> StoreHttpsAsync(
        Uri artifactUri,
        string artifactSha256,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifactUri);
        if (!string.Equals(artifactUri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
            !IsSha256(artifactSha256))
        {
            throw InvalidPackage();
        }

        Directory.CreateDirectory(_paths.UserPluginsDirectory);
        var archivePath = Path.Combine(
            _paths.UserPluginsDirectory,
            $".artifact-{Guid.NewGuid():N}.zip");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, artifactUri);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            var finalUri = response.RequestMessage?.RequestUri ?? artifactUri;
            if (!string.Equals(finalUri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
                !SameOrigin(artifactUri, finalUri))
            {
                throw InvalidPackage();
            }

            await using var source =
                await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var destination = new FileStream(
                archivePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long total = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                total = checked(total + read);
                if (total > MaximumArchiveBytes)
                {
                    throw InvalidPackage();
                }

                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            await destination.FlushAsync(cancellationToken);
            destination.Flush(flushToDisk: true);
            if (!string.Equals(
                    Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(),
                    artifactSha256,
                    StringComparison.Ordinal))
            {
                throw new PluginPackageException(
                    PluginErrorCodes.ArtifactDigestMismatch,
                    "Plugin artifact digest does not match.");
            }

            return await StoreArchiveAsync(archivePath, cancellationToken);
        }
        finally
        {
            TryDeleteFile(archivePath);
        }
    }

    public async Task<string> ValidateStoredAsync(
        string contentSha256,
        CancellationToken cancellationToken = default)
    {
        if (!IsSha256(contentSha256))
        {
            throw InvalidPackage();
        }

        var storePath = _paths.ResolvePluginStore(contentSha256).PhysicalPath;
        var packagePath = Path.Combine(storePath, "package");
        if (!Directory.Exists(packagePath))
        {
            throw new PluginPackageException(
                PluginErrorCodes.StoreDigestMismatch,
                "Plugin package is missing from the content store.");
        }

        var observed = await ComputeContentSha256Async(packagePath, cancellationToken);
        if (!string.Equals(observed, contentSha256, StringComparison.Ordinal))
        {
            throw new PluginPackageException(
                PluginErrorCodes.StoreDigestMismatch,
                "Plugin content store digest does not match.");
        }

        _ = await ReadManifestAsync(packagePath, cancellationToken);
        return observed;
    }

    internal async Task<StoredPluginPackage> OpenStoredAsync(
        string contentSha256,
        CancellationToken cancellationToken = default)
    {
        await ValidateStoredAsync(contentSha256, cancellationToken);
        var packagePath = Path.Combine(
            _paths.ResolvePluginStore(contentSha256).PhysicalPath,
            "package");
        return new StoredPluginPackage(
            await ReadManifestAsync(packagePath, cancellationToken),
            contentSha256,
            packagePath);
    }

    private async Task<StoredPluginPackage> StoreArchiveAsync(
        string archivePath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.UserPluginsDirectory);
        Directory.CreateDirectory(_paths.PluginStoreDirectory);
        var stagingPath = Path.Combine(
            _paths.UserPluginsDirectory,
            $".staging-{Guid.NewGuid():N}");
        var packagePath = Path.Combine(stagingPath, "package");
        Directory.CreateDirectory(packagePath);
        try
        {
            await ExtractAsync(archivePath, packagePath, cancellationToken);
            var manifest = await ReadManifestAsync(packagePath, cancellationToken);
            var contentSha256 =
                await ComputeContentSha256Async(packagePath, cancellationToken);
            var storePath = _paths.ResolvePluginStore(contentSha256).PhysicalPath;
            if (Directory.Exists(storePath))
            {
                await ValidateStoredAsync(contentSha256, cancellationToken);
                return new StoredPluginPackage(
                    manifest,
                    contentSha256,
                    Path.Combine(storePath, "package"));
            }

            Directory.Move(stagingPath, storePath);
            stagingPath = string.Empty;
            return new StoredPluginPackage(
                manifest,
                contentSha256,
                Path.Combine(storePath, "package"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PluginPackageException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                InvalidDataException or JsonException or ArgumentException or
                DecoderFallbackException or OverflowException)
        {
            throw InvalidPackage(exception);
        }
        finally
        {
            if (stagingPath.Length != 0)
            {
                TryDeleteDirectory(stagingPath);
            }
        }
    }

    private static async Task ExtractAsync(
        string archivePath,
        string packagePath,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        var ordinal = new HashSet<string>(StringComparer.Ordinal);
        var caseInsensitive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expandedBytes = 0;
        var fileCount = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = NormalizeEntryPath(entry.FullName);
            if (!ordinal.Add(relativePath) || !caseInsensitive.Add(relativePath))
            {
                throw InvalidPackage();
            }

            var isDirectory = entry.FullName.EndsWith('/');
            ValidateEntryType(entry, isDirectory);
            var destination = Path.GetFullPath(
                relativePath.Replace('/', Path.DirectorySeparatorChar),
                packagePath);
            if (!IsContained(packagePath, destination))
            {
                throw InvalidPackage();
            }

            if (isDirectory)
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            fileCount = checked(fileCount + 1);
            expandedBytes = checked(expandedBytes + entry.Length);
            if (fileCount > MaximumFileCount ||
                entry.Length > MaximumFileBytes ||
                expandedBytes > MaximumExpandedBytes)
            {
                throw InvalidPackage();
            }

            Directory.CreateDirectory(
                Path.GetDirectoryName(destination) ?? throw InvalidPackage());
            await using var source = entry.Open();
            await using var output = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var copied = await CopyBoundedAsync(
                source,
                output,
                MaximumFileBytes,
                cancellationToken);
            if (copied != entry.Length)
            {
                throw InvalidPackage();
            }

            await output.FlushAsync(cancellationToken);
            if (!OperatingSystem.IsWindows() && IsExecutable(entry))
            {
                var mode = File.GetUnixFileMode(destination);
                File.SetUnixFileMode(
                    destination,
                    mode | UnixFileMode.UserExecute);
            }
        }
    }

    private static async Task<long> CopyBoundedAsync(
        Stream source,
        Stream destination,
        long limit,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return total;
            }

            total = checked(total + read);
            if (total > limit)
            {
                throw InvalidPackage();
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static async Task<string> ComputeContentSha256Async(
        string packagePath,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var intBuffer = new byte[sizeof(int)];
        var longBuffer = new byte[sizeof(long)];
        foreach (var file in EnumerateFiles(packagePath)
                     .OrderBy(item => item.RelativePath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pathBytes = StrictUtf8.GetBytes(file.RelativePath);
            BinaryPrimitives.WriteInt32BigEndian(intBuffer, pathBytes.Length);
            hash.AppendData(intBuffer);
            hash.AppendData(pathBytes);
            hash.AppendData([file.Executable ? (byte)1 : (byte)0]);
            BinaryPrimitives.WriteInt64BigEndian(longBuffer, file.Length);
            hash.AppendData(longBuffer);
            await using var stream = new FileStream(
                file.FullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = new byte[81920];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, read);
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static IEnumerable<ContentFile> EnumerateFiles(string packagePath)
    {
        var pending = new Stack<string>();
        pending.Push(packagePath);
        var files = 0;
        long bytes = 0;
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in new DirectoryInfo(directory)
                         .EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw InvalidPackage();
                }

                if (entry is DirectoryInfo child)
                {
                    pending.Push(child.FullName);
                    continue;
                }

                if (entry is not FileInfo file)
                {
                    throw InvalidPackage();
                }

                files = checked(files + 1);
                bytes = checked(bytes + file.Length);
                if (files > MaximumFileCount ||
                    file.Length > MaximumFileBytes ||
                    bytes > MaximumExpandedBytes)
                {
                    throw InvalidPackage();
                }

                var relative = Path.GetRelativePath(packagePath, file.FullName)
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .Normalize(NormalizationForm.FormC);
                yield return new ContentFile(
                    relative,
                    file.FullName,
                    file.Length,
                    !OperatingSystem.IsWindows() &&
                    (File.GetUnixFileMode(file.FullName) &
                     (UnixFileMode.UserExecute |
                      UnixFileMode.GroupExecute |
                      UnixFileMode.OtherExecute)) != 0);
            }
        }
    }

    private static async Task<PluginManifest> ReadManifestAsync(
        string packagePath,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(packagePath, "opencowork.plugin.json");
        if (!File.Exists(path) ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new PluginPackageException(
                PluginErrorCodes.ManifestInvalid,
                "Plugin manifest is invalid.");
        }

        var bytes = await ReadBoundedAsync(path, MaximumManifestBytes, cancellationToken);
        try
        {
            _ = StrictUtf8.GetString(bytes);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            EnsureUniqueProperties(document.RootElement);
            var root = document.RootElement;
            RequireObject(
                root,
                [
                    "schemaVersion",
                    "hostApiVersion",
                    "id",
                    "version",
                    "displayName",
                    "entryPoint",
                    "contributions",
                ],
                [
                    "schemaVersion",
                    "hostApiVersion",
                    "id",
                    "version",
                    "displayName",
                    "contributions",
                ]);
            if (RequireInt32(root, "schemaVersion") != 1 ||
                RequireInt32(root, "hostApiVersion") != 1)
            {
                throw InvalidManifest();
            }

            var id = RequireString(root, "id");
            var version = RequireString(root, "version");
            if (!PluginIdPattern().IsMatch(id) ||
                id.StartsWith("opencowork/", StringComparison.Ordinal) ||
                !SemVerPattern().IsMatch(version))
            {
                throw InvalidManifest();
            }

            PluginEntryPoint? entryPoint = null;
            if (root.TryGetProperty("entryPoint", out var entry))
            {
                RequireObject(
                    entry,
                    ["assembly", "type"],
                    ["assembly", "type"]);
                entryPoint = new PluginEntryPoint(
                    RequirePackagePath(entry, "assembly"),
                    RequireString(entry, "type"));
            }

            var contributionElement = root.GetProperty("contributions");
            var contributionNames = new[]
            {
                "skills",
                "providers",
                "authProfiles",
                "mcpServers",
                "lspServers",
                "tools",
                "hooks",
            };
            RequireObject(
                contributionElement,
                contributionNames,
                contributionNames);
            var contributions = new PluginContributions(
                RequirePaths(contributionElement, "skills"),
                RequirePaths(contributionElement, "providers"),
                RequirePaths(contributionElement, "authProfiles"),
                RequirePaths(contributionElement, "mcpServers"),
                RequirePaths(contributionElement, "lspServers"),
                RequirePaths(contributionElement, "tools"),
                RequirePaths(contributionElement, "hooks"));
            var allPaths = contributions.All
                .Append(entryPoint?.Assembly)
                .Where(item => item is not null)
                .Cast<string>()
                .ToArray();
            if (allPaths.Distinct(StringComparer.Ordinal).Count() != allPaths.Length ||
                allPaths.Any(relative => !IsRegularContainedFile(packagePath, relative)) ||
                (contributions.Tools.Count != 0 || contributions.Hooks.Count != 0) !=
                (entryPoint is not null))
            {
                throw InvalidManifest();
            }

            return new PluginManifest(
                id,
                version,
                RequireString(root, "displayName"),
                entryPoint,
                contributions);
        }
        catch (PluginPackageException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or DecoderFallbackException or
                InvalidOperationException or ArgumentException)
        {
            throw InvalidManifest(exception);
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        string path,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[limit + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (read == 0)
            {
                return buffer[..total];
            }

            total += read;
        }

        throw InvalidManifest();
    }

    private static IReadOnlyList<string> RequirePaths(JsonElement parent, string name)
    {
        var value = parent.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw InvalidManifest();
        }

        var result = value.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String
                ? NormalizePackagePath(item.GetString()!)
                : throw InvalidManifest())
            .ToArray();
        if (result.Distinct(StringComparer.Ordinal).Count() != result.Length)
        {
            throw InvalidManifest();
        }

        return Array.AsReadOnly(result);
    }

    private static string RequirePackagePath(JsonElement parent, string name) =>
        NormalizePackagePath(RequireString(parent, name));

    private static string NormalizePackagePath(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormC);
        if (normalized.Length == 0 ||
            normalized != value ||
            normalized.Contains('\\') ||
            normalized.Contains('\0') ||
            normalized.StartsWith('/') ||
            Path.IsPathRooted(normalized) ||
            normalized.Split('/').Any(part => part is "" or "." or ".."))
        {
            throw InvalidManifest();
        }

        return normalized;
    }

    private static string NormalizeEntryPath(string value)
    {
        var directory = value.EndsWith('/');
        var trimmed = directory ? value.TrimEnd('/') : value;
        try
        {
            return NormalizePackagePath(trimmed);
        }
        catch (PluginPackageException)
        {
            throw InvalidPackage();
        }
    }

    private static bool IsRegularContainedFile(string packagePath, string relativePath)
    {
        var fullPath = Path.GetFullPath(
            relativePath.Replace('/', Path.DirectorySeparatorChar),
            packagePath);
        return IsContained(packagePath, fullPath) &&
               File.Exists(fullPath) &&
               (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) == 0;
    }

    private static void ValidateEntryType(ZipArchiveEntry entry, bool directory)
    {
        var unixType = (entry.ExternalAttributes >> 16) & UnixFileTypeMask;
        var expected = directory ? UnixDirectory : UnixRegularFile;
        if (unixType != 0 && unixType != expected ||
            (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
        {
            throw InvalidPackage();
        }
    }

    private static bool IsExecutable(ZipArchiveEntry entry)
    {
        var mode = (entry.ExternalAttributes >> 16) & 0x1FF;
        return (mode & 0x49) != 0;
    }

    private static bool IsContained(string root, string path)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.GetFullPath(path);
        return string.Equals(
                   fullRoot,
                   fullPath,
                   OperatingSystem.IsWindows()
                       ? StringComparison.OrdinalIgnoreCase
                       : StringComparison.Ordinal) ||
               fullPath.StartsWith(
                   fullRoot + Path.DirectorySeparatorChar,
                   OperatingSystem.IsWindows()
                       ? StringComparison.OrdinalIgnoreCase
                       : StringComparison.Ordinal);
    }

    private static void RequireObject(
        JsonElement element,
        IReadOnlyCollection<string> allowed,
        IReadOnlyCollection<string> required)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw InvalidManifest();
        }

        var properties = element.EnumerateObject().Select(item => item.Name).ToArray();
        if (properties.Any(name => !allowed.Contains(name, StringComparer.Ordinal)) ||
            required.Any(name => !properties.Contains(name, StringComparer.Ordinal)))
        {
            throw InvalidManifest();
        }
    }

    private static string RequireString(JsonElement parent, string name)
    {
        var value = parent.GetProperty(name);
        var result = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return !string.IsNullOrWhiteSpace(result) &&
               result == result.Trim() &&
               !result.Any(char.IsControl)
            ? result
            : throw InvalidManifest();
    }

    private static int RequireInt32(JsonElement parent, string name)
    {
        var value = parent.GetProperty(name);
        return value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt32(out var result)
            ? result
            : throw InvalidManifest();
    }

    private static void EnsureUniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw InvalidManifest();
                }

                EnsureUniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                EnsureUniqueProperties(item);
            }
        }
    }

    private static bool SameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static PluginPackageException InvalidPackage(Exception? inner = null) =>
        new(
            PluginErrorCodes.PackageInvalid,
            "Plugin package is invalid.",
            inner);

    private static PluginPackageException InvalidManifest(Exception? inner = null) =>
        new(
            PluginErrorCodes.ManifestInvalid,
            "Plugin manifest is invalid.",
            inner);

    [GeneratedRegex(
        @"^[a-z0-9](?:[a-z0-9.-]{0,62})/[a-z0-9](?:[a-z0-9.-]{0,62})$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex PluginIdPattern();

    [GeneratedRegex(
        @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-((?:0|[1-9][0-9]*|[0-9]*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[a-zA-Z-][0-9a-zA-Z-]*))*))?(?:\+([0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*))?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SemVerPattern();

    private sealed record ContentFile(
        string RelativePath,
        string FullPath,
        long Length,
        bool Executable);
}
