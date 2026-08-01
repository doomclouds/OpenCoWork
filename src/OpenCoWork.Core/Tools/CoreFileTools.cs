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
    private readonly int? _failPatchCommitAt;
    private readonly string _root;

    public CoreFileTools(OpenCoWorkPaths paths, int? failPatchCommitAt = null)
        : this(paths?.WorkspaceRoot ??
               throw new ArgumentNullException(nameof(paths)))
    {
        _failPatchCommitAt = failPatchCommitAt;
    }

    private CoreFileTools(string root)
    {
        _root = Path.GetFullPath(root);
        _anchor = Path.Combine(_root, ".opencowork-anchor");
    }

    public ValueTask<ToolBindingResult> ListAsync(
        ToolInvocationContext context,
        CancellationToken cancellationToken) =>
        InvokeContextual(context, static (tool, arguments, token) =>
            tool.ListAsync(arguments, token), cancellationToken);

    public ValueTask<ToolBindingResult> ReadAsync(
        ToolInvocationContext context,
        CancellationToken cancellationToken) =>
        InvokeContextual(context, static (tool, arguments, token) =>
            tool.ReadAsync(arguments, token), cancellationToken);

    public ValueTask<ToolBindingResult> WriteAsync(
        ToolInvocationContext context,
        CancellationToken cancellationToken) =>
        InvokeContextual(context, static (tool, arguments, token) =>
            tool.WriteAsync(arguments, token), cancellationToken);

    public ValueTask<ToolBindingResult> ApplyPatchAsync(
        ToolInvocationContext context,
        CancellationToken cancellationToken) =>
        InvokeContextual(context, static (tool, arguments, token) =>
            tool.ApplyPatchAsync(arguments, token), cancellationToken);

    public async ValueTask<ToolBindingResult> ApplyPatchAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var patch = RequiredString(arguments, "patch");
            if (Encoding.UTF8.GetByteCount(patch) >
                ToolRuntimeLimits.MaximumArgumentsBytes)
            {
                return Failure(
                    ToolErrorCodes.InputInvalid,
                    "Patch input exceeds the size limit.");
            }

            var operations = ParsePatch(patch);
            var prepared = new List<PreparedPatch>(operations.Count);
            var paths = new HashSet<string>(
                OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal);
            foreach (var operation in operations)
            {
                var source = ResolvePatchPath(operation.Path);
                var sourceName = Relative(source.LogicalPath);
                if (!paths.Add(sourceName))
                {
                    throw InvalidPatch("Patch contains duplicate paths.");
                }

                ResolvedWorkspacePath? destination = null;
                if (operation.Destination is { } configuredDestination)
                {
                    destination = ResolvePatchPath(configuredDestination);
                    if (!paths.Add(Relative(destination.LogicalPath)))
                    {
                        throw InvalidPatch("Patch contains duplicate paths.");
                    }
                }

                prepared.Add(await PreparePatchAsync(
                    operation,
                    source,
                    destination,
                    cancellationToken));
            }

            var committedPaths = new List<string>();
            var results = new List<PatchResult>();
            var commitIndex = 0;
            foreach (var item in prepared.OrderBy(
                         value => Relative(value.Source.LogicalPath),
                         StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ToolBindingResult commit;
                if (_failPatchCommitAt == commitIndex++)
                {
                    commit = Failure(
                        ToolErrorCodes.ExecutionFailed,
                        "Injected patch commit failure.");
                }
                else switch (item.Operation.Kind)
                    {
                        case PatchKind.Add:
                            commit = await WriteAsync(
                                JsonSerializer.SerializeToElement(new
                                {
                                    path = Relative(item.Source.LogicalPath),
                                    content = item.NewContent,
                                }),
                                cancellationToken);
                            if (commit.IsSuccess)
                            {
                                committedPaths.Add(Relative(item.Source.LogicalPath));
                            }

                            break;
                        case PatchKind.Update:
                            commit = await WriteAsync(
                                JsonSerializer.SerializeToElement(new
                                {
                                    path = Relative(item.Source.LogicalPath),
                                    content = item.NewContent,
                                    expectedSha256 = item.BeforeSha256,
                                }),
                                cancellationToken);
                            if (commit.IsSuccess)
                            {
                                committedPaths.Add(Relative(item.Source.LogicalPath));
                            }

                            break;
                        case PatchKind.Delete:
                            commit = await DeletePreparedAsync(
                                item.Source,
                                item.BeforeSha256!,
                                cancellationToken);
                            if (commit.IsSuccess)
                            {
                                committedPaths.Add(Relative(item.Source.LogicalPath));
                            }

                            break;
                        case PatchKind.Move:
                            commit = await WriteAsync(
                                JsonSerializer.SerializeToElement(new
                                {
                                    path = Relative(item.Destination!.LogicalPath),
                                    content = item.NewContent,
                                }),
                                cancellationToken);
                            if (commit.IsSuccess)
                            {
                                committedPaths.Add(Relative(item.Destination.LogicalPath));
                                commit = await DeletePreparedAsync(
                                    item.Source,
                                    item.BeforeSha256!,
                                    cancellationToken);
                                if (commit.IsSuccess)
                                {
                                    committedPaths.Add(Relative(item.Source.LogicalPath));
                                }
                            }

                            break;
                        default:
                            throw InvalidPatch("Patch operation is invalid.");
                    }

                if (!commit.IsSuccess)
                {
                    return committedPaths.Count == 0
                        ? commit
                        : OutcomeUnknown(committedPaths, prepared
                            .SelectMany(value => value.Destination is null
                                ? [Relative(value.Source.LogicalPath)]
                                : new[]
                                {
                                    Relative(value.Source.LogicalPath),
                                    Relative(value.Destination.LogicalPath),
                                })
                            .Except(committedPaths, paths.Comparer));
                }

                results.Add(new PatchResult(
                    item.Operation.Kind.ToString().ToLowerInvariant(),
                    Relative(item.Source.LogicalPath),
                    item.Destination is null
                        ? null
                        : Relative(item.Destination.LogicalPath),
                    item.BeforeSha256,
                    item.AfterSha256));
            }

            return ToolBindingResult.Success(JsonSerializer.SerializeToElement(new
            {
                operations = results.Select(result => new
                {
                    operation = result.Operation,
                    path = result.Path,
                    destination = result.Destination,
                    beforeSha256 = result.BeforeSha256,
                    afterSha256 = result.AfterSha256,
                    status = "completed",
                }).ToArray(),
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
                ArgumentException or InvalidOperationException or
                DecoderFallbackException or EncoderFallbackException)
        {
            return Failure(
                ToolErrorCodes.ExecutionFailed,
                "Patch operation failed.");
        }
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

    private ValueTask<ToolBindingResult> InvokeContextual(
        ToolInvocationContext context,
        Func<CoreFileTools, JsonElement, CancellationToken,
            ValueTask<ToolBindingResult>> invoke,
        CancellationToken cancellationToken)
    {
        try
        {
            var hasArea = context.Arguments.TryGetProperty("area", out var configured);
            if (hasArea && context.CoWorkProvenance is null)
            {
                throw Denied();
            }

            var area = hasArea ? configured.GetString() : "workspace";
            var root = area switch
            {
                "workspace" => WorkspacePathGuard.ResolveExecutionRoot(
                    context.ExecutionWorkspace,
                    _root),
                "scratchpad" when context.ExecutionWorkspace is not null &&
                                   context.CoWorkProvenance is not null =>
                    Path.GetFullPath(context.ExecutionWorkspace.ScratchpadRoot),
                _ => throw Denied(),
            };
            return invoke(
                new CoreFileTools(root),
                context.Arguments,
                cancellationToken);
        }
        catch (Exception exception) when (
            exception is CoreFileException or InvalidOperationException or
                ArgumentException or NotSupportedException)
        {
            return ValueTask.FromResult(Failure(
                ToolErrorCodes.PathDenied,
                "Workspace path is denied."));
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

    private ResolvedWorkspacePath ResolvePatchPath(string path) =>
        Resolve(JsonSerializer.SerializeToElement(new { path }), write: true);

    private async Task<PreparedPatch> PreparePatchAsync(
        PatchOperation operation,
        ResolvedWorkspacePath source,
        ResolvedWorkspacePath? destination,
        CancellationToken cancellationToken)
    {
        var sourceExists = File.Exists(source.PhysicalPath);
        if (Directory.Exists(source.PhysicalPath))
        {
            throw new CoreFileException(
                ToolErrorCodes.ContentUnsupported,
                "Patch path is not a text file.");
        }

        if (operation.Kind == PatchKind.Add)
        {
            RequireNewFile(source);
            var content = operation.AddedLines.Count == 0
                ? string.Empty
                : string.Join('\n', operation.AddedLines) + "\n";
            var bytes = StrictUtf8.GetBytes(content);
            return new PreparedPatch(
                operation,
                source,
                destination,
                content,
                BeforeSha256: null,
                Sha256(bytes));
        }

        if (!sourceExists)
        {
            throw new CoreFileException(
                ToolErrorCodes.PathNotFound,
                "Patch source file was not found.");
        }

        if (new FileInfo(source.PhysicalPath).Length > 4 * 1024 * 1024)
        {
            throw new CoreFileException(
                ToolErrorCodes.ContentUnsupported,
                "Patch source file exceeds the size limit.");
        }

        var bytesBefore = await File.ReadAllBytesAsync(
            source.PhysicalPath,
            cancellationToken);
        var beforeSha256 = Sha256(bytesBefore);
        if (!string.Equals(
                beforeSha256,
                operation.ExpectedSha256,
                StringComparison.Ordinal))
        {
            throw new CoreFileException(
                ToolErrorCodes.PreconditionFailed,
                "Patch file hash precondition failed.");
        }

        if (operation.Kind == PatchKind.Delete)
        {
            return new PreparedPatch(
                operation,
                source,
                destination,
                NewContent: null,
                beforeSha256,
                AfterSha256: null);
        }

        if (destination is not null)
        {
            RequireNewFile(destination);
        }

        var hadBom = bytesBefore.AsSpan().StartsWith(Utf8Bom);
        string text;
        try
        {
            text = StrictUtf8.GetString(
                hadBom ? bytesBefore.AsSpan(Utf8Bom.Length) : bytesBefore);
        }
        catch (DecoderFallbackException)
        {
            throw new CoreFileException(
                ToolErrorCodes.ContentUnsupported,
                "Patch source is not valid UTF-8 text.");
        }

        if (!IsSupportedText(text))
        {
            throw new CoreFileException(
                ToolErrorCodes.ContentUnsupported,
                "Patch source is not supported text.");
        }

        var lineEnding = text.Contains("\r\n", StringComparison.Ordinal)
            ? "\r\n"
            : "\n";
        var hasFinalNewline = text.EndsWith('\n') || text.EndsWith('\r');
        var normalized = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var lines = normalized.Split('\n').ToList();
        if (hasFinalNewline && lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        ApplyHunks(lines, operation.Hunks);
        var newContent = string.Join(lineEnding, lines) +
                         (hasFinalNewline ? lineEnding : string.Empty);
        if (hadBom)
        {
            newContent = "\uFEFF" + newContent;
        }

        var bytesAfter = StrictUtf8.GetBytes(newContent);
        return new PreparedPatch(
            operation,
            source,
            destination,
            newContent,
            beforeSha256,
            Sha256(bytesAfter));
    }

    private static void RequireNewFile(ResolvedWorkspacePath path)
    {
        if (File.Exists(path.PhysicalPath) || Directory.Exists(path.PhysicalPath))
        {
            throw new CoreFileException(
                ToolErrorCodes.PreconditionFailed,
                "Patch target already exists.");
        }

        var parent = Path.GetDirectoryName(path.PhysicalPath);
        if (parent is null || !Directory.Exists(parent))
        {
            throw new CoreFileException(
                ToolErrorCodes.PathNotFound,
                "Patch target parent was not found.");
        }
    }

    private async ValueTask<ToolBindingResult> DeletePreparedAsync(
        ResolvedWorkspacePath path,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var revalidated = WorkspacePathGuard.RevalidateForWrite(path);
        if (!PathEquals(path.PhysicalPath, revalidated.PhysicalPath) ||
            !File.Exists(path.PhysicalPath) ||
            !string.Equals(
                await HashFileAsync(path.PhysicalPath, cancellationToken),
                expectedSha256,
                StringComparison.Ordinal))
        {
            return Failure(
                ToolErrorCodes.PreconditionFailed,
                "Patch delete precondition failed.");
        }

        File.Delete(path.PhysicalPath);
        return File.Exists(path.PhysicalPath)
            ? Failure(
                ToolErrorCodes.OutcomeUnknown,
                "Patch delete outcome is unknown.")
            : ToolBindingResult.Success(JsonSerializer.SerializeToElement(new
            {
                deleted = true,
            }));
    }

    private static IReadOnlyList<PatchOperation> ParsePatch(string patch)
    {
        if (!IsSupportedText(patch))
        {
            throw InvalidPatch("Patch contains unsupported text.");
        }

        var normalized = patch
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var lines = normalized.Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        if (lines.Count < 2 || lines[0] != "*** Begin Patch")
        {
            throw InvalidPatch("Patch header is invalid.");
        }

        var result = new List<PatchOperation>();
        var index = 1;
        while (index < lines.Count && lines[index] != "*** End Patch")
        {
            var header = lines[index++];
            if (header.StartsWith("*** Add File: ", StringComparison.Ordinal))
            {
                var path = RequiredPatchPath(header[14..]);
                var added = new List<string>();
                while (index < lines.Count &&
                       !lines[index].StartsWith("*** ", StringComparison.Ordinal))
                {
                    if (!lines[index].StartsWith('+'))
                    {
                        throw InvalidPatch("Added file lines must start with '+'.");
                    }

                    added.Add(lines[index++][1..]);
                }

                result.Add(new PatchOperation(
                    PatchKind.Add,
                    path,
                    Destination: null,
                    ExpectedSha256: null,
                    added,
                    Hunks: []));
                continue;
            }

            if (header.StartsWith("*** Delete File: ", StringComparison.Ordinal))
            {
                var path = RequiredPatchPath(header[17..]);
                var expected = ReadExpectedSha256(lines, ref index);
                result.Add(new PatchOperation(
                    PatchKind.Delete,
                    path,
                    Destination: null,
                    expected,
                    AddedLines: [],
                    Hunks: []));
                continue;
            }

            if (!header.StartsWith("*** Update File: ", StringComparison.Ordinal))
            {
                throw InvalidPatch("Patch operation header is invalid.");
            }

            var updatePath = RequiredPatchPath(header[17..]);
            string? destination = null;
            if (index < lines.Count &&
                lines[index].StartsWith("*** Move to: ", StringComparison.Ordinal))
            {
                destination = RequiredPatchPath(lines[index++][13..]);
            }

            var updateExpected = ReadExpectedSha256(lines, ref index);
            var hunks = new List<PatchHunk>();
            while (index < lines.Count &&
                   !lines[index].StartsWith("*** ", StringComparison.Ordinal))
            {
                if (lines[index] != "@@" &&
                    !lines[index].StartsWith("@@ ", StringComparison.Ordinal))
                {
                    throw InvalidPatch("Patch hunk header is invalid.");
                }

                index++;
                var hunkLines = new List<PatchLine>();
                while (index < lines.Count &&
                       !lines[index].StartsWith("*** ", StringComparison.Ordinal) &&
                       lines[index] != "@@" &&
                       !lines[index].StartsWith("@@ ", StringComparison.Ordinal))
                {
                    var value = lines[index++];
                    if (value.Length == 0 || value[0] is not (' ' or '+' or '-'))
                    {
                        throw InvalidPatch("Patch hunk line is invalid.");
                    }

                    hunkLines.Add(new PatchLine(value[0], value[1..]));
                }

                if (hunkLines.Count == 0 ||
                    !hunkLines.Any(line => line.Kind is ' ' or '-'))
                {
                    throw InvalidPatch("Patch hunk has no source context.");
                }

                hunks.Add(new PatchHunk(hunkLines));
            }

            if (hunks.Count == 0)
            {
                throw InvalidPatch("Update patch has no hunks.");
            }

            result.Add(new PatchOperation(
                destination is null ? PatchKind.Update : PatchKind.Move,
                updatePath,
                destination,
                updateExpected,
                AddedLines: [],
                hunks));
        }

        if (result.Count == 0 ||
            index != lines.Count - 1 ||
            lines[index] != "*** End Patch")
        {
            throw InvalidPatch("Patch footer is invalid.");
        }

        return result.AsReadOnly();
    }

    private static string ReadExpectedSha256(
        IReadOnlyList<string> lines,
        ref int index)
    {
        const string prefix = "*** Expected SHA256: ";
        if (index >= lines.Count ||
            !lines[index].StartsWith(prefix, StringComparison.Ordinal))
        {
            throw InvalidPatch("Patch SHA-256 precondition is missing.");
        }

        var value = lines[index++][prefix.Length..];
        if (value.Length != 64 ||
            !value.All(character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            throw InvalidPatch("Patch SHA-256 precondition is invalid.");
        }

        return value;
    }

    private static string RequiredPatchPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim())
        {
            throw InvalidPatch("Patch path is invalid.");
        }

        return value;
    }

    private static void ApplyHunks(
        List<string> source,
        IReadOnlyList<PatchHunk> hunks)
    {
        var cursor = 0;
        foreach (var hunk in hunks)
        {
            var oldLines = hunk.Lines
                .Where(line => line.Kind != '+')
                .Select(line => line.Text)
                .ToArray();
            var newLines = hunk.Lines
                .Where(line => line.Kind != '-')
                .Select(line => line.Text)
                .ToArray();
            var lastStart = source.Count - oldLines.Length;
            var matches = (lastStart < cursor
                    ? Enumerable.Empty<int>()
                    : Enumerable.Range(cursor, lastStart - cursor + 1))
                .Where(candidate => source
                    .Skip(candidate)
                    .Take(oldLines.Length)
                    .SequenceEqual(oldLines, StringComparer.Ordinal))
                .Take(2)
                .ToArray();
            if (matches.Length != 1)
            {
                throw new CoreFileException(
                    ToolErrorCodes.PreconditionFailed,
                    "Patch hunk context precondition failed.");
            }

            var position = matches[0];
            source.RemoveRange(position, oldLines.Length);
            source.InsertRange(position, newLines);
            cursor = position + newLines.Length;
        }
    }

    private static ToolBindingResult OutcomeUnknown(
        IEnumerable<string> committed,
        IEnumerable<string> uncommitted)
    {
        static string Paths(IEnumerable<string> values) =>
            string.Join(", ", values.Distinct().Take(16));
        return Failure(
            ToolErrorCodes.OutcomeUnknown,
            $"Patch outcome is unknown; committed: {Paths(committed)}; uncommitted: {Paths(uncommitted)}.");
    }

    private static CoreFileException InvalidPatch(string message) =>
        new(ToolErrorCodes.InputInvalid, message);

    private enum PatchKind
    {
        Add,
        Update,
        Delete,
        Move,
    }

    private sealed record PatchLine(char Kind, string Text);

    private sealed record PatchHunk(IReadOnlyList<PatchLine> Lines);

    private sealed record PatchOperation(
        PatchKind Kind,
        string Path,
        string? Destination,
        string? ExpectedSha256,
        IReadOnlyList<string> AddedLines,
        IReadOnlyList<PatchHunk> Hunks);

    private sealed record PreparedPatch(
        PatchOperation Operation,
        ResolvedWorkspacePath Source,
        ResolvedWorkspacePath? Destination,
        string? NewContent,
        string? BeforeSha256,
        string? AfterSha256);

    private sealed record PatchResult(
        string Operation,
        string Path,
        string? Destination,
        string? BeforeSha256,
        string? AfterSha256);

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
