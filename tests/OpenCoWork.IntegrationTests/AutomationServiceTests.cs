using System.Text.Json;
using OpenCoWork.Abstractions;
using OpenCoWork.Automations;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Sessions;
using OpenCoWork.Core.State;
using OpenCoWork.Core.Tools;
using OpenCoWork.Core.Workspaces;
using Xunit;

namespace OpenCoWork.IntegrationTests;

public sealed class AutomationServiceTests
{
    private static readonly AutomationActorContext Host =
        new(AutomationActorKind.Host, "wire:test");

    [Fact]
    public async Task Queries_use_keyset_pages_filters_and_stable_order()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.WriteAsync("charlie", enabled: true, scheduled: false);
        await fixture.WriteAsync("alpha", enabled: true, scheduled: true);
        await fixture.WriteAsync("bravo", enabled: false, scheduled: true);
        await fixture.ScanAsync();

        var first = await fixture.Service.ListDefinitionsAsync(
            new ListAutomationDefinitionsRequest(Host, PageSize: 2),
            TestContext.Current.CancellationToken);
        Assert.True(first.IsSuccess);
        Assert.Equal(["alpha", "bravo"], first.Value!.Items.Select(x => x.AutomationId));
        Assert.NotNull(first.Value.NextCursor);

        var second = await fixture.Service.ListDefinitionsAsync(
            new ListAutomationDefinitionsRequest(Host, PageSize: 2, first.Value.NextCursor),
            TestContext.Current.CancellationToken);
        Assert.True(second.IsSuccess);
        Assert.Equal(["charlie"], second.Value!.Items.Select(x => x.AutomationId));
        Assert.Null(second.Value.NextCursor);

        var schedules = await fixture.Service.ListSchedulesAsync(
            new ListAutomationSchedulesRequest(Host, PageSize: 1),
            TestContext.Current.CancellationToken);
        Assert.True(schedules.IsSuccess);
        Assert.Equal("alpha", Assert.Single(schedules.Value!.Items).AutomationId);
        Assert.NotNull(schedules.Value.NextCursor);

        var invalid = await fixture.Service.ListDefinitionsAsync(
            new ListAutomationDefinitionsRequest(Host, Cursor: "not-a-cursor"),
            TestContext.Current.CancellationToken);
        Assert.Equal(AutomationErrorCodes.InvalidCursor, invalid.Error!.Code);

