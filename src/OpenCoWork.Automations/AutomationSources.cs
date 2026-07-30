using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Cronos;
using OpenCoWork.Abstractions;

namespace OpenCoWork.Automations;

internal sealed record AutomationScheduleProjection(
    string Cron,
    string TimeZone,
    DateTimeOffset? NextOccurrenceUtc,
    DateTimeOffset? LastOccurrenceUtc,
    DateTimeOffset? CoalescedOccurrenceUtc,
    long Revision);

internal sealed record AutomationSourceProjection(
    string AutomationId,
    AutomationDefinitionSourceStatus Status,
    string? SourceSha256,
    string? DefinitionVersion,
    string DisplayName,
    bool Enabled,
    string? DefinitionJson,
    IReadOnlyList<OpenCoWorkDiagnostic> Diagnostics,
    long Revision,
    long AutomationRevision,
    AutomationScheduleProjection? Schedule);

internal sealed record AutomationScheduleAdvance(
    DateTimeOffset? CoalescedOccurrenceUtc,
    DateTimeOffset? NextOccurrenceUtc);

internal static class AutomationScheduleCalculator
{
    public static DateTimeOffset? Next(
        string cron,
        string timeZone,
        DateTimeOffset fromUtc)
    {
        var expression = CronExpression.Parse(cron, CronFormat.Standard);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        return expression.GetNextOccurrence(fromUtc.ToUniversalTime(), zone);
    }

    public static AutomationScheduleAdvance Advance(
        string cron,
        string timeZone,
        DateTimeOffset persistedNextUtc,
        DateTimeOffset nowUtc)
    {
        if (persistedNextUtc > nowUtc)
        {
            return new AutomationScheduleAdvance(null, persistedNextUtc);
        }

        var expression = CronExpression.Parse(cron, CronFormat.Standard);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        var coalesced = expression.GetPreviousOccurrence(
            nowUtc.ToUniversalTime(),
            zone,
            inclusive: true);
        if (coalesced < persistedNextUtc)
        {
            coalesced = persistedNextUtc;
        }

        return new AutomationScheduleAdvance(
            coalesced,
            expression.GetNextOccurrence(
                nowUtc.ToUniversalTime(),
                zone,
                inclusive: false));
    }

    public static string IdempotencyKey(
        string automationId,
        string definitionVersion,
        DateTimeOffset scheduledForUtc) =>
        $"{automationId}:{definitionVersion}:{scheduledForUtc.ToUniversalTime():O}";
}

internal sealed class AutomationSourceRuntime : IAsyncDisposable
{
    internal static readonly TimeSpan WatcherDebounce = TimeSpan.FromMilliseconds(250);

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly IWorkspaceStateStore _store;
    private readonly AutomationDefinitionLoader _loader;
    private readonly TimeProvider _timeProvider;
    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private CancellationTokenSource? _lifetime;
    private FileSystemWatcher? _watcher;
    private Task? _worker;
    private int _healthy;

