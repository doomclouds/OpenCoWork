using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using OpenCoWork.Abstractions;
using OpenCoWork.Protocol;

return await ProtocolTestClient.RunAsync(args);

internal static class ProtocolTestClient
{
    private const string SecretEnvironment = "DEEPSEEK_API_KEY";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public static async Task<int> RunAsync(string[] args)
    {
        var server = ParseServer(args);
        if (server is null)
        {
            await Console.Error.WriteLineAsync(
                "Usage: OpenCoWork.Protocol.TestClient --server <opencowork>");
            return 2;
        }

        var workspace = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-m6-{Guid.NewGuid():N}");
        var transcript = new ConcurrentQueue<string>();
        var secret = $"m6-secret-{Guid.NewGuid():N}";
        var stage = "workspace initialization";
        Directory.CreateDirectory(workspace);
        try
        {
            await InitializeWorkspaceAsync(server, workspace, transcript);
            await WriteCapabilitySkillAsync(workspace);
            await WriteCapabilityAuthAsync(workspace);
            await WriteAutomationDefinitionAsync(workspace);
            await InitializeGitAsync(workspace);
            await WriteConfigAsync(workspace);

            stage = "Wire stdio";
            var wire = await RunWireAsync(
                server,
                workspace,
                secret,
                transcript);
            stage = "Wire 1.1 capabilities";
            var capabilities = await RunCapabilityWireAsync(
                server,
                workspace,
                secret,
                transcript);
            stage = "Wire 1.2 CoWork";
            var cowork = await RunCoWorkWireAsync(
                server,
                workspace,
                secret,
                transcript);
            stage = "Wire 1.3 Automations";
            var automations = await RunAutomationWireAsync(
                server,
                workspace,
                secret,
                transcript);
            stage = "Wire 1.4 Operations";
            var operations = await RunOperationsWireAsync(
                server,
                workspace,
                secret,
                transcript);
            stage = "ACP";
            var acp = await RunAcpAsync(
                server,
                workspace,
                secret,
                transcript);
            stage = "WebSocket";
            var websocket = await RunWebSocketAsync(
                server,
                workspace,
                secret,
                transcript);
            stage = "secret scan";
            EnsureSecretAbsent(secret, transcript, workspace);

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                passed = true,
                platform = Environment.OSVersion.Platform.ToString(),
                architecture = System.Runtime.InteropServices
                    .RuntimeInformation.ProcessArchitecture.ToString(),
                scenarios = new[]
                {
                    wire,
                    capabilities,
                    cowork,
                    automations,
                    operations,
                    acp,
                    websocket,
                    "secret-canary",
                },
            }));
            return 0;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync($"{stage}: {exception}");
            foreach (var line in transcript.TakeLast(20))
            {
                await Console.Error.WriteLineAsync(
                    line.Replace(secret, "[REDACTED]", StringComparison.Ordinal));
            }
            return 1;
        }
        finally
        {
            TryDelete(workspace);
        }
    }

    private static async Task<string> RunOperationsWireAsync(
        string server,
        string workspace,
        string secret,
        ConcurrentQueue<string> transcript)
    {
        await using var client = StartLineClient(
            server,
            workspace,
            secret,
            transcript,
            "app-server");
        var initialized = await InitializeWire14Async(client, workspace);
        Require(
            initialized.GetProperty("result").GetProperty("wireVersion")
                .GetString() == "1.4",
            "Wire 1.4 negotiation failed.");

        var channels = await client.RequestAsync(2, "channel/list", new { pageSize = 10 });
        Require(
            channels.GetProperty("result").GetProperty("operationsRevision")
                .GetInt64() >= 0,
            "Channel operations revision was not projected.");
        var workspaces = await client.RequestAsync(
            3,
            "hub/workspace/list",
            new { pageSize = 10 });
        Require(
            workspaces.GetProperty("result").GetProperty("items")
                .EnumerateArray().Any(),
            "Hub did not project the active workspace.");
        _ = await client.RequestAsync(
            4,
            "usage/query",
            new
            {
                fromUtc = DateTimeOffset.UtcNow.AddHours(-1),
                toUtc = DateTimeOffset.UtcNow,
                bucket = "hour",
            });
        _ = await client.RequestAsync(5, "trace/list", new { pageSize = 10 });
        _ = await client.RequestAsync(6, "heartbeat/get", new { });

        var commandId = Guid.CreateVersion7();
        var run = await client.RequestAsync(7, "insight/run", new { commandId });
        var replay = await client.RequestAsync(8, "insight/run", new { commandId });
        Require(
            run.GetProperty("result").GetProperty("insightRunId").GetGuid() == commandId &&
            replay.GetProperty("result").GetProperty("insightRunId").GetGuid() == commandId,
            "Insight command replay was not stable.");
        _ = await client.RequestAsync(
            9,
            "insight/list",
            new { kind = "runs", pageSize = 10 });
        return "wire-14-operations-hub-idempotency";
    }

    private static async Task<string> RunAutomationWireAsync(
        string server,
        string workspace,
        string secret,
        ConcurrentQueue<string> transcript)
    {
        await using var client = StartLineClient(
            server,
            workspace,
            secret,
            transcript,
            "app-server");
        var initialized = await InitializeWire13Async(client, workspace);
        Require(
            initialized.GetProperty("result").GetProperty("wireVersion")
                .GetString() == "1.3",
            "Wire 1.3 negotiation failed.");

        var definitions = await client.RequestAsync(
            2,
            "automation/list",
            new { pageSize = 1 });
        var definitionPage = definitions.GetProperty("result");
        Require(
            definitionPage.GetProperty("automationRevision").GetInt64() > 0 &&
            definitionPage.GetProperty("value").GetProperty("items")[0]
                .GetProperty("automationId").GetString() == "wire-smoke",
            "Automation Definition was not projected.");
        _ = await client.RequestAsync(
            3,
            "automation/get",
            new { automationId = "wire-smoke" });
        _ = await client.RequestAsync(
            4,
            "schedule/get",
            new { automationId = "wire-smoke" });
        var runs = await client.RequestAsync(
            5,
            "automationRun/list",
            new { automationId = "wire-smoke", pageSize = 1 });
        Require(
            !runs.GetProperty("result").GetProperty("value")
                .GetProperty("items").EnumerateArray().Any(),
            "Fresh Automation Definition unexpectedly has runs.");
        var definitionPath = Path.Combine(
            workspace,
            ".opencowork",
            "automations",
            "definitions",
            "wire-smoke.yaml");
        var definition = await File.ReadAllTextAsync(definitionPath);
        await File.WriteAllTextAsync(
            definitionPath,
            definition
                .Replace("displayName: Wire Smoke", "displayName: Wire Smoke Updated")
                .Replace("cron: \"0 2 * * *\"", "cron: \"5 2 * * *\""));
        var changed = new HashSet<string>(StringComparer.Ordinal);
        while (changed.Count != 2)
        {
            var message = await client.ReadMessageAsync();
            if (message.TryGetProperty("method", out var method) &&
                method.GetString() is "automation/changed" or "schedule/changed")
            {
                var parameters = message.GetProperty("params");
                Require(
                    parameters.GetProperty("entityId").GetString() == "wire-smoke" &&
                    parameters.GetProperty("automationRevision").GetInt64() >
                    definitionPage.GetProperty("automationRevision").GetInt64(),
                    "Automation Changed notification leaked or lost its revision.");
                changed.Add(method.GetString()!);
            }
        }

        return "wire-13-automation-catalog-schedule-runs-notifications";
    }

    private static async Task<string> RunCoWorkWireAsync(
        string server,
        string workspace,
        string secret,
        ConcurrentQueue<string> transcript)
    {
        await using var client = StartLineClient(
            server,
            workspace,
            secret,
            transcript,
            "app-server");
        var initialized = await InitializeWire12Async(client, workspace);
        Require(
            initialized.GetProperty("result").GetProperty("wireVersion")
                .GetString() == "1.2",
            "Wire 1.2 negotiation failed.");

        var listed = await client.RequestAsync(
            2,
            "agent/profile/list",
            new { pageSize = 100 });
        Require(
            listed.GetProperty("result").GetProperty("coWorkRevision")
                .GetInt64() >= 0,
            "CoWork revision was not projected.");

        var commandId = Guid.CreateVersion7();
        var request = new
        {
            commandId,
            expectedRevision = (long?)null,
            profileId = (Guid?)null,
            name = "M7 Wire Profile",
            description = "Wire 1.2 black-box profile.",
            instructions = "Return concise results.",
            providerId = "deepseek",
            modelId = "deepseek-v4-flash",
            skillAllowlist = Array.Empty<string>(),
            toolAllowlist = Array.Empty<string>(),
        };
        var created = await client.RequestAsync(
            3,
            "agent/profile/upsert",
            request);
        var replayed = await client.RequestAsync(
            4,
            "agent/profile/upsert",
            request);
        Require(
            created.GetProperty("result").GetProperty("coWorkRevision")
                .GetInt64() ==
            replayed.GetProperty("result").GetProperty("coWorkRevision")
                .GetInt64(),
            "CoWork command replay changed the revision.");
        Require(
            client.Messages.Count(message =>
                message.TryGetProperty("method", out var method) &&
                method.GetString() == "agent/changed") == 1,
            "CoWork command replay duplicated its notification.");
        return "wire-12-cowork-idempotency-notification";
    }

    private static async Task<string> RunCapabilityWireAsync(
        string server,
        string workspace,
        string secret,
        ConcurrentQueue<string> transcript)
    {
        var childPidPath = Path.Combine(workspace, "m6-terminal-child.pid");
        int childPid;
        await using (var client = StartLineClient(
                         server,
                         workspace,
                         secret,
                         transcript,
                         "app-server"))
        {
            var initialized = await InitializeWire11Async(client, workspace);
            Require(
                initialized.GetProperty("result").GetProperty("wireVersion")
                    .GetString() == "1.1",
                "Wire 1.1 negotiation failed.");

            var firstPage = await client.RequestAsync(
                2,
                "capability/catalog",
                new { limit = 1 });
            var catalog = firstPage.GetProperty("result");
            var revision = catalog.GetProperty("revision").GetInt64();
            var first = catalog.GetProperty("items")[0];
            Require(
                catalog.GetProperty("nextCursor").ValueKind == JsonValueKind.String,
                "Capability Catalog pagination cursor is missing.");
            var secondPage = await client.RequestAsync(
                3,
                "capability/catalog",
                new
                {
                    limit = 100,
                    cursor = catalog.GetProperty("nextCursor").GetString(),
                });
            _ = await client.RequestAsync(
                4,
                "capability/read",
                new
                {
                    kind = first.GetProperty("kind").GetString(),
                    id = first.GetProperty("id").GetString(),
                });
            var conflict = await client.RequestRawAsync(
                5,
                "capability/refresh",
                new { expectedRevision = revision + 1 });
            Require(
                conflict.GetProperty("error").GetProperty("data")
                    .GetProperty("errorCode").GetString() ==
                "capability.revisionConflict",
                "Capability revision conflict was not projected.");
            _ = await client.RequestAsync(
                6,
                "capability/refresh",
                new { expectedRevision = revision });
            Require(
                new[] { first }
                    .Concat(secondPage.GetProperty("result").GetProperty("items")
                        .EnumerateArray())
                    .Any(item =>
                        item.GetProperty("kind").GetString() == "skill" &&
                        item.GetProperty("id").GetString() == "m6/wire"),
                "Wire 1.1 catalog did not include the workspace Skill.");
            var disabled = await client.RequestAsync(
                60,
                "capability/setEnabled",
                new
                {
                    kind = "skill",
                    id = "m6/wire",
                    enabled = false,
                    expectedRevision = revision,
                });
            revision = disabled.GetProperty("result").GetProperty("revision").GetInt64();
            Require(
                disabled.GetProperty("result").GetProperty("changed").GetBoolean(),
                "Capability override did not change the catalog.");
            if (!client.Messages.Any(message =>
                    message.TryGetProperty("method", out var changedMethod) &&
                    changedMethod.GetString() == "capability/changed"))
            {
                var changed = await client.ReadMessageAsync();
                Require(
                    changed.GetProperty("method").GetString() == "capability/changed",
                    "Capability change notification was not emitted.");
            }

            var staleCursor = await client.RequestRawAsync(
                61,
                "capability/catalog",
                new
                {
                    limit = 100,
                    cursor = catalog.GetProperty("nextCursor").GetString(),
                });
            Require(
                staleCursor.GetProperty("error").GetProperty("data")
                    .GetProperty("errorCode").GetString() ==
                "capability.revisionConflict",
                "Stale Capability cursor was accepted.");

            var secretStored = false;
            try
            {
                var stored = await client.RequestSensitiveAsync(
                    62,
                    "auth/secret/set",
                    new
                    {
                        arguments = new
                        {
                            profileId = "auth/m6-os",
                            secret,
                        },
                    },
                    secret);
                secretStored = stored.GetProperty("result").GetProperty("result")
                    .GetProperty("stored").GetBoolean();
                Require(secretStored, "OS Secret Store write failed.");
            }
            finally
            {
                if (secretStored)
                {
                    var cleared = await client.RequestAsync(
                        63,
                        "auth/secret/clear",
                        new
                        {
                            arguments = new { profileId = "auth/m6-os" },
                        });
                    Require(
                        !cleared.GetProperty("result").GetProperty("result")
                            .GetProperty("stored").GetBoolean(),
                        "OS Secret Store cleanup failed.");
                }
            }

            var created = await client.RequestAsync(
                7,
                "thread/create",
                new
                {
                    idempotencyKey = Guid.CreateVersion7(),
                    expectedSequence = 0,
                    displayName = "M6 capability black-box",
                    providerId = "deepseek",
                    modelId = "deepseek-v4-flash",
                    mode = "agent",
                });
            var threadId = created.GetProperty("result").GetProperty("thread")
                .GetProperty("threadId").GetGuid();
            var sequence = created.GetProperty("result")
                .GetProperty("currentSequence").GetInt64();
            var memoryId = Guid.CreateVersion7();
            var written = await client.RequestAsync(
                8,
                "memory/write",
                new
                {
                    arguments = new
                    {
                        memoryId,
                        expectedVersion = 0,
                        title = "M6 memory",
                        summary = "Wire black-box memory.",
                        tags = new[] { "m6", "wire" },
                        body = "M6 memory body.",
                    },
                });
            Require(
                written.GetProperty("result").GetProperty("result")
                    .GetProperty("version").GetInt32() == 1,
                "Workspace Memory write failed.");
            var memory = await client.RequestAsync(
                9,
                "memory/read",
                new { arguments = new { memoryId } });
            Require(
                memory.GetProperty("result").GetProperty("result")
                    .GetProperty("body").GetString() == "M6 memory body.",
                "Workspace Memory read failed.");
            _ = await client.RequestAsync(
                10,
                "memory/archive",
                new { arguments = new { memoryId, expectedVersion = 1 } });

            var sourceIdentity = await client.RequestAsync(
                11,
                "sourceControl/inspect",
                new { arguments = new { } });
            var source = sourceIdentity.GetProperty("result").GetProperty("result");
            var sourceTrust = new
            {
                expectedRevision = revision,
                sourceKind = "workspace",
                sourceId = source.GetProperty("sourceId").GetString(),
                sourceVersion = source.GetProperty("version").GetString(),
                sha256 = source.GetProperty("sha256").GetString(),
                allowedScopes = new[] { "outOfProcess" },
                deniedScopes = Array.Empty<string>(),
            };
            _ = await client.RequestAsync(
                12,
                "trust/decide",
                new { arguments = sourceTrust });
            try
            {
                var git = await client.RequestAsync(
                    13,
                    "sourceControl/status",
                    new { arguments = new { } });
                Require(
                    git.GetProperty("result").GetProperty("result")
                        .GetProperty("operation").GetString() == "status",
                    "Source Control status failed.");
            }
            finally
            {
                _ = await client.RequestAsync(
                    64,
                    "trust/revoke",
                    new { arguments = sourceTrust });
            }

            var registrationId = Guid.CreateVersion7();
            var inputSchema = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { text = new { type = "string" } },
                required = new[] { "text" },
                additionalProperties = false,
            });
            const string dynamicName = "echo";
            const string dynamicDescription = "Echo one text value.";
            _ = await client.RequestAsync(
                14,
                "tool/dynamic/register",
                new
                {
                    threadId,
                    registrationId,
                    definition = new
                    {
                        name = dynamicName,
                        description = dynamicDescription,
                        inputSchema,
                        effects = Array.Empty<string>(),
                        replaySafety = "safe",
                    },
                    definitionSha256 = DynamicDefinitionSha256(
                        dynamicName,
                        dynamicDescription,
                        inputSchema),
                    leaseSeconds = 60,
                });
            var terminalId = Guid.CreateVersion7();
            _ = await client.RequestAsync(
                120,
                "terminal/start",
                new
                {
                    threadId,
                    arguments = TerminalStartArguments(
                        terminalId,
                        childPidPath),
                });
            await WaitForFileAsync(childPidPath);
            childPid = int.Parse(
                await File.ReadAllTextAsync(childPidPath),
                System.Globalization.CultureInfo.InvariantCulture);
        }

        await WaitForProcessExitAsync(childPid);
        return "wire-11-catalog-dynamic-memory-git-terminal-cleanup";
    }

    private static async Task<string> RunWireAsync(
        string server,
        string workspace,
        string secret,
        ConcurrentQueue<string> transcript)
    {
        Guid threadId;
        long watermark;
        await using (var client = StartLineClient(
                         server,
                         workspace,
                         secret,
                         transcript,
                         "app-server"))
        {
            await InitializeWireAsync(client, workspace);
            var created = await client.RequestAsync(
                2,
                "thread/create",
                new
                {
                    idempotencyKey = Guid.CreateVersion7(),
                    expectedSequence = 0,
                    displayName = "M5 black-box",
                    providerId = "deepseek",
                    modelId = "deepseek-v4-flash",
                    mode = "agent",
                });
            threadId = created.GetProperty("result").GetProperty("thread")
                .GetProperty("threadId").GetGuid();
            watermark = created.GetProperty("result")
                .GetProperty("currentSequence").GetInt64();
            await client.RequestAsync(
                3,
                "thread/subscribe",
                new { threadId, mode = "snapshotThenLive" });
        }

        await using (var reconnect = StartLineClient(
                         server,
                         workspace,
                         secret,
                         transcript,
                         "app-server"))
        {
            await InitializeWireAsync(reconnect, workspace);
            var subscribed = await reconnect.RequestAsync(
                2,
                "thread/subscribe",
                new
                {
                    threadId,
                    mode = "resumeAfterSequence",
                    afterSequence = 0,
                });
            var currentSequence = subscribed.GetProperty("result")
                .GetProperty("currentSequence").GetInt64();
            Require(
                currentSequence == watermark,
                "Wire reconnect watermark changed.");
            await reconnect.ReadThroughSequenceAsync(currentSequence);
            var sequences = reconnect.NotificationSequences.ToArray();
            Require(
                sequences.Length > 0 &&
                sequences.SequenceEqual(sequences.Distinct()) &&
                sequences.SequenceEqual(sequences.Order()),
                "Wire reconnect replay was empty, duplicated, or out of order.");
        }

        return "wire-stdio-reconnect";
    }

    private static async Task<string> RunAcpAsync(
        string server,
        string workspace,
        string secret,
        ConcurrentQueue<string> transcript)
    {
        string sessionId;
        await using (var client = StartLineClient(
                         server,
                         workspace,
                         secret,
                         transcript,
                         "acp"))
        {
            var initialized = await client.RequestAsync(
                1,
                "initialize",
                new { protocolVersion = 1 });
            Require(
                initialized.GetProperty("result")
                    .GetProperty("protocolVersion").GetInt32() == 1,
                "ACP v1 negotiation failed.");
            var created = await client.RequestAsync(
                2,
                "session/new",
                new { cwd = workspace, mcpServers = Array.Empty<object>() });
            sessionId = created.GetProperty("result")
                .GetProperty("sessionId").GetString()
                ?? throw new InvalidOperationException(
                    "ACP session ID is missing.");
        }

        await using (var reconnect = StartLineClient(
                         server,
                         workspace,
                         secret,
                         transcript,
                         "acp"))
        {
            await reconnect.RequestAsync(
                1,
                "initialize",
                new { protocolVersion = 1 });
            await reconnect.RequestAsync(
                2,
                "session/load",
                new
                {
                    sessionId,
                    cwd = workspace,
                    mcpServers = Array.Empty<object>(),
                });
        }

        return "acp-v1-session-load";
    }

    private static async Task<string> RunWebSocketAsync(
        string server,
        string workspace,
        string secret,
        ConcurrentQueue<string> transcript)
    {
        var port = ReservePort();
        var token = $"m5-token-{Guid.NewGuid():N}";
        await using var process = ChildProcess.Start(
            server,
            ["app-server", "--workspace", workspace, "--transport", "websocket",
                "--port", port.ToString()],
            workspace,
            new Dictionary<string, string>
            {
                [SecretEnvironment] = secret,
                [OpenCoWorkProtocolServer.WebSocketTokenEnvironment] = token,
            },
            transcript);
        await WaitForWebSocketServerAsync(port, token);

        using var http = new HttpClient();
        var endpoint =
            $"http://127.0.0.1:{port}{OpenCoWorkProtocolServer.WebSocketPath}";
        using var unauthorized = await http.GetAsync(endpoint);
        Require(
            unauthorized.StatusCode == HttpStatusCode.Unauthorized,
            "WebSocket endpoint accepted a request without bearer auth.");
        using var queryOnly = await http.GetAsync($"{endpoint}?token={token}");
        Require(
            queryOnly.StatusCode == HttpStatusCode.Unauthorized,
            "WebSocket endpoint accepted a query-string token.");

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {token}");
        await socket.ConnectAsync(
            new Uri(endpoint.Replace("http://", "ws://", StringComparison.Ordinal)),
            CancellationToken.None);
        await SendWebSocketAsync(
            socket,
            Rpc(1, "initialize", new
            {
                client = new { name = "m5-test-client", version = "1" },
                wireVersions = new[] { "1.0" },
                workspace = new { path = workspace },
            }));
        var initialized = await ReceiveWebSocketAsync(socket);
        transcript.Enqueue(initialized);
        using var initializedDocument = JsonDocument.Parse(initialized);
        Require(
            initializedDocument.RootElement
                .GetProperty("result").GetProperty("wireVersion").GetString() ==
            "1.0",
            "WebSocket Wire negotiation failed.");

        const int delayedReads = 24;
        for (var id = 2; id < delayedReads + 2; id++)
        {
            await SendWebSocketAsync(socket, Rpc(id, "thread/list", new { }));
        }

        await Task.Delay(250);
        var responseIds = new HashSet<int>();
        while (responseIds.Count < delayedReads)
        {
            var message = await ReceiveWebSocketAsync(socket);
            transcript.Enqueue(message);
            using var document = JsonDocument.Parse(message);
            if (document.RootElement.TryGetProperty("id", out var id))
            {
                responseIds.Add(id.GetInt32());
            }
        }

        Require(
            responseIds.Count == delayedReads,
            "WebSocket slow reader lost responses.");
        await socket.CloseAsync(
            WebSocketCloseStatus.NormalClosure,
            "done",
            CancellationToken.None);
        await process.TerminateAsync();
        return "wire-websocket-auth-slow-reader";
    }

    private static LineClient StartLineClient(
        string server,
        string workspace,
        string secret,
        ConcurrentQueue<string> transcript,
        string command) =>
        new(
            ChildProcess.Start(
                server,
                [command, "--workspace", workspace],
                workspace,
                new Dictionary<string, string>
                {
                    [SecretEnvironment] = secret,
                },
                transcript),
            transcript);

    private static Task<JsonElement> InitializeWireAsync(
        LineClient client,
        string workspace) =>
        client.RequestAsync(
            1,
            "initialize",
            new
            {
                client = new { name = "m5-test-client", version = "1" },
                wireVersions = new[] { "1.0" },
                workspace = new { path = workspace },
            });

    private static Task<JsonElement> InitializeWire11Async(
        LineClient client,
        string workspace) =>
        client.RequestAsync(
            1,
            "initialize",
            new
            {
                client = new { name = "m6-test-client", version = "1" },
                wireVersions = new[] { "1.1", "1.0" },
                workspace = new { path = workspace },
                capabilities = new[]
                {
                    "serverRequests",
                    "dynamicToolExecution",
                },
            });

    private static Task<JsonElement> InitializeWire12Async(
        LineClient client,
        string workspace) =>
        client.RequestAsync(
            1,
            "initialize",
            new
            {
                client = new { name = "m7-test-client", version = "1" },
                wireVersions = new[] { "1.2", "1.1", "1.0" },
                workspace = new { path = workspace },
            });

    private static Task<JsonElement> InitializeWire13Async(
        LineClient client,
        string workspace) =>
        client.RequestAsync(
            1,
            "initialize",
            new
            {
                client = new { name = "m8-test-client", version = "1" },
                wireVersions = new[] { "1.3", "1.2", "1.1", "1.0" },
                workspace = new { path = workspace },
            });

    private static Task<JsonElement> InitializeWire14Async(
        LineClient client,
        string workspace) =>
        client.RequestAsync(
            1,
            "initialize",
            new
            {
                client = new { name = "m10-test-client", version = "1" },
                wireVersions = new[] { "1.4", "1.3", "1.2", "1.1", "1.0" },
                workspace = new { path = workspace },
            });

    private static async Task InitializeWorkspaceAsync(
        string server,
        string workspace,
        ConcurrentQueue<string> transcript)
    {
        await using var process = ChildProcess.Start(
            server,
            ["init", "--workspace", workspace],
            workspace,
            environment: null,
            transcript);
        await process.CompleteAsync();
        Require(process.ExitCode == 0, "Workspace initialization failed.");
    }

    private static async Task InitializeGitAsync(string workspace)
    {
        using var process = Process.Start(new ProcessStartInfo("git")
        {
            WorkingDirectory = workspace,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "init", "--quiet" },
        }) ?? throw new InvalidOperationException("Could not start git.");
        await process.WaitForExitAsync();
        Require(process.ExitCode == 0, "Git workspace initialization failed.");
    }

    private static async Task WriteCapabilitySkillAsync(string workspace)
    {
        var directory = Path.Combine(
            workspace,
            ".opencowork",
            "skills",
            "m6-wire");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "SKILL.md"),
            """
            ---
            id: m6/wire
            name: M6 Wire
            description: Exercise Wire capability overrides.
            ---
            Use the M6 Wire capability path.
            """);
    }

    private static object TerminalStartArguments(Guid sessionId, string pidPath)
    {
        if (OperatingSystem.IsWindows())
        {
            var escaped = pidPath.Replace("'", "''", StringComparison.Ordinal);
            return new
            {
                sessionId,
                command = "powershell.exe",
                arguments = new[]
                {
                    "-NoProfile",
                    "-NonInteractive",
                    "-Command",
                    "$p=Start-Process ping.exe -ArgumentList '-n','30'," +
                    "'127.0.0.1' -WindowStyle Hidden -PassThru; " +
                    $"Set-Content -LiteralPath '{escaped}' " +
                    "-Value $p.Id; $p.WaitForExit()",
                },
                maxDurationSeconds = 60,
            };
        }

        var shellPath = pidPath.Replace("'", "'\\''", StringComparison.Ordinal);
        return new
        {
            sessionId,
            command = "/bin/sh",
            arguments = new[]
            {
                "-c",
                $"sleep 30 & echo $! > '{shellPath}'; wait",
            },
            maxDurationSeconds = 60,
        };
    }

    private static async Task WaitForFileAsync(string path)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (!File.Exists(path))
        {
            await Task.Delay(25, timeout.Token);
        }
    }

    private static async Task WaitForProcessExitAsync(int processId)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        while (true)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }

            await Task.Delay(25, timeout.Token);
        }
    }

    private static string DynamicDefinitionSha256(
        string name,
        string description,
        JsonElement inputSchema)
    {
        var definition = JsonSerializer.SerializeToElement(new
        {
            Name = name,
            Description = description,
            InputSchema = inputSchema,
            Effects = ToolEffect.None,
            ReplaySafety = ToolReplaySafety.Safe,
        });
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        WriteCanonical(writer, definition);
        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan))
            .ToLowerInvariant();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject()
                             .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number when value.TryGetInt64(out var signed):
                writer.WriteNumberValue(signed);
                break;
            case JsonValueKind.Number when value.TryGetUInt64(out var unsigned):
                writer.WriteNumberValue(unsigned);
                break;
            case JsonValueKind.Number when value.TryGetDecimal(out var decimalValue):
                writer.WriteNumberValue(decimalValue);
                break;
            case JsonValueKind.Number:
                writer.WriteNumberValue(value.GetDouble());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new JsonException("Unsupported JSON value.");
        }
    }

    private static Task WriteConfigAsync(string workspace)
    {
        var path = Path.Combine(workspace, ".opencowork", "config.jsonc");
        var content =
            $$"""
            {
              "models": {
                "defaultModel": "deepseek-v4-flash",
                "reasoningEffort": "low"
              },
              "automations": {
                "enabled": true
              }
            }
            """;
        return File.WriteAllTextAsync(path, content);
    }

    private static async Task WriteAutomationDefinitionAsync(string workspace)
    {
        var directory = Path.Combine(
            workspace,
            ".opencowork",
            "automations",
            "definitions");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "wire-smoke.yaml"),
            """
            schemaVersion: 1
            id: wire-smoke
            displayName: Wire Smoke
            enabled: true
            schedule:
              cron: "0 2 * * *"
              timeZone: UTC
            workspace:
              mode: project
            prompt: Inspect the workspace.
            inputSchema:
              type: object
              additionalProperties: false
            defaults: {}
            allow:
              effects: []
            runTimeout: 30m
            attentionTimeout: 24h
            """);
    }

    private static Task WriteCapabilityAuthAsync(string workspace) =>
        File.WriteAllTextAsync(
            Path.Combine(workspace, ".opencowork", "auth.json"),
            """
            {
              "schemaVersion": 1,
              "profiles": [{
                "id": "auth/m6-os",
                "kind": "apiKey",
                "source": { "kind": "osSecretStore" },
                "placement": { "kind": "bearer" }
              }]
            }
            """);

    private static async Task WaitForWebSocketServerAsync(int port, string token)
    {
        using var http = new HttpClient();
        using var timeout = new CancellationTokenSource(Timeout);
        var endpoint =
            $"http://127.0.0.1:{port}{OpenCoWorkProtocolServer.WebSocketPath}";
        while (true)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue(
                        "Bearer",
                        token);
                using var response = await http.SendAsync(request, timeout.Token);
                if (response.StatusCode == HttpStatusCode.BadRequest)
                {
                    return;
                }
            }
            catch (HttpRequestException) when (!timeout.IsCancellationRequested)
            {
            }

            await Task.Delay(50, timeout.Token);
        }
    }

    private static async Task SendWebSocketAsync(
        ClientWebSocket socket,
        string message)
    {
        await socket.SendAsync(
            Encoding.UTF8.GetBytes(message),
            WebSocketMessageType.Text,
            endOfMessage: true,
            CancellationToken.None);
    }

    private static async Task<string> ReceiveWebSocketAsync(
        ClientWebSocket socket)
    {
        using var timeout = new CancellationTokenSource(Timeout);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            Require(
                result.MessageType == WebSocketMessageType.Text,
                "WebSocket closed before returning a text response.");
            output.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(output.ToArray());
            }
        }
    }

    private static string Rpc(long id, string method, object parameters) =>
        JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params = parameters,
        });

    private static string? ParseServer(string[] args)
    {
        if (args.Length != 2 ||
            !string.Equals(args[0], "--server", StringComparison.Ordinal))
        {
            return null;
        }

        var path = Path.GetFullPath(args[1]);
        return File.Exists(path) ? path : null;
    }

    private static void EnsureSecretAbsent(
        string secret,
        ConcurrentQueue<string> transcript,
        string workspace)
    {
        var leaked = transcript.Any(line =>
            line.Contains(secret, StringComparison.Ordinal));
        foreach (var path in Directory.EnumerateFiles(
                     workspace,
                     "*",
                     SearchOption.AllDirectories))
        {
            if (File.ReadAllText(path).Contains(secret, StringComparison.Ordinal))
            {
                leaked = true;
                break;
            }
        }

        Require(!leaked, "Secret canary leaked into protocol, logs, or workspace.");
    }

    private static int ReservePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}

