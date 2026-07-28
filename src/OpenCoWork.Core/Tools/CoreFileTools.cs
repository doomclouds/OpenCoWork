using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Workspaces;

namespace OpenCoWork.Core.Tools;

internal sealed class CoreFileTools
{
    private const int DefaultLineCount = 200;
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly string _anchor;
    private readonly string _root;

    public CoreFileTools(OpenCoWorkPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _root = paths.WorkspaceRoot;
        _anchor = Path.Combine(_root, ".opencowork-anchor");
    }

    public ValueTask<ToolBindingResult> ListAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Resolve(arguments, write: false);
            if (!Directory.Exists(path.PhysicalPath))
            {
                return ValueTask.FromResult(
                    File.Exists(path.PhysicalPath)
                        ? Failure(
                            ToolErrorCodes.ContentUnsupported,
                            "Workspace path is not a directory.")
                        : Failure(
                            ToolErrorCodes.PathNotFound,
                            "Workspace path was not found."));
            }

            var relativeDirectory = Relative(path.LogicalPath);
            var entries = new List<(string Path, JsonElement Value)>();
            var outputBytes = Encoding.UTF8.GetByteCount("""{"entries":[]}""");
            foreach (var entry in new DirectoryInfo(path.PhysicalPath)
                         .EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = relativeDirectory == "."
                    ? Normalize(entry.Name)
                    : relativeDirectory + "/" + Normalize(entry.Name);
                if (IsDenied(relative, write: false))
                {
                    continue;
                }

                var attributes = entry.Attributes;
                var type = (attributes & FileAttributes.ReparsePoint) != 0
                    ? "link"
                    : (attributes & FileAttributes.Directory) != 0
                        ? "directory"
                        : "file";
                var byteCount = type == "file"
                    ? ((FileInfo)entry).Length
                    : 0L;
                var value = JsonSerializer.SerializeToElement(new
                {
                    name = entry.Name,
                    type,
                    byteCount,
                    lastWriteTimeUtc = entry.LastWriteTimeUtc.ToString(
                        "O",
                        CultureInfo.InvariantCulture),
                });
                outputBytes = checked(
                    outputBytes +
                    Encoding.UTF8.GetByteCount(value.GetRawText()) +
                    1);
                if (outputBytes > ToolRuntimeLimits.MaximumBindingResultBytes)
                {
                    return ValueTask.FromResult(Failure(
                        ToolErrorCodes.OutputLimitExceeded,
                        "Directory listing exceeds the size limit."));
                }

                entries.Add((relative, value));
            }

