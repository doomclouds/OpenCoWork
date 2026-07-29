using System.Text;
using System.Text.Json;

namespace OpenCoWork.LspFixture;

public static class FixtureMarker;

internal static class Program
{
    private const int MaximumFrameBytes = 1024 * 1024;

    public static async Task Main()
    {
        var cancellationToken = CancellationToken.None;
        var input = Console.OpenStandardInput();
        var output = Console.OpenStandardOutput();
        if (Environment.GetEnvironmentVariable("OPENCOWORK_LSP_PID_FILE") is
            { Length: > 0 } pidPath)
        {
            await File.WriteAllTextAsync(
                pidPath,
                Environment.ProcessId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                cancellationToken);
        }

        string? documentUri = null;
        while (await ReadAsync(input, cancellationToken) is { } message)
        {
            if (Environment.GetEnvironmentVariable("OPENCOWORK_LSP_TRACE_FILE") is
                { Length: > 0 } tracePath)
            {
                await File.AppendAllTextAsync(
                    tracePath,
                    message.GetRawText() + Environment.NewLine,
                    cancellationToken);
            }

            var method = message.GetProperty("method").GetString();
            if (!message.TryGetProperty("id", out var id))
            {
                if (method == "textDocument/didOpen")
                {
                    documentUri = message
                        .GetProperty("params")
                        .GetProperty("textDocument")
                        .GetProperty("uri")
                        .GetString();
                }
                else if (method == "exit")
                {
                    return;
                }

                continue;
            }

            switch (method)
            {
                case "initialize":
                    if (Environment.GetEnvironmentVariable(
                            "OPENCOWORK_LSP_HALF_FRAME") == "1")
                    {
                        await output.WriteAsync(
                            "Content-Length: 20\r\n\r\n{\"jsonrpc\""u8.ToArray(),
                            cancellationToken);
                        await output.FlushAsync(cancellationToken);
                        return;
                    }

                    await ReplyAsync(
                        output,
                        id,
                        JsonSerializer.SerializeToElement(new
                        {
                            capabilities = new
                            {
                                hoverProvider = true,
                                definitionProvider = true,
                                referencesProvider = true,
                                documentSymbolProvider = true,
                                workspaceSymbolProvider = true,
                                diagnosticProvider = new { },
                                renameProvider = true,
                                executeCommandProvider = new
                                {
                                    commands = new[] { "fixture.write" },
                                },
                            },
                        }),
                        cancellationToken);
                    break;
                case "textDocument/hover":
                    await ReplyAsync(
                        output,
                        id,
                        JsonSerializer.SerializeToElement(new
                        {
                            contents = new
                            {
                                kind = "plaintext",
                                value = "fixture hover",
                            },
                        }),
                        cancellationToken);
                    break;
                case "textDocument/definition":
                    var uri = Environment.GetEnvironmentVariable(
                                  "OPENCOWORK_LSP_EXTERNAL_URI") ??
                              documentUri ??
                              throw new InvalidOperationException(
                                  "No document URI was received.");
                    await ReplyAsync(
                        output,
                        id,
                        JsonSerializer.SerializeToElement(new
                        {
                            uri,
                            range = new
                            {
                                start = new { line = 0, character = 0 },
                                end = new { line = 0, character = 1 },
                            },
                        }),
                        cancellationToken);
                    break;
                case "textDocument/references":
                    await ReplyAsync(
                        output,
                        id,
                        JsonSerializer.SerializeToElement(Array.Empty<object>()),
                        cancellationToken);
                    break;
                case "textDocument/documentSymbol":
                    await ReplyAsync(
                        output,
                        id,
                        JsonSerializer.SerializeToElement(new[]
                        {
                            new
                            {
                                name = "Fixture",
                                kind = 5,
                                range = new
                                {
                                    start = new { line = 0, character = 0 },
                                    end = new { line = 0, character = 7 },
                                },
                                selectionRange = new
                                {
                                    start = new { line = 0, character = 6 },
                                    end = new { line = 0, character = 7 },
                                },
                            },
                        }),
                        cancellationToken);
                    break;
                case "workspace/symbol":
                    await ReplyAsync(
                        output,
                        id,
                        JsonSerializer.SerializeToElement(Array.Empty<object>()),
                        cancellationToken);
                    break;
                case "textDocument/diagnostic":
                    await ReplyAsync(
                        output,
                        id,
                        JsonSerializer.SerializeToElement(new
                        {
                            kind = "full",
                            items = Array.Empty<object>(),
                        }),
                        cancellationToken);
                    break;
                case "shutdown":
                    await ReplyAsync(
                        output,
                        id,
                        JsonSerializer.SerializeToElement<object?>(null),
                        cancellationToken);
                    break;
                default:
                    await ErrorAsync(output, id, cancellationToken);
                    break;
            }
        }
    }

    private static async Task<JsonElement?> ReadAsync(
        Stream input,
        CancellationToken cancellationToken)
    {
        var header = new List<byte>();
        while (header.Count < 8192)
        {
            var next = new byte[1];
            if (await input.ReadAsync(next, cancellationToken) == 0)
            {
                return null;
            }

            header.Add(next[0]);
            if (header.Count >= 4 &&
                header[^4] == '\r' &&
                header[^3] == '\n' &&
                header[^2] == '\r' &&
                header[^1] == '\n')
            {
                break;
            }
        }

        var line = Encoding.ASCII.GetString(header.ToArray())
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .Single(value => value.StartsWith(
                "Content-Length:",
                StringComparison.OrdinalIgnoreCase));
        var length = int.Parse(
            line["Content-Length:".Length..].Trim(),
            System.Globalization.CultureInfo.InvariantCulture);
        if (length is < 1 or > MaximumFrameBytes)
        {
            throw new InvalidDataException("Invalid Content-Length.");
        }

        var body = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = await input.ReadAsync(
                body.AsMemory(offset),
                cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            offset += read;
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static Task ReplyAsync(
        Stream output,
        JsonElement id,
        JsonElement result,
        CancellationToken cancellationToken) =>
        WriteAsync(
            output,
            JsonSerializer.SerializeToElement(new
            {
                jsonrpc = "2.0",
                id,
                result,
            }),
            cancellationToken);

    private static Task ErrorAsync(
        Stream output,
        JsonElement id,
        CancellationToken cancellationToken) =>
        WriteAsync(
            output,
            JsonSerializer.SerializeToElement(new
            {
                jsonrpc = "2.0",
                id,
                error = new
                {
                    code = -32601,
                    message = "Method not found.",
                },
            }),
            cancellationToken);

    private static async Task WriteAsync(
        Stream output,
        JsonElement message,
        CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message);
        var header = Encoding.ASCII.GetBytes(
            $"Content-Length: {body.Length}\r\n\r\n");
        await output.WriteAsync(header, cancellationToken);
        await output.WriteAsync(body, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }
}