    public AutomationSourceRuntime(
        IWorkspaceStateStore store,
        WorkspaceRuntimeDescriptor workspace,
        AutomationDefinitionLoader loader,
        TimeProvider timeProvider)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        ArgumentNullException.ThrowIfNull(workspace);
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        DefinitionsDirectory = Path.Combine(
            workspace.DataRoot,
            "automations",
            "definitions");
    }

    public string DefinitionsDirectory { get; }

    public bool IsHealthy => Volatile.Read(ref _healthy) != 0;

    public event Action? Changed;

    public event Action<Exception>? Faulted;

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        if (_lifetime is not null)
        {
            return;
        }

        Directory.CreateDirectory(DefinitionsDirectory);
        var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var watcher = new FileSystemWatcher(DefinitionsDirectory, "*.yaml")
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName |
                           NotifyFilters.LastWrite |
                           NotifyFilters.CreationTime |
                           NotifyFilters.Size,
        };
        watcher.Changed += OnSourceChanged;
        watcher.Created += OnSourceChanged;
        watcher.Deleted += OnSourceChanged;
        watcher.Renamed += OnSourceChanged;
        watcher.Error += OnWatcherError;
        watcher.EnableRaisingEvents = true;
        _lifetime = lifetime;
        _watcher = watcher;
        _worker = RunAsync(lifetime.Token);
        try
        {
            await ScanAsync(cancellationToken);
        }
        catch
        {
            await StopAsync(CancellationToken.None);
            throw;
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        var lifetime = Interlocked.Exchange(ref _lifetime, null);
        if (lifetime is null)
        {
            return;
        }

        var watcher = Interlocked.Exchange(ref _watcher, null);
        if (watcher is not null)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }

        await lifetime.CancelAsync();
        var worker = Interlocked.Exchange(ref _worker, null);
        if (worker is not null)
        {
            try
            {
                await worker.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
        }

        lifetime.Dispose();
    }

    public async ValueTask ScanAsync(CancellationToken cancellationToken)
    {
        await _scanGate.WaitAsync(cancellationToken);
        try
        {
            var candidates = await ReadCandidatesAsync(cancellationToken);
            var now = _timeProvider.GetUtcNow();
            await _store.WriteAsync(
                (connection, transaction, token) =>
                    PublishAsync(connection, transaction, candidates, now, token),
                cancellationToken);
            Volatile.Write(ref _healthy, 1);
            Changed?.Invoke();
        }
        finally
        {
            _scanGate.Release();
        }
    }

    public ValueTask<AutomationSourceProjection?> ReadAsync(
        string automationId,
        CancellationToken cancellationToken) =>
        _store.ReadAsync(
            (connection, token) => ReadProjectionAsync(connection, automationId, token),
            cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        _scanGate.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (await _wake.Reader.WaitToReadAsync(cancellationToken))
            {
                while (_wake.Reader.TryRead(out _))
                {
                }

                await Task.Delay(WatcherDebounce, _timeProvider, cancellationToken);
                while (_wake.Reader.TryRead(out _))
                {
                }

                try
                {
                    await ScanAsync(cancellationToken);
                }
                catch (Exception exception) when (
                    exception is not OperationCanceledException)
                {
                    Volatile.Write(ref _healthy, 0);
                    Faulted?.Invoke(exception);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void OnSourceChanged(object sender, FileSystemEventArgs args) =>
        _wake.Writer.TryWrite(true);

    private void OnWatcherError(object sender, ErrorEventArgs args) =>
        _wake.Writer.TryWrite(true);

    private async Task<IReadOnlyDictionary<string, Candidate>> ReadCandidatesAsync(
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var result = new Dictionary<string, Candidate>(StringComparer.Ordinal);
                foreach (var path in Directory.EnumerateFiles(
                             DefinitionsDirectory,
                             "*",
                             SearchOption.TopDirectoryOnly)
                         .Where(path => string.Equals(
                             Path.GetExtension(path),
                             ".yaml",
                             StringComparison.Ordinal))
                         .Order(StringComparer.Ordinal))
                {
                    var fileName = Path.GetFileName(path);
                    var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
                    var loaded = _loader.Load(fileName, bytes);
                    var id = Path.GetFileNameWithoutExtension(fileName);
                    result[id] = new Candidate(
                        id,
                        Path.Combine("automations", "definitions", fileName)
                            .Replace(Path.DirectorySeparatorChar, '/'),
                        loaded);
                }

                return result;
            }
            catch (Exception exception) when (
                attempt == 0 &&
                exception is FileNotFoundException or DirectoryNotFoundException)
            {
                Directory.CreateDirectory(DefinitionsDirectory);
            }
        }
    }

    private async ValueTask<long> PublishAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlyDictionary<string, Candidate> candidates,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var existingIds = await ReadIdsAsync(connection, transaction, cancellationToken);
        var changed = false;
        foreach (var candidate in candidates.Values)
        {
            changed |= await PublishCandidateAsync(
                connection,
                transaction,
                candidate,
                now,
                cancellationToken);
        }

        foreach (var missingId in existingIds.Except(candidates.Keys, StringComparer.Ordinal))
        {
            changed |= await PublishMissingAsync(
                connection,
                transaction,
                missingId,
                now,
                cancellationToken);
        }

        if (!changed)
        {
            return await ReadAutomationRevisionAsync(
                connection,
                transaction,
                cancellationToken);
        }

        await ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE automation_state
            SET automation_revision = automation_revision + 1,
                updated_utc = $now
            WHERE id = 1;
            """,
            cancellationToken,
            ("$now", Milliseconds(now)));
        return await ReadAutomationRevisionAsync(
            connection,
            transaction,
            cancellationToken);
    }

    private async ValueTask<bool> PublishCandidateAsync(
        DbConnection connection,
        DbTransaction transaction,
        Candidate candidate,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var loaded = candidate.Loaded;
        var ready = loaded.IsValid;
        var status = ready ? "ready" : "faulted";
        var definition = loaded.Definition;
        var definitionJson = definition?.CanonicalDefinition.GetRawText();
        var diagnosticsJson = JsonSerializer.Serialize(loaded.Diagnostics, JsonOptions);
        var existing = await ReadDefinitionRowAsync(
            connection,
            transaction,
            candidate.Id,
            cancellationToken);
        var semanticSame = existing is not null &&
                           existing.Status == status &&
                           (ready
                               ? existing.DefinitionVersion == loaded.DefinitionVersion
                               : existing.SourceSha256 == loaded.SourceSha256 &&
                                 existing.DiagnosticsJson == diagnosticsJson);
        if (semanticSame)
        {
            if (existing!.SourceSha256 != loaded.SourceSha256)
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    UPDATE automation_definitions
                    SET source_sha256 = $sourceSha, updated_utc = $now
                    WHERE automation_id = $id;
                    """,
                    cancellationToken,
                    ("$sourceSha", loaded.SourceSha256),
                    ("$now", Milliseconds(now)),
                    ("$id", candidate.Id));
            }

            return false;
        }

        if (existing is null)
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO automation_definitions (
                    automation_id, source_relative_path, source_status,
                    source_sha256, definition_version, display_name, enabled,
                    definition_json, diagnostics_json, has_schedule,
                    revision, created_utc, updated_utc, missing_utc)
                VALUES (
                    $id, $path, $status,
                    $sourceSha, $definitionVersion, $displayName, $enabled,
                    $definitionJson, $diagnosticsJson, $hasSchedule,
                    1, $now, $now, NULL);
                """,
                cancellationToken,
                ("$id", candidate.Id),
                ("$path", candidate.RelativePath),
                ("$status", status),
                ("$sourceSha", loaded.SourceSha256),
                ("$definitionVersion", loaded.DefinitionVersion),
                ("$displayName", definition?.DisplayName ?? candidate.Id),
                ("$enabled", definition?.Enabled == true ? 1 : 0),
                ("$definitionJson", definitionJson),
                ("$diagnosticsJson", diagnosticsJson),
                ("$hasSchedule", definition?.Schedule is null ? 0 : 1),
                ("$now", Milliseconds(now)));
        }
        else
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE automation_definitions
                SET source_relative_path = $path,
                    source_status = $status,
                    source_sha256 = $sourceSha,
                    definition_version = $definitionVersion,
                    display_name = $displayName,
                    enabled = $enabled,
                    definition_json = $definitionJson,
                    diagnostics_json = $diagnosticsJson,
                    has_schedule = $hasSchedule,
                    revision = revision + 1,
                    updated_utc = $now,
                    missing_utc = NULL
                WHERE automation_id = $id;
                """,
                cancellationToken,
                ("$id", candidate.Id),
                ("$path", candidate.RelativePath),
                ("$status", status),
                ("$sourceSha", loaded.SourceSha256),
                ("$definitionVersion", loaded.DefinitionVersion),
                ("$displayName", definition?.DisplayName ?? candidate.Id),
                ("$enabled", definition?.Enabled == true ? 1 : 0),
                ("$definitionJson", definitionJson),
                ("$diagnosticsJson", diagnosticsJson),
                ("$hasSchedule", definition?.Schedule is null ? 0 : 1),
                ("$now", Milliseconds(now)));
        }

        if (definition?.Schedule is { } schedule)
        {
            await PublishScheduleAsync(
                connection,
                transaction,
                definition.Id,
                schedule,
                now,
                cancellationToken);
        }
        else
        {
            await ExecuteAsync(
                connection,
                transaction,
                "DELETE FROM automation_schedules WHERE automation_id = $id;",
                cancellationToken,
                ("$id", candidate.Id));
        }

        return true;
    }

    private async ValueTask PublishScheduleAsync(
        DbConnection connection,
        DbTransaction transaction,
        string automationId,
        AutomationScheduleCandidate schedule,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var existing = await ReadScheduleRowAsync(
            connection,
            transaction,
            automationId,
            cancellationToken);
        if (existing is not null &&
            existing.Cron == schedule.Cron &&
            existing.TimeZone == schedule.TimeZone)
        {
            return;
        }

        var next = AutomationScheduleCalculator.Next(
            schedule.Cron,
            schedule.TimeZone,
            now);
        await ExecuteAsync(
            connection,
            transaction,
            """
            INSERT INTO automation_schedules (
                automation_id, cron, time_zone, next_occurrence_utc,
                last_occurrence_utc, coalesced_occurrence_utc, revision, updated_utc)
            VALUES (
                $id, $cron, $timeZone, $next, NULL, NULL, 1, $now)
            ON CONFLICT (automation_id) DO UPDATE SET
                cron = excluded.cron,
                time_zone = excluded.time_zone,
                next_occurrence_utc = excluded.next_occurrence_utc,
                last_occurrence_utc = NULL,
                coalesced_occurrence_utc = NULL,
                revision = automation_schedules.revision + 1,
                updated_utc = excluded.updated_utc;
            """,
            cancellationToken,
            ("$id", automationId),
            ("$cron", schedule.Cron),
            ("$timeZone", schedule.TimeZone),
            ("$next", next is null ? null : Milliseconds(next.Value)),
            ("$now", Milliseconds(now)));
    }

    private static async ValueTask<bool> PublishMissingAsync(
        DbConnection connection,
        DbTransaction transaction,
        string automationId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var existing = await ReadDefinitionRowAsync(
            connection,
            transaction,
            automationId,
            cancellationToken);
        if (existing?.Status == "missing")
        {
            return false;
        }

        await ExecuteAsync(
            connection,
            transaction,
            """
            UPDATE automation_definitions
            SET source_status = 'missing',
                source_sha256 = NULL,
                definition_version = NULL,
                enabled = 0,
                definition_json = NULL,
                diagnostics_json = '[]',
                has_schedule = 0,
                revision = revision + 1,
                updated_utc = $now,
                missing_utc = $now
            WHERE automation_id = $id;
            DELETE FROM automation_schedules WHERE automation_id = $id;
            """,
            cancellationToken,
            ("$id", automationId),
            ("$now", Milliseconds(now)));
        return true;
    }

    private static async ValueTask<AutomationSourceProjection?> ReadProjectionAsync(
        DbConnection connection,
        string automationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT d.source_status, d.source_sha256, d.definition_version,
                   d.display_name, d.enabled, d.definition_json,
                   d.diagnostics_json, d.revision, s.automation_revision,
                   c.cron, c.time_zone, c.next_occurrence_utc,
                   c.last_occurrence_utc, c.coalesced_occurrence_utc, c.revision
            FROM automation_definitions AS d
            CROSS JOIN automation_state AS s
            LEFT JOIN automation_schedules AS c
              ON c.automation_id = d.automation_id
            WHERE d.automation_id = $id AND s.id = 1;
            """;
        AddParameter(command, "$id", automationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var status = reader.GetString(0) switch
        {
            "ready" => AutomationDefinitionSourceStatus.Ready,
            "faulted" => AutomationDefinitionSourceStatus.Faulted,
            "missing" => AutomationDefinitionSourceStatus.Missing,
            _ => throw new InvalidDataException("Automation source status is invalid."),
        };
        AutomationScheduleProjection? schedule = null;
        if (!reader.IsDBNull(9))
        {
            schedule = new AutomationScheduleProjection(
                reader.GetString(9),
                reader.GetString(10),
                Instant(reader, 11),
                Instant(reader, 12),
                Instant(reader, 13),
                reader.GetInt64(14));
        }

        return new AutomationSourceProjection(
            automationId,
            status,
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4) != 0,
            reader.IsDBNull(5) ? null : reader.GetString(5),
            JsonSerializer.Deserialize<OpenCoWorkDiagnostic[]>(
                reader.GetString(6),
                JsonOptions) ?? [],
            reader.GetInt64(7),
            reader.GetInt64(8),
            schedule);
    }

    private static async ValueTask<IReadOnlyList<string>> ReadIdsAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT automation_id FROM automation_definitions ORDER BY automation_id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private static async ValueTask<DefinitionRow?> ReadDefinitionRowAsync(
        DbConnection connection,
        DbTransaction transaction,
        string automationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT source_status, source_sha256, definition_version, diagnostics_json
            FROM automation_definitions
            WHERE automation_id = $id;
            """;
        AddParameter(command, "$id", automationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new DefinitionRow(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3))
            : null;
    }

    private static async ValueTask<ScheduleRow?> ReadScheduleRowAsync(
        DbConnection connection,
        DbTransaction transaction,
        string automationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT cron, time_zone
            FROM automation_schedules
            WHERE automation_id = $id;
            """;
        AddParameter(command, "$id", automationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ScheduleRow(reader.GetString(0), reader.GetString(1))
            : null;
    }

    private static async ValueTask<long> ReadAutomationRevisionAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT automation_revision FROM automation_state WHERE id = 1;";
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async ValueTask ExecuteAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            AddParameter(command, name, value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameter(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static long Milliseconds(DateTimeOffset value) =>
        value.ToUniversalTime().ToUnixTimeMilliseconds();

    private static DateTimeOffset? Instant(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(ordinal));

    private sealed record Candidate(
        string Id,
        string RelativePath,
        AutomationDefinitionLoadResult Loaded);

    private sealed record DefinitionRow(
        string Status,
        string? SourceSha256,
        string? DefinitionVersion,
        string DiagnosticsJson);

    private sealed record ScheduleRow(string Cron, string TimeZone);
}