internal sealed class LineClient : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private readonly ChildProcess _process;
    private readonly ConcurrentQueue<string> _transcript;

    public LineClient(
        ChildProcess process,
        ConcurrentQueue<string> transcript)
    {
        _process = process;
        _transcript = transcript;
    }

    public List<JsonElement> Messages { get; } = [];

    public IEnumerable<long> NotificationSequences =>
        Messages
            .Where(message =>
                message.TryGetProperty("method", out _) &&
                message.TryGetProperty("params", out var parameters) &&
                parameters.TryGetProperty("sequence", out _))
            .Select(message =>
                message.GetProperty("params").GetProperty("sequence").GetInt64());

    public async Task<JsonElement> RequestAsync(
        long id,
        string method,
        object parameters)
    {
        await SendRequestAsync(id, method, parameters);
        return await ReadResponseAsync(id);
    }

    public async Task<JsonElement> RequestRawAsync(
        long id,
        string method,
        object parameters)
    {
        await SendRequestAsync(id, method, parameters);
        return await ReadResponseAsync(id, throwOnError: false);
    }

    public async Task<JsonElement> RequestSensitiveAsync(
        long id,
        string method,
        object parameters,
        string sensitiveValue)
    {
        await SendAsync(
            new
            {
                jsonrpc = "2.0",
                id,
                method,
                @params = parameters,
            },
            sensitiveValue);
        return await ReadResponseAsync(id);
    }

    public Task SendRequestAsync(long id, string method, object parameters) =>
        SendAsync(new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params = parameters,
        });

    public Task SendNotificationAsync(string method, object parameters) =>
        SendAsync(new
        {
            jsonrpc = "2.0",
            method,
            @params = parameters,
        });

    public Task SendResultAsync(string id, object result) =>
        SendAsync(new
        {
            jsonrpc = "2.0",
            id,
            result,
        });

    public Task<JsonElement> ReadMessageAsync() => ReadAsync();

    public async Task<JsonElement> ReadResponseAsync(long id)
    {
        return await ReadResponseAsync(id, throwOnError: true);
    }

    private async Task<JsonElement> ReadResponseAsync(
        long id,
        bool throwOnError)
    {
        while (true)
        {
            var message = await ReadAsync();
            if (!message.TryGetProperty("id", out var responseId) ||
                responseId.ValueKind != JsonValueKind.Number ||
                responseId.GetInt64() != id)
            {
                continue;
            }

            if (throwOnError && message.TryGetProperty("error", out var error))
            {
                throw new InvalidOperationException(
                    $"Protocol request {id} failed: {error.GetRawText()}");
            }

            return message;
        }
    }

    public async Task ReadThroughSequenceAsync(long sequence)
    {
        while (NotificationSequences.DefaultIfEmpty().Max() < sequence)
        {
            await ReadAsync();
        }
    }

    private async Task SendAsync(object message, string? sensitiveValue = null)
    {
        var line = JsonSerializer.Serialize(message);
        _transcript.Enqueue(sensitiveValue is null
            ? line
            : line.Replace(sensitiveValue, "[REDACTED]", StringComparison.Ordinal));
        await _process.Process.StandardInput.WriteLineAsync(line);
        await _process.Process.StandardInput.FlushAsync();
    }

    private async Task<JsonElement> ReadAsync()
    {
        using var timeout = new CancellationTokenSource(Timeout);
        var line = await _process.Process.StandardOutput
            .ReadLineAsync(timeout.Token);
        if (line is null)
        {
            throw new InvalidOperationException(
                $"Protocol process exited early: {_process.ErrorText}");
        }

        _transcript.Enqueue(line);
        using var document = JsonDocument.Parse(line);
        var message = document.RootElement.Clone();
        Messages.Add(message);
        return message;
    }

    public async ValueTask DisposeAsync()
    {
        _process.Process.StandardInput.Close();
        await _process.DisposeAsync();
    }
}

