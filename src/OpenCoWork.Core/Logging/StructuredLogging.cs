using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OpenCoWork.Core.Configuration;

namespace OpenCoWork.Core.Logging;

public sealed class SecretRedactor
{
    public const string Replacement = "[REDACTED]";
    private static readonly string[] SensitiveKeys =
    [
        "password",
        "token",
        "secret",
        "apikey",
    ];
    private static readonly Regex SensitiveAssignment = new(
        @"(?ix)\b(?<key>password|token|secret|api[_-]?key)\s*(?<separator>[:=])\s*(?<value>""[^""]*""|'[^']*'|[^\s,;}\]]+)",
        RegexOptions.CultureInvariant);
    private readonly string[] _knownValues;

    public SecretRedactor(IEnumerable<string> knownValues)
    {
        ArgumentNullException.ThrowIfNull(knownValues);
        _knownValues = knownValues
            .Where(value => !string.IsNullOrEmpty(value))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(value => value.Length)
            .ToArray();
    }

    public static SecretRedactor FromSnapshot(EffectiveConfigSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new SecretRedactor(snapshot.GetSecretValues());
    }

    public string RedactText(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        var redacted = value;
        foreach (var secret in _knownValues)
        {
            redacted = redacted.Replace(
                secret,
                Replacement,
                StringComparison.Ordinal);
        }

        return SensitiveAssignment.Replace(
            redacted,
            match =>
                match.Groups["key"].Value +
                match.Groups["separator"].Value +
                Replacement);
    }

    internal object? RedactValue(string? key, object? value)
    {
        if (key is not null && IsSensitiveKey(key))
        {
            return Replacement;
        }

        return value switch
        {
            null => null,
            string text => RedactText(text),
            Exception exception => RedactText(exception.ToString()),
            IEnumerable<KeyValuePair<string, object?>> pairs =>
                pairs.ToDictionary(
                    pair => pair.Key,
                    pair => RedactValue(pair.Key, pair.Value),
                    StringComparer.Ordinal),
            IDictionary dictionary => RedactDictionary(dictionary),
            IEnumerable enumerable when value is not string =>
                enumerable.Cast<object?>().Select(item => RedactValue(null, item)).ToArray(),
            _ when value.GetType().IsPrimitive || value is decimal => value,
            _ => RedactText(Convert.ToString(value, CultureInfo.InvariantCulture)),
        };
    }

    internal RedactedLogState Materialize<TState>(
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        var properties = Array.Empty<KeyValuePair<string, object?>>();
        if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
        {
            properties = pairs.Select(pair =>
                {
                    var redacted = RedactValue(pair.Key, pair.Value);
                    if (!string.Equals(
                            pair.Key,
                            "{OriginalFormat}",
                            StringComparison.Ordinal))
                    {
                        var originalText = Convert.ToString(
                            pair.Value,
                            CultureInfo.InvariantCulture);
                        var redactedText = Convert.ToString(
                            redacted,
                            CultureInfo.InvariantCulture);
                        if (!string.IsNullOrEmpty(originalText) &&
                            !string.Equals(
                                originalText,
                                redactedText,
                                StringComparison.Ordinal))
                        {
                            message = message.Replace(
                                originalText,
                                redactedText,
                                StringComparison.Ordinal);
                        }
                    }

                    return new KeyValuePair<string, object?>(pair.Key, redacted);
                })
                .ToArray();
        }

        return new RedactedLogState(
            RedactText(message),
            properties);
    }

    private Dictionary<string, object?> RedactDictionary(IDictionary dictionary)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in dictionary)
        {
            var key = Convert.ToString(entry.Key, CultureInfo.InvariantCulture)
                ?? string.Empty;
            result[key] = RedactValue(key, entry.Value);
        }

        return result;
    }

    private static bool IsSensitiveKey(string key)
    {
        var normalized = new string(
            key.Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());
        return SensitiveKeys.Any(
            sensitive => normalized.EndsWith(sensitive, StringComparison.Ordinal));
    }
}

internal sealed class RedactedLogState(
    string message,
    IReadOnlyList<KeyValuePair<string, object?>> properties)
    : IReadOnlyList<KeyValuePair<string, object?>>
{
    public string Message { get; } = message;

    public int Count => properties.Count;

    public KeyValuePair<string, object?> this[int index] => properties[index];

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() =>
        properties.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => Message;
}

