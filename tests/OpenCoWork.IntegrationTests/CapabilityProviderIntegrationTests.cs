using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenCoWork.App;
using OpenCoWork.Core.Capabilities;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Logging;
using OpenCoWork.Core.Workspaces;
using Xunit;

namespace OpenCoWork.IntegrationTests;

public sealed class CapabilityProviderIntegrationTests
{
    [Fact]
    public async Task Workspace_provider_streams_through_existing_runtime_without_persisting_secret()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        const string secret = "capability-provider-secret-9e41";
        var root = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-capability-provider-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await using var server = new FakeProviderServer(secret);
        try
        {
            var paths = new OpenCoWorkPaths(root);
            Directory.CreateDirectory(paths.OpenCoWorkDirectory);
            await File.WriteAllTextAsync(
                paths.AuthPath,
                """
                {
                  "schemaVersion": 1,
                  "profiles": [{
                    "id": "auth/acme",
                    "kind": "apiKey",
                    "source": { "kind": "environment", "name": "ACME_API_KEY" },
                    "placement": { "kind": "bearer" }
                  }]
                }
                """,
                cancellationToken);
            await File.WriteAllTextAsync(
                paths.ProvidersPath,
                $$"""
                  {
                    "schemaVersion": 1,
                    "providers": [{
                      "id": "workspace/acme",
                      "protocol": "openaiCompatible",
                      "baseUrl": "{{server.BaseUri.AbsoluteUri.TrimEnd('/')}}/v1",
                      "authProfileId": "auth/acme",
                      "timeouts": {
                        "responseHeaderMs": 30000,
                        "streamIdleMs": 60000
                      },
                      "models": [{
                        "id": "qwen3.8-max-preview",
                        "capabilities": ["streaming", "toolCalls", "usage"],
                        "tokenizerProfileId": "qwen-o200k",
                        "tokenizerProfileVersion": "1",
                        "contextWindowTokens": 983616,
                        "maxOutputTokens": 131072,
                        "tokenizerPath": null,
                        "tokenizerSha256": null
                      }]
                    }]
                  }
                  """,
                cancellationToken);
            var models = new ModelsConfig
            {
                DefaultProvider = "workspace/acme",
                DefaultModel = "qwen3.8-max-preview",
                Providers = new Dictionary<string, ProviderConfig>(StringComparer.Ordinal),
            };
            var declarations = new ProviderDeclarationCatalog(
                paths,
                name => name == "ACME_API_KEY" ? secret : null);
            var redactor = new SecretRedactor([]);
            var secretStore = new InMemoryOsSecretStore();
            using var host = OpenCoWorkCompositionRoot.Build(
                [],
                root,
                services =>
                {
                    services.AddSingleton(models);
                    services.AddSingleton(declarations);
                    services.AddSingleton(redactor);
                    services.AddSingleton<IProviderOsSecretStore>(secretStore);
                    services.AddSingleton(new ProviderAuthService(
                        models,
                        declarations,
                        secretStore,
                        redactor,
                        name => name == "ACME_API_KEY" ? secret : null,
                        paths));
                });
            await host.StartAsync(cancellationToken);
            var output = new StringWriter();
            var error = new StringWriter();

            var exitCode = await ChatCommandRunner.RunAsync(
                host.Services,
                requestedThreadId: null,
                providerId: null,
                modelId: null,
                new StringReader("hello\n"),
                output,
                error,
                isInteractive: false,
                cancellationToken);

            await server.Completed.WaitAsync(cancellationToken);
            Assert.Equal(0, exitCode);
            Assert.Equal("OK" + Environment.NewLine, output.ToString());
            Assert.DoesNotContain(secret, output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
            Assert.False(DirectoryContains(root, Encoding.UTF8.GetBytes(secret)));
            await host.StopAsync(cancellationToken);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static bool DirectoryContains(string root, byte[] value)
    {
        foreach (var path in Directory.EnumerateFiles(
                     root,
                     "*",
                     SearchOption.AllDirectories))
        {
            try
            {
                if (File.ReadAllBytes(path).AsSpan().IndexOf(value) >= 0)
                {
                    return true;
                }
            }
            catch (IOException)
            {
            }
        }

        return false;
    }

    private sealed class FakeProviderServer : IAsyncDisposable
    {
        private readonly string _secret;
        private readonly TcpListener _listener =
            new(IPAddress.Loopback, 0);
        private readonly Task _run;

        public FakeProviderServer(string secret)
        {
            _secret = secret;
            _listener.Start();
            var endpoint = (IPEndPoint)_listener.LocalEndpoint;
            BaseUri = new Uri($"http://127.0.0.1:{endpoint.Port}/");
            _run = RunAsync();
        }

        public Uri BaseUri { get; }

        public Task Completed => _run;

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try
            {
                await _run;
            }
            catch (Exception exception) when (
                exception is OperationCanceledException or SocketException)
            {
            }
        }

        private async Task RunAsync()
        {
            for (var round = 0; round < 2; round++)
            {
                using var client = await _listener.AcceptTcpClientAsync();
                await using var stream = client.GetStream();
                using var reader = new StreamReader(
                    stream,
                    Encoding.ASCII,
                    detectEncodingFromByteOrderMarks: false,
                    leaveOpen: true);
                var headers = new List<string>();
                string? line;
                var contentLength = 0;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
                {
                    headers.Add(line);
                    if (line.StartsWith(
                            "Content-Length:",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        contentLength = int.Parse(
                            line["Content-Length:".Length..].Trim(),
                            System.Globalization.CultureInfo.InvariantCulture);
                    }
                }

                Assert.Contains(
                    headers,
                    item => string.Equals(
                        item,
                        $"Authorization: Bearer {_secret}",
                        StringComparison.OrdinalIgnoreCase));
                var body = new char[contentLength];
                _ = await reader.ReadBlockAsync(body);
                using var request =
                    System.Text.Json.JsonDocument.Parse(new string(body));
                Assert.Equal(
                    "qwen3.8-max-preview",
                    request.RootElement.GetProperty("model").GetString());
                if (round == 0)
                {
                    Assert.Contains(
                        request.RootElement.GetProperty("tools").EnumerateArray(),
                        tool => tool.GetProperty("function")
                            .GetProperty("name")
                            .GetString() == "file__list");
                }
                else
                {
                    Assert.Contains(
                        request.RootElement.GetProperty("messages").EnumerateArray(),
                        message => message.GetProperty("role").GetString() == "tool");
                }

                var sse = round == 0
                    ? """
                      data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call-1","function":{"name":"file__list","arguments":"{\"path\":\".\"}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":10,"completion_tokens":1,"total_tokens":11}}

                      data: [DONE]

                      """
                    : """
                      data: {"choices":[{"index":0,"delta":{"content":"OK"},"finish_reason":"stop"}],"usage":{"prompt_tokens":12,"completion_tokens":1,"total_tokens":13}}

                      data: [DONE]

                      """;
                var bytes = Encoding.UTF8.GetBytes(sse);
                var response = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: text/event-stream\r\n" +
                    $"Content-Length: {bytes.Length}\r\n" +
                    "Connection: close\r\n\r\n");
                await stream.WriteAsync(response);
                await stream.WriteAsync(bytes);
                await stream.FlushAsync();
            }
        }
    }
}
