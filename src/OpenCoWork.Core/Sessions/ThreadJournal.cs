using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Workspaces;

namespace OpenCoWork.Core.Sessions;

internal enum ThreadJournalLocation
{
    Active,
    Archived,
    Deleting,
}

internal enum ThreadJournalHealth
{
    Healthy,
    Repaired,
    RecoveryRequired,
}

internal enum ThreadJournalFaultPoint
{
    BeforeWrite,
    HalfLineWritten,
    BeforeFlush,
    AfterFlushBeforeMemory,
    AfterMemoryBeforeProjection,
    AfterProjectionBeforeEvent,
    AfterRecoveryTruncate,
}

internal sealed record ThreadJournalDraft(
    Guid ThreadId,
    long Sequence,
    Guid EntryId,
    DateTimeOffset Timestamp,
    SessionEventType EntryType,
    Guid IdempotencyKey,
    object Payload);

internal sealed record ThreadJournalEntry(
    Guid ThreadId,
    long Sequence,
    Guid EntryId,
    DateTimeOffset Timestamp,
    SessionEventType EntryType,
    Guid IdempotencyKey,
    JsonElement Payload,
    string Checksum);

internal sealed record ThreadJournalReplayResult(
    ThreadJournalHealth Health,
    IReadOnlyList<ThreadJournalEntry> Entries,
    string? DiagnosticCode,
    string? Diagnostic,
    string? BackupPath);

internal sealed record ThreadJournalMatch(
    ThreadJournalLocation Location,
    ThreadJournalReplayResult Replay,
    ThreadJournalEntry Entry);

internal sealed class ThreadJournalException : IOException
{
    public ThreadJournalException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

internal sealed class ThreadJournalCommittedException : IOException
{
    public ThreadJournalCommittedException(
        ThreadJournalEntry entry,
        Exception innerException)
        : base("Thread journal entry was committed before a later fault.", innerException)
    {
        Entry = entry;
    }

    public ThreadJournalEntry Entry { get; }
}

internal sealed class ThreadJournal
{
    internal const int MaxEntryBytes = 1024 * 1024;
    internal const int MaxTextBytes = 256 * 1024;
    private const int SchemaVersion = 1;
    private static readonly string[] PropertyOrder =
    [
        "schemaVersion",
        "threadId",
        "sequence",
        "entryId",
        "timestamp",
        "entryType",
        "idempotencyKey",
        "payload",
        "checksum",
    ];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    private readonly OpenCoWorkPaths _paths;
    private readonly Action<ThreadJournalFaultPoint>? _faultInjector;

    public ThreadJournal(
        OpenCoWorkPaths paths,
        Action<ThreadJournalFaultPoint>? faultInjector = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _faultInjector = faultInjector;
    }

    public string GetPath(ThreadJournalLocation location, Guid threadId)
    {
        SessionIds.RequireVersion7(threadId, nameof(threadId), "Thread ID");
        return Path.Combine(
            GetDirectory(location),
            $"{ToWire(threadId)}.jsonl");
    }

