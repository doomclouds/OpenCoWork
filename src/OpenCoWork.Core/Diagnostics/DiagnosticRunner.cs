using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Logging;
using OpenCoWork.Core.State;
using OpenCoWork.Core.Tools;
using OpenCoWork.Core.Workspaces;

namespace OpenCoWork.Core.Diagnostics;

public enum DiagnosticStatus
{
    Passed,
    Warning,
    Failed,
    Skipped,
}

public sealed record ProductMetadata(
    string ProductVersion,
    string InformationalVersion,
    string? Commit,
    string RuntimeVersion,
    string Platform)
{
    public static ProductMetadata FromAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var version = assembly.GetName().Version
            ?? throw new InvalidOperationException("Product assembly has no version.");
        var productVersion = $"{version.Major}.{version.Minor}.{version.Build}";
        var informationalVersion =
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
            ?? productVersion;
        var separator = informationalVersion.IndexOf('+');
        var commit = separator >= 0 && separator < informationalVersion.Length - 1
            ? informationalVersion[(separator + 1)..]
            : null;
        return new ProductMetadata(
            productVersion,
            informationalVersion,
            commit,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.RuntimeIdentifier);
    }
}

public sealed record DiagnosticCheck(
    string Id,
    DiagnosticStatus Status,
    string Message);

public sealed record DoctorReport(
    int SchemaVersion,
    ProductMetadata Product,
    IReadOnlyList<DiagnosticCheck> Checks)
{
    [JsonIgnore]
    public bool HasFailures =>
        Checks.Any(check => check.Status == DiagnosticStatus.Failed);
}

public sealed record DoctorRequest(
    string StartupDirectory,
    string UserProfileDirectory,
    ProductMetadata Product,
    IReadOnlyList<ConfigSectionDescriptor> ConfigSections)
{
    public string? ExplicitWorkspace { get; init; }

    public string? ExplicitConfigPath { get; init; }

    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyList<string> SetOverrides { get; init; } = [];

    public bool StrictConfig { get; init; }
}

