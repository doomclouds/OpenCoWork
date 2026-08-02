using System.Buffers;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Workspaces;

namespace OpenCoWork.Core.Gateway;

public sealed class GatewayMediaStore(
    OpenCoWorkPaths paths,
    IWorkspaceStateStore state)
{
    private const int MaximumAttachments = 8;
    private const int MaximumAttachmentBytes = 8 * 1024 * 1024;
    private const int MaximumTotalBytes = 16 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<ChannelMediaChunk> ReadAsync(
        ChannelMediaReadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateVersionSeven(request.MediaId, nameof(request.MediaId));
        ArgumentOutOfRangeException.ThrowIfNegative(request.Offset);
        if (request.Length is < 1 or > 256 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        var metadata = await state.ReadAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT relative_path, media_type, content_length
                    FROM channel_media WHERE media_id = $mediaId;
                    """;
                Add(command, "$mediaId", request.MediaId.ToString("D"));
                await using var reader = await command.ExecuteReaderAsync(token);
                return await reader.ReadAsync(token)
                    ? new MediaReadMetadata(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetInt64(2))
                    : null;
            },
            cancellationToken);
        if (metadata is null)
        {
            throw new ChannelServiceException(
                ChannelErrorCodes.MediaNotFound,
                "Channel media was not found.");
        }
        if (request.Offset > metadata.ContentLength)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        var root = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(paths.ExternalChannelMediaDirectory));
        var path = Path.GetFullPath(metadata.RelativePath, root);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, comparison))
        {
            throw new IOException("Media path escapes its root.");
        }
        EnsureSafeFile(path);
        if (new FileInfo(path).Length != metadata.ContentLength)
        {
            throw new IOException("Media length does not match its metadata.");
        }

        var count = (int)Math.Min(request.Length, metadata.ContentLength - request.Offset);
        var data = new byte[count];
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        stream.Position = request.Offset;
        await stream.ReadExactlyAsync(data, cancellationToken);
        return new ChannelMediaChunk(
            request.MediaId,
            metadata.MediaType,
            request.Offset,
            data,
            request.Offset + count == metadata.ContentLength);
    }

    public async Task<IReadOnlyList<ChannelMediaReference>> CommitAsync(
        string channelId,
        Guid inboundMessageId,
        IReadOnlyList<ChannelMediaInput> attachments,
        CancellationToken cancellationToken = default)
    {
        ValidateVersionSeven(inboundMessageId, nameof(inboundMessageId));
        var committed = await StoreFilesAsync(channelId, attachments, cancellationToken);
        await state.WriteAsync(
            async (connection, transaction, token) =>
            {
                await InsertMetadataAsync(
                    connection,
                    transaction,
                    inboundMessageId,
                    committed,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    token);
                return true;
            },
            cancellationToken);
        return committed;
    }

    internal async Task<IReadOnlyList<ChannelMediaReference>> StoreFilesAsync(
        string channelId,
        IReadOnlyList<ChannelMediaInput> attachments,
        CancellationToken cancellationToken = default)
    {
        ValidateChannelId(channelId);
        ArgumentNullException.ThrowIfNull(attachments);
        if (attachments.Count > MaximumAttachments)
        {
            throw Rejected();
        }

        EnsureSafeDirectory(paths.RuntimeDirectory);
        EnsureSafeDirectory(paths.ExternalChannelMediaDirectory);
        var channelDirectory = EnsureSafeChildDirectory(
            paths.ExternalChannelMediaDirectory,
            channelId);
        var stagingDirectory = EnsureSafeChildDirectory(channelDirectory, ".staging");
        var staged = new List<StagedMedia>();
        try
        {
            long total = 0;
            foreach (var attachment in attachments)
            {
                ValidateInput(attachment);
                var temporaryPath = Path.Combine(
                    stagingDirectory,
                    $"media-{Guid.NewGuid():N}.tmp");
                var item = await StageAsync(
                    temporaryPath,
                    attachment,
                    cancellationToken);
                staged.Add(item);
                total = checked(total + item.ContentLength);
                if (total > MaximumTotalBytes)
                {
                    throw Rejected();
                }
            }

            var committed = new List<ChannelMediaReference>(staged.Count);
            foreach (var item in staged)
            {
                var prefixDirectory = EnsureSafeChildDirectory(
                    channelDirectory,
                    item.ContentSha256[..2]);
                var destination = Path.Combine(prefixDirectory, item.ContentSha256);
                await CommitFileAsync(item, destination, cancellationToken);
                var relativePath = Path.GetRelativePath(
                        paths.ExternalChannelMediaDirectory,
                        destination)
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .Replace(Path.AltDirectorySeparatorChar, '/');
                committed.Add(new ChannelMediaReference(
                    Guid.CreateVersion7(),
                    item.Input.MediaType,
                    item.Input.DisplayName,
                    item.ContentLength,
                    item.ContentSha256,
                    relativePath));
            }

            return committed;
        }
        finally
        {
            foreach (var item in staged)
            {
                DeleteIfExists(item.TemporaryPath);
            }
        }
    }

    internal async Task<int> CleanupOrphansAsync(
        DateTimeOffset olderThan,
        CancellationToken cancellationToken = default)
    {
        EnsureSafeDirectory(paths.RuntimeDirectory);
        EnsureSafeDirectory(paths.ExternalChannelMediaDirectory);
        var referenced = await state.ReadAsync(
            async (connection, token) =>
            {
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT relative_path FROM channel_media;";
                await using var reader = await command.ExecuteReaderAsync(token);
                var result = new HashSet<string>(StringComparer.Ordinal);
                while (await reader.ReadAsync(token))
                {
                    result.Add(reader.GetString(0));
                }
                return result;
            },
            cancellationToken);
        var removed = 0;
        foreach (var channelDirectory in Directory.EnumerateDirectories(
                     paths.ExternalChannelMediaDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSafeDirectory(channelDirectory);
            foreach (var contentDirectory in Directory.EnumerateDirectories(channelDirectory))
            {
                EnsureSafeDirectory(contentDirectory);
                foreach (var file in Directory.EnumerateFiles(contentDirectory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureSafeFile(file);
                    var relative = Path.GetRelativePath(
                            paths.ExternalChannelMediaDirectory,
                            file)
                        .Replace(Path.DirectorySeparatorChar, '/')
                        .Replace(Path.AltDirectorySeparatorChar, '/');
                    if (!referenced.Contains(relative) &&
                        File.GetLastWriteTimeUtc(file) <= olderThan.UtcDateTime)
                    {
                        File.Delete(file);
                        removed++;
                    }
                }
            }
        }

        return removed;
    }

    internal static async ValueTask InsertMetadataAsync(
        DbConnection connection,
        DbTransaction transaction,
        Guid inboundMessageId,
        IReadOnlyList<ChannelMediaReference> media,
        long createdUtc,
        CancellationToken cancellationToken)
    {
        for (var ordinal = 0; ordinal < media.Count; ordinal++)
        {
            var item = media[ordinal];
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO channel_media (
                    media_id, inbound_message_id, ordinal, relative_path,
                    media_type, content_length, content_sha256, display_name,
                    created_utc)
                VALUES (
                    $media_id, $inbound_message_id, $ordinal, $relative_path,
                    $media_type, $content_length, $content_sha256, $display_name,
                    $created_utc);
                """;
            Add(command, "$media_id", item.MediaId.ToString("D"));
            Add(command, "$inbound_message_id", inboundMessageId.ToString("D"));
            Add(command, "$ordinal", ordinal);
            Add(command, "$relative_path", item.RelativePath);
            Add(command, "$media_type", item.MediaType);
            Add(command, "$content_length", item.ContentLength);
            Add(command, "$content_sha256", item.ContentSha256);
            Add(command, "$display_name", item.DisplayName);
            Add(command, "$created_utc", createdUtc);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<StagedMedia> StageAsync(
        string temporaryPath,
        ChannelMediaInput input,
        CancellationToken cancellationToken)
    {
        var maximumEncoded = ((MaximumAttachmentBytes + 2) / 3) * 4;
        if (input.ContentBase64.Length > maximumEncoded ||
            input.ContentBase64.Length % 4 != 0 ||
            input.ContentBase64.Any(character =>
                character is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z' or
                    >= '0' and <= '9' or '+' or '/' or '=')))
        {
            throw Rejected();
        }

        var inputBuffer = ArrayPool<byte>.Shared.Rent(8 * 1024);
        var outputBuffer = ArrayPool<byte>.Shared.Rent(8 * 1024);
        var header = new byte[16];
        var headerLength = 0;
        long contentLength = 0;
        try
        {
            await using var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            using var transform = new FromBase64Transform(
                FromBase64TransformMode.DoNotIgnoreWhiteSpaces);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var offset = 0;
            while (offset < input.ContentBase64.Length)
            {
                var remaining = input.ContentBase64.Length - offset;
                var take = Math.Min(inputBuffer.Length - inputBuffer.Length % 4, remaining);
                for (var index = 0; index < take; index++)
                {
                    inputBuffer[index] = (byte)input.ContentBase64[offset + index];
                }

                byte[]? final = null;
                var written = remaining == take
                    ? (final = transform.TransformFinalBlock(inputBuffer, 0, take)).Length
                    : transform.TransformBlock(inputBuffer, 0, take, outputBuffer, 0);
                var bytes = final is null
                    ? outputBuffer.AsMemory(0, written)
                    : final.AsMemory();
                contentLength += written;
                if (contentLength > MaximumAttachmentBytes)
                {
                    throw Rejected();
                }

                if (headerLength < header.Length)
                {
                    var copy = Math.Min(header.Length - headerLength, written);
                    bytes.Span[..copy].CopyTo(header.AsSpan(headerLength));
                    headerLength += copy;
                }

                hash.AppendData(bytes.Span);
                await output.WriteAsync(bytes, cancellationToken);
                offset += take;
            }

            if (input.ContentBase64.Length == 0)
            {
                _ = transform.TransformFinalBlock([], 0, 0);
            }

            await output.FlushAsync(cancellationToken);
            output.Flush(flushToDisk: true);
            var sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!await MediaTypeMatchesAsync(
                    input.MediaType,
                    temporaryPath,
                    header.AsMemory(0, headerLength),
                    cancellationToken))
            {
                throw Rejected();
            }

            return new StagedMedia(
                input,
                temporaryPath,
                contentLength,
                sha256);
        }
        catch (FormatException)
        {
            DeleteIfExists(temporaryPath);
            throw Rejected();
        }
        catch
        {
            DeleteIfExists(temporaryPath);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(inputBuffer, clearArray: true);
            ArrayPool<byte>.Shared.Return(outputBuffer, clearArray: true);
        }
    }

    private static async Task CommitFileAsync(
        StagedMedia item,
        string destination,
        CancellationToken cancellationToken)
    {
        if (File.Exists(destination))
        {
            await VerifyExistingAsync(item, destination, cancellationToken);
            return;
        }

        var temporaryDestination = $"{destination}.tmp-{Guid.NewGuid():N}";
        try
        {
            File.Move(item.TemporaryPath, temporaryDestination);
            EnsureSafeFile(temporaryDestination);
            try
            {
                File.Move(temporaryDestination, destination);
            }
            catch (IOException) when (File.Exists(destination))
            {
                DeleteIfExists(temporaryDestination);
                await VerifyExistingAsync(item, destination, cancellationToken);
            }
        }
        finally
        {
            DeleteIfExists(temporaryDestination);
        }
    }

    private static async Task VerifyExistingAsync(
        StagedMedia item,
        string destination,
        CancellationToken cancellationToken)
    {
        EnsureSafeFile(destination);
        var info = new FileInfo(destination);
        if (info.Length != item.ContentLength)
        {
            throw Rejected();
        }

        await using var stream = new FileStream(
            destination,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken))
            .ToLowerInvariant();
        if (!string.Equals(actual, item.ContentSha256, StringComparison.Ordinal))
        {
            throw Rejected();
        }
    }

    private static async Task<bool> MediaTypeMatchesAsync(
        string mediaType,
        string path,
        ReadOnlyMemory<byte> header,
        CancellationToken cancellationToken)
    {
        if (mediaType == "text/plain")
        {
            try
            {
                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var reader = new StreamReader(
                    stream,
                    StrictUtf8,
                    detectEncodingFromByteOrderMarks: false,
                    bufferSize: 64 * 1024,
                    leaveOpen: false);
                var chars = ArrayPool<char>.Shared.Rent(32 * 1024);
                try
                {
                    while (await reader.ReadAsync(chars, cancellationToken) > 0)
                    {
                    }

                    return true;
                }
                finally
                {
                    ArrayPool<char>.Shared.Return(chars, clearArray: true);
                }
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
        }

        var bytes = header.Span;
        return mediaType switch
        {
            "application/pdf" => bytes.StartsWith("%PDF-"u8),
            "image/png" => bytes.StartsWith(
                new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }),
            "image/jpeg" => bytes.StartsWith(new byte[] { 0xff, 0xd8, 0xff }),
            "image/gif" => bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8),
            "image/webp" => bytes.Length >= 12 &&
                            bytes[..4].SequenceEqual("RIFF"u8) &&
                            bytes.Slice(8, 4).SequenceEqual("WEBP"u8),
            _ => false,
        };
    }

    private static string EnsureSafeChildDirectory(string root, string relative)
    {
        var candidate = Path.GetFullPath(relative, root);
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!candidate.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                comparison))
        {
            throw new IOException("Media path escapes its root.");
        }

        EnsureSafeDirectory(candidate);
        return candidate;
    }

    private static void EnsureSafeDirectory(string path)
    {
        if (File.Exists(path))
        {
            throw new IOException("Media directory is not a directory.");
        }

        Directory.CreateDirectory(path);
        var info = new DirectoryInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Media directories cannot be reparse points.");
        }

        var parent = info.Parent;
        if (parent is not null &&
            (parent.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Media parent directories cannot be reparse points.");
        }
    }

    private static void EnsureSafeFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException("Media files cannot be reparse points.");
        }

        EnsureSafeDirectory(info.DirectoryName!);
    }

    private static void ValidateInput(ChannelMediaInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrEmpty(input.DisplayName) ||
            input.DisplayName.Length > 1024 ||
            input.DisplayName.Any(char.IsControl) ||
            string.IsNullOrEmpty(input.MediaType) ||
            input.ContentBase64 is null)
        {
            throw Rejected();
        }
    }

    private static void ValidateChannelId(string channelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        if (channelId.Length > 64 ||
            channelId[0] == '-' ||
            channelId[^1] == '-' ||
            channelId.Contains("--", StringComparison.Ordinal) ||
            channelId.Any(character =>
                character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
        {
            throw Rejected();
        }
    }

    private static void ValidateVersionSeven(Guid value, string parameterName)
    {
        if (value.Version != 7)
        {
            throw new ArgumentException("Value must be a UUIDv7.", parameterName);
        }
    }

    private static ChannelServiceException Rejected() =>
        new(ChannelErrorCodes.MediaRejected, "Channel media was rejected.");

    private static void Add(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private sealed record StagedMedia(
        ChannelMediaInput Input,
        string TemporaryPath,
        long ContentLength,
        string ContentSha256);

    private sealed record MediaReadMetadata(
        string RelativePath,
        string MediaType,
        long ContentLength);
}
