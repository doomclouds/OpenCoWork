using System.Reflection;
using System.Text.Json;
using OpenCoWork.Abstractions;
using OpenCoWork.Protocol;
using Xunit;

namespace OpenCoWork.Protocol.Tests;

public sealed class AutomationWireTests
{
    [Fact]
    public void Wire_13_declares_only_the_frozen_catalog()
    {
        var clientMethods = typeof(OpenCoWorkJsonRpcConnection)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Select(method => method
                .GetCustomAttribute<OpenCoWorkWireMethodAttribute>())
            .Where(attribute => attribute?.Since == OpenCoWorkWire.AutomationVersion)
            .Cast<OpenCoWorkWireMethodAttribute>()
            .ToArray();
        var serverMethods = typeof(OpenCoWorkJsonRpcConnection).Assembly
            .GetType("OpenCoWork.Protocol.AutomationWireCatalog")!
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Select(method => method
                .GetCustomAttribute<OpenCoWorkWireMethodAttribute>())
            .Where(attribute => attribute is not null)
            .Cast<OpenCoWorkWireMethodAttribute>()
            .ToArray();

        Assert.Equal(
            [
                "automation/get",
                "automation/list",
                "automationRun/cancel",
                "automationRun/get",
                "automationRun/list",
                "automationRun/resolveAttention",
                "automationRun/start",
                "schedule/get",
                "schedule/list",
            ],
            clientMethods.Select(attribute => attribute.Method)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            ["automation/changed", "automationRun/changed", "schedule/changed"],
            serverMethods.Select(attribute => attribute.Method)
                .Order(StringComparer.Ordinal));
        Assert.All(
            clientMethods.Where(attribute => attribute.Mutates),
            attribute => Assert.Equal(
                OpenCoWorkWire.RequiredIdempotency,
                attribute.Idempotency));
        Assert.DoesNotContain(
            clientMethods,
            attribute => attribute.Method.Contains("retry", StringComparison.OrdinalIgnoreCase) ||
                         attribute.Method.Contains("yaml", StringComparison.OrdinalIgnoreCase) ||
                         attribute.Method.Contains("model", StringComparison.OrdinalIgnoreCase));
        Assert.All(
            clientMethods,
            attribute => Assert.DoesNotContain(
                attribute.Request.GetProperties(),
                property => property.Name is "Actor" or "PrincipalId"));
    }

    [Fact]
    public async Task Wire_13_negotiates_and_older_versions_cannot_observe_methods()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        foreach (var version in new[] { "1.0", "1.1", "1.2" })
        {
            var output = new List<JsonElement>();
            await using var connection = Connection(
                DispatchProxy.Create<IAutomationService, ThrowingAutomationProxy>(),
                output,
                version == "1.0"
                    ? null
                    : DispatchProxy.Create<
                        ICapabilityService,
                        ThrowingCapabilityProxy>(),
                version == "1.2"
                    ? DispatchProxy.Create<ICoWorkService, ThrowingCoWorkProxy>()
                    : null);
            await InitializeAsync(connection, [version], cancellationToken);
            await connection.ProcessAsync(
                """{"jsonrpc":"2.0","id":2,"method":"automation/list","params":{}}"""u8
                    .ToArray(),
                cancellationToken);

            Assert.Equal(
                -32601,
                output[1].GetProperty("error").GetProperty("code").GetInt32());
        }

        var latestOutput = new List<JsonElement>();
        await using var latest = Connection(
            DispatchProxy.Create<IAutomationService, ThrowingAutomationProxy>(),
            latestOutput);
        await InitializeAsync(latest, ["1.3"], cancellationToken);

