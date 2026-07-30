using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Logging;
using OpenCoWork.Core.Sessions;
using OpenCoWork.Core.State;
using OpenCoWork.Core.Workspaces;

namespace OpenCoWork.Core.Tools;

internal sealed class BackgroundTerminalRuntime
{
    private const int MaximumPerThread = 4;
    private const int MaximumPerWorkspace = 16;
    private const int MaximumArguments = 128;
    private const int MaximumInputBytes = 64 * 1024;
    private const int MaximumDurationSeconds = 60 * 60;
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly string[] SensitiveEnvironmentMarkers =
    [
        "APIKEY",
        "API_KEY",
        "AUTH",
        "BEARER",
        "CREDENTIAL",
        "PASSWORD",
        "PRIVATE",
        "SECRET",
        "TOKEN",
    ];
    private readonly OpenCoWorkPaths _paths;
    private readonly StateRuntime _state;
    private readonly SecretRedactor _redactor;
    private readonly ConcurrentDictionary<Guid, TerminalSession> _sessions = [];
    private readonly SemaphoreSlim _gate = new(1, 1);

    public BackgroundTerminalRuntime(
        OpenCoWorkPaths paths,
        StateRuntime state,
        SecretRedactor? redactor = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _redactor = redactor ?? new SecretRedactor([]);
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        _state.WriteCoordinator.ExecuteAsync(
            async (connection, transaction, token) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    UPDATE terminal_sessions
                    SET status = 'lost',
                        updated_utc = $updated_utc,
                        ended_utc = $updated_utc
                    WHERE status = 'running';
                    """;
                command.Parameters.AddWithValue(
                    "$updated_utc",
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                await command.ExecuteNonQueryAsync(token);
            },
            cancellationToken);

    public async ValueTask<ToolBindingResult> StartAsync(
        ToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        Process? process = null;
        try
        {
            var request = ParseStart(context.Arguments);
            var requestSha256 = Sha256(ThreadJournal.Canonicalize(context.Arguments));
            await _gate.WaitAsync(cancellationToken);
            try
            {
                var existing = await ReadMetadataAsync(
                    request.SessionId,
                    cancellationToken);
                if (existing is not null)
                {
                    if (existing.ThreadId != context.ThreadId ||
                        !string.Equals(
                            existing.RequestSha256,
                            requestSha256,
                            StringComparison.Ordinal))
                    {
                        throw new TerminalRuntimeException(
                            BackgroundTerminalErrorCodes.SessionConflict,
                            "Terminal Session ID is bound to another request.");
                    }

                    return _sessions.TryGetValue(request.SessionId, out var live)
                        ? Success(live)
                        : Success(existing);
                }

                var counts = await ReadRunningCountsAsync(
                    context.ThreadId,
                    cancellationToken);
                if (counts.Thread >= MaximumPerThread ||
                    counts.Workspace >= MaximumPerWorkspace)
                {
                    throw new TerminalRuntimeException(
                        BackgroundTerminalErrorCodes.LimitExceeded,
                        "Background Terminal session limit was reached.");
                }

                process = new Process
                {
                    StartInfo = CreateStartInfo(
                        request,
                        WorkspacePathGuard.ResolveExecutionRoot(
                            context.ExecutionWorkspace,
                            _paths.WorkspaceRoot)),
                    EnableRaisingEvents = true,
                };
                if (!process.Start())
                {
                    throw new TerminalRuntimeException(
                        ToolErrorCodes.ExecutionFailed,
                        "Background Terminal process did not start.");
                }

                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var session = new TerminalSession(
                    request.SessionId,
                    context.ThreadId,
                    requestSha256,
                    process,
                    now,
                    TimeSpan.FromSeconds(request.MaxDurationSeconds));
                try
                {
                    await InsertMetadataAsync(session, cancellationToken);
                }
                catch
                {
                    await KillAsync(process);
                    process.Dispose();
                    process = null;
                    throw;
                }

                if (!_sessions.TryAdd(request.SessionId, session))
                {
                    await KillAsync(process);
                    await UpdateStatusAsync(
                        session,
                        "failed",
                        exitCode: null,
                        CancellationToken.None);
                    process.Dispose();
                    process = null;
                    throw new TerminalRuntimeException(
                        BackgroundTerminalErrorCodes.SessionConflict,
                        "Terminal Session ID is already active.");
                }

                process = null;
                session.Completion = MonitorAsync(session);
                return Success(session);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (TerminalRuntimeException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
        catch (Exception exception) when (
            exception is SqliteException or IOException or UnauthorizedAccessException or
                InvalidOperationException or ArgumentException)
        {
            if (process is not null)
            {
                await KillAsync(process);
                process.Dispose();
            }

            return Failure(
                ToolErrorCodes.ExecutionFailed,
                "Background Terminal start failed.");
        }
    }

    public async ValueTask<ToolBindingResult> ListAsync(
        ToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection =
                await _state.OpenReadOnlyConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT terminal_session_id, status, started_utc, updated_utc,
                       ended_utc, exit_code
                FROM terminal_sessions
                WHERE thread_id = $thread_id
                ORDER BY started_utc, terminal_session_id;
                """;
            command.Parameters.AddWithValue(
                "$thread_id",
                context.ThreadId.ToString("D"));
            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);
            var items = new List<object>();
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new
                {
                    sessionId = reader.GetString(0),
                    status = reader.GetString(1),
                    startedUtc = reader.GetInt64(2),
                    updatedUtc = reader.GetInt64(3),
                    endedUtc = reader.IsDBNull(4)
                        ? (long?)null
                        : reader.GetInt64(4),
                    exitCode = reader.IsDBNull(5)
                        ? (int?)null
                        : reader.GetInt32(5),
                });
            }

            return ToolBindingResult.Success(JsonSerializer.SerializeToElement(new
            {
                items,
            }));
        }
        catch (Exception exception) when (
            exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            return Failure(
                ToolErrorCodes.ExecutionFailed,
                "Background Terminal list failed.");
        }
    }

    public ValueTask<ToolBindingResult> ReadAsync(
        ToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sessionId = RequiredGuid(context.Arguments, "sessionId");
            var offset = RequiredNonNegativeInt64(context.Arguments, "offset");
            var maxBytes = OptionalPositive(
                context.Arguments,
                "maxBytes",
                64 * 1024,
                128 * 1024);
            var session = RequireLive(context.ThreadId, sessionId);
            var read = session.Output.Read(offset, maxBytes);
            return ValueTask.FromResult(ToolBindingResult.Success(
                JsonSerializer.SerializeToElement(new
                {
                    sessionId,
                    status = session.Status,
                    exitCode = session.ExitCode,
                    baseOffset = read.BaseOffset,
                    nextOffset = read.NextOffset,
                    content = _redactor.RedactText(read.Content),
                })));
        }
        catch (TerminalRuntimeException exception)
        {
            return ValueTask.FromResult(Failure(exception.Code, exception.Message));
        }
    }

    public async ValueTask<ToolBindingResult> WriteAsync(
        ToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var sessionId = RequiredGuid(context.Arguments, "sessionId");
            var input = RequiredString(context.Arguments, "input");
            if (StrictUtf8.GetByteCount(input) > MaximumInputBytes)
            {
                return Failure(
                    ToolErrorCodes.InputTooLarge,
                    "Background Terminal input exceeds the size limit.");
            }

            var session = RequireLive(context.ThreadId, sessionId);
            if (!string.Equals(session.Status, "running", StringComparison.Ordinal))
            {
                throw new TerminalRuntimeException(
                    ToolErrorCodes.PreconditionFailed,
                    "Background Terminal is not running.");
            }

            await session.Process.StandardInput.WriteAsync(
                input.AsMemory(),
                cancellationToken);
            await session.Process.StandardInput.FlushAsync(cancellationToken);
            return ToolBindingResult.Success(JsonSerializer.SerializeToElement(new
            {
                sessionId,
                writtenBytes = StrictUtf8.GetByteCount(input),
            }));
        }
        catch (EncoderFallbackException)
        {
            return Failure(
                ToolErrorCodes.ContentUnsupported,
                "Background Terminal input is not valid UTF-8 text.");
        }
        catch (TerminalRuntimeException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
            return Failure(
                ToolErrorCodes.ExecutionFailed,
                "Background Terminal write failed.");
        }
    }

    public async ValueTask<ToolBindingResult> StopAsync(
        ToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var sessionId = RequiredGuid(context.Arguments, "sessionId");
            var session = RequireLive(context.ThreadId, sessionId);
            await StopSessionAsync(session, cancellationToken);
            return Success(session);
        }
        catch (TerminalRuntimeException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
    }

    public async ValueTask<ToolBindingResult> ReleaseAsync(
        ToolInvocationContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var sessionId = RequiredGuid(context.Arguments, "sessionId");
            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (_sessions.TryGetValue(sessionId, out var live) &&
                    live.ThreadId != context.ThreadId)
                {
                    throw NotFound();
                }

                if (live is not null &&
                    string.Equals(live.Status, "running", StringComparison.Ordinal))
                {
                    throw new TerminalRuntimeException(
                        ToolErrorCodes.PreconditionFailed,
                        "Running Background Terminal cannot be released.");
                }

                var deleted = false;
                await _state.WriteCoordinator.ExecuteAsync(
                    async (connection, transaction, token) =>
                    {
                        await using var command = connection.CreateCommand();
                        command.Transaction = transaction;
                        command.CommandText =
                            """
                            DELETE FROM terminal_sessions
                            WHERE terminal_session_id = $session_id
                              AND thread_id = $thread_id
                              AND status IN ('exited', 'stopped', 'lost', 'failed');
                            """;
                        command.Parameters.AddWithValue(
                            "$session_id",
                            sessionId.ToString("D"));
                        command.Parameters.AddWithValue(
                            "$thread_id",
                            context.ThreadId.ToString("D"));
                        deleted = await command.ExecuteNonQueryAsync(token) != 0;
                    },
                    cancellationToken);
                if (!deleted)
                {
                    throw NotFound();
                }

                if (_sessions.TryRemove(sessionId, out var removed))
                {
                    removed.Process.Dispose();
                }

                return ToolBindingResult.Success(JsonSerializer.SerializeToElement(new
                {
                    sessionId,
                    released = true,
                }));
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (TerminalRuntimeException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
        catch (Exception exception) when (
            exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            return Failure(
                ToolErrorCodes.ExecutionFailed,
                "Background Terminal release failed.");
        }
    }

    public async Task StopThreadAsync(
        Guid threadId,
        CancellationToken cancellationToken = default)
    {
        var sessions = _sessions.Values
            .Where(item =>
                item.ThreadId == threadId &&
                string.Equals(item.Status, "running", StringComparison.Ordinal))
            .ToArray();
        await StopSessionsAsync(sessions, cancellationToken);
    }

    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        var sessions = _sessions.Values
            .Where(item =>
                string.Equals(item.Status, "running", StringComparison.Ordinal))
            .ToArray();
        await StopSessionsAsync(sessions, cancellationToken);
    }

    private async Task StopSessionsAsync(
        IReadOnlyList<TerminalSession> sessions,
        CancellationToken cancellationToken)
    {
        var errors = new List<Exception>();
        foreach (var session in sessions)
        {
            try
            {
                await StopSessionAsync(session, cancellationToken);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException)
            {
                errors.Add(exception);
            }
        }

        if (errors.Count != 0)
        {
            throw new AggregateException(
                "Background Terminal cleanup failed.",
                errors);
        }
    }

    private async Task MonitorAsync(TerminalSession session)
    {
        try
        {
            var stdout = PumpAsync(session.Process.StandardOutput, session);
            var stderr = PumpAsync(session.Process.StandardError, session);
            var exit = session.Process.WaitForExitAsync();
            var expiry = Task.Delay(session.MaximumDuration);
            if (await Task.WhenAny(exit, expiry) == expiry)
            {
                session.StopRequested = true;
                await KillAsync(session.Process);
            }

            await exit;
            await Task.WhenAll(stdout, stderr);
            await UpdateStatusAsync(
                session,
                session.StopRequested ? "stopped" : "exited",
                session.Process.ExitCode,
                CancellationToken.None);
        }
        catch
        {
            await KillAsync(session.Process);
            try
            {
                await UpdateStatusAsync(
                    session,
                    "failed",
                    exitCode: null,
                    CancellationToken.None);
            }
            catch
            {
            }
        }
    }

    private static async Task PumpAsync(
        StreamReader reader,
        TerminalSession session)
    {
        var buffer = new char[1024];
        while (true)
        {
            var read = await reader.ReadAsync(buffer);
            if (read == 0)
            {
                return;
            }

            session.Output.Append(new string(buffer, 0, read));
        }
    }

    private async Task StopSessionAsync(
        TerminalSession session,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(session.Status, "running", StringComparison.Ordinal))
        {
            return;
        }

        session.StopRequested = true;
        await KillAsync(session.Process);
        try
        {
            await session.Process.WaitForExitAsync(cancellationToken);
        }
        catch (InvalidOperationException)
        {
        }

        await UpdateStatusAsync(
            session,
            "stopped",
            TryExitCode(session.Process),
            CancellationToken.None);
    }

    private async Task UpdateStatusAsync(
        TerminalSession session,
        string status,
        int? exitCode,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await _state.WriteCoordinator.ExecuteAsync(
            async (connection, transaction, token) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    UPDATE terminal_sessions
                    SET status = $status,
                        updated_utc = $updated_utc,
                        ended_utc = $updated_utc,
                        exit_code = $exit_code
                    WHERE terminal_session_id = $session_id;
                    """;
                command.Parameters.AddWithValue("$status", status);
                command.Parameters.AddWithValue("$updated_utc", now);
                command.Parameters.AddWithValue(
                    "$exit_code",
                    exitCode is null ? DBNull.Value : exitCode.Value);
                command.Parameters.AddWithValue(
                    "$session_id",
                    session.SessionId.ToString("D"));
                await command.ExecuteNonQueryAsync(token);
            },
            cancellationToken);
        session.Status = status;
        session.ExitCode = exitCode;
    }

    private Task InsertMetadataAsync(
        TerminalSession session,
        CancellationToken cancellationToken) =>
        _state.WriteCoordinator.ExecuteAsync(
            async (connection, transaction, token) =>
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO terminal_sessions (
                        terminal_session_id, thread_id, request_sha256, status,
                        started_utc, updated_utc)
                    VALUES (
                        $session_id, $thread_id, $request_sha256, 'running',
                        $started_utc, $started_utc);
                    """;
                command.Parameters.AddWithValue(
                    "$session_id",
                    session.SessionId.ToString("D"));
                command.Parameters.AddWithValue(
                    "$thread_id",
                    session.ThreadId.ToString("D"));
                command.Parameters.AddWithValue(
                    "$request_sha256",
                    session.RequestSha256);
                command.Parameters.AddWithValue(
                    "$started_utc",
                    session.StartedUtc);
                await command.ExecuteNonQueryAsync(token);
            },
            cancellationToken);

    private async Task<TerminalMetadata?> ReadMetadataAsync(
        Guid sessionId,
        CancellationToken cancellationToken)
    {
        await using var connection =
            await _state.OpenReadOnlyConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT thread_id, request_sha256, status, started_utc, updated_utc,
                   ended_utc, exit_code
            FROM terminal_sessions
            WHERE terminal_session_id = $session_id;
            """;
        command.Parameters.AddWithValue("$session_id", sessionId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new TerminalMetadata(
                sessionId,
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                reader.IsDBNull(6) ? null : reader.GetInt32(6))
            : null;
    }

    private async Task<(int Thread, int Workspace)> ReadRunningCountsAsync(
        Guid threadId,
        CancellationToken cancellationToken)
    {
        await using var connection =
            await _state.OpenReadOnlyConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                sum(CASE WHEN thread_id = $thread_id THEN 1 ELSE 0 END),
                count(*)
            FROM terminal_sessions
            WHERE status = 'running';
            """;
        command.Parameters.AddWithValue("$thread_id", threadId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return (
            reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
            reader.GetInt32(1));
    }

    private ProcessStartInfo CreateStartInfo(
        StartRequest request,
        string root)
    {
        string workingDirectory;
        try
        {
            workingDirectory = request.WorkingDirectory is null
                ? root
                : WorkspacePathGuard.ResolveContained(
                    root,
                    Path.Combine(
                        root,
                        ".opencowork-terminal-anchor"),
                    request.WorkingDirectory).PhysicalPath;
        }
        catch (WorkspacePathEscapeException)
        {
            throw new TerminalRuntimeException(
                ToolErrorCodes.PathDenied,
                "Background Terminal working directory is denied.");
        }

        if (!Directory.Exists(workingDirectory))
        {
            throw new TerminalRuntimeException(
                ToolErrorCodes.PathNotFound,
                "Background Terminal working directory was not found.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = request.Command,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardInputEncoding = StrictUtf8,
            StandardOutputEncoding = StrictUtf8,
            StandardErrorEncoding = StrictUtf8,
        };
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var name in startInfo.Environment.Keys.ToArray())
        {
            if (IsSensitiveEnvironmentName(name))
            {
                startInfo.Environment.Remove(name);
            }
        }

        return startInfo;
    }

    private TerminalSession RequireLive(Guid threadId, Guid sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session) ||
            session.ThreadId != threadId)
        {
            throw new TerminalRuntimeException(
                BackgroundTerminalErrorCodes.Lost,
                "Background Terminal output is unavailable.");
        }

        return session;
    }

    private static StartRequest ParseStart(JsonElement arguments)
    {
        var sessionId = RequiredGuid(arguments, "sessionId");
        if (sessionId.Version != 7)
        {
            throw Invalid();
        }

        var command = RequiredString(arguments, "command");
        if (string.IsNullOrWhiteSpace(command) || command.Length > 4096)
        {
            throw Invalid();
        }

        if (!arguments.TryGetProperty("arguments", out var argumentsElement) ||
            argumentsElement.ValueKind != JsonValueKind.Array)
        {
            throw Invalid();
        }

        var processArguments = argumentsElement.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String
                ? item.GetString()
                : null)
            .ToArray();
        if (processArguments.Length > MaximumArguments ||
            processArguments.Any(item => item is null or { Length: > 4096 }))
        {
            throw Invalid();
        }

        var maximumDuration = RequiredPositive(
            arguments,
            "maxDurationSeconds",
            MaximumDurationSeconds);
        string? workingDirectory = null;
        if (arguments.TryGetProperty("workingDirectory", out var working))
        {
            if (working.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(working.GetString()))
            {
                throw Invalid();
            }

            workingDirectory = working.GetString();
        }

        return new StartRequest(
            sessionId,
            command,
            processArguments!,
            workingDirectory,
            maximumDuration);
    }

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

    private static long RequiredNonNegativeInt64(JsonElement arguments, string name)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty(name, out var value) ||
            !value.TryGetInt64(out var result) ||
            result < 0)
        {
            throw Invalid();
        }

        return result;
    }

    private static int RequiredPositive(
        JsonElement arguments,
        string name,
        int maximum)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty(name, out var value) ||
            !value.TryGetInt32(out var result) ||
            result <= 0 ||
            result > maximum)
        {
            throw Invalid();
        }

        return result;
    }

    private static int OptionalPositive(
        JsonElement arguments,
        string name,
        int defaultValue,
        int maximum) =>
        arguments.TryGetProperty(name, out var value)
            ? value.TryGetInt32(out var result) &&
              result is >= 4096 &&
              result <= maximum
                ? result
                : throw Invalid()
            : defaultValue;

    private static bool IsSensitiveEnvironmentName(string name)
    {
        var normalized = name.Replace("-", "_", StringComparison.Ordinal)
            .ToUpperInvariant();
        return SensitiveEnvironmentMarkers.Any(marker =>
            normalized.Contains(marker, StringComparison.Ordinal));
    }

    private static int? TryExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task KillAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static ToolBindingResult Success(TerminalSession session) =>
        ToolBindingResult.Success(JsonSerializer.SerializeToElement(new
        {
            sessionId = session.SessionId,
            status = session.Status,
            startedUtc = session.StartedUtc,
            exitCode = session.ExitCode,
        }));

    private static ToolBindingResult Success(TerminalMetadata session) =>
        ToolBindingResult.Success(JsonSerializer.SerializeToElement(new
        {
            sessionId = session.SessionId,
            status = session.Status,
            startedUtc = session.StartedUtc,
            updatedUtc = session.UpdatedUtc,
            endedUtc = session.EndedUtc,
            exitCode = session.ExitCode,
        }));

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static ToolBindingResult Failure(string code, string message) =>
        ToolBindingResult.Failure(new SessionError(
            code,
            message,
            IsRetryable: false));

    private static TerminalRuntimeException Invalid() =>
        new(ToolErrorCodes.InputInvalid, "Background Terminal arguments are invalid.");

    private static TerminalRuntimeException NotFound() =>
        new(ToolErrorCodes.NotFound, "Background Terminal was not found.");

    private sealed record StartRequest(
        Guid SessionId,
        string Command,
        string[] Arguments,
        string? WorkingDirectory,
        int MaxDurationSeconds);

    private sealed record TerminalMetadata(
        Guid SessionId,
        Guid ThreadId,
        string RequestSha256,
        string Status,
        long StartedUtc,
        long UpdatedUtc,
        long? EndedUtc,
        int? ExitCode);

    private sealed class TerminalSession(
        Guid sessionId,
        Guid threadId,
        string requestSha256,
        Process process,
        long startedUtc,
        TimeSpan maximumDuration)
    {
        private volatile string _status = "running";
        private volatile bool _stopRequested;
        private int _exitCode = int.MinValue;

        public Guid SessionId { get; } = sessionId;

        public Guid ThreadId { get; } = threadId;

        public string RequestSha256 { get; } = requestSha256;

        public Process Process { get; } = process;

        public long StartedUtc { get; } = startedUtc;

        public TimeSpan MaximumDuration { get; } = maximumDuration;

        public OutputRing Output { get; } = new();

        public string Status
        {
            get => _status;
            set => _status = value;
        }

        public int? ExitCode
        {
            get
            {
                var value = Volatile.Read(ref _exitCode);
                return value == int.MinValue ? null : value;
            }
            set => Volatile.Write(ref _exitCode, value ?? int.MinValue);
        }

        public bool StopRequested
        {
            get => _stopRequested;
            set => _stopRequested = value;
        }

        public Task Completion { get; set; } = Task.CompletedTask;
    }

    private sealed class OutputRing
    {
        private const int MaximumBytes = 1024 * 1024;
        private readonly object _gate = new();
        private readonly List<OutputChunk> _chunks = [];
        private int _storedBytes;
        private long _nextOffset;

        public void Append(string content)
        {
            if (content.Length == 0)
            {
                return;
            }

            var bytes = StrictUtf8.GetByteCount(content);
            lock (_gate)
            {
                var chunk = new OutputChunk(_nextOffset, content, bytes);
                _chunks.Add(chunk);
                _nextOffset += bytes;
                _storedBytes += bytes;
                while (_storedBytes > MaximumBytes && _chunks.Count > 0)
                {
                    _storedBytes -= _chunks[0].ByteCount;
                    _chunks.RemoveAt(0);
                }
            }
        }

        public OutputRead Read(long offset, int maximumBytes)
        {
            lock (_gate)
            {
                var baseOffset = _chunks.Count == 0
                    ? _nextOffset
                    : _chunks[0].Offset;
                if (offset < baseOffset ||
                    offset > _nextOffset ||
                    offset != _nextOffset &&
                    !_chunks.Any(item => item.Offset == offset))
                {
                    throw new TerminalRuntimeException(
                        BackgroundTerminalErrorCodes.ResetRequired,
                        "Background Terminal reader must reset to the current base offset.");
                }

                var builder = new StringBuilder();
                var bytes = 0;
                var nextOffset = offset;
                foreach (var chunk in _chunks.Where(item => item.Offset >= offset))
                {
                    if (bytes + chunk.ByteCount > maximumBytes)
                    {
                        break;
                    }

                    builder.Append(chunk.Content);
                    bytes += chunk.ByteCount;
                    nextOffset = chunk.Offset + chunk.ByteCount;
                }

                return new OutputRead(
                    baseOffset,
                    nextOffset,
                    builder.ToString());
            }
        }
    }

    private sealed record OutputChunk(long Offset, string Content, int ByteCount);

    private sealed record OutputRead(
        long BaseOffset,
        long NextOffset,
        string Content);

    private sealed class TerminalRuntimeException(string code, string message)
        : Exception(message)
    {
        public string Code { get; } = code;
    }
}
