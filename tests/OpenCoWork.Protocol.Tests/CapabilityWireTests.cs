using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using OpenCoWork.Abstractions;
using OpenCoWork.Protocol;
using Xunit;

namespace OpenCoWork.Protocol.Tests;

public sealed class CapabilityWireTests
{
    [Fact]
    public async Task Wire_11_negotiates_highest_version_and_reads_catalog()
    {
        var capabilities = new StubCapabilityService();
        var output = new ConcurrentQueue<JsonElement>();
        await using var connection = Connection(capabilities, output);

        await ProcessAsync(
            connection,
            """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"client":{"name":"test","version":"1"},"wireVersions":["1.0","1.1"],"workspace":{"path":"/workspace"}}}
            """);
        await ProcessAsync(
            connection,
            """
            {"jsonrpc":"2.0","id":2,"method":"capability/catalog","params":{"limit":10}}
            """);

        Assert.Equal(
            "1.1",
            output.ElementAt(0).GetProperty("result")
                .GetProperty("wireVersion").GetString());
        var catalog = output.ElementAt(1).GetProperty("result");
        Assert.Equal(7, catalog.GetProperty("revision").GetInt64());
        Assert.Equal("ready", catalog.GetProperty("runtimeState").GetString());
        Assert.Equal("core/file.read", catalog.GetProperty("items")[0]
            .GetProperty("id").GetString());
    }