internal sealed class ChildProcess : IAsyncDisposable
{
    private readonly Task _errorReader;
    private readonly ConcurrentQueue<string> _transcript;
    private int _disposed;

    private ChildProcess(
        Process process,
        ConcurrentQueue<string> transcript)
    {
        Process = process;
        _transcript = transcript;
        _errorReader = ReadErrorsAsync();
    }

    public Process Process { get; }

    public int ExitCode => Process.ExitCode;

    public string ErrorText { get; private set; } = string.Empty;

    public static ChildProcess Start(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        ConcurrentQueue<string> transcript)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                start.Environment[pair.Key] = pair.Value;
            }
        }

        var process = System.Diagnostics.Process.Start(start)
            ?? throw new InvalidOperationException(
                $"Could not start {Path.GetFileName(executable)}.");
        return new ChildProcess(process, transcript);
    }

    public async Task CompleteAsync()
    {
        Process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await Process.WaitForExitAsync(timeout.Token);
        await _errorReader;
    }

    public async Task TerminateAsync()
    {
        if (!Process.HasExited)
        {
            Process.Kill(entireProcessTree: true);
            await Process.WaitForExitAsync();
        }

        await _errorReader;
    }

    private async Task ReadErrorsAsync()
    {
        var error = await Process.StandardError.ReadToEndAsync();
        ErrorText = error;
        if (!string.IsNullOrEmpty(error))
        {
            _transcript.Enqueue(error);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (!Process.HasExited)
        {
            try
            {
                Process.StandardInput.Close();
                using var timeout =
                    new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await Process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                Process.Kill(entireProcessTree: true);
                await Process.WaitForExitAsync();
            }
        }

        await _errorReader;
        Process.Dispose();
    }
}