        Assert.Equal(
            "1.3",
            latestOutput[0].GetProperty("result")
                .GetProperty("wireVersion").GetString());
    }

    [Fact]
    public async Task Wire_13_injects_host_actor_and_projects_revision_and_cursor()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (automations, proxy) = CreateProxy();
        AutomationActorContext? actor = null;
        proxy.Handler = (method, args) =>
        {
            Assert.Equal(nameof(IAutomationService.ListDefinitionsAsync), method.Name);
            actor = ((ListAutomationDefinitionsRequest)args![0]!).Actor;
            return Task.FromResult(new AutomationResult<
                AutomationPage<AutomationDefinitionSummary>>(
                new AutomationPage<AutomationDefinitionSummary>([], "next"),
                AutomationRevision: 7,
                Error: null));
        };
        var output = new List<JsonElement>();
        await using var connection = Connection(automations, output);
        await InitializeAsync(connection, ["1.3"], cancellationToken);

        await connection.ProcessAsync(
            """{"jsonrpc":"2.0","id":2,"method":"automation/list","params":{"pageSize":20,"cursor":"cursor"}}"""u8
                .ToArray(),
            cancellationToken);

        Assert.Equal(AutomationActorKind.Host, actor?.Kind);
        Assert.StartsWith("wire:", actor?.PrincipalId, StringComparison.Ordinal);
        var result = output[1].GetProperty("result");
        Assert.Equal(7, result.GetProperty("automationRevision").GetInt64());
        Assert.Equal(
            "next",
            result.GetProperty("value").GetProperty("nextCursor").GetString());
    }

    [Fact]
    public async Task Wire_13_suppresses_replay_notification_and_redacts_change()
    {
        const string canary = "automation-secret-canary-29f4";
        var cancellationToken = TestContext.Current.CancellationToken;
        var runId = Guid.CreateVersion7();
        var (automations, proxy) = CreateProxy();
        var replay = true;
        proxy.Handler = (_, _) => Task.FromResult(
            new AutomationResult<AutomationRunSnapshot>(
                Run(runId, canary),
                AutomationRevision: 9,
                Error: null,
                IsReplay: replay));
        var output = new List<JsonElement>();
        await using var connection = Connection(automations, output);
        await InitializeAsync(connection, ["1.3"], cancellationToken);
        var request = JsonSerializer.SerializeToUtf8Bytes(new
        {
            jsonrpc = "2.0",
            id = 2,
            method = "automationRun/start",
            @params = new
            {
                automationId = "daily",
                inputs = new { value = canary },
                commandId = Guid.CreateVersion7(),
                expectedRevision = 8,
            },
        });

        await connection.ProcessAsync(request, cancellationToken);
        Assert.DoesNotContain(output, message => message.TryGetProperty("method", out _));

        replay = false;
        await connection.ProcessAsync(request, cancellationToken);
        var notification = Assert.Single(
            output,
            message => message.TryGetProperty("method", out _));
        Assert.Equal(
            "automationRun/changed",
            notification.GetProperty("method").GetString());
        Assert.Equal(
            runId.ToString("D"),
            notification.GetProperty("params").GetProperty("entityId").GetString());
        Assert.DoesNotContain(canary, notification.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wire_13_maps_domain_errors_without_private_details()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (automations, proxy) = CreateProxy();
        proxy.Handler = (_, _) => Task.FromResult(
            new AutomationResult<AutomationPage<AutomationDefinitionSummary>>(
                Value: null,
                AutomationRevision: 11,
                new AutomationError(
                    AutomationErrorCodes.PermissionDenied,
                    "private automation database detail")));
        var output = new List<JsonElement>();
        await using var connection = Connection(automations, output);
        await InitializeAsync(connection, ["1.3"], cancellationToken);

        await connection.ProcessAsync(
            """{"jsonrpc":"2.0","id":2,"method":"automation/list","params":{}}"""u8
                .ToArray(),
            cancellationToken);

        var error = output[1].GetProperty("error");
        Assert.Equal(-32000, error.GetProperty("code").GetInt32());
        Assert.Equal(
            AutomationErrorCodes.PermissionDenied,
            error.GetProperty("data").GetProperty("errorCode").GetString());
        Assert.Equal(
            11,
            error.GetProperty("data").GetProperty("currentRevision").GetInt64());
        Assert.DoesNotContain(
            "private automation database detail",
            output[1].GetRawText(),
            StringComparison.Ordinal);
    }

    private static AutomationRunSnapshot Run(Guid runId, string safeSummary) =>
        new(
            new AutomationRunSummary(
                runId,
                "daily",
                AutomationTriggerKind.Manual,
                AutomationRunStatus.Pending,
                AttentionKind: null,
                DateTimeOffset.UtcNow,
                StartedAtUtc: null,
                CompletedAtUtc: null,
                Revision: 1),
            safeSummary,
            Error: null,
            ThreadId: null,
            AutomationResourceAvailability.Missing,
            WorktreeId: null,
            AutomationResourceAvailability.Missing,
            DateTimeOffset.UtcNow.AddMinutes(30),
            AttentionDeadlineUtc: null,
            "provider",
            "model",
            new AutomationPermissionSnapshot(
                "trust",
                CatalogRevision: 1,
                Plugins: [],
                Skills: [],
                Tools: [],
                Effects: []),
            Capabilities: []);

    private static OpenCoWorkJsonRpcConnection Connection(
        IAutomationService automations,
        List<JsonElement> output,
        ICapabilityService? capabilities = null,
        ICoWorkService? coWork = null) =>
        new(
            DispatchProxy.Create<ISessionService, ThrowingSessionProxy>(),
            capabilities,
            coWork,
            automations,
            "/workspace",
            "stdio",
            (message, _) =>
            {
                using var document = JsonDocument.Parse(message);
                output.Add(document.RootElement.Clone());
                return ValueTask.CompletedTask;
            });

    private static async Task InitializeAsync(
        OpenCoWorkJsonRpcConnection connection,
        string[] versions,
        CancellationToken cancellationToken) =>
        await connection.ProcessAsync(
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    client = new { name = "test", version = "1" },
                    wireVersions = versions,
                    workspace = new { path = "/workspace" },
                },
            }),
            cancellationToken);

    private static (IAutomationService Service, ThrowingAutomationProxy Proxy)
        CreateProxy()
    {
        var service =
            DispatchProxy.Create<IAutomationService, ThrowingAutomationProxy>();
        return (service, (ThrowingAutomationProxy)(object)service);
    }

    private class ThrowingSessionProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException(targetMethod?.Name);
    }

    private class ThrowingAutomationProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?>? Handler { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.IsSpecialName == true
                ? null
                : Handler?.Invoke(targetMethod!, args) ??
            throw new NotSupportedException(targetMethod?.Name);
    }

    private class ThrowingCapabilityProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.IsSpecialName == true ||
            targetMethod?.Name == nameof(ICapabilityService.DisconnectDynamicTools)
                ? null
                : throw new NotSupportedException(targetMethod?.Name);
    }

    private class ThrowingCoWorkProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException(targetMethod?.Name);
    }
}