    public async Task<ThreadJournalEntry> AppendAsync(
        ThreadJournalLocation location,
        ThreadJournalDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ValidateDraft(draft);
        var payload = JsonSerializer.SerializeToElement(
            draft.Payload,
            draft.Payload.GetType(),
            JsonOptions);
        ValidatePayload(payload, draft.Payload);

        var unsigned = Serialize(draft, payload, checksum: null);
        var checksum = Hash(unsigned);
        var encoded = Serialize(draft, payload, checksum);
        if (encoded.Length > MaxEntryBytes)
        {
            throw EntryTooLarge("Journal entry exceeds 1 MiB.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var directory = EnsureDirectory(location);
        var path = Path.Combine(directory, $"{ToWire(draft.ThreadId)}.jsonl");
        GuardPath(path);
        _faultInjector?.Invoke(ThreadJournalFaultPoint.BeforeWrite);

        var line = new byte[encoded.Length + 1];
        encoded.CopyTo(line, 0);
        line[^1] = (byte)'\n';
        var committedEntry = new ThreadJournalEntry(
            draft.ThreadId,
            draft.Sequence,
            draft.EntryId,
            draft.Timestamp.ToUniversalTime(),
            draft.EntryType,
            draft.IdempotencyKey,
            payload,
            checksum);
        await using var stream = new FileStream(
            path,
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        stream.Seek(0, SeekOrigin.End);

        if (_faultInjector is null)
        {
            await stream.WriteAsync(line, cancellationToken);
        }
        else
        {
            var half = Math.Max(1, line.Length / 2);
            await stream.WriteAsync(line.AsMemory(0, half), cancellationToken);
            _faultInjector.Invoke(ThreadJournalFaultPoint.HalfLineWritten);
            await stream.WriteAsync(line.AsMemory(half), cancellationToken);
        }

        _faultInjector?.Invoke(ThreadJournalFaultPoint.BeforeFlush);
        stream.Flush(flushToDisk: true);
        try
        {
            _faultInjector?.Invoke(ThreadJournalFaultPoint.AfterFlushBeforeMemory);
        }
        catch (Exception exception)
        {
            throw new ThreadJournalCommittedException(committedEntry, exception);
        }

        return committedEntry;
    }

    public async Task<ThreadJournalReplayResult> ReplayAsync(
        ThreadJournalLocation location,
        Guid threadId,
        CancellationToken cancellationToken = default)
    {
        var path = GetPath(location, threadId);
        GuardPath(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Thread journal does not exist.");
        }

        var scan = await ScanAsync(path, threadId, cancellationToken);
        RecoveryIntent? intent;
        try
        {
            intent = await ReadIntentAsync(location, threadId, cancellationToken);
        }
        catch (Exception exception) when (
            exception is JsonException
                or NotSupportedException
                or ThreadJournalException)
        {
            var invalidIntentBackup = await BackupCorruptAsync(
                threadId,
                path,
                cancellationToken);
            return new ThreadJournalReplayResult(
                ThreadJournalHealth.RecoveryRequired,
                scan.Entries,
                SessionErrorCodes.JournalCorrupt,
                "Thread journal recovery intent is invalid.",
                invalidIntentBackup);
        }

        if (intent is not null)
        {
            return await ResumeRecoveryAsync(
                path,
                intent,
                scan,
                cancellationToken);
        }

        if (scan.Failure == ScanFailure.None)
        {
            return new ThreadJournalReplayResult(
                ThreadJournalHealth.Healthy,
                scan.Entries,
                null,
                null,
                null);
        }

        if (scan.Failure == ScanFailure.IncompleteTail)
        {
            return await RecoverTailAsync(
                location,
                threadId,
                path,
                scan,
                cancellationToken);
        }

        var backupPath = await BackupCorruptAsync(
            threadId,
            path,
            cancellationToken);
        return new ThreadJournalReplayResult(
            ThreadJournalHealth.RecoveryRequired,
            scan.Entries,
            scan.DiagnosticCode ?? SessionErrorCodes.JournalCorrupt,
            scan.Diagnostic ?? "Thread journal is corrupt.",
            backupPath);
    }

    public async Task<ThreadJournalMatch?> FindByIdempotencyKeyAsync(
        Guid idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        SessionIds.RequireVersion7(
            idempotencyKey,
            nameof(idempotencyKey),
            "Idempotency key");
        ThreadJournalMatch? match = null;
        foreach (var location in Enum.GetValues<ThreadJournalLocation>())
        {
            var directory = GetDirectory(location);
            GuardPath(directory);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(
                         directory,
                         "*.jsonl",
                         SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileName = Path.GetFileNameWithoutExtension(path);
                if (!string.Equals(
                        fileName,
                        fileName.ToLowerInvariant(),
                        StringComparison.Ordinal) ||
                    !Guid.TryParseExact(fileName, "D", out var threadId) ||
                    threadId.Version != 7)
                {
                    continue;
                }

                var replay = await ReplayAsync(
                    location,
                    threadId,
                    cancellationToken);
                foreach (var entry in replay.Entries.Where(
                             entry => entry.IdempotencyKey == idempotencyKey))
                {
                    if (match is not null)
                    {
                        throw new ThreadJournalException(
                            SessionErrorCodes.IdempotencyConflict,
                            "Idempotency key appears in more than one journal entry.");
                    }

                    match = new ThreadJournalMatch(location, replay, entry);
                }
            }
        }

        return match;
    }

    private async Task<ThreadJournalReplayResult> RecoverTailAsync(
        ThreadJournalLocation location,
        Guid threadId,
        string path,
        JournalScan scan,
        CancellationToken cancellationToken)
    {
        var recoveryDirectory = EnsureRecoveryDirectory(threadId);
        var operationId = Guid.CreateVersion7();
        var backupPath = Path.Combine(
            recoveryDirectory,
            $"{ToWire(operationId)}.backup");
        await CopyDurablyAsync(path, backupPath, cancellationToken);
        var intent = new RecoveryIntent(
            threadId,
            location,
            scan.FileLength,
            scan.LastValidOffset,
            HashFile(path),
            backupPath,
            scan.Entries.Count == 0 ? null : Guid.CreateVersion7(),
            scan.Entries.Count == 0 ? null : Guid.CreateVersion7(),
            DateTimeOffset.UtcNow);
        await WriteIntentAsync(intent, cancellationToken);
        return await ApplyRecoveryAsync(path, intent, scan, cancellationToken);
    }

    private async Task<ThreadJournalReplayResult> ResumeRecoveryAsync(
        string path,
        RecoveryIntent intent,
        JournalScan scan,
        CancellationToken cancellationToken)
    {
        if (!IsValidRecoveryIntent(path, intent))
        {
            return RecoveryIntentFailure(scan);
        }

        if (scan.Failure == ScanFailure.Corrupt)
        {
            return RecoveryIntentFailure(scan);
        }

        if (scan.Failure == ScanFailure.None &&
            intent.RecoveryEntryId is { } recoveryEntryId &&
            scan.Entries.LastOrDefault()?.EntryId == recoveryEntryId)
        {
            DeleteIntent(intent.ThreadId);
            return new ThreadJournalReplayResult(
                ThreadJournalHealth.Repaired,
                scan.Entries,
                null,
                null,
                intent.BackupPath);
        }

        if (scan.LastValidOffset != intent.TruncatedLength)
        {
            return RecoveryIntentFailure(scan);
        }

        return await ApplyRecoveryAsync(path, intent, scan, cancellationToken);
    }

    private async Task<ThreadJournalReplayResult> ApplyRecoveryAsync(
        string path,
        RecoveryIntent intent,
        JournalScan scan,
        CancellationToken cancellationToken)
    {
        GuardPath(path);
        await using (var stream = new FileStream(
                         path,
                         FileMode.Open,
                         FileAccess.Write,
                         FileShare.Read,
                         bufferSize: 4096,
                         FileOptions.WriteThrough))
        {
            stream.SetLength(intent.TruncatedLength);
            stream.Flush(flushToDisk: true);
        }

        _faultInjector?.Invoke(ThreadJournalFaultPoint.AfterRecoveryTruncate);

        if (intent.RecoveryEntryId is null || intent.RecoveryIdempotencyKey is null)
        {
            DeleteIntent(intent.ThreadId);
            return new ThreadJournalReplayResult(
                ThreadJournalHealth.Repaired,
                Array.Empty<ThreadJournalEntry>(),
                null,
                null,
                intent.BackupPath);
        }

        var entries = scan.Entries
            .Where(entry => entry.Sequence <= scan.Entries.Count)
            .ToList();
        var recovered = await AppendAsync(
            intent.Location,
            new ThreadJournalDraft(
                intent.ThreadId,
                entries.Count + 1,
                intent.RecoveryEntryId.Value,
                intent.Timestamp,
                SessionEventType.ThreadJournalRecovered,
                intent.RecoveryIdempotencyKey.Value,
                new ThreadJournalRecoveredPayload(
                    intent.OriginalLength,
                    intent.TruncatedLength,
                    intent.OriginalSha256)),
            cancellationToken);
        entries.Add(recovered);
        DeleteIntent(intent.ThreadId);
        return new ThreadJournalReplayResult(
            ThreadJournalHealth.Repaired,
            entries.AsReadOnly(),
            null,
            null,
            intent.BackupPath);
    }

    private static ThreadJournalReplayResult RecoveryIntentFailure(JournalScan scan) =>
        new(
            ThreadJournalHealth.RecoveryRequired,
            scan.Entries,
            SessionErrorCodes.JournalCorrupt,
            "Thread journal recovery intent does not match the journal.",
            null);

    private bool IsValidRecoveryIntent(string journalPath, RecoveryIntent intent)
    {
        try
        {
            if (intent.TruncatedLength < 0 ||
                intent.TruncatedLength > intent.OriginalLength ||
                !IsLowerHexSha256(intent.OriginalSha256) ||
                intent.Timestamp.Offset != TimeSpan.Zero ||
                intent.RecoveryEntryId is null !=
                (intent.RecoveryIdempotencyKey is null))
            {
                return false;
            }

            if (intent.RecoveryEntryId is { } entryId &&
                (entryId.Version != 7 ||
                 intent.RecoveryIdempotencyKey!.Value.Version != 7))
            {
                return false;
            }

            var recoveryDirectory = EnsureRecoveryDirectory(intent.ThreadId);
            var backupPath = Path.GetFullPath(intent.BackupPath);
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!string.Equals(
                    Path.GetDirectoryName(backupPath),
                    recoveryDirectory,
                    comparison))
            {
                return false;
            }

            GuardPath(backupPath);
            return File.Exists(backupPath) &&
                   string.Equals(
                       HashFile(backupPath),
                       intent.OriginalSha256,
                       StringComparison.Ordinal) &&
                   PrefixMatches(
                       journalPath,
                       backupPath,
                       intent.TruncatedLength);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or ArgumentException)
        {
            return false;
        }
    }

    private async Task<JournalScan> ScanAsync(
        string path,
        Guid expectedThreadId,
        CancellationToken cancellationToken)
    {
        var entries = new List<ThreadJournalEntry>();
        var line = new ArrayBufferWriter<byte>();
        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        long offset = 0;
        long lastValidOffset = 0;
        var oversized = false;

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 8192,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var fileLength = stream.Length;
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                for (var index = 0; index < read; index++)
                {
                    var value = buffer[index];
                    offset++;
                    if (value != (byte)'\n')
                    {
                        if (line.WrittenCount < MaxEntryBytes + 1)
                        {
                            line.GetSpan(1)[0] = value;
                            line.Advance(1);
                        }
                        else
                        {
                            oversized = true;
                        }

                        continue;
                    }

                    if (oversized || line.WrittenCount > MaxEntryBytes)
                    {
                        return Corrupt(
                            entries,
                            lastValidOffset,
                            fileLength,
                            "Journal entry exceeds 1 MiB.");
                    }

                    if (!TryParseLine(
                            line.WrittenMemory,
                            expectedThreadId,
                            entries.Count + 1L,
                            out var entry,
                            out var diagnosticCode,
                            out var diagnostic))
                    {
                        return Corrupt(
                            entries,
                            lastValidOffset,
                            fileLength,
                            diagnostic,
                            diagnosticCode);
                    }

                    entries.Add(entry);
                    lastValidOffset = offset;
                    line.Clear();
                    oversized = false;
                }
            }

            return line.WrittenCount > 0 || oversized
                ? new JournalScan(
                    entries.AsReadOnly(),
                    lastValidOffset,
                    fileLength,
                    ScanFailure.IncompleteTail,
                    null,
                    "Thread journal has an incomplete tail.")
                : new JournalScan(
                    entries.AsReadOnly(),
                    lastValidOffset,
                    fileLength,
                    ScanFailure.None,
                    null,
                    null);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static JournalScan Corrupt(
        List<ThreadJournalEntry> entries,
        long lastValidOffset,
        long fileLength,
        string diagnostic,
        string? diagnosticCode = null) =>
        new(
            entries.AsReadOnly(),
            lastValidOffset,
            fileLength,
            ScanFailure.Corrupt,
            diagnosticCode ?? SessionErrorCodes.JournalCorrupt,
            diagnostic);

    private static bool TryParseLine(
        ReadOnlyMemory<byte> line,
        Guid expectedThreadId,
        long expectedSequence,
        out ThreadJournalEntry entry,
        out string diagnosticCode,
        out string diagnostic)
    {
        entry = null!;
        diagnosticCode = SessionErrorCodes.JournalCorrupt;
        diagnostic = "Thread journal entry is invalid.";

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.EnumerateObject()
                    .Select(property => property.Name)
                    .SequenceEqual(PropertyOrder, StringComparer.Ordinal))
            {
                diagnostic = "Thread journal properties are not canonical.";
                return false;
            }

            var schemaVersion = root.GetProperty("schemaVersion").GetInt32();
            if (schemaVersion != SchemaVersion)
            {
                diagnosticCode = SessionErrorCodes.JournalUnsupportedSchema;
                diagnostic = $"Thread journal schema {schemaVersion} is unsupported.";
                return false;
            }

            if (!TryReadId(root, "threadId", out var threadId) ||
                threadId != expectedThreadId)
            {
                diagnostic = "Thread journal Thread ID does not match its file name.";
                return false;
            }

            var sequence = root.GetProperty("sequence").GetInt64();
            if (sequence != expectedSequence)
            {
                diagnostic = $"Thread journal sequence {sequence} does not follow {expectedSequence - 1}.";
                return false;
            }

            if (!TryReadId(root, "entryId", out var entryId) ||
                !TryReadId(root, "idempotencyKey", out var idempotencyKey))
            {
                diagnostic = "Thread journal IDs must be lowercase UUIDv7.";
                return false;
            }

            var timestampText = root.GetProperty("timestamp").GetString();
            if (!DateTimeOffset.TryParseExact(
                    timestampText,
                    "O",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var timestamp) ||
                timestamp.Offset != TimeSpan.Zero)
            {
                diagnostic = "Thread journal timestamp must be RFC 3339 UTC.";
                return false;
            }

            var entryTypeText = root.GetProperty("entryType").GetString();
            if (!TryParseEntryType(entryTypeText, out var entryType))
            {
                diagnostic = "Thread journal entry type is unknown.";
                return false;
            }

            var payload = root.GetProperty("payload").Clone();
            ValidatePayload(payload, payload);
            var checksum = root.GetProperty("checksum").GetString() ?? string.Empty;
            if (!IsLowerHexSha256(checksum))
            {
                diagnostic = "Thread journal checksum is invalid.";
                return false;
            }

            var draft = new ThreadJournalDraft(
                threadId,
                sequence,
                entryId,
                timestamp,
                entryType,
                idempotencyKey,
                payload);
            var expectedChecksum = Hash(Serialize(draft, payload, checksum: null));
            if (!string.Equals(checksum, expectedChecksum, StringComparison.Ordinal))
            {
                diagnostic = "Thread journal checksum does not match.";
                return false;
            }

            var canonical = Serialize(draft, payload, checksum);
            if (!line.Span.SequenceEqual(canonical))
            {
                diagnostic = "Thread journal entry encoding is not canonical.";
                return false;
            }

            entry = new ThreadJournalEntry(
                threadId,
                sequence,
                entryId,
                timestamp,
                entryType,
                idempotencyKey,
                payload,
                checksum);
            return true;
        }
        catch (Exception exception) when (
            exception is JsonException
                or FormatException
                or InvalidOperationException
                or ThreadJournalException)
        {
            diagnostic = "Thread journal entry cannot be decoded safely.";
            return false;
        }
    }

