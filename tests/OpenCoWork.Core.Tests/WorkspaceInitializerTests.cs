using System.Text;
using Microsoft.Data.Sqlite;
using OpenCoWork.Core.Workspaces;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class WorkspaceInitializerTests
{
    [Fact]
    public async Task First_init_is_minimal_atomic_and_rerun_preserves_user_content()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();

        await WorkspaceInitializer.InitializeAsync(
            files.Paths,
            TimeSpan.FromSeconds(2),
            cancellationToken);

        Assert.Equal(
            [".gitignore", "config.jsonc", "runtime/state.db"],
            Directory.EnumerateFiles(
                    files.Paths.OpenCoWorkDirectory,
                    "*",
                    SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(files.Paths.OpenCoWorkDirectory, path)
                    .Replace('\\', '/'))
                .Order()
                .ToArray());
        Assert.Equal(
            "// OpenCoWork workspace configuration.\n{}\n",
            await File.ReadAllTextAsync(files.Paths.ConfigPath, cancellationToken));
        var ignore = await File.ReadAllTextAsync(
            Path.Combine(files.Paths.OpenCoWorkDirectory, ".gitignore"),
            cancellationToken);
        Assert.Contains("config.local.jsonc\nruntime/\n", ignore, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', ignore);

        const string customConfig = """{"runtime":{"stopTimeout":"12s"}}""";
        await File.WriteAllTextAsync(
            files.Paths.ConfigPath,
            customConfig,
            new UTF8Encoding(false),
            cancellationToken);
        var ignorePath = Path.Combine(files.Paths.OpenCoWorkDirectory, ".gitignore");
        await File.WriteAllTextAsync(
            ignorePath,
            "user-file.txt\r\n" + ignore,
            new UTF8Encoding(false),
            cancellationToken);
        await using (var connection = new SqliteConnection(
                         $"Data Source={files.Paths.StateDatabasePath}"))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE state_info SET error = 'keep-me' WHERE id = 1;";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await WorkspaceInitializer.InitializeAsync(
            files.Paths,
            TimeSpan.FromSeconds(2),
            cancellationToken);

        Assert.Equal(
            customConfig,
            await File.ReadAllTextAsync(files.Paths.ConfigPath, cancellationToken));
        Assert.StartsWith(
            "user-file.txt\r\n",
            await File.ReadAllTextAsync(ignorePath, cancellationToken),
            StringComparison.Ordinal);
        await using var state = new SqliteConnection(
            $"Data Source={files.Paths.StateDatabasePath};Mode=ReadOnly");
        await state.OpenAsync(cancellationToken);
        await using var read = state.CreateCommand();
        read.CommandText = "SELECT error FROM state_info WHERE id = 1;";
        Assert.Equal("keep-me", await read.ExecuteScalarAsync(cancellationToken));
    }

    [Fact]
    public async Task Failed_first_init_leaves_no_workspace_or_temporary_directory()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();

        await Assert.ThrowsAsync<InjectedInitException>(
            () => WorkspaceInitializer.InitializeAsync(
                files.Paths,
                TimeSpan.FromSeconds(2),
                (_, _) => throw new InjectedInitException(),
                cancellationToken));

        Assert.False(Directory.Exists(files.Paths.OpenCoWorkDirectory));
        Assert.Empty(Directory.EnumerateDirectories(
            files.Root,
            ".opencowork.init-*",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task Existing_foreign_metadata_fails_but_recognized_partial_init_is_repaired()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var foreign = new TempWorkspace();
        Directory.CreateDirectory(foreign.Paths.OpenCoWorkDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(foreign.Paths.OpenCoWorkDirectory, "foreign.txt"),
            "not OpenCoWork",
            cancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => WorkspaceInitializer.InitializeAsync(
                foreign.Paths,
                TimeSpan.FromSeconds(2),
                cancellationToken));

        using var partial = new TempWorkspace();
        Directory.CreateDirectory(partial.Paths.OpenCoWorkDirectory);
        await File.WriteAllTextAsync(
            partial.Paths.ConfigPath,
            "{}",
            cancellationToken);

        await WorkspaceInitializer.InitializeAsync(
            partial.Paths,
            TimeSpan.FromSeconds(2),
            cancellationToken);

        Assert.True(File.Exists(partial.Paths.StateDatabasePath));
        Assert.True(File.Exists(
            Path.Combine(partial.Paths.OpenCoWorkDirectory, ".gitignore")));
    }

    private sealed class TempWorkspace : IDisposable
    {
        public TempWorkspace()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"opencowork-init-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            Paths = new OpenCoWorkPaths(Root);
        }

        public string Root { get; }

        public OpenCoWorkPaths Paths { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class InjectedInitException : Exception;
}
