using OpenCoWork.Core.Agents;
using OpenCoWork.Core.Capabilities;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Logging;
using OpenCoWork.Core.Workspaces;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class ProviderAuthTests
{
    [Fact]
    public void External_provider_reuses_existing_model_and_tokenizer_runtime()
    {
        var (workspace, user) = CreateDirectories();
        try
        {
            var paths = new OpenCoWorkPaths(workspace);
            Directory.CreateDirectory(paths.OpenCoWorkDirectory);
            File.WriteAllText(
                Path.Combine(paths.OpenCoWorkDirectory, "auth.json"),
                """
                {
                  "schemaVersion": 1,
                  "profiles": [{
                    "id": "auth/acme",
                    "kind": "apiKey",
                    "source": { "kind": "environment", "name": "ACME_API_KEY" },
                    "placement": { "kind": "header", "name": "X-Api-Key" }
                  }]
                }
                """);
            File.WriteAllText(
                Path.Combine(paths.OpenCoWorkDirectory, "providers.json"),
                """
                {
                  "schemaVersion": 1,
                  "providers": [{
                    "id": "workspace/acme",
                    "protocol": "openaiCompatible",
                    "baseUrl": "https://api.example.test/v1",
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
                """);
            var declarations = new ProviderDeclarationCatalog(
                paths,
                name => name == "ACME_API_KEY" ? "secret" : null);
            var registry = new ProviderRegistry(
                new ModelsConfig(),
                AppContext.BaseDirectory,
                workspace,
                declarations);

            var provider = registry.Resolve(
                "workspace/acme",
                "qwen3.8-max-preview");

            Assert.Equal("auth/acme", provider.AuthProfileId);
            Assert.Equal(ProviderAuthPlacementKind.Header, provider.AuthPlacement.Kind);
            Assert.Equal("X-Api-Key", provider.AuthPlacement.HeaderName);
            Assert.True(provider.SupportsToolCalls);
            Assert.Equal(TimeSpan.FromSeconds(30), provider.ResponseHeaderTimeout);
            Assert.Equal(TimeSpan.FromSeconds(60), provider.StreamIdleTimeout);
            Assert.Contains(
                declarations.Contributions.SelectMany(set => set.Items),
                item => item.Kind == OpenCoWork.Abstractions.CapabilityKind.Provider &&
                        item.Id == "workspace/acme" &&
                        item.Status == OpenCoWork.Abstractions.CapabilityStatus.Ready);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(workspace)!, recursive: true);
        }
    }

    [Fact]
    public void Secret_is_resolved_for_each_lease_and_registered_only_while_active()
    {
        var (workspace, _) = CreateDirectories();
        try
        {
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ACME_API_KEY"] = "first-secret-value",
            };
            var paths = new OpenCoWorkPaths(workspace);
            Directory.CreateDirectory(paths.OpenCoWorkDirectory);
            File.WriteAllText(
                Path.Combine(paths.OpenCoWorkDirectory, "auth.json"),
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
                """);
            var redactor = new SecretRedactor([]);
            var declarations = new ProviderDeclarationCatalog(
                paths,
                name => environment.GetValueOrDefault(name));
            var auth = new ProviderAuthService(
                new ModelsConfig(),
                declarations,
                new InMemoryOsSecretStore(),
                redactor,
                name => environment.GetValueOrDefault(name));

            using (var first = auth.Acquire("auth/acme"))
            {
                Assert.Equal("first-secret-value", first.Secret);
                Assert.DoesNotContain(
                    "first-secret-value",
                    redactor.RedactText("first-secret-value"),
                    StringComparison.Ordinal);
            }

            Assert.Equal(
                "first-secret-value",
                redactor.RedactText("first-secret-value"));
            environment["ACME_API_KEY"] = "rotated-secret-value";
            using var second = auth.Acquire("auth/acme");
            Assert.Equal("rotated-secret-value", second.Secret);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(workspace)!, recursive: true);
        }
    }

    [Fact]
    public void Invalid_model_is_isolated_without_hiding_valid_sibling()
    {
        var (workspace, _) = CreateDirectories();
        try
        {
            var paths = new OpenCoWorkPaths(workspace);
            Directory.CreateDirectory(paths.OpenCoWorkDirectory);
            File.WriteAllText(
                Path.Combine(paths.OpenCoWorkDirectory, "providers.json"),
                """
                {
                  "schemaVersion": 1,
                  "providers": [{
                    "id": "workspace/acme",
                    "protocol": "openaiCompatible",
                    "baseUrl": "https://api.example.test/v1",
                    "authProfileId": null,
                    "timeouts": {
                      "responseHeaderMs": 30000,
                      "streamIdleMs": 60000
                    },
                    "models": [
                      {
                        "id": "qwen3.8-max-preview",
                        "capabilities": ["streaming", "usage"],
                        "tokenizerProfileId": "qwen-o200k",
                        "tokenizerProfileVersion": "1",
                        "contextWindowTokens": 983616,
                        "maxOutputTokens": 131072,
                        "tokenizerPath": null,
                        "tokenizerSha256": null
                      },
                      {
                        "id": "broken",
                        "capabilities": ["magic"],
                        "tokenizerProfileId": "broken",
                        "tokenizerProfileVersion": "1",
                        "contextWindowTokens": 0,
                        "maxOutputTokens": 1,
                        "tokenizerPath": null,
                        "tokenizerSha256": null
                      }
                    ]
                  }]
                }
                """);

            var declarations = new ProviderDeclarationCatalog(paths, _ => null);

            Assert.Single(declarations.Providers["workspace/acme"].Models);
            Assert.Contains(
                declarations.Contributions.SelectMany(set => set.Items),
                item => item.Kind == OpenCoWork.Abstractions.CapabilityKind.Model &&
                        item.Id.EndsWith("/broken", StringComparison.Ordinal) &&
                        item.Status == OpenCoWork.Abstractions.CapabilityStatus.Faulted);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(workspace)!, recursive: true);
        }
    }

    private static (string Workspace, string User) CreateDirectories()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-provider-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "workspace");
        var user = Path.Combine(root, "user");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(user);
        return (workspace, user);
    }
}