    private static byte[] Serialize(
        ThreadJournalDraft draft,
        JsonElement payload,
        string? checksum)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", SchemaVersion);
        writer.WriteString("threadId", ToWire(draft.ThreadId));
        writer.WriteNumber("sequence", draft.Sequence);
        writer.WriteString("entryId", ToWire(draft.EntryId));
        writer.WriteString(
            "timestamp",
            draft.Timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        writer.WriteString("entryType", ToWire(draft.EntryType));
        writer.WriteString("idempotencyKey", ToWire(draft.IdempotencyKey));
        writer.WritePropertyName("payload");
        WriteCanonical(writer, payload);
        if (checksum is not null)
        {
            writer.WriteString("checksum", checksum);
        }

        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject()
                             .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number when value.TryGetInt64(out var signed):
                writer.WriteNumberValue(signed);
                break;
            case JsonValueKind.Number when value.TryGetUInt64(out var unsigned):
                writer.WriteNumberValue(unsigned);
                break;
            case JsonValueKind.Number when value.TryGetDecimal(out var decimalValue):
                writer.WriteNumberValue(decimalValue);
                break;
            case JsonValueKind.Number:
                writer.WriteNumberValue(value.GetDouble());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new JsonException("Unsupported JSON payload value.");
        }
    }