    [Fact]
    public async Task Wire_10_does_not_expose_capability_methods()
    {
        var output = new ConcurrentQueue<JsonElement>();
        await using var connection = Connection(new StubCapabilityService(), output);

        await ProcessAsync(
            connection,
            """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"client":{"name":"test","version":"1"},"wireVersions":["1.0"],"workspace":{"path":"/workspace"}}}
            """);
        await ProcessAsync(
            connection,
            """
            {"jsonrpc":"2.0","id":2,"method":"capability/catalog","params":{}}
            """);

        Assert.Equal(
            -32601,
            output.ElementAt(1).GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Wire_11_projects_revision_conflict_and_change_notification()
    {
        var capabilities = new StubCapabilityService();
        var output = new ConcurrentQueue<JsonElement>();
        await using var connection = Connection(capabilities, output);
        await ProcessAsync(
            connection,
            """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"client":{"name":"test","version":"1"},"wireVersions":["1.1"],"workspace":{"path":"/workspace"}}}
            """);

        await ProcessAsync(
            connection,
            """
            {"jsonrpc":"2.0","id":2,"method":"capability/refresh","params":{"expectedRevision":6}}
            """);
        capabilities.Notify(revision: 8);

        var error = output.ElementAt(1).GetProperty("error");
        Assert.Equal(
            "capability.revisionConflict",
            error.GetProperty("data").GetProperty("errorCode").GetString());
        Assert.Equal(
            7,
            error.GetProperty("data").GetProperty("currentRevision").GetInt64());
        var notification = await WaitForAsync(
            output,
            item => item.TryGetProperty("method", out var method) &&
                    method.GetString() == "capability/changed");
        Assert.Equal(
            8,
            notification.GetProperty("params").GetProperty("revision").GetInt64());
    }

    [Fact]
    public async Task Wire_11_forwards_domain_operation()
    {
        var capabilities = new StubCapabilityService();
        var output = new ConcurrentQueue<JsonElement>();
        await using var connection = Connection(capabilities, output);
        await Initialize11Async(connection);

        await ProcessAsync(
            connection,
            """
            {"jsonrpc":"2.0","id":2,"method":"memory/list","params":{"arguments":{"limit":5}}}
            """);

        Assert.Equal("memory/list", capabilities.LastOperation);
        Assert.Equal(
            5,
            output.ElementAt(1).GetProperty("result").GetProperty("result")
                .GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task Wire_11_dynamic_tool_uses_single_server_request()
    {
        var capabilities = new StubCapabilityService();
        var output = new ConcurrentQueue<JsonElement>();
        await using var connection = Connection(capabilities, output);
        await Initialize11Async(connection, dynamicTools: true);
        var threadId = Guid.CreateVersion7();
        var registrationId = Guid.CreateVersion7();
        await RegisterDynamicToolAsync(connection, threadId, registrationId);

        var invocation = capabilities.Executor!(
            JsonSerializer.SerializeToElement(new { text = "hello" }),
            TestContext.Current.CancellationToken).AsTask();
        var request = await WaitForAsync(
            output,
            item => item.TryGetProperty("method", out var method) &&
                    method.GetString() == "tool/invoke");
        await ProcessAsync(
            connection,
            new
            {
                jsonrpc = "2.0",
                id = request.GetProperty("id").GetString(),
                result = new { text = "hello" },
            });

        var result = await invocation;
        Assert.True(result.IsSuccess);
        Assert.Equal("hello", result.Output?.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Wire_11_disconnect_fails_pending_dynamic_request()
    {
        var capabilities = new StubCapabilityService();
        var output = new ConcurrentQueue<JsonElement>();
        var connection = Connection(capabilities, output);
        await Initialize11Async(connection, dynamicTools: true);
        var threadId = Guid.CreateVersion7();
        var registrationId = Guid.CreateVersion7();
        await RegisterDynamicToolAsync(connection, threadId, registrationId);
        var invocation = capabilities.Executor!(
            JsonSerializer.SerializeToElement(new { }),
            TestContext.Current.CancellationToken).AsTask();
        _ = await WaitForAsync(
            output,
            item => item.TryGetProperty("method", out var method) &&
                    method.GetString() == "tool/invoke");

        await connection.DisposeAsync();
        var result = await invocation;

        Assert.Equal(DynamicToolErrorCodes.Disconnected, result.Error?.Code);
        Assert.True(capabilities.Disconnected);
    }

    [Fact]
    public async Task Wire_11_dynamic_cancellation_notifies_client()
    {
        var capabilities = new StubCapabilityService();
        var output = new ConcurrentQueue<JsonElement>();
        await using var connection = Connection(capabilities, output);
        await Initialize11Async(connection, dynamicTools: true);
        var threadId = Guid.CreateVersion7();
        var registrationId = Guid.CreateVersion7();
        await RegisterDynamicToolAsync(
            connection,
            threadId,
            registrationId);
        using var cancellation = new CancellationTokenSource();
        var invocation = capabilities.Executor!(
            JsonSerializer.SerializeToElement(new { }),
            cancellation.Token).AsTask();
        _ = await WaitForAsync(
            output,
            item => item.TryGetProperty("method", out var method) &&
                    method.GetString() == "tool/invoke");

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await invocation);
        _ = await WaitForAsync(
            output,
            item => item.TryGetProperty("method", out var method) &&
                    method.GetString() == "$/cancelRequest");
    }

    private static OpenCoWorkJsonRpcConnection Connection(
        ICapabilityService capabilities,
        ConcurrentQueue<JsonElement> output) =>
        new(
            DispatchProxy.Create<ISessionService, ThrowingSessionProxy>(),
            capabilities,
            "/workspace",
            "stdio",
            (message, _) =>
            {
                using var document = JsonDocument.Parse(message);
                output.Enqueue(document.RootElement.Clone());
                return ValueTask.CompletedTask;
            });

    private static Task ProcessAsync(
        OpenCoWorkJsonRpcConnection connection,
        string json) =>
        connection.ProcessAsync(
            System.Text.Encoding.UTF8.GetBytes(json),
            TestContext.Current.CancellationToken);

    private static Task ProcessAsync(
        OpenCoWorkJsonRpcConnection connection,
        object value) =>
        ProcessAsync(connection, JsonSerializer.Serialize(value));

    private static Task Initialize11Async(
        OpenCoWorkJsonRpcConnection connection,
        bool dynamicTools = false) =>
        ProcessAsync(
            connection,
            new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    client = new { name = "test", version = "1" },
                    wireVersions = new[] { "1.1" },
                    workspace = new { path = "/workspace" },
                    capabilities = dynamicTools
                        ? new[] { "serverRequests", "dynamicToolExecution" }
                        : Array.Empty<string>(),
                },
            });

    private static Task RegisterDynamicToolAsync(
        OpenCoWorkJsonRpcConnection connection,
        Guid threadId,
        Guid registrationId) =>
        ProcessAsync(
            connection,
            new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "tool/dynamic/register",
                @params = new
                {
                    threadId,
                    registrationId,
                    definition = new
                    {
                        name = "echo",
                        description = "Echo.",
                        inputSchema = new { type = "object" },
                        effects = Array.Empty<string>(),
                        replaySafety = "safe",
                    },
                    definitionSha256 = new string('a', 64),
                },
            });

    private static async Task<JsonElement> WaitForAsync(
        ConcurrentQueue<JsonElement> output,
        Func<JsonElement, bool> predicate)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (!output.Any(predicate))
        {
            await Task.Delay(10, timeout.Token);
        }

        return output.First(predicate);
    }

    private sealed class StubCapabilityService : ICapabilityService
    {
        private static readonly CapabilitySourceDescriptor Source = new(
            CapabilitySourceKind.Core,
            "opencowork.core",
            "1",
            new string('a', 64));

        public event EventHandler<CapabilityCatalogChangedEventArgs>? CatalogChanged;

        public string? LastOperation { get; private set; }

        public ToolExecutor? Executor { get; private set; }

        public bool Disconnected { get; private set; }

        public ValueTask<CapabilityCatalogPage> GetCatalogAsync(
            CapabilityCatalogQuery query,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new CapabilityCatalogPage(
                1,
                7,
                new string('b', 64),
                CapabilityRuntimeState.Ready,
                [
                    new CapabilityCatalogItem(
                        CapabilityKind.Tool,
                        "core/file.read",
                        "core.file_read",
                        "Read a file.",
                        Source,
                        CapabilityStatus.Ready,
                        [],
                        1,
                        []),
                ],
                NextCursor: null));

        public ValueTask<CapabilityCatalogEntry> ReadAsync(
            CapabilityIdentity identity,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<CapabilityCatalogChange> RefreshAsync(
            long expectedRevision,
            CancellationToken cancellationToken = default) =>
            throw new CapabilityServiceException(
                CapabilityErrorCodes.RevisionConflict,
                "Revision conflict.",
                currentRevision: 7);

        public ValueTask<CapabilityCatalogChange> SetEnabledAsync(
            CapabilitySetEnabledRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<CapabilityDomainResult> ExecuteDomainAsync(
            CapabilityDomainRequest request,
            CancellationToken cancellationToken = default)
        {
            LastOperation = request.Operation;
            return ValueTask.FromResult(new CapabilityDomainResult(
                request.Arguments.Clone()));
        }

        public ValueTask<CapabilityDynamicToolRegistration>
            RegisterDynamicToolAsync(
                Guid connectionId,
                CapabilityDynamicToolRegistrationRequest request,
                ToolExecutor executor,
                CancellationToken cancellationToken = default)
        {
            Executor = executor;
            return ValueTask.FromResult(new CapabilityDynamicToolRegistration(
                connectionId,
                request.ThreadId,
                request.RegistrationId,
                request.DefinitionSha256,
                CapabilityStatus.PendingTrust,
                "dynamic.test",
                DateTimeOffset.UtcNow.AddMinutes(1)));
        }

        public ValueTask<CapabilityDynamicToolRegistration>
            RenewDynamicToolAsync(
                Guid connectionId,
                Guid threadId,
                Guid registrationId,
                TimeSpan leaseDuration,
                CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask UnregisterDynamicToolAsync(
            Guid connectionId,
            Guid threadId,
            Guid registrationId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void DisconnectDynamicTools(Guid connectionId) =>
            Disconnected = true;

        public void Notify(long revision) =>
            CatalogChanged?.Invoke(
                this,
                new CapabilityCatalogChangedEventArgs(
                    revision,
                    CapabilityRuntimeState.Ready));
    }

    private class ThrowingSessionProxy : DispatchProxy
    {
        protected override object? Invoke(
            MethodInfo? targetMethod,
            object?[]? args) =>
            throw new NotSupportedException(targetMethod?.Name);
    }
}