public static class DiagnosticRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static Task<DoctorReport> RunAsync(
        DoctorRequest request,
        CancellationToken cancellationToken = default) =>
        RunAsync(request, [], cancellationToken);

    public static async Task<DoctorReport> RunAsync(
        DoctorRequest request,
        IEnumerable<IWorkspaceStateMigrationContributor> contributors,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(contributors);
        var frozenContributors = contributors.ToArray();
        var checks = new List<DiagnosticCheck>(8);
        checks.Add(await CheckRuntimeAsync(request.StartupDirectory, cancellationToken));
        checks.Add(CheckPlatform());

        OpenCoWorkPaths? paths = null;
        var workspace = CheckWorkspace(request, out paths);
        checks.Add(workspace);

        var workspaceReady =
            paths is not null && Directory.Exists(paths.OpenCoWorkDirectory);
        checks.Add(workspaceReady
            ? CheckPaths(paths!)
            : Skipped("paths", "Workspace is not initialized."));

        ConfigLoadResult? config = null;
        if (workspaceReady &&
            checks[^1].Status != DiagnosticStatus.Failed)
        {
            config = CheckConfiguration(request, paths!, out var configuration);
            checks.Add(configuration);
        }
        else
        {
            checks.Add(Skipped("configuration", "Workspace paths are unavailable."));
        }

        if (config?.Snapshot is not null)
        {
            var sqlite = await CheckSqliteAsync(
                paths!,
                config.Snapshot,
                frozenContributors,
                cancellationToken);
            checks.Add(sqlite);
            checks.Add(sqlite.Status == DiagnosticStatus.Passed
                ? await CheckMemoryAsync(
                    paths!,
                    config.Snapshot,
                    frozenContributors,
                    cancellationToken)
                : Skipped("memory", "SQLite state is unavailable."));
        }
        else
        {
            checks.Add(Skipped("sqlite", "Effective configuration is unavailable."));
            checks.Add(Skipped("memory", "Effective configuration is unavailable."));
        }

        checks.Add(CheckTrust(request.UserProfileDirectory));
        var redactor = config?.Snapshot is null
            ? new SecretRedactor([])
            : SecretRedactor.FromSnapshot(config.Snapshot);
        return new DoctorReport(
            1,
            request.Product,
            checks
                .Select(check => check with
                {
                    Message = redactor.RedactText(check.Message),
                })
                .ToArray());
    }

    public static string FormatText(DoctorReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var lines = new List<string>
        {
            $"OpenCoWork doctor {report.Product.ProductVersion}",
        };
        lines.AddRange(report.Checks.Select(
            check => $"{check.Id,-14} {check.Status,-7} {check.Message}"));
        return string.Join(Environment.NewLine, lines);
    }

    public static string FormatJson(DoctorReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, JsonOptions);
    }

    private static async Task<DiagnosticCheck> CheckRuntimeAsync(
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            var sdk = await ReadSdkVersionAsync(workingDirectory, cancellationToken);
            var validRuntime = Environment.Version.Major == 10;
            var validSdk = Version.TryParse(sdk, out var parsed) &&
                           parsed.Major == 10 &&
                           parsed.Minor == 0 &&
                           parsed.Build is >= 300 and <= 399;
            return validRuntime && validSdk
                ? Passed(
                    "runtime",
                    $".NET {Environment.Version}; SDK {sdk}.")
                : Failed(
                    "runtime",
                    $"Requires .NET 10 and SDK 10.0.3xx; found runtime {Environment.Version}, SDK {sdk}.");
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException)
        {
            return Failed("runtime", $"Unable to inspect .NET SDK: {exception.Message}");
        }
    }

    private static DiagnosticCheck CheckPlatform()
    {
        var supported =
            OperatingSystem.IsWindows() &&
            RuntimeInformation.ProcessArchitecture == Architecture.X64 ||
            OperatingSystem.IsMacOS() &&
            RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        return supported
            ? Passed("platform", RuntimeInformation.RuntimeIdentifier)
            : Failed(
                "platform",
                $"Unsupported M1 platform: {RuntimeInformation.RuntimeIdentifier}; expected win-x64 or osx-arm64.");
    }

    private static DiagnosticCheck CheckWorkspace(
        DoctorRequest request,
        out OpenCoWorkPaths? paths)
    {
        try
        {
            paths = WorkspaceDiscovery.Discover(
                request.StartupDirectory,
                request.ExplicitWorkspace);
            return Directory.Exists(paths.OpenCoWorkDirectory)
                ? Passed("workspace", paths.WorkspaceRoot)
                : Warning(
                    "workspace",
                    $"Workspace is not initialized: {paths.WorkspaceRoot}");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            paths = null;
            return Failed("workspace", exception.Message);
        }
    }

    private static DiagnosticCheck CheckPaths(OpenCoWorkPaths paths)
    {
        try
        {
            var anchor = Path.Combine(paths.WorkspaceRoot, ".opencowork-path-anchor");
            foreach (var candidate in new[]
                     {
                         paths.OpenCoWorkDirectory,
                         paths.ConfigPath,
                         paths.LocalConfigPath,
                         paths.RuntimeDirectory,
                         paths.StateDatabasePath,
                         paths.LogsDirectory,
                         paths.TeamsRuntimeDirectory,
                         paths.MissionsDirectory,
                         paths.SubAgentsDirectory,
                         paths.WorktreesDirectory,
                     })
            {
                WorkspacePathGuard.ResolveContained(
                    paths.WorkspaceRoot,
                    anchor,
                    Path.GetRelativePath(paths.WorkspaceRoot, candidate));
            }

            return Passed("paths", "All M1 paths stay inside the workspace.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Failed("paths", exception.Message);
        }
    }

    private static ConfigLoadResult CheckConfiguration(
        DoctorRequest request,
        OpenCoWorkPaths paths,
        out DiagnosticCheck check)
    {
        var result = ConfigLoader.Load(new ConfigLoadRequest(request.ConfigSections)
        {
            UserConfigPath = Path.Combine(
                request.UserProfileDirectory,
                ".opencowork",
                "config.jsonc"),
            WorkspaceConfigPath = paths.ConfigPath,
            LocalConfigPath = paths.LocalConfigPath,
            ExplicitConfigPath = request.ExplicitConfigPath,
            Environment = request.Environment,
            SetOverrides = request.SetOverrides,
            Strict = request.StrictConfig,
        });
        var message = result.Validation.Diagnostics.Count == 0
            ? "Configuration is valid."
            : string.Join(
                " | ",
                result.Validation.Diagnostics.Select(
                    diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
        check = !result.Validation.IsValid
            ? Failed("configuration", message)
            : result.Validation.Diagnostics.Any(
                diagnostic =>
                    diagnostic.Severity == OpenCoWorkDiagnosticSeverity.Warning)
                ? Warning("configuration", message)
                : Passed("configuration", message);
        return result;
    }

    private static async Task<DiagnosticCheck> CheckSqliteAsync(
        OpenCoWorkPaths paths,
        EffectiveConfigSnapshot config,
        IReadOnlyList<IWorkspaceStateMigrationContributor> contributors,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(paths.StateDatabasePath))
        {
            return Failed("sqlite", $"State database does not exist: {paths.StateDatabasePath}");
        }

        try
        {
            var runtime = config.GetRequiredSection<RuntimeConfig>();
            var state = await new StateRuntime(
                    paths,
                    runtime.State.BusyTimeout,
                    contributors)
                .InspectAsync(cancellationToken);
            var expectedVersion = contributors.Count == 0
                ? StateMigrations.VersionFiveOnly[^1].Version
                : StateMigrations.CurrentVersion;
            var valid = state.SchemaVersion == expectedVersion &&
                        state.TargetVersion == expectedVersion &&
                        string.Equals(
                            state.MigrationStatus,
                            "Completed",
                            StringComparison.Ordinal) &&
                        state.Error is null &&
                        string.Equals(state.JournalMode, "wal", StringComparison.OrdinalIgnoreCase) &&
                        state.Synchronous == 2 &&
                        state.ForeignKeys &&
                        state.SecureDelete &&
                        state.BusyTimeoutMilliseconds ==
                        (int)runtime.State.BusyTimeout.TotalMilliseconds &&
                        state.QueryOnly;
            return valid
                ? Passed(
                    "sqlite",
                    $"Schema {expectedVersion}, Session tables and " +
                    "read-only PRAGMA policy are valid.")
                : Failed(
                    "sqlite",
                    $"SQLite state is inconsistent: schema={state.SchemaVersion}, " +
                    $"status={state.MigrationStatus}, target={state.TargetVersion}, " +
                    $"tables={state.Tables}, journal={state.JournalMode}.");
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException)
        {
            return Failed("sqlite", exception.Message);
        }
    }

    private static async Task<DiagnosticCheck> CheckMemoryAsync(
        OpenCoWorkPaths paths,
        EffectiveConfigSnapshot config,
        IReadOnlyList<IWorkspaceStateMigrationContributor> contributors,
        CancellationToken cancellationToken)
    {
        try
        {
            var runtime = config.GetRequiredSection<RuntimeConfig>();
            var state = new StateRuntime(
                paths,
                runtime.State.BusyTimeout,
                contributors);
            var orphans = await new WorkspaceMemoryRuntime(paths, state)
                .FindOrphanBlobNamesAsync(cancellationToken);
            return orphans.Count == 0
                ? Passed("memory", "Workspace Memory has no orphan blobs.")
                : Warning(
                    "memory",
                    $"Workspace Memory has {orphans.Count} orphan blob(s).");
        }
        catch (Exception exception) when (
            exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            return Failed(
                "memory",
                $"Unable to inspect Workspace Memory: {exception.Message}");
        }
    }

    private static DiagnosticCheck CheckTrust(string userProfileDirectory)
    {
        var trustPath = Path.Combine(
            userProfileDirectory,
            ".opencowork",
            "trust",
            "decisions.json");
        if (!File.Exists(trustPath))
        {
            return Passed("trust", "Trust store is absent; no workspace is authorized.");
        }

        try
        {
            var anchor = Path.Combine(userProfileDirectory, ".opencowork-path-anchor");
            WorkspacePathGuard.ResolveContained(
                userProfileDirectory,
                anchor,
                Path.GetRelativePath(userProfileDirectory, trustPath));
            using var document = JsonDocument.Parse(File.ReadAllText(trustPath));
            var root = document.RootElement;
            if (!root.TryGetProperty("schemaVersion", out var schemaVersion) ||
                schemaVersion.ValueKind != JsonValueKind.Number ||
                schemaVersion.GetInt32() != 1 ||
                !root.TryGetProperty("decisions", out var decisions) ||
                decisions.ValueKind != JsonValueKind.Array)
            {
                return Failed(
                    "trust",
                    "Trust store must contain schemaVersion 1 and a decisions array.");
            }

            var permission = CheckTrustPermissions(trustPath);
            return permission.Status == DiagnosticStatus.Passed
                ? permission with
                {
                    Message =
                        $"Trust store is valid with {decisions.GetArrayLength()} decision(s).",
                }
                : permission;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                JsonException or ArgumentException)
        {
            return Failed("trust", exception.Message);
        }
    }

    private static DiagnosticCheck CheckTrustPermissions(string trustPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return CheckWindowsTrustPermissions(trustPath);
        }

        if (OperatingSystem.IsMacOS())
        {
            var mode = File.GetUnixFileMode(trustPath);
            if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            {
                return Failed("trust", "Trust store is writable by group or other users.");
            }

            return (mode & UnixFileMode.UserWrite) == 0
                ? Warning("trust", "Trust store is read-only.")
                : Passed("trust", "Trust store permissions are safe.");
        }

        return Warning(
            "trust",
            "Trust store permission checks are unavailable on this platform.");
    }

    [SupportedOSPlatform("windows")]
    private static DiagnosticCheck CheckWindowsTrustPermissions(string trustPath)
    {
        var writeRights =
            FileSystemRights.Write |
            FileSystemRights.Modify |
            FileSystemRights.FullControl |
            FileSystemRights.Delete |
            FileSystemRights.ChangePermissions |
            FileSystemRights.TakeOwnership;
        var rules = FileSystemAclExtensions
            .GetAccessControl(new FileInfo(trustPath))
            .GetAccessRules(
                includeExplicit: true,
                includeInherited: true,
                typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>();
        var broadlyWritable = rules.Any(rule =>
            rule.AccessControlType == AccessControlType.Allow &&
            rule.IdentityReference is SecurityIdentifier sid &&
            (sid.IsWellKnown(WellKnownSidType.WorldSid) ||
             sid.IsWellKnown(WellKnownSidType.BuiltinUsersSid)) &&
            (rule.FileSystemRights & writeRights) != 0);
        if (broadlyWritable)
        {
            return Failed(
                "trust",
                "Trust store grants write access to Everyone or built-in Users.");
        }

        return new FileInfo(trustPath).IsReadOnly
            ? Warning("trust", "Trust store is read-only.")
            : Passed("trust", "Trust store permissions are safe.");
    }

    private static async Task<string> ReadSdkVersionAsync(
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "--version",
                WorkingDirectory = Path.GetFullPath(workingDirectory),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        if (!process.Start())
        {
            throw new InvalidOperationException("dotnet process did not start.");
        }

        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = (await standardOutput).Trim();
        var error = (await standardError).Trim();
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? $"dotnet --version exited with {process.ExitCode}."
                    : error);
        }

        return output;
    }

    private static DiagnosticCheck Passed(string id, string message) =>
        new(id, DiagnosticStatus.Passed, message);

    private static DiagnosticCheck Warning(string id, string message) =>
        new(id, DiagnosticStatus.Warning, message);

    private static DiagnosticCheck Failed(string id, string message) =>
        new(id, DiagnosticStatus.Failed, message);

    private static DiagnosticCheck Skipped(string id, string message) =>
        new(id, DiagnosticStatus.Skipped, message);
}
