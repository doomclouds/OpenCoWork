using OpenCoWork.Abstractions;
using OpenCoWork.Automations;
using OpenCoWork.Core.State;
using OpenCoWork.Core.Tools;
using OpenCoWork.Core.Workspaces;
using OpenCoWork.Teams;
using Xunit;

namespace OpenCoWork.IntegrationTests;

internal sealed class AutomationSourceTestWorkspace : IAsyncDisposable
{
    private AutomationSourceTestWorkspace(
        string root,
        StateRuntime store,
        AutomationSourceRuntime source)
    {
        Root = root;
        Store = store;
        Source = source;
    }

    public string Root { get; }

    public StateRuntime Store { get; }

    public AutomationSourceRuntime Source { get; }

    public string DefinitionPath(string id) =>
        Path.Combine(Source.DefinitionsDirectory, id + ".yaml");

    public static async Task<AutomationSourceTestWorkspace> CreateAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"opencowork-automation-source-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var paths = new OpenCoWorkPaths(root);
        var store = new StateRuntime(
            paths,
            TimeSpan.FromSeconds(2),
            [
                .. TeamsStateMigrationContributors.Create(),
                .. AutomationsStateMigrationContributors.Create(),
            ]);
        await store.InitializeAsync(TestContext.Current.CancellationToken);
        var descriptor = new WorkspaceRuntimeDescriptor(
            paths.WorkspaceRoot,
            paths.OpenCoWorkDirectory,
            paths.RuntimeDirectory,
            paths.TeamsRuntimeDirectory,
            paths.MissionsDirectory,
            paths.SubAgentsDirectory,
            paths.WorktreesDirectory);
        var source = new AutomationSourceRuntime(
            store,
            descriptor,
            new AutomationDefinitionLoader(
                new JsonSchemaValidationService(),
                new NoSensitiveDataService()),
            TimeProvider.System);
        Directory.CreateDirectory(source.DefinitionsDirectory);
        return new AutomationSourceTestWorkspace(root, store, source);
    }

    public async ValueTask DisposeAsync()
    {
        await Source.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(Root, recursive: true);
    }
}

internal sealed class NoSensitiveDataService : ISensitiveDataService
{
    public bool ContainsSensitiveData(string value) => false;

    public string Redact(string value) => value;

    public ValueTask<bool> ContainsSensitiveDataAsync(
        Stream source,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(false);
}