        var definition = await fixture.Service.GetDefinitionAsync(
            new GetAutomationDefinitionRequest(Host, "alpha"),
            TestContext.Current.CancellationToken);
        Assert.True(definition.IsSuccess);
        Assert.True(definition.Value!.Activation.GlobalEnabled);
        Assert.True(definition.Value.Activation.WorkspaceTrusted);
        Assert.Equal(AutomationTrustBoundary.Source, definition.Value.Activation.TrustSource);
        Assert.NotNull(definition.Value.Definition);
    }

    [Fact]
    public async Task Manual_start_freezes_run_and_replays_command_receipt()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.WriteAsync("nightly-maintenance", enabled: true, scheduled: true);
        await fixture.ScanAsync();
        var definition = await fixture.Service.GetDefinitionAsync(
            new GetAutomationDefinitionRequest(Host, "nightly-maintenance"),
            TestContext.Current.CancellationToken);
        using var inputs = JsonDocument.Parse("""{"task":"focused"}""");
        var commandId = Guid.CreateVersion7();
        var request = new StartAutomationRunRequest(
            Host,
            "nightly-maintenance",
            inputs.RootElement.Clone(),
            commandId,
            definition.Value!.Summary.Revision);

        var created = await fixture.Service.StartRunAsync(
            request,
            TestContext.Current.CancellationToken);

        Assert.True(created.IsSuccess, created.Error?.Code);
        Assert.False(created.IsReplay);
        Assert.Equal(commandId, created.Value!.Summary.RunId);
        Assert.Equal(AutomationRunStatus.Pending, created.Value.Summary.Status);
        Assert.Equal("provider-a", created.Value.ProviderId);
        Assert.Equal("model-a", created.Value.ModelId);
        Assert.Equal(
            ["sample-plugin"],
            created.Value.Permissions.Plugins);
        Assert.Equal(["review"], created.Value.Permissions.Skills);
        Assert.Equal(["file.read"], created.Value.Permissions.Tools);
        Assert.Collection(
            created.Value.Permissions.Effects,
            effect =>
            {
                Assert.Equal("workspaceRead", effect.Effect);
                Assert.Equal(ToolAuthorityDecision.Allow, effect.Decision);
            },
            effect =>
            {
                Assert.Equal("workspaceWrite", effect.Effect);
                Assert.Equal(ToolAuthorityDecision.RequireApproval, effect.Decision);
            });
        Assert.Equal(
            ["plugin", "skill", "tool"],
            created.Value.Capabilities.Select(x => x.Kind));

        var prepared = await fixture.Prepared.ReadAsync(
            fixture.PreparedId(commandId),
            TestContext.Current.CancellationToken);
        Assert.NotNull(prepared);
        Assert.Contains(commandId.ToString("D"), prepared.RenderedPrompt, StringComparison.Ordinal);
        Assert.Contains("focused", prepared.RenderedPrompt, StringComparison.Ordinal);
        Assert.False(await fixture.RunStorageContainsAsync(commandId, "focused"));
        Assert.False(await fixture.RunStorageContainsAsync(
            commandId,
            prepared.RenderedPrompt));

        var replay = await fixture.Service.StartRunAsync(
            request,
            TestContext.Current.CancellationToken);
        Assert.True(replay.IsSuccess);
        Assert.True(replay.IsReplay);
        Assert.Equal(created.Value.Summary, replay.Value!.Summary);
        Assert.Equal(created.Value.ProviderId, replay.Value.ProviderId);
        Assert.Equal(created.Value.ModelId, replay.Value.ModelId);
        Assert.Equal(created.Value.Permissions.Plugins, replay.Value.Permissions.Plugins);
        Assert.Equal(created.Value.Permissions.Skills, replay.Value.Permissions.Skills);
        Assert.Equal(created.Value.Permissions.Tools, replay.Value.Permissions.Tools);
        Assert.Equal(created.Value.Permissions.Effects, replay.Value.Permissions.Effects);
        Assert.Equal(created.Value.Capabilities, replay.Value.Capabilities);

        fixture.Runtime.ProviderId = "provider-b";
        fixture.Runtime.ModelId = "model-b";
        fixture.Runtime.Capabilities =
        [
            new AutomationCapabilitySnapshot(
                "tool",
                "file.apply_patch",
                "2",
                new string('d', 64),
                99),
        ];
        await fixture.WriteAsync(
            "nightly-maintenance",
            enabled: true,
            scheduled: true,
            displayName: "Changed");
        await fixture.ScanAsync();

        var frozen = await fixture.Service.GetRunAsync(
            new GetAutomationRunRequest(Host, commandId),
            TestContext.Current.CancellationToken);
        Assert.Equal("provider-a", frozen.Value!.ProviderId);
        Assert.Equal("Nightly Maintenance", frozen.Value.Summary.AutomationId == "nightly-maintenance"
            ? definition.Value.Summary.DisplayName
            : null);
        Assert.Equal(["file.read"], frozen.Value.Permissions.Tools);

        var listed = await fixture.Service.ListRunsAsync(
            new ListAutomationRunsRequest(
                Host,
                AutomationId: "nightly-maintenance",
                Status: AutomationRunStatus.Pending,
                PageSize: 1),
            TestContext.Current.CancellationToken);
        Assert.Equal(commandId, Assert.Single(listed.Value!.Items).RunId);
    }

    [Fact]
    public async Task Run_queries_use_composite_keyset_and_filters()
    {
        await using var fixture = await Fixture.CreateAsync();
        foreach (var id in new[] { "alpha", "bravo", "charlie" })
        {
            await fixture.WriteAsync(id, enabled: true, scheduled: false);
        }

        await fixture.ScanAsync();
        var runIds = new List<Guid>();
        foreach (var id in new[] { "alpha", "bravo", "charlie" })
        {
            var definition = await fixture.Service.GetDefinitionAsync(
                new GetAutomationDefinitionRequest(Host, id),
                TestContext.Current.CancellationToken);
            using var inputs = JsonDocument.Parse("""{"task":"query"}""");
            var runId = Guid.CreateVersion7();
            var created = await fixture.Service.StartRunAsync(
                new StartAutomationRunRequest(
                    Host,
                    id,
                    inputs.RootElement.Clone(),
                    runId,
                    definition.Value!.Summary.Revision),
                TestContext.Current.CancellationToken);
            Assert.True(created.IsSuccess, created.Error?.Code);
            runIds.Add(runId);
        }

        var first = await fixture.Service.ListRunsAsync(
            new ListAutomationRunsRequest(Host, PageSize: 2),
            TestContext.Current.CancellationToken);
        var stable = await fixture.Service.ListRunsAsync(
            new ListAutomationRunsRequest(Host, PageSize: 2),
            TestContext.Current.CancellationToken);
        var second = await fixture.Service.ListRunsAsync(
            new ListAutomationRunsRequest(
                Host,
                PageSize: 2,
                Cursor: first.Value!.NextCursor),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            first.Value.Items.Select(item => item.RunId),
            stable.Value!.Items.Select(item => item.RunId));
        Assert.NotNull(first.Value.NextCursor);
        Assert.Single(second.Value!.Items);
        Assert.Null(second.Value.NextCursor);
        Assert.Equal(
            runIds.Order(),
            first.Value.Items.Concat(second.Value.Items)
                .Select(item => item.RunId)
                .Order());

        var filtered = await fixture.Service.ListRunsAsync(
            new ListAutomationRunsRequest(
                Host,
                AutomationId: "bravo",
                Status: AutomationRunStatus.Pending),
            TestContext.Current.CancellationToken);
        var bravo = Assert.Single(filtered.Value!.Items);
        Assert.Equal("bravo", bravo.AutomationId);
        Assert.Equal(
            bravo,
            (await fixture.Service.GetRunAsync(
                new GetAutomationRunRequest(Host, bravo.RunId),
                TestContext.Current.CancellationToken)).Value!.Summary);
    }

    [Fact]
    public async Task Manual_start_fails_closed_before_persistence()
    {
        const string canary = "manual-secret-canary";
        await using var fixture = await Fixture.CreateAsync(canary);
        await fixture.WriteAsync("nightly-maintenance", enabled: true, scheduled: false);
        await fixture.ScanAsync();
        var definition = await fixture.Service.GetDefinitionAsync(
            new GetAutomationDefinitionRequest(Host, "nightly-maintenance"),
            TestContext.Current.CancellationToken);

        using var invalidInputs = JsonDocument.Parse("""{"unknown":true}""");
        var invalid = await fixture.Service.StartRunAsync(
            new StartAutomationRunRequest(
                Host,
                "nightly-maintenance",
                invalidInputs.RootElement.Clone(),
                Guid.CreateVersion7(),
                definition.Value!.Summary.Revision),
            TestContext.Current.CancellationToken);
        Assert.Equal(AutomationErrorCodes.InputInvalid, invalid.Error!.Code);

        using var secretInputs = JsonDocument.Parse($$"""{"task":"{{canary}}"}""");
        var secretCommand = Guid.CreateVersion7();
        var secret = await fixture.Service.StartRunAsync(
            new StartAutomationRunRequest(
                Host,
                "nightly-maintenance",
                secretInputs.RootElement.Clone(),
                secretCommand,
                definition.Value.Summary.Revision),
            TestContext.Current.CancellationToken);
        Assert.Equal(AutomationErrorCodes.SecretDetected, secret.Error!.Code);
        Assert.Null(await fixture.Prepared.ReadAsync(
            fixture.PreparedId(secretCommand),
            TestContext.Current.CancellationToken));
        Assert.Equal(0, await fixture.CountRunsAsync(canary));
    }

    [Fact]
    public async Task Manual_start_enforces_activation_revision_command_and_singleton_gates()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.WriteAsync("nightly-maintenance", enabled: true, scheduled: false);
        await fixture.ScanAsync();
        var definition = await fixture.Service.GetDefinitionAsync(
            new GetAutomationDefinitionRequest(Host, "nightly-maintenance"),
            TestContext.Current.CancellationToken);
        using var inputs = JsonDocument.Parse("""{"task":"first"}""");

        var stale = await fixture.Service.StartRunAsync(
            new StartAutomationRunRequest(
                Host,
                "nightly-maintenance",
                inputs.RootElement.Clone(),
                Guid.CreateVersion7(),
                definition.Value!.Summary.Revision + 1),
            TestContext.Current.CancellationToken);
        Assert.Equal(AutomationErrorCodes.Conflict, stale.Error!.Code);

        fixture.Runtime.WorkspaceTrusted = false;
        var untrusted = await fixture.Service.StartRunAsync(
            new StartAutomationRunRequest(
                Host,
                "nightly-maintenance",
                inputs.RootElement.Clone(),
                Guid.CreateVersion7(),
                definition.Value.Summary.Revision),
            TestContext.Current.CancellationToken);
        Assert.Equal(AutomationErrorCodes.PermissionDenied, untrusted.Error!.Code);
        fixture.Runtime.WorkspaceTrusted = true;

        var commandId = Guid.CreateVersion7();
        var created = await fixture.Service.StartRunAsync(
            new StartAutomationRunRequest(
                Host,
                "nightly-maintenance",
                inputs.RootElement.Clone(),
                commandId,
                definition.Value.Summary.Revision),
            TestContext.Current.CancellationToken);
        Assert.True(created.IsSuccess);

        using var changedInputs = JsonDocument.Parse("""{"task":"different"}""");
        var commandConflict = await fixture.Service.StartRunAsync(
            new StartAutomationRunRequest(
                Host,
                "nightly-maintenance",
                changedInputs.RootElement.Clone(),
                commandId,
                definition.Value.Summary.Revision),
            TestContext.Current.CancellationToken);
        Assert.Equal(AutomationErrorCodes.Conflict, commandConflict.Error!.Code);

        var activeConflict = await fixture.Service.StartRunAsync(
            new StartAutomationRunRequest(
                Host,
                "nightly-maintenance",
                inputs.RootElement.Clone(),
                Guid.CreateVersion7(),
                definition.Value.Summary.Revision),
            TestContext.Current.CancellationToken);
        Assert.Equal(AutomationErrorCodes.RunConflict, activeConflict.Error!.Code);

        await using var disabledFixture = await Fixture.CreateAsync(enabled: false);
        await disabledFixture.WriteAsync("disabled", enabled: true, scheduled: false);
        await disabledFixture.ScanAsync();
        var disabledDefinition = await disabledFixture.Service.GetDefinitionAsync(
            new GetAutomationDefinitionRequest(Host, "disabled"),
            TestContext.Current.CancellationToken);
        var globallyDisabled = await disabledFixture.Service.StartRunAsync(
            new StartAutomationRunRequest(
                Host,
                "disabled",
                inputs.RootElement.Clone(),
                Guid.CreateVersion7(),
                disabledDefinition.Value!.Summary.Revision),
            TestContext.Current.CancellationToken);
        Assert.Equal(AutomationErrorCodes.Unavailable, globallyDisabled.Error!.Code);
    }

    internal sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            AutomationSourceTestWorkspace workspace,
            MutableRuntimeSnapshotProvider runtime,
            AutomationsConfig config,
            PreparedAutomationTurnStore prepared,
            AutomationService service,
            SessionService sessions,
            ProjectWriterLeaseService writerLeases,
            WorkspaceRuntimeDescriptor descriptor)
        {
            Workspace = workspace;
            Runtime = runtime;
            Config = config;
            Prepared = prepared;
            Service = service;
            Sessions = sessions;
            WriterLeases = writerLeases;
            Descriptor = descriptor;
        }

        public AutomationSourceTestWorkspace Workspace { get; }

        public MutableRuntimeSnapshotProvider Runtime { get; }

        public AutomationsConfig Config { get; }

        public PreparedAutomationTurnStore Prepared { get; }

        public AutomationService Service { get; }

        public SessionService Sessions { get; }

        public ProjectWriterLeaseService WriterLeases { get; }

        public WorkspaceRuntimeDescriptor Descriptor { get; }

        public static async Task<Fixture> CreateAsync(
            string? secret = null,
            bool enabled = true,
            int maxConcurrentRuns = AutomationRuntimeLimits.DefaultMaxConcurrentRuns,
            ISessionExecutor? executor = null)
        {
            var workspace = await AutomationSourceTestWorkspace.CreateAsync();
            ISensitiveDataService sensitive = secret is null
                ? new NoSensitiveDataService()
                : new CanarySensitiveDataService(secret);
            var runtime = new MutableRuntimeSnapshotProvider();
            var config = new AutomationsConfig
            {
                Enabled = enabled,
                MaxConcurrentRuns = maxConcurrentRuns,
            };
            var paths = new OpenCoWorkPaths(workspace.Root);
            var descriptor = new WorkspaceRuntimeDescriptor(
                paths.WorkspaceRoot,
                paths.OpenCoWorkDirectory,
                paths.RuntimeDirectory,
                paths.TeamsRuntimeDirectory,
                paths.MissionsDirectory,
                paths.SubAgentsDirectory,
                paths.WorktreesDirectory);
            var prepared = new PreparedAutomationTurnStore(paths, sensitive);
            var sessions = new SessionService(
                workspace.Store,
                new ThreadJournal(paths),
                new SessionProjection(workspace.Store),
                new SessionConfig(),
                executor: executor,
                executorKind: executor?.GetType().FullName,
                paths: paths);
            var writerLeases = new ProjectWriterLeaseService(
                workspace.Store,
                TimeProvider.System);
            var renderer = new AutomationTemplateRenderer(
                new JsonSchemaValidationService(),
                sensitive,
                TimeProvider.System);
            return new Fixture(
                workspace,
                runtime,
                config,
                prepared,
                new AutomationService(
                    workspace.Store,
                    workspace.Source,
                    new AutomationDefinitionLoader(
                        new JsonSchemaValidationService(),
                        sensitive),
                    renderer,
                    prepared,
                    runtime,
                    config,
                    TimeProvider.System,
                    sessions: sessions,
                    writerLeases: writerLeases),
                sessions,
                writerLeases,
                descriptor);
        }

        public Guid PreparedId(Guid commandId) =>
            AutomationService.PreparedTurnId(commandId);

        public Task WriteAsync(
            string id,
            bool enabled,
            bool scheduled,
            string? displayName = null,
            AutomationWorkspaceMode workspaceMode = AutomationWorkspaceMode.Worktree) =>
            File.WriteAllTextAsync(
                Workspace.DefinitionPath(id),
                Definition(id, enabled, scheduled, displayName, workspaceMode),
                TestContext.Current.CancellationToken);

        public AutomationDispatcher CreateDispatcher() =>
            new(
                Workspace.Store,
                Prepared,
                Sessions,
                new ManagedWorktreeService(new OpenCoWorkPaths(Workspace.Root)),
                WriterLeases,
                Descriptor,
                TimeProvider.System);

        public Task ScanAsync() =>
            Workspace.Source.ScanAsync(TestContext.Current.CancellationToken).AsTask();

        public Task<long> CountRunsAsync(string canary) =>
            Workspace.Store.ReadAsync(
                async (connection, cancellationToken) =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText =
                        """
                        SELECT count(*)
                        FROM automation_runs
                        WHERE definition_snapshot_json LIKE $canary
                           OR permission_snapshot_json LIKE $canary
                           OR capability_snapshot_json LIKE $canary;
                        """;
                    var parameter = command.CreateParameter();
                    parameter.ParameterName = "$canary";
                    parameter.Value = $"%{canary}%";
                    command.Parameters.Add(parameter);
                    var leaked = Convert.ToInt64(
                        await command.ExecuteScalarAsync(cancellationToken));

                    command.Parameters.Clear();
                    command.CommandText = "SELECT count(*) FROM automation_runs;";
                    var total = Convert.ToInt64(
                        await command.ExecuteScalarAsync(cancellationToken));
                    Assert.Equal(0, leaked);
                    return total;
                },
                TestContext.Current.CancellationToken).AsTask();

        public Task<bool> RunStorageContainsAsync(Guid runId, string value) =>
            Workspace.Store.ReadAsync(
                async (connection, cancellationToken) =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText =
                        """
                        SELECT count(*)
                        FROM automation_runs
                        WHERE automation_run_id = $runId
                          AND (
                            definition_snapshot_json LIKE $value OR
                            permission_snapshot_json LIKE $value OR
                            capability_snapshot_json LIKE $value OR
                            safe_summary LIKE $value OR
                            diagnostic LIKE $value);
                        """;
                    var run = command.CreateParameter();
                    run.ParameterName = "$runId";
                    run.Value = runId.ToString("D");
                    command.Parameters.Add(run);
                    var needle = command.CreateParameter();
                    needle.ParameterName = "$value";
                    needle.Value = $"%{value}%";
                    command.Parameters.Add(needle);
                    return Convert.ToInt64(
                        await command.ExecuteScalarAsync(cancellationToken)) != 0;
                },
                TestContext.Current.CancellationToken).AsTask();

        public async ValueTask DisposeAsync()
        {
            await Sessions.StopRuntimeAsync(CancellationToken.None);
            await Workspace.DisposeAsync();
        }

        private static string Definition(
            string id,
            bool enabled,
            bool scheduled,
            string? displayName,
            AutomationWorkspaceMode workspaceMode) =>
            $$"""
            schemaVersion: 1
            id: {{id}}
            displayName: {{displayName ?? (id == "nightly-maintenance" ? "Nightly Maintenance" : id)}}
            enabled: {{enabled.ToString().ToLowerInvariant()}}
            {{(scheduled ? """
            schedule:
              cron: "0 2 * * *"
              timeZone: UTC
            """ : string.Empty)}}
            workspace:
              mode: {{(workspaceMode == AutomationWorkspaceMode.Project ? "project" : "worktree")}}
              allowDirtyOrigin: false
            prompt: {{"Run {{ run.id }} do {{ inputs.task }}"}}
            inputSchema:
              type: object
              properties:
                task:
                  type: string
              required: [task]
              additionalProperties: false
            defaults:
              task: cleanup
            allow:
              plugins: [sample-plugin]
              skills: [review]
              tools: [file.read, file.apply_patch]
              effects: [workspaceRead, workspaceWrite]
            runTimeout: 30m
            attentionTimeout: 24h
            """;
    }

    internal sealed class MutableRuntimeSnapshotProvider : IAutomationRuntimeSnapshotProvider
    {
        public bool WorkspaceTrusted { get; set; } = true;

        public string ProviderId { get; set; } = "provider-a";

        public string ModelId { get; set; } = "model-a";

        public IReadOnlyList<AutomationCapabilitySnapshot> Capabilities { get; set; } =
        [
            new("plugin", "sample-plugin", "1", new string('a', 64), 1),
            new("skill", "review", "1", new string('b', 64), 2),
            new("tool", "file.read", "1", new string('c', 64), 3),
        ];

        public ValueTask<AutomationWorkspaceTrustSnapshot> GetWorkspaceTrustAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new AutomationWorkspaceTrustSnapshot(
                WorkspaceTrusted,
                AutomationTrustBoundary.Source,
                WorkspaceTrusted ? new string('f', 64) : AutomationTrustBoundary.Source.Sha256));

        public Task<AutomationRuntimeCaptureResult> CaptureAsync(
            AutomationRuntimeSnapshotRequest request,
            CancellationToken cancellationToken = default)
        {
            var trust = new AutomationWorkspaceTrustSnapshot(
                WorkspaceTrusted,
                AutomationTrustBoundary.Source,
                WorkspaceTrusted ? new string('f', 64) : AutomationTrustBoundary.Source.Sha256);
            if (!WorkspaceTrusted)
            {
                return Task.FromResult(new AutomationRuntimeCaptureResult(
                    null,
                    new AutomationError(
                        AutomationErrorCodes.PermissionDenied,
                        "Workspace trust is required.")));
            }

            return Task.FromResult(new AutomationRuntimeCaptureResult(
                new AutomationRuntimeSnapshot(
                    trust,
                    ProviderId,
                    ModelId,
                    new AutomationPermissionSnapshot(
                        trust.TrustSnapshotId,
                        CatalogRevision: 7,
                        ["sample-plugin"],
                        ["review"],
                        ["file.read"],
                        [
                            new AutomationEffectPermissionSnapshot(
                                "workspaceRead",
                                ToolAuthorityDecision.Allow),
                            new AutomationEffectPermissionSnapshot(
                                "workspaceWrite",
                                ToolAuthorityDecision.RequireApproval),
                        ]),
                    Capabilities),
                null));
        }
    }

    private sealed class CanarySensitiveDataService(string secret)
        : ISensitiveDataService
    {
        public bool ContainsSensitiveData(string value) =>
            value.Contains(secret, StringComparison.Ordinal);

        public string Redact(string value) =>
            value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);

        public async ValueTask<bool> ContainsSensitiveDataAsync(
            Stream source,
            CancellationToken cancellationToken = default)
        {
            using var reader = new StreamReader(source, leaveOpen: true);
            return ContainsSensitiveData(
                await reader.ReadToEndAsync(cancellationToken));
        }
    }
}
