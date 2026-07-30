using System.Reflection;
using System.Text.Json;
using OpenCoWork.Abstractions;
using OpenCoWork.Protocol;
using Xunit;

namespace OpenCoWork.Protocol.Tests;

public sealed class CoWorkWireTests
{
    [Fact]
    public void Wire_12_declares_the_frozen_40_method_catalog()
    {
        var declarations = typeof(OpenCoWorkJsonRpcConnection)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Select(method => method
                .GetCustomAttribute<OpenCoWorkWireMethodAttribute>())
            .Where(attribute => attribute?.Since == OpenCoWorkWire.CoWorkVersion)
            .Cast<OpenCoWorkWireMethodAttribute>()
            .ToArray();
        var methods = declarations.Select(attribute => attribute.Method).ToArray();

        Assert.Equal(40, methods.Length);
        Assert.Equal(40, methods.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("agent/profile/upsert", methods);
        Assert.Contains("mission/task/review", methods);
        Assert.Contains("mailbox/retry", methods);
        Assert.Contains("worktree/remove", methods);
        Assert.DoesNotContain(methods, method => method.StartsWith("cowork/"));
        Assert.All(
            declarations.Where(attribute => attribute.Mutates),
            attribute => Assert.Equal(
                OpenCoWorkWire.RequiredIdempotency,
                attribute.Idempotency));
    }

    [Fact]
    public async Task Wire_12_injects_host_actor_and_projects_revision()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (coWork, proxy) = CreateProxy();
        CoWorkActorContext? actor = null;
        proxy.Handler = (method, args) =>
        {
            Assert.Equal(nameof(ICoWorkService.ListAgentProfilesAsync), method.Name);
            actor = ((ListAgentProfilesRequest)args![0]!).Actor;
            return Task.FromResult(new CoWorkResult<CoWorkPage<AgentProfileSnapshot>>(
                new CoWorkPage<AgentProfileSnapshot>([], null),
                CoWorkRevision: 7,
                Error: null));
        };
        var output = new List<JsonElement>();
        await using var connection = Connection(coWork, output);

        await InitializeAsync(connection, ["1.2"], cancellationToken);
        await connection.ProcessAsync(
            """{"jsonrpc":"2.0","id":2,"method":"agent/profile/list","params":{"pageSize":20}}"""u8
                .ToArray(),
            cancellationToken);

        Assert.Equal("1.2", output[0].GetProperty("result")
            .GetProperty("wireVersion").GetString());
        Assert.Equal(CoWorkActorKind.Host, actor?.Kind);
        Assert.Equal(7, output[1].GetProperty("result")
            .GetProperty("coWorkRevision").GetInt64());
    }

    [Fact]
    public async Task Wire_10_and_11_cannot_observe_12_methods()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        foreach (var version in new[] { "1.0", "1.1" })
        {
            var output = new List<JsonElement>();
            await using var connection = Connection(
                DispatchProxy.Create<ICoWorkService, ThrowingCoWorkProxy>(),
                output,
                version == "1.1"
                    ? DispatchProxy.Create<ICapabilityService, ThrowingCapabilityProxy>()
                    : null);
            await InitializeAsync(connection, [version], cancellationToken);

            await connection.ProcessAsync(
                """{"jsonrpc":"2.0","id":2,"method":"agent/profile/list","params":{}}"""u8
                    .ToArray(),
                cancellationToken);

            Assert.Equal(
                -32601,
                output[1].GetProperty("error").GetProperty("code").GetInt32());
        }
    }

    [Fact]
    public async Task Wire_12_emits_one_redacted_domain_notification_per_mutation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profileId = Guid.CreateVersion7();
        var (coWork, proxy) = CreateProxy();
        proxy.Handler = (_, _) => Task.FromResult(
            new CoWorkResult<AgentProfileSnapshot>(
                new AgentProfileSnapshot(
                    profileId,
                    "reviewer",
                    "Reviews work",
                    "Review carefully",
                    "provider",
                    "model",
                    [],
                    [],
                    true,
                    9,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow),
                CoWorkRevision: 9,
                Error: null));
        var output = new List<JsonElement>();
        await using var connection = Connection(coWork, output);
        await InitializeAsync(connection, ["1.2"], cancellationToken);

        await connection.ProcessAsync(
            JsonSerializer.SerializeToUtf8Bytes(new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "agent/profile/upsert",
                @params = new
                {
                    commandId = Guid.CreateVersion7(),
                    expectedRevision = 8,
                    name = "reviewer",
                    description = "Reviews work",
                    providerId = "provider",
                    modelId = "model",
                    toolAllowlist = Array.Empty<string>(),
                },
            }),
            cancellationToken);

        var notification = Assert.Single(
            output,
            message => message.TryGetProperty("method", out _));
        Assert.Equal("agent/changed", notification.GetProperty("method").GetString());
        Assert.Equal(9, notification.GetProperty("params")
            .GetProperty("coWorkRevision").GetInt64());
        Assert.Equal(
            profileId,
            Assert.Single(notification.GetProperty("params")
                .GetProperty("affectedIds").EnumerateArray()).GetGuid());
        Assert.DoesNotContain(
            "Reviews work",
            notification.GetRawText(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wire_12_projects_domain_errors_without_private_details()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (coWork, proxy) = CreateProxy();
        proxy.Handler = (_, _) => Task.FromResult(
            new CoWorkResult<CoWorkPage<TeamSnapshot>>(
                Value: null,
                CoWorkRevision: 11,
                new CoWorkError(
                    CoWorkErrorCodes.PermissionDenied,
                    "private database detail")));
        var output = new List<JsonElement>();
        await using var connection = Connection(coWork, output);
        await InitializeAsync(connection, ["1.2"], cancellationToken);

        await connection.ProcessAsync(
            """{"jsonrpc":"2.0","id":2,"method":"team/list","params":{}}"""u8
                .ToArray(),
            cancellationToken);

        var error = output[1].GetProperty("error");
        Assert.Equal(
            CoWorkErrorCodes.PermissionDenied,
            error.GetProperty("data").GetProperty("errorCode").GetString());
        Assert.Equal(
            11,
            error.GetProperty("data").GetProperty("currentRevision").GetInt64());
        Assert.DoesNotContain(
            "private database detail",
            output[1].GetRawText(),
            StringComparison.Ordinal);
    }

    private static OpenCoWorkJsonRpcConnection Connection(
        ICoWorkService coWork,
        List<JsonElement> output,
        ICapabilityService? capabilities = null) =>
        new(
            DispatchProxy.Create<ISessionService, ThrowingSessionProxy>(),
            capabilities,
            coWork,
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

    private static (ICoWorkService Service, ThrowingCoWorkProxy Proxy) CreateProxy()
    {
        var service = DispatchProxy.Create<ICoWorkService, ThrowingCoWorkProxy>();
        return (service, (ThrowingCoWorkProxy)(object)service);
    }

    private class ThrowingSessionProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException(targetMethod?.Name);
    }

    private class ThrowingCoWorkProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?>? Handler { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            Handler?.Invoke(targetMethod!, args) ??
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
}
