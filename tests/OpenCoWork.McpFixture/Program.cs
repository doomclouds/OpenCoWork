using System.Text.Json;

namespace OpenCoWork.McpFixture;

public static class FixtureMarker;

internal static class Program
{
    public static async Task Main()
    {
        var cancellationToken = CancellationToken.None;
        if (Environment.GetEnvironmentVariable("OPENCOWORK_MCP_PID_FILE") is
            { Length: > 0 } pidPath)
        {
            await File.WriteAllTextAsync(
                pidPath,
                Environment.ProcessId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                cancellationToken);
        }

        JsonElement? slowRequestId = null;
        while (await Console.In.ReadLineAsync(cancellationToken) is { } line)
        {
            if (Environment.GetEnvironmentVariable("OPENCOWORK_MCP_TRACE_FILE") is
                { Length: > 0 } tracePath)
            {
                await File.AppendAllTextAsync(
                    tracePath,
                    line + Environment.NewLine,
                    cancellationToken);
            }

            using var document = JsonDocument.Parse(line);
            var message = document.RootElement;
            var method = message.GetProperty("method").GetString();
            if (!message.TryGetProperty("id", out var id))
            {
                if (method == "notifications/cancelled")
                {
                    if (Environment.GetEnvironmentVariable(
                            "OPENCOWORK_MCP_CANCEL_FILE") is { Length: > 0 } cancelPath)
                    {
                        await File.WriteAllTextAsync(
                            cancelPath,
                            "cancelled",
                            cancellationToken);
                    }

                    if (slowRequestId is { } pending)
                    {
                        await ReplyErrorAsync(
                            pending,
                            -32800,
                            "Cancelled by client.",
                            cancellationToken);
                        slowRequestId = null;
                    }
                }

                continue;
            }

            switch (method)
            {
                case "server/discover":
                    await ReplyAsync(
                        id,
                        """
                        {
                          "supportedVersions": ["2025-11-25"],
                          "capabilities": {
                            "tools": { "listChanged": false },
                            "resources": { "listChanged": false }
                          }
                        }
                        """,
                        cancellationToken);
                    break;
                case "initialize":
                    if (Environment.GetEnvironmentVariable(
                            "OPENCOWORK_MCP_HALF_FRAME") == "1")
                    {
                        await Console.Out.WriteAsync(
                            "{\"jsonrpc".AsMemory(),
                            cancellationToken);
                        await Console.Out.FlushAsync(cancellationToken);
                        return;
                    }

                    await ReplyAsync(
                        id,
                        """
                        {
                          "protocolVersion": "2025-11-25",
                          "capabilities": {
                            "tools": { "listChanged": false },
                            "resources": { "listChanged": false }
                          },
                          "serverInfo": {
                            "name": "OpenCoWork.McpFixture",
                            "version": "1.0.0"
                          }
                        }
                        """,
                        cancellationToken);
                    break;
                case "tools/list":
                    await ReplyAsync(
                        id,
                        """
                        {
                          "tools": [
                            {
                              "name": "echo",
                              "description": "Returns a fixed response.",
                              "inputSchema": {
                                "type": "object",
                                "additionalProperties": false
                              }
                            },
                            {
                              "name": "slow",
                              "description": "Waits for cancellation.",
                              "inputSchema": {
                                "type": "object",
                                "additionalProperties": false
                              }
                            },
                            {
                              "name": "fail",
                              "description": "Returns an unsafe remote error.",
                              "inputSchema": {
                                "type": "object",
                                "additionalProperties": false
                              }
                            }
                          ]
                        }
                        """,
                        cancellationToken);
                    break;
                case "resources/list":
                    await ReplyAsync(
                        id,
                        """
                        {
                          "resources": [{
                            "uri": "test://fixture",
                            "name": "fixture"
                          }]
                        }
                        """,
                        cancellationToken);
                    break;
                case "resources/read":
                    await ReplyAsync(
                        id,
                        """
                        {
                          "contents": [{
                            "uri": "test://fixture",
                            "mimeType": "text/plain",
                            "text": "fixture resource"
                          }]
                        }
                        """,
                        cancellationToken);
                    break;
                case "tools/call":
                    var name = message
                        .GetProperty("params")
                        .GetProperty("name")
                        .GetString();
                    if (name == "slow")
                    {
                        slowRequestId = id.Clone();
                    }
                    else if (name == "fail")
                    {
                        await ReplyErrorAsync(
                            id,
                            -32603,
                            "malicious-fixture-secret",
                            cancellationToken);
                    }
                    else
                    {
                        await ReplyAsync(
                            id,
                            """
                            {
                              "content": [{
                                "type": "text",
                                "text": "fixture ok"
                              }],
                              "isError": false
                            }
                            """,
                            cancellationToken);
                    }

                    break;
            }
        }
    }

    private static Task ReplyAsync(
        JsonElement id,
        string result,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(result);
        return WriteAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":" + id.GetRawText() +
            ",\"result\":" + JsonSerializer.Serialize(document.RootElement) + "}",
            cancellationToken);
    }

    private static Task ReplyErrorAsync(
        JsonElement id,
        int code,
        string message,
        CancellationToken cancellationToken) =>
        WriteAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":" + id.GetRawText() +
            ",\"error\":{\"code\":" + code +
            ",\"message\":" + JsonSerializer.Serialize(message) + "}}",
            cancellationToken);

    private static async Task WriteAsync(
        string message,
        CancellationToken cancellationToken)
    {
        await Console.Out.WriteLineAsync(message.AsMemory(), cancellationToken);
        await Console.Out.FlushAsync(cancellationToken);
    }
}