    private static void ValidateDraft(ThreadJournalDraft draft)
    {
        SessionIds.RequireVersion7(draft.ThreadId, nameof(draft.ThreadId), "Thread ID");
        SessionIds.RequireVersion7(draft.EntryId, nameof(draft.EntryId), "Entry ID");
        SessionIds.RequireVersion7(
            draft.IdempotencyKey,
            nameof(draft.IdempotencyKey),
            "Idempotency key");
        ArgumentNullException.ThrowIfNull(draft.Payload);
        if (!Enum.IsDefined(draft.EntryType))
        {
            throw new ArgumentOutOfRangeException(nameof(draft.EntryType));
        }

        if (draft.Sequence < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(draft.Sequence),
                "Journal sequence must start at one.");
        }
    }

    private static void ValidatePayload(JsonElement payload, object source)
    {
        ValidateStrings(payload);
        if (source is SessionExecutionCheckpoint)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using var writer = new Utf8JsonWriter(buffer);
            WriteCanonical(writer, payload);
            writer.Flush();
            if (buffer.WrittenCount > MaxTextBytes)
            {
                throw EntryTooLarge("Execution checkpoint exceeds 256 KiB.");
            }
        }
    }

    private static void ValidateStrings(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    ValidateStrings(property.Value);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    ValidateStrings(item);
                }

                break;
            case JsonValueKind.String:
                if (Encoding.UTF8.GetByteCount(value.GetString() ?? string.Empty) > MaxTextBytes)
                {
                    throw EntryTooLarge("Journal text exceeds 256 KiB.");
                }

                break;
        }
    }

    private static ThreadJournalException EntryTooLarge(string message) =>
        new(SessionErrorCodes.JournalEntryTooLarge, message);

    private static bool TryReadId(
        JsonElement root,
        string propertyName,
        out Guid value)
    {
        value = default;
        var text = root.GetProperty(propertyName).GetString();
        return text is not null &&
               string.Equals(text, text.ToLowerInvariant(), StringComparison.Ordinal) &&
               Guid.TryParseExact(text, "D", out value) &&
               value.Version == 7;
    }

    private static bool TryParseEntryType(string? value, out SessionEventType entryType)
    {
        foreach (var candidate in Enum.GetValues<SessionEventType>())
        {
            if (string.Equals(ToWire(candidate), value, StringComparison.Ordinal))
            {
                entryType = candidate;
                return true;
            }
        }

        entryType = default;
        return false;
    }

    private static string ToWire(SessionEventType value)
    {
        var name = value.ToString();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static string ToWire(Guid value) =>
        value.ToString("D", CultureInfo.InvariantCulture).ToLowerInvariant();

    private static string Hash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string HashFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool PrefixMatches(
        string firstPath,
        string secondPath,
        long length)
    {
        using var first = new FileStream(
            firstPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        using var second = new FileStream(
            secondPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        var firstBuffer = new byte[8192];
        var secondBuffer = new byte[8192];
        long compared = 0;
        while (compared < length)
        {
            var requested = (int)Math.Min(firstBuffer.Length, length - compared);
            var firstRead = first.Read(firstBuffer, 0, requested);
            var secondRead = second.Read(secondBuffer, 0, requested);
            if (firstRead != requested ||
                secondRead != requested ||
                !firstBuffer.AsSpan(0, requested)
                    .SequenceEqual(secondBuffer.AsSpan(0, requested)))
            {
                return false;
            }

            compared += requested;
        }

        return true;
    }

    private static bool IsLowerHexSha256(string value) =>
        value.Length == 64 &&
        value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private async Task<string> BackupCorruptAsync(
        Guid threadId,
        string path,
        CancellationToken cancellationToken)
    {
        var recoveryDirectory = EnsureRecoveryDirectory(threadId);
        var backupPath = Path.Combine(
            recoveryDirectory,
            $"{HashFile(path)}.corrupt.backup");
        if (!File.Exists(backupPath))
        {
            await CopyDurablyAsync(path, backupPath, cancellationToken);
        }

        return backupPath;
    }

    private async Task CopyDurablyAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        GuardPath(sourcePath);
        GuardPath(destinationPath);
        var temporaryPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
        GuardPath(temporaryPath);
        try
        {
            await using (var source = new FileStream(
                             sourcePath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             bufferSize: 8192,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.Read,
                             bufferSize: 8192,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await source.CopyToAsync(destination, cancellationToken);
                destination.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temporaryPath, destinationPath);
            }
            catch (IOException) when (
                File.Exists(destinationPath) &&
                string.Equals(
                    HashFile(sourcePath),
                    HashFile(destinationPath),
                    StringComparison.Ordinal))
            {
                // The content-addressed corruption backup already exists.
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private async Task<RecoveryIntent?> ReadIntentAsync(
        ThreadJournalLocation location,
        Guid threadId,
        CancellationToken cancellationToken)
    {
        var path = GetIntentPath(threadId);
        if (!File.Exists(path))
        {
            return null;
        }

        GuardPath(path);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var intent = JsonSerializer.Deserialize<RecoveryIntent>(bytes, JsonOptions)
            ?? throw new ThreadJournalException(
                SessionErrorCodes.JournalCorrupt,
                "Thread journal recovery intent is invalid.");
        return intent.ThreadId == threadId && intent.Location == location
            ? intent
            : throw new ThreadJournalException(
                SessionErrorCodes.JournalCorrupt,
                "Thread journal recovery intent targets another journal.");
    }

    private async Task WriteIntentAsync(
        RecoveryIntent intent,
        CancellationToken cancellationToken)
    {
        var path = GetIntentPath(intent.ThreadId);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        GuardPath(temporaryPath);
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(intent, JsonOptions);
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

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private void DeleteIntent(Guid threadId)
    {
        var path = GetIntentPath(threadId);
        GuardPath(path);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string GetIntentPath(Guid threadId) =>
        Path.Combine(
            EnsureRecoveryDirectory(threadId),
            $"{ToWire(threadId)}.intent.json");

    private string EnsureDirectory(ThreadJournalLocation location)
    {
        var directory = GetDirectory(location);
        GuardPath(directory);
        Directory.CreateDirectory(directory);
        GuardPath(directory);
        return directory;
    }

    private string EnsureRecoveryDirectory(Guid threadId)
    {
        SessionIds.RequireVersion7(threadId, nameof(threadId), "Thread ID");
        GuardPath(_paths.ThreadRecoveryDirectory);
        Directory.CreateDirectory(_paths.ThreadRecoveryDirectory);
        var directory = Path.Combine(
            _paths.ThreadRecoveryDirectory,
            ToWire(threadId));
        GuardPath(directory);
        Directory.CreateDirectory(directory);
        GuardPath(directory);
        return directory;
    }

    private string GetDirectory(ThreadJournalLocation location) =>
        location switch
        {
            ThreadJournalLocation.Active => _paths.ActiveThreadsDirectory,
            ThreadJournalLocation.Archived => _paths.ArchivedThreadsDirectory,
            ThreadJournalLocation.Deleting => _paths.DeletingThreadsDirectory,
            _ => throw new ArgumentOutOfRangeException(nameof(location)),
        };

    private void GuardPath(string path)
    {
        var declaration = Path.Combine(
            _paths.WorkspaceRoot,
            ".opencowork-journal-anchor");
        var relative = Path.GetRelativePath(_paths.WorkspaceRoot, path);
        var resolved = WorkspacePathGuard.ResolveContained(
            _paths.WorkspaceRoot,
            declaration,
            relative);
        WorkspacePathGuard.RevalidateForWrite(resolved);
    }

    private enum ScanFailure
    {
        None,
        IncompleteTail,
        Corrupt,
    }

    private sealed record JournalScan(
        IReadOnlyList<ThreadJournalEntry> Entries,
        long LastValidOffset,
        long FileLength,
        ScanFailure Failure,
        string? DiagnosticCode,
        string? Diagnostic);

    private sealed record RecoveryIntent(
        Guid ThreadId,
        ThreadJournalLocation Location,
        long OriginalLength,
        long TruncatedLength,
        string OriginalSha256,
        string BackupPath,
        Guid? RecoveryEntryId,
        Guid? RecoveryIdempotencyKey,
        DateTimeOffset Timestamp);

    private sealed record ThreadJournalRecoveredPayload(
        long OriginalLength,
        long TruncatedLength,
        string OriginalSha256);
}