public sealed class RedactingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly ILoggerProvider _inner;
    private readonly SecretRedactor _redactor;

    public RedactingLoggerProvider(
        ILoggerProvider inner,
        SecretRedactor redactor)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
    }

    public ILogger CreateLogger(string categoryName) =>
        new RedactingLogger(_inner.CreateLogger(categoryName), _redactor);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        if (_inner is ISupportExternalScope scopeAware)
        {
            scopeAware.SetScopeProvider(
                new RedactingScopeProvider(scopeProvider, _redactor));
        }
    }

    public void Dispose() => _inner.Dispose();

    private sealed class RedactingLogger(
        ILogger inner,
        SecretRedactor redactor) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            inner.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var redactedState = redactor.Materialize(state, exception, formatter);
            var redactedException = exception is null
                ? null
                : new RedactedException(redactor.RedactText(exception.ToString()));
            inner.Log(
                logLevel,
                eventId,
                redactedState,
                redactedException,
                static (materialized, _) => materialized.Message);
        }
    }

    private sealed class RedactingScopeProvider(
        IExternalScopeProvider inner,
        SecretRedactor redactor) : IExternalScopeProvider
    {
        public void ForEachScope<TState>(
            Action<object?, TState> callback,
            TState state) =>
            inner.ForEachScope(
                (scope, current) =>
                    callback(redactor.RedactValue(null, scope), current),
                state);

        public IDisposable Push(object? state) => inner.Push(state);
    }

    private sealed class RedactedException(string text) : Exception
    {
        public override string Message => text;

        public override string ToString() => text;
    }
}

public sealed class JsonLinesFileLoggerProvider :
    ILoggerProvider,
    ISupportExternalScope
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private readonly object _gate = new();
    private readonly LogLevel _minimumLevel;
    private readonly Func<DateTimeOffset> _clock;
    private readonly StreamWriter _writer;
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();
    private bool _disposed;

    public JsonLinesFileLoggerProvider(
        string logsDirectory,
        LogLevel minimumLevel)
        : this(
            logsDirectory,
            minimumLevel,
            static () => DateTimeOffset.UtcNow,
            Environment.ProcessId)
    {
    }

    internal JsonLinesFileLoggerProvider(
        string logsDirectory,
        LogLevel minimumLevel,
        Func<DateTimeOffset> clock,
        int processId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logsDirectory);
        _minimumLevel = minimumLevel;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        var directory = Path.GetFullPath(logsDirectory);
        Directory.CreateDirectory(directory);
        var timestamp = _clock()
            .ToUniversalTime()
            .ToString("yyyyMMdd'T'HHmmss.fffffff'Z'", CultureInfo.InvariantCulture);
        FilePath = Path.Combine(
            directory,
            $"opencowork-{timestamp}-{processId}.jsonl");
        _writer = new StreamWriter(
            new FileStream(
                FilePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public string FilePath { get; }

    public ILogger CreateLogger(string categoryName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new JsonLinesFileLogger(this, categoryName);
    }

    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        _scopeProvider = scopeProvider
            ?? throw new ArgumentNullException(nameof(scopeProvider));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _writer.Flush();
            _writer.Dispose();
            _disposed = true;
        }
    }

    private void Write<TState>(
        string category,
        LogLevel level,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (level < _minimumLevel || _disposed)
        {
            return;
        }

        var properties = state is IEnumerable<KeyValuePair<string, object?>> pairs
            ? pairs
                .Where(pair => !string.Equals(
                    pair.Key,
                    "{OriginalFormat}",
                    StringComparison.Ordinal))
                .ToDictionary(
                    pair => pair.Key,
                    pair => Normalize(pair.Value),
                    StringComparer.Ordinal)
            : new Dictionary<string, object?>(StringComparer.Ordinal);
        var scopes = new List<object?>();
        _scopeProvider.ForEachScope(
            (scope, list) => list.Add(Normalize(scope)),
            scopes);
        var entry = new LogEntry(
            _clock().ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            level.ToString(),
            category,
            eventId.Id,
            eventId.Name,
            formatter(state, exception),
            exception?.ToString(),
            properties,
            scopes);
        var json = JsonSerializer.Serialize(entry, JsonOptions);

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _writer.WriteLine(json);
        }
    }

    private static object? Normalize(object? value) =>
        value switch
        {
            null => null,
            string or bool or byte or sbyte or short or ushort or int or uint or
                long or ulong or float or double or decimal => value,
            IEnumerable<KeyValuePair<string, object?>> pairs =>
                pairs.ToDictionary(
                    pair => pair.Key,
                    pair => Normalize(pair.Value),
                    StringComparer.Ordinal),
            IDictionary dictionary => dictionary.Keys
                .Cast<object?>()
                .ToDictionary(
                    key => Convert.ToString(key, CultureInfo.InvariantCulture)
                        ?? string.Empty,
                    key => Normalize(dictionary[key!]),
                    StringComparer.Ordinal),
            IEnumerable enumerable when value is not string =>
                enumerable.Cast<object?>().Select(Normalize).ToArray(),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture),
        };

    private sealed class JsonLinesFileLogger(
        JsonLinesFileLoggerProvider provider,
        string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            provider._scopeProvider.Push(state);

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel >= provider._minimumLevel;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(
                    category,
                    logLevel,
                    eventId,
                    state,
                    exception,
                    formatter);
            }
        }
    }

    private sealed record LogEntry(
        string TimestampUtc,
        string Level,
        string Category,
        int EventId,
        string? EventName,
        string Message,
        string? Exception,
        IReadOnlyDictionary<string, object?> Properties,
        IReadOnlyList<object?> Scopes);
}
