using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenCoWork.Core.Logging;
using Xunit;

namespace OpenCoWork.Core.Tests;

[Collection("Console redirection")]
public sealed class StructuredLoggingTests
{
    [Fact]
    public void Redaction_happens_before_file_provider_for_message_scope_properties_and_exception()
    {
        const string canary = "secret-canary-a91f6d36";
        const string transient = "transient-token-831b2f";
        using var files = new TempDirectory();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        Console.SetOut(stdout);
        Console.SetError(stderr);

        try
        {
            var fileProvider = new JsonLinesFileLoggerProvider(
                files.Path,
                LogLevel.Trace,
                () => new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero),
                processId: 4242);
            using (var factory = LoggerFactory.Create(builder =>
                   {
                       builder.ClearProviders();
                       builder.SetMinimumLevel(LogLevel.Trace);
                       builder.AddProvider(new RedactingLoggerProvider(
                           fileProvider,
                           new SecretRedactor([canary])));
                   }))
            {
                var logger = factory.CreateLogger("OpenCoWork.Tests");
                using (logger.BeginScope(new Dictionary<string, object?>
                {
                    ["scopeToken"] = canary,
                }))
                {
                    logger.LogError(
                        new InvalidOperationException($"exception={canary}"),
                        "request token={Token}; transient={TransientToken}; canary={Canary}",
                        canary,
                        transient,
                        canary);
                }
            }
            fileProvider.Dispose();

            var allOutput =
                stdout + stderr.ToString() + File.ReadAllText(fileProvider.FilePath);
            Assert.DoesNotContain(canary, allOutput, StringComparison.Ordinal);
            Assert.DoesNotContain(transient, allOutput, StringComparison.Ordinal);
            Assert.Empty(stdout.ToString());
            Assert.Empty(stderr.ToString());

            using var document = JsonDocument.Parse(File.ReadAllText(fileProvider.FilePath));
            var root = document.RootElement;
            Assert.Equal("2026-07-25T12:00:00.0000000+00:00", root.GetProperty("timestampUtc").GetString());
            Assert.Equal("Error", root.GetProperty("level").GetString());
            Assert.Equal("OpenCoWork.Tests", root.GetProperty("category").GetString());
            Assert.Equal("[REDACTED]", root.GetProperty("properties").GetProperty("Token").GetString());
            Assert.Contains(
                "[REDACTED]",
                root.GetProperty("exception").GetString(),
                StringComparison.Ordinal);
            Assert.Contains(
                "[REDACTED]",
                root.GetProperty("scopes")[0].GetRawText(),
                StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    [Fact]
    public async Task Concurrent_entries_are_independent_json_lines_and_dispose_flushes_all()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempDirectory();
        var fileProvider = new JsonLinesFileLoggerProvider(
            files.Path,
            LogLevel.Information);

        using (var factory = LoggerFactory.Create(builder =>
               {
                   builder.ClearProviders();
                   builder.SetMinimumLevel(LogLevel.Information);
                   builder.AddProvider(new RedactingLoggerProvider(
                       fileProvider,
                       new SecretRedactor([])));
               }))
        {
            var logger = factory.CreateLogger("Concurrent");
            await Task.WhenAll(Enumerable.Range(0, 100).Select(index => Task.Run(
                () => logger.LogInformation(
                    new EventId(index, "entry"),
                    "entry {Index}",
                    index),
                cancellationToken)));
        }
        fileProvider.Dispose();

        var lines = await File.ReadAllLinesAsync(
            fileProvider.FilePath,
            cancellationToken);
        Assert.Equal(100, lines.Length);
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            Assert.Equal("Concurrent", document.RootElement.GetProperty("category").GetString());
            Assert.Equal("entry", document.RootElement.GetProperty("eventName").GetString());
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"opencowork-logs-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

[CollectionDefinition("Console redirection", DisableParallelization = true)]
public sealed class ConsoleRedirectionCollection;
