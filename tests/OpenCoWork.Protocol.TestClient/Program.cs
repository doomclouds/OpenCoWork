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
    private const string SecretEnvironment = "OPENCOWORK_PROTOCOL_TEST_KEY";
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
            await InitializeGitAsync(workspace);
            await using var provider = new HoldingHttpEndpoint();
            await using var dynamicProvider = new DynamicToolHttpEndpoint(secret);
            await WriteConfigAsync(workspace, provider.Port, dynamicProvider.Port);

            stage = "Wire stdio";
            var wire = await RunWireAsync(
                server,
                workspace,
                secret,
                provider,
                transcript);
            stage = "Wire 1.1 capabilities";
            var capabilities = await RunCapabilityWireAsync(
                server,
                workspace,
                secret,
                dynamicProvider,
                transcript);
            stage = "Wire 1.2 CoWork";
            var cowork = await RunCoWorkWireAsync(
                server,
                workspace,
                secret,
                transcript);
            stage = "ACP";
            var acp = await RunAcpAsync(
                server,
                workspace,
                secret,
                provider,
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
            providerId = "m5-test",
            modelId = "qwen3.8-max-preview",
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
        DynamicToolHttpEndpoint dynamicProvider,
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
                    providerId = "m6-dynamic",
                    modelId = "qwen3.8-max-preview",
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
            _ = await client.RequestAsync(
                15,
                "trust/decide",
                new { arguments = new { dynamicConnection = true } });
            _ = await client.RequestAsync(
                16,
                "thread/subscribe",
                new { threadId, mode = "snapshotThenLive" });
            await client.SendRequestAsync(
                17,
                "turn/start",
                new
                {
                    threadId,
                    idempotencyKey = Guid.CreateVersion7(),
                    expectedSequence = sequence,
                    text = "Call the dynamic echo tool once.",
                });
            var turnAccepted = false;
            var callbackCompleted = false;
            while (!turnAccepted || !callbackCompleted)
            {
                var message = await client.ReadMessageAsync();
                if (message.TryGetProperty("id", out var messageId) &&
                    messageId.ValueKind == JsonValueKind.Number &&
                    messageId.GetInt64() == 17)
                {
                    Require(
                        message.TryGetProperty("result", out _),
                        "Dynamic Tool turn was not accepted.");
                    turnAccepted = true;
                }
                else if (message.TryGetProperty("method", out var method) &&
                         method.GetString() == "tool/invoke")
                {
                    var parameters = message.GetProperty("params");
                    Require(
                        parameters.GetProperty("threadId").GetGuid() == threadId &&
                        parameters.GetProperty("registrationId").GetGuid() ==
                        registrationId &&
                        parameters.GetProperty("arguments").GetProperty("text")
                            .GetString() == "ping",
                        "Dynamic Tool callback parameters changed.");
                    await client.SendResultAsync(
                        message.GetProperty("id").GetString()!,
                        new { text = "pong" });
                    callbackCompleted = true;
                }
            }

            await dynamicProvider.Completion.WaitAsync(Timeout);
            for (var attempt = 0; attempt < 100; attempt++)
            {
                var current = await client.RequestAsync(
                    18 + attempt,
                    "thread/get",
                    new { threadId });
                var thread = current.GetProperty("result").GetProperty("thread");
                if (!thread.TryGetProperty("activeTurnId", out var activeTurnId) ||
                    activeTurnId.ValueKind == JsonValueKind.Null)
                {
                    break;
                }

                await Task.Delay(25);
                Require(attempt < 99, "Dynamic Tool turn did not complete.");
            }

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
        HoldingHttpEndpoint provider,
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
                    providerId = "m5-test",
                    modelId = "qwen3.8-max-preview",
                    mode = "agent",
                });
            threadId = created.GetProperty("result").GetProperty("thread")
                .GetProperty("threadId").GetGuid();
            var sequence = created.GetProperty("result")
                .GetProperty("currentSequence").GetInt64();
            await client.RequestAsync(
                3,
                "thread/subscribe",
                new { threadId, mode = "snapshotThenLive" });
            var started = await client.RequestAsync(
                4,
                "turn/start",
                new
                {
                    threadId,
                    idempotencyKey = Guid.CreateVersion7(),
                    expectedSequence = sequence,
                    text = "Hold until cancellation.",
                });
            var turnId = started.GetProperty("result")
                .GetProperty("turnId").GetGuid();
            await provider.WaitForConnectionAsync(Timeout);
            var current = await client.RequestAsync(
                5,
                "thread/get",
                new { threadId });
            sequence = current.GetProperty("result")
                .GetProperty("currentSequence").GetInt64();
            var cancelled = await client.RequestAsync(
                6,
                "turn/cancel",
                new
                {
                    threadId,
                    turnId,
                    idempotencyKey = Guid.CreateVersion7(),
                    expectedSequence = sequence,
                });
            Require(
                cancelled.GetProperty("result").GetProperty("turn")
                    .GetProperty("status").GetString() == "cancelled",
                "Wire cancellation did not reach a terminal state.");
            watermark = cancelled.GetProperty("result")
                .GetProperty("currentSequence").GetInt64();
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

        return "wire-stdio-reconnect-cancel";
    }

    private static async Task<string> RunAcpAsync(
        string server,
        string workspace,
        string secret,
        HoldingHttpEndpoint provider,
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
            await client.SendRequestAsync(
                3,
                "session/prompt",
                new
                {
                    sessionId,
                    prompt = new[]
                    {
                        new { type = "text", text = "Hold until cancellation." },
                    },
                });
            await provider.WaitForConnectionAsync(Timeout);
            await client.SendNotificationAsync(
                "session/cancel",
                new { sessionId });
            var prompt = await client.ReadResponseAsync(3);
            Require(
                prompt.GetProperty("result").GetProperty("stopReason")
                    .GetString() == "cancelled",
                "ACP cancellation did not map to cancelled.");
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
            Require(
                reconnect.Messages.Any(message =>
                    message.TryGetProperty("method", out var method) &&
                    method.GetString() == "session/update"),
                "ACP reconnect did not replay session history.");
        }

        return "acp-v1-reconnect-cancel";
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

    private static Task WriteConfigAsync(
        string workspace,
        int providerPort,
        int dynamicProviderPort)
    {
        var path = Path.Combine(workspace, ".opencowork", "config.jsonc");
        var content =
            $$"""
            {
              "models": {
                "defaultProvider": "m5-test",
                "defaultModel": "qwen3.8-max-preview",
                "providers": {
                  "m5-test": {
                    "baseUrl": "http://127.0.0.1:{{providerPort}}/v1",
                    "apiKey": { "environment": "{{SecretEnvironment}}" },
                    "models": {
                      "qwen3.8-max-preview": {
                        "tokenizerProfileId": "qwen-o200k",
                        "tokenizerProfileVersion": "1",
                        "contextWindowTokens": 983616,
                        "maxOutputTokens": 131072
                      }
                    }
                  },
                  "m6-dynamic": {
                    "baseUrl": "http://127.0.0.1:{{dynamicProviderPort}}/v1",
                    "apiKey": { "environment": "{{SecretEnvironment}}" },
                    "models": {
                      "qwen3.8-max-preview": {
                        "tokenizerProfileId": "qwen-o200k",
                        "tokenizerProfileVersion": "1",
                        "contextWindowTokens": 983616,
                        "maxOutputTokens": 131072
                      }
                    }
                  }
                }
              }
            }
            """;
        return File.WriteAllTextAsync(path, content);
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

internal sealed class DynamicToolHttpEndpoint : IAsyncDisposable
{
    private readonly string _secret;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Task _run;

    public DynamicToolHttpEndpoint(string secret)
    {
        _secret = secret;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _run = RunAsync();
    }

    public int Port { get; }

    public Task Completion => _run;

    private async Task RunAsync()
    {
        for (var round = 0; round < 2; round++)
        {
            using var client = await _listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            using var request = await ReadRequestAsync(stream);
            var root = request.RootElement;
            if (round == 0)
            {
                var toolName = root.GetProperty("tools")
                    .EnumerateArray()
                    .Select(tool => tool.GetProperty("function")
                        .GetProperty("name").GetString())
                    .Single(name =>
                        name?.StartsWith("dynamic_", StringComparison.Ordinal) == true &&
                        name.EndsWith("__echo", StringComparison.Ordinal));
                var chunk = JsonSerializer.Serialize(new
                {
                    choices = new[]
                    {
                        new
                        {
                            index = 0,
                            delta = new
                            {
                                tool_calls = new[]
                                {
                                    new
                                    {
                                        index = 0,
                                        id = "call-dynamic",
                                        function = new
                                        {
                                            name = toolName,
                                            arguments = """{"text":"ping"}""",
                                        },
                                    },
                                },
                            },
                            finish_reason = "tool_calls",
                        },
                    },
                    usage = new
                    {
                        prompt_tokens = 10,
                        completion_tokens = 1,
                        total_tokens = 11,
                    },
                });
                await WriteSseAsync(
                    stream,
                    $"data: {chunk}\n\ndata: [DONE]\n\n");
            }
            else
            {
                var toolResult = root.GetProperty("messages")
                    .EnumerateArray()
                    .Single(message =>
                        message.GetProperty("role").GetString() == "tool")
                    .GetProperty("content").GetString();
                if (toolResult?.Contains("pong", StringComparison.Ordinal) != true)
                {
                    throw new InvalidOperationException(
                        "Dynamic Tool result did not return to the provider.");
                }

                await WriteSseAsync(
                    stream,
                    """
                    data: {"choices":[{"index":0,"delta":{"content":"OK"},"finish_reason":"stop"}],"usage":{"prompt_tokens":12,"completion_tokens":1,"total_tokens":13}}

                    data: [DONE]

                    """);
            }
        }
    }

    private async Task<JsonDocument> ReadRequestAsync(NetworkStream stream)
    {
        var header = new ArrayBufferWriter<byte>();
        var one = new byte[1];
        while (header.WrittenCount < 64 * 1024)
        {
            if (await stream.ReadAsync(one) == 0)
            {
                throw new InvalidOperationException("Provider request ended early.");
            }

            header.Write(one);
            if (header.WrittenCount >= 4 &&
                header.WrittenSpan[^4..].SequenceEqual("\r\n\r\n"u8))
            {
                break;
            }
        }

        if (header.WrittenCount < 4 ||
            !header.WrittenSpan[^4..].SequenceEqual("\r\n\r\n"u8))
        {
            throw new InvalidOperationException(
                "Provider request headers are too large.");
        }

        var headers = Encoding.ASCII.GetString(header.WrittenSpan);
        if (!headers.Contains(
                $"Authorization: Bearer {_secret}",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Provider authorization header is missing.");
        }

        var contentLength = headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith(
                "Content-Length:",
                StringComparison.OrdinalIgnoreCase))
            .Select(line => int.Parse(
                line["Content-Length:".Length..].Trim(),
                System.Globalization.CultureInfo.InvariantCulture))
            .Single();
        var body = new byte[contentLength];
        await stream.ReadExactlyAsync(body);
        return JsonDocument.Parse(body);
    }

    private static async Task WriteSseAsync(NetworkStream stream, string sse)
    {
        var body = Encoding.UTF8.GetBytes(sse);
        var header = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/event-stream\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n");
        await stream.WriteAsync(header);
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

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
}

internal sealed class HoldingHttpEndpoint : IAsyncDisposable
{
    private readonly TcpListener _listener =
        new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Channel<bool> _connections =
        Channel.CreateUnbounded<bool>();
    private readonly Task _acceptLoop;

    public HoldingHttpEndpoint()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptAsync();
    }

    public int Port { get; }

    public async Task WaitForConnectionAsync(TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        await _connections.Reader.ReadAsync(cancellation.Token);
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_lifetime.Token);
                await _connections.Writer.WriteAsync(true, _lifetime.Token);
                _ = HoldAsync(client);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (SocketException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private async Task HoldAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, _lifetime.Token);
            }
            catch (OperationCanceledException) when (
                _lifetime.IsCancellationRequested)
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _listener.Stop();
        await _acceptLoop;
        _lifetime.Dispose();
    }
}
