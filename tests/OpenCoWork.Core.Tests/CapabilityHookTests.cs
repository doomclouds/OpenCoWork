using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Capabilities;
using OpenCoWork.Core.Logging;
using OpenCoWork.Core.Tools;
using OpenCoWork.Core.Workspaces;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class CapabilityHookTests
{
    [Fact]
    public async Task Pre_hooks_use_stable_order_and_strict_intersection()
    {
        var order = new List<string>();
        var runtime = new CapabilityHookRuntime(
        [
            CapabilityHook.Pre(
                "z-last",
                (context, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    order.Add("z-last");
                    return ValueTask.FromResult(new ToolPreUseDecision(
                        ToolAuthorityDecision.Allow,
                        TimeSpan.FromSeconds(5)));
                }),
            CapabilityHook.Pre(
                "a-first",
                (context, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    order.Add("a-first");
                    return ValueTask.FromResult(new ToolPreUseDecision(
                        ToolAuthorityDecision.RequireApproval,
                        TimeSpan.FromSeconds(2)));
                }),
        ]);

        var decision = await runtime.PreToolUseAsync(
            Context(),
            TestContext.Current.CancellationToken);

        Assert.Equal(["a-first", "z-last"], order);
        Assert.Equal(
            ToolAuthorityDecision.RequireApproval,
            decision.Authority);
        Assert.Equal(TimeSpan.FromSeconds(2), decision.TimeoutCap);
    }

    [Fact]
    public async Task Terminal_hook_failure_does_not_change_result_or_skip_later_hooks()
    {
        ToolResultSnapshot? observed = null;
        var runtime = new CapabilityHookRuntime(
        [
            CapabilityHook.TerminalHook(
                "a-fails",
                static (_, _, _) => ValueTask.FromException(
                    new InvalidOperationException("boom"))),
            CapabilityHook.TerminalHook(
                "b-observes",
                (context, result, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    observed = result;
                    return ValueTask.CompletedTask;
                }),
        ]);
        var result = new ToolResultSnapshot(
            Guid.CreateVersion7(),
            "call-1",
            ToolInvocationStatus.Completed,
            JsonSerializer.SerializeToElement(new { ok = true }),
            Error: null,
            IsTruncated: false,
            OriginalByteCount: 11,
            ResultSha256: new string('a', 64),
            AttemptCount: 1);

        await runtime.ToolTerminalAsync(
            Context(),
            result,
            TestContext.Current.CancellationToken);

        Assert.Same(result, observed);
        Assert.Equal(ToolInvocationStatus.Completed, result.Status);
    }

    [Fact]
    public async Task Trusted_process_hook_uses_json_stdio()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-hook-{Guid.NewGuid():N}");
        var workspace = Path.Combine(root, "workspace");
        var user = Path.Combine(root, "user");
        Directory.CreateDirectory(Path.Combine(workspace, ".opencowork"));
        Directory.CreateDirectory(user);
        try
        {
            var execution = OperatingSystem.IsWindows()
                ? new
                {
                    kind = "process",
                    command = "powershell.exe",
                    arguments = new[]
                    {
                        "-NoLogo",
                        "-NoProfile",
                        "-NonInteractive",
                        "-Command",
                        "$null=[Console]::In.ReadLine();" +
                        "[Console]::Out.Write('{\"authority\":\"deny\"}')",
                    },
                    workingDirectory = "workspace",
                    environment = new Dictionary<string, string>(),
                }
                : new
                {
                    kind = "process",
                    command = "sh",
                    arguments = new[]
                    {
                        "-c",
                        "read line; printf '{\"authority\":\"deny\"}'",
                    },
                    workingDirectory = "workspace",
                    environment = new Dictionary<string, string>(),
                };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new[]
            {
                new
                {
                    id = "protect",
                    @event = "preToolUse",
                    execution,
                    timeoutMs = 2000,
                },
            });
            var hookPath = Path.Combine(workspace, ".opencowork", "hooks.json");
            await File.WriteAllBytesAsync(
                hookPath,
                bytes,
                TestContext.Current.CancellationToken);
            var paths = new CapabilityPersistencePaths(
                new OpenCoWorkPaths(workspace),
                user);
            var files = new CapabilityFileStore(paths);
            await files.SaveTrustDecisionsAsync(
                new TrustDecisionsDocument(
                    1,
                    [new CapabilityTrustDecision(
                        workspace,
                        CapabilitySourceKind.Workspace,
                        WorkspaceProcessHookSource.TrustSourceId,
                        SourceVersion: null,
                        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                        [
                            CapabilityTrustScope.OutOfProcess,
                            CapabilityTrustScope.TrustedHook,
                        ],
                        [])]),
                TestContext.Current.CancellationToken);
            var source = new WorkspaceProcessHookSource(
                paths.WorkspacePaths,
                files,
                new SecretRedactor([]));
            var hooks = await source.LoadAsync(
                TestContext.Current.CancellationToken);

            var decision = await new CapabilityHookRuntime(hooks)
                .PreToolUseAsync(
                    Context(),
                    TestContext.Current.CancellationToken);

            Assert.Equal(ToolAuthorityDecision.Deny, decision.Authority);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ToolInvocationContext Context()
    {
        var runtime = new ToolRuntime();
        var snapshot = runtime.BuildSnapshot(
            AgentMode.Agent,
            new OpenCoWork.Core.Configuration.ToolsConfig());
        var arguments = JsonSerializer.SerializeToElement(new { query = "tool" });
        return new ToolInvocationContext(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            0,
            "call-hook",
            snapshot.CanonicalToProviderNames["tool.search"],
            arguments,
            new string('a', 64),
            SensitiveInputDetected: false,
            snapshot);
    }
}
