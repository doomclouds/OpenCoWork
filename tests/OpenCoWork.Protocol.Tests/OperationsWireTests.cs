using System.Reflection;
using System.Text.Json;
using OpenCoWork.Abstractions;
using OpenCoWork.Protocol;
using Xunit;

namespace OpenCoWork.Protocol.Tests;

public sealed class OperationsWireTests
{
    [Fact]
    public void Wire_14_declares_only_the_frozen_operations_catalog()
    {
        var clientMethods = typeof(OpenCoWorkJsonRpcConnection)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Select(method => method.GetCustomAttribute<OpenCoWorkWireMethodAttribute>())
            .Where(attribute => attribute?.Since == OpenCoWorkWire.OperationsVersion)
            .Cast<OpenCoWorkWireMethodAttribute>()
            .ToArray();
        var serverMethods = typeof(OpenCoWorkJsonRpcConnection).Assembly
            .GetType("OpenCoWork.Protocol.OperationsWireCatalog")!
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Select(method => method.GetCustomAttribute<OpenCoWorkWireMethodAttribute>())
            .Where(attribute => attribute is not null)
            .Cast<OpenCoWorkWireMethodAttribute>()
            .ToArray();

        Assert.Equal(
            [
                "channel/deadLetter/retry",
                "channel/get",
                "channel/inbound/list",
                "channel/list",
                "channel/media/read",
                "channel/outbox/list",
                "heartbeat/get",
                "hub/dashboard/get",
                "hub/workspace/get",
                "hub/workspace/list",
                "insight/archive",
                "insight/get",
                "insight/list",
                "insight/run",
                "trace/get",
                "trace/list",
                "usage/query",
            ],
            clientMethods.Select(attribute => attribute.Method)
                .Order(StringComparer.Ordinal));
        Assert.Equal(
            ["channel/changed", "heartbeat/changed", "insight/changed"],
            serverMethods.Select(attribute => attribute.Method)
                .Order(StringComparer.Ordinal));
        Assert.All(
            clientMethods.Where(attribute => attribute.Mutates),
            attribute => Assert.Equal(
                OpenCoWorkWire.RequiredIdempotency,
                attribute.Idempotency));
        Assert.All(
            clientMethods.Where(attribute => attribute.Method.StartsWith("hub/")),
            attribute => Assert.Equal(OpenCoWorkWire.UserAuthority, attribute.Authority));
    }

    [Fact]
    public async Task Wire_14_negotiates_only_with_operations_services_and_hides_from_older_clients()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var output = new List<JsonElement>();
        await using var connection = CreateConnection(output, withOperations: true);
        await InitializeAsync(connection, ["1.4", "1.3"], cancellationToken);
        Assert.Equal(
            "1.4",
            output[0].GetProperty("result").GetProperty("wireVersion").GetString());

        var oldOutput = new List<JsonElement>();
        await using var old = CreateConnection(oldOutput, withOperations: true);
        await InitializeAsync(old, ["1.3"], cancellationToken);
        await old.ProcessAsync(
            """{"jsonrpc":"2.0","id":2,"method":"heartbeat/get","params":{}}"""u8
                .ToArray(),
            cancellationToken);
        Assert.Equal(
            -32601,
            oldOutput[1].GetProperty("error").GetProperty("code").GetInt32());

        var legacyOutput = new List<JsonElement>();
        await using var legacy = CreateConnection(legacyOutput, withOperations: false);
        await InitializeAsync(legacy, ["1.4", "1.3"], cancellationToken);
        Assert.Equal(
            "1.3",
            legacyOutput[0].GetProperty("result").GetProperty("wireVersion").GetString());
    }

    [Fact]
    public async Task Wire_14_maps_channel_errors_safely_and_emits_filtered_notifications()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var output = new List<JsonElement>();
        var changes = new TestChangeSource();
        await using var connection = CreateConnection(
            output,
            withOperations: true,
            channels: new FailingChannelService(),
            changes: changes);
        await InitializeAsync(connection, ["1.4"], cancellationToken);
        await connection.ProcessAsync(
            """{"jsonrpc":"2.0","id":2,"method":"channel/get","params":{"channelId":"missing"}}"""u8
                .ToArray(),
            cancellationToken);
        Assert.Equal(-32002, output[1].GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(
            ChannelErrorCodes.NotFound,
            output[1].GetProperty("error").GetProperty("data")
                .GetProperty("errorCode").GetString());
        Assert.DoesNotContain("private-detail", output[1].GetRawText(), StringComparison.Ordinal);

        changes.Publish(new OperationsChangedEvent(
            OperationsChangeKind.Heartbeat,
            "updated"));
        await WaitForAsync(() => output.Count == 3, cancellationToken);
        Assert.Equal("heartbeat/changed", output[2].GetProperty("method").GetString());
    }

    private static OpenCoWorkJsonRpcConnection CreateConnection(
        List<JsonElement> output,
        bool withOperations,
        IChannelService? channels = null,
        IOperationsChangeSource? changes = null) =>
        new(
            DispatchProxy.Create<ISessionService, ThrowingProxy>(),
            capabilities: null,
            coWork: null,
            automations: DispatchProxy.Create<IAutomationService, ThrowingProxy>(),
            channels: withOperations
                ? channels ?? DispatchProxy.Create<IChannelService, ThrowingProxy>()
                : null,
            hub: withOperations ? DispatchProxy.Create<IHubService, ThrowingProxy>() : null,
            operations: withOperations
                ? DispatchProxy.Create<IOperationsQueryService, ThrowingProxy>()
                : null,
            insights: withOperations
                ? DispatchProxy.Create<IWorkspaceInsightService, ThrowingProxy>()
                : null,
            changes,
            "/workspace",
            "stdio",
            (message, _) =>
            {
                using var document = JsonDocument.Parse(message);
                output.Add(document.RootElement.Clone());
                return ValueTask.CompletedTask;
            });

    private static Task InitializeAsync(
        OpenCoWorkJsonRpcConnection connection,
        string[] versions,
        CancellationToken cancellationToken) =>
        connection.ProcessAsync(
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

    private static async Task WaitForAsync(
        Func<bool> condition,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(10, cancellationToken);
        }
        Assert.True(condition());
    }

    private sealed class TestChangeSource : IOperationsChangeSource
    {
        public event EventHandler<OperationsChangedEvent>? Changed;

        public void Publish(OperationsChangedEvent change) =>
            Changed?.Invoke(this, change);
    }

    private sealed class FailingChannelService : IChannelService
    {
        public Task<ChannelSnapshot?> GetChannelAsync(
            string channelId,
            CancellationToken cancellationToken = default) =>
            throw new ChannelServiceException(
                ChannelErrorCodes.NotFound,
                "private-detail");

        public Task<ChannelPage<ChannelSnapshot>> ListChannelsAsync(
            ChannelListQuery query,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ChannelPage<ChannelInboundSummary>> ListInboundAsync(
            ChannelInboundQuery query,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ChannelPage<ChannelOutboxSummary>> ListOutboxAsync(
            ChannelOutboxQuery query,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ChannelMediaChunk> ReadMediaAsync(
            ChannelMediaReadRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ChannelOutboxSummary> RetryDeadLetterAsync(
            ChannelDeadLetterRetryRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private class ThrowingProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.IsSpecialName == true
                ? null
                : throw new NotSupportedException(targetMethod?.Name);
    }
}