            return ValueTask.FromResult(ToolBindingResult.Success(
                JsonSerializer.SerializeToElement(new
                {
                    entries = entries
                        .OrderBy(entry => entry.Path, StringComparer.Ordinal)
                        .Select(entry => entry.Value)
                        .ToArray(),
                })));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CoreFileException exception)
        {
            return ValueTask.FromResult(Failure(exception.Code, exception.Message));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException)
        {
            return ValueTask.FromResult(Failure(
                ToolErrorCodes.ExecutionFailed,
                "File operation failed."));
        }
    }

    public async ValueTask<ToolBindingResult> ReadAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Resolve(arguments, write: false);
            if (Directory.Exists(path.PhysicalPath))
            {
                return Failure(
                    ToolErrorCodes.ContentUnsupported,
                    "Workspace path is not a text file.");
            }

            if (!File.Exists(path.PhysicalPath))
            {
                return Failure(
                    ToolErrorCodes.PathNotFound,
                    "Workspace path was not found.");
            }

            var bytes = await ReadBoundedAsync(
                path.PhysicalPath,
                cancellationToken);
            if (bytes is null)
            {
                return Failure(
                    ToolErrorCodes.OutputLimitExceeded,
                    "File content exceeds the size limit.");
            }

            var textBytes = bytes.AsSpan();
            if (textBytes.StartsWith(Utf8Bom))
            {
                textBytes = textBytes[Utf8Bom.Length..];
            }

            string content;
            try
            {
                content = StrictUtf8.GetString(textBytes);
            }
            catch (DecoderFallbackException)
            {
                return Failure(
                    ToolErrorCodes.ContentUnsupported,
                    "File content is not valid UTF-8 text.");
            }

            if (!IsSupportedText(content))
            {
                return Failure(
                    ToolErrorCodes.ContentUnsupported,
                    "File content is not supported text.");
            }

            content = content
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');
            var lines = content.Length == 0
                ? []
                : content.Split('\n');
            if (lines.Length > 0 &&
                lines[^1].Length == 0 &&
                content.EndsWith('\n'))
            {
                lines = lines[..^1];
            }

            var startLine = OptionalPositive(arguments, "startLine", 1);
            var lineCount = OptionalPositive(
                arguments,
                "lineCount",
                DefaultLineCount);
            var startIndex = Math.Min(startLine - 1, lines.Length);
            var selectedCount = Math.Min(lineCount, lines.Length - startIndex);
            var selected = string.Join(
                '\n',
                lines.AsSpan(startIndex, selectedCount).ToArray());
            var output = JsonSerializer.SerializeToElement(new
            {
                path = Relative(path.LogicalPath),
                startLine,
                endLine = selectedCount == 0
                    ? startLine - 1
                    : startLine + selectedCount - 1,
                hasMore = startIndex + selectedCount < lines.Length,
                content = selected,
                sha256 = Sha256(bytes),
            });
            if (JsonSerializer.SerializeToUtf8Bytes(output).Length >
                ToolRuntimeLimits.MaximumBindingResultBytes)
            {
                return Failure(
                    ToolErrorCodes.OutputLimitExceeded,
                    "File content exceeds the size limit.");
            }

            return ToolBindingResult.Success(output);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CoreFileException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or OverflowException)
        {
            return Failure(
                ToolErrorCodes.ExecutionFailed,
                "File operation failed.");
        }
    }

    public async ValueTask<ToolBindingResult> WriteAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        string? temporaryPath = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Resolve(arguments, write: true);
            if (Directory.Exists(path.PhysicalPath))
            {
                return Failure(
                    ToolErrorCodes.ContentUnsupported,
                    "Workspace path is not a file.");
            }

            var parent = Path.GetDirectoryName(path.PhysicalPath);
            if (parent is null || !Directory.Exists(parent))
            {
                return Failure(
                    ToolErrorCodes.PathNotFound,
                    "Parent directory was not found.");
            }

            var content = RequiredString(arguments, "content");
            if (!IsSupportedText(content))
            {
                return Failure(
                    ToolErrorCodes.ContentUnsupported,
                    "File content is not supported text.");
            }

            byte[] bytes;
            try
            {
                bytes = StrictUtf8.GetBytes(content);
            }
            catch (EncoderFallbackException)
            {
                return Failure(
                    ToolErrorCodes.ContentUnsupported,
                    "File content is not valid UTF-8 text.");
            }

            var expectedSha256 = arguments.TryGetProperty(
                "expectedSha256",
                out var expected)
                ? expected.GetString()
                : null;
            var existed = File.Exists(path.PhysicalPath);
            if (existed != (expectedSha256 is not null))
            {
                return Failure(
                    ToolErrorCodes.PreconditionFailed,
                    "File precondition failed.");
            }

            if (existed &&
                !string.Equals(
                    await HashFileAsync(path.PhysicalPath, cancellationToken),
                    expectedSha256,
                    StringComparison.Ordinal))
            {
                return Failure(
                    ToolErrorCodes.PreconditionFailed,
                    "File precondition failed.");
            }

            var beforeTemporaryWrite =
                WorkspacePathGuard.RevalidateForWrite(path);
            if (!PathEquals(
                    path.PhysicalPath,
                    beforeTemporaryWrite.PhysicalPath))
            {
                return Failure(
                    ToolErrorCodes.PathDenied,
                    "Workspace path changed before write.");
            }

            temporaryPath = Path.Combine(
                parent,
                $".opencowork-write-{Guid.NewGuid():N}.tmp");
            await using (var temporary = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81920,
                             FileOptions.Asynchronous |
                             FileOptions.SequentialScan))
            {
                await temporary.WriteAsync(bytes, cancellationToken);
                await temporary.FlushAsync(cancellationToken);
                temporary.Flush(flushToDisk: true);
            }

            var revalidated = WorkspacePathGuard.RevalidateForWrite(path);
            if (!PathEquals(path.PhysicalPath, revalidated.PhysicalPath) ||
                !PathEquals(
                    parent,
                    Path.GetDirectoryName(revalidated.PhysicalPath) ?? string.Empty))
            {
                return Failure(
                    ToolErrorCodes.PathDenied,
                    "Workspace path changed before write.");
            }

            if (existed)
            {
                if (!File.Exists(path.PhysicalPath) ||
                    !string.Equals(
                        await HashFileAsync(
                            path.PhysicalPath,
                            cancellationToken),
                        expectedSha256,
                        StringComparison.Ordinal))
                {
                    return Failure(
                        ToolErrorCodes.PreconditionFailed,
                        "File precondition failed.");
                }
            }
            else if (File.Exists(path.PhysicalPath) ||
                     Directory.Exists(path.PhysicalPath))
            {
                return Failure(
                    ToolErrorCodes.PreconditionFailed,
                    "File precondition failed.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var newSha256 = Sha256(bytes);
            try
            {
                if (existed)
                {
                    File.Replace(
                        temporaryPath,
                        path.PhysicalPath,
                        destinationBackupFileName: null);
                }
                else
                {
                    File.Move(
                        temporaryPath,
                        path.PhysicalPath,
                        overwrite: false);
                }

                temporaryPath = null;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                var observed = File.Exists(path.PhysicalPath)
                    ? await HashFileAsync(
                        path.PhysicalPath,
                        CancellationToken.None)
                    : null;
                if (!string.Equals(
                        observed,
                        newSha256,
                        StringComparison.Ordinal))
                {
                    return Failure(
                        ToolErrorCodes.OutcomeUnknown,
                        "File write outcome is unknown.");
                }
            }

            return ToolBindingResult.Success(JsonSerializer.SerializeToElement(new
            {
                path = Relative(path.LogicalPath),
                created = !existed,
                byteCount = bytes.Length,
                sha256 = newSha256,
            }));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CoreFileException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or InvalidOperationException)
        {
            return Failure(
                ToolErrorCodes.ExecutionFailed,
                "File operation failed.");
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private ResolvedWorkspacePath Resolve(JsonElement arguments, bool write)
    {
        var configuredPath = RequiredString(arguments, "path");
        if (string.IsNullOrWhiteSpace(configuredPath) ||
            Path.IsPathRooted(configuredPath))
        {
            throw Denied();
        }

        try
        {
            var path = WorkspacePathGuard.ResolveContained(
                _root,
                _anchor,
                configuredPath);
            if (IsDenied(Relative(path.LogicalPath), write))
            {
                throw Denied();
            }

            return path;
        }
        catch (WorkspacePathEscapeException)
        {
            throw Denied();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException)
        {
            throw Denied();
        }
    }

    private string Relative(string logicalPath)
    {
        var relative = Normalize(Path.GetRelativePath(_root, logicalPath));
        return relative.Length == 0 ? "." : relative;
    }

    private static bool IsDenied(string relativePath, bool write)
    {
        if (IsSameOrDescendant(relativePath, ".git") ||
            IsSameOrDescendant(relativePath, ".opencowork/runtime") ||
            PathEquals(relativePath, ".opencowork/config.local.jsonc"))
        {
            return true;
        }

        return write && IsSameOrDescendant(relativePath, ".opencowork");
    }

    private static bool IsSameOrDescendant(string path, string denied) =>
        PathEquals(path, denied) ||
        path.StartsWith(
            denied + "/",
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static bool PathEquals(string left, string right) =>
        string.Equals(
            left,
            right,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static string Normalize(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');

    private static bool IsSupportedText(string content) =>
        !content.Any(character =>
            char.IsControl(character) &&
            character is not '\t' and not '\n' and not '\r');

    private static string RequiredString(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String)
        {
            throw new CoreFileException(
                ToolErrorCodes.InputInvalid,
                "Tool arguments are invalid.");
        }

        return value.GetString()!;
    }

    private static int OptionalPositive(
        JsonElement arguments,
        string name,
        int defaultValue)
    {
        if (!arguments.TryGetProperty(name, out var value))
        {
            return defaultValue;
        }

        if (!value.TryGetInt32(out var result) || result <= 0)
        {
            throw new CoreFileException(
                ToolErrorCodes.InputInvalid,
                "Tool arguments are invalid.");
        }

        return result;
    }

    private static async Task<byte[]?> ReadBoundedAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var file = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var bytes = new byte[ToolRuntimeLimits.MaximumBindingResultBytes + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await file.ReadAsync(
                bytes.AsMemory(length, bytes.Length - length),
                cancellationToken);
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        if (length > ToolRuntimeLimits.MaximumBindingResultBytes)
        {
            return null;
        }

        return bytes.AsSpan(0, length).ToArray();
    }

    private static async Task<string> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var file = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(file, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static ToolBindingResult Failure(string code, string message) =>
        ToolBindingResult.Failure(new SessionError(
            code,
            message,
            IsRetryable: false));

    private static CoreFileException Denied() =>
        new(ToolErrorCodes.PathDenied, "Workspace path is denied.");

    private sealed class CoreFileException(string code, string message)
        : Exception(message)
    {
        public string Code { get; } = code;
    }
}
