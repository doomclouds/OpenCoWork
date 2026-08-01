using OpenCoWork.Abstractions;
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
    public void DeepSeek_auth_prefers_environment_then_uses_workspace_scoped_secret_store()
    {
        var (workspace, _) = CreateDirectories();
        try
        {
            const string environmentSecret = "environment-secret-value";
            const string storedSecret = "stored-secret-value";
            var environment = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["DEEPSEEK_API_KEY"] = environmentSecret,
            };
            var paths = new OpenCoWorkPaths(workspace);
            Directory.CreateDirectory(paths.OpenCoWorkDirectory);
            var store = new InMemoryOsSecretStore();
            var redactor = new SecretRedactor([]);
            var auth = new ProviderAuthService(
                new ProviderDeclarationCatalog(paths, _ => null),
                store,
                redactor,
                name => environment.GetValueOrDefault(name),
                paths);
            auth.Set("auth/deepseek", storedSecret);

            using (var lease = auth.Acquire("auth/deepseek"))
            {
                Assert.Equal(environmentSecret, lease.Secret);
                Assert.DoesNotContain(
                    environmentSecret,
                    redactor.RedactText(environmentSecret),
                    StringComparison.Ordinal);
            }

            environment.Clear();
            using (var lease = auth.Acquire("auth/deepseek"))
            {
                Assert.Equal(storedSecret, lease.Secret);
            }

            var otherWorkspace = Path.Combine(Path.GetDirectoryName(workspace)!, "other");
            Directory.CreateDirectory(otherWorkspace);
            var otherPaths = new OpenCoWorkPaths(otherWorkspace);
            var isolated = new ProviderAuthService(
                new ProviderDeclarationCatalog(otherPaths, _ => null),
                store,
                new SecretRedactor([]),
                _ => null,
                otherPaths);
            Assert.Throws<AgentPreparationException>(
                () => isolated.Acquire("auth/deepseek"));

            auth.Clear("auth/deepseek");
            Assert.Throws<AgentPreparationException>(
                () => auth.Acquire("auth/deepseek"));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(workspace)!, recursive: true);
        }
    }

    [Fact]
    public void Workspace_auth_profiles_remain_available_for_non_provider_capabilities()
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
    public void Legacy_workspace_provider_file_is_rejected_before_model_resolution()
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
                    "models": []
                  }]
                }
                """);

            var declarations = new ProviderDeclarationCatalog(paths, _ => null);
            var registry = new ProviderRegistry(
                new ModelsConfig(),
                AppContext.BaseDirectory,
                workspace,
                declarations);

            Assert.Contains(
                declarations.Contributions.SelectMany(set => set.Items),
                item => item.Kind == OpenCoWork.Abstractions.CapabilityKind.Provider &&
                        item.Status == OpenCoWork.Abstractions.CapabilityStatus.Faulted &&
                        item.DiagnosticCodes.Contains(
                            "provider.legacyConfigurationUnsupported",
                            StringComparer.Ordinal));
            var error = Assert.Throws<AgentPreparationException>(() =>
                registry.Resolve("deepseek", "deepseek-v4-flash"));
            Assert.Equal(AgentErrorCodes.ContextInputInvalid, error.Code);
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
