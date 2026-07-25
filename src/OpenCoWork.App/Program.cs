using System.Collections;
using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Diagnostics;
using OpenCoWork.Core.Hosting;
using OpenCoWork.Core.Logging;
using OpenCoWork.Core.Workspaces;
using OpenCoWork.Generated;

return await OpenCoWork.App.OpenCoWorkCli.RunAsync(args);

namespace OpenCoWork.App
{
    public static class OpenCoWorkCli
    {
        public static Task<int> RunAsync(
            string[] args,
            CancellationToken cancellationToken = default) =>
            RunAsync(
                args,
                Console.Out,
                Console.Error,
                Environment.CurrentDirectory,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                cancellationToken);

        public static async Task<int> RunAsync(
            string[] args,
            TextWriter output,
            TextWriter error,
            string workingDirectory,
            string userProfileDirectory,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(args);
            ArgumentNullException.ThrowIfNull(output);
            ArgumentNullException.ThrowIfNull(error);
            ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
            ArgumentException.ThrowIfNullOrWhiteSpace(userProfileDirectory);

            var product = ProductMetadata.FromAssembly(typeof(OpenCoWorkCli).Assembly);
            var root = CreateRootCommand(
                product,
                workingDirectory,
                userProfileDirectory);
            var parseResult = root.Parse(args);
            var exitCode = await parseResult.InvokeAsync(
                new InvocationConfiguration
                {
                    Output = output,
                    Error = error,
                    EnableDefaultExceptionHandler = false,
                },
                cancellationToken);
            return parseResult.Errors.Count == 0 ? exitCode : 2;
        }

        private static RootCommand CreateRootCommand(
            ProductMetadata product,
            string workingDirectory,
            string userProfileDirectory)
        {
            var root = new RootCommand(
                "OpenCoWork agent collaboration runtime.");
            root.SetAction(parseResult =>
            {
                parseResult.InvocationConfiguration.Output.WriteLine(
                    """
                    Description:
                      OpenCoWork agent collaboration runtime.

                    Usage:
                      opencowork [options] [command]

                    Commands:
                      init      Initialize an OpenCoWork workspace.
                      doctor    Inspect runtime and workspace health without modifying state.

                    Options:
                      --version  Show version information.
                      -?, -h, --help  Show help and usage information.
                    """);
                return 0;
            });

            var versionOption = root.Options
                .OfType<VersionOption>()
                .Single();
            versionOption.Action = new ProductVersionAction(product.ProductVersion);

            var initWorkspace = CreateWorkspaceOption();
            var init = new Command(
                "init",
                "Initialize an OpenCoWork workspace.")
            {
                initWorkspace,
            };
            init.SetAction((parseResult, cancellationToken) =>
                RunInitAsync(
                    parseResult.GetValue(initWorkspace),
                    workingDirectory,
                    parseResult.InvocationConfiguration.Output,
                    parseResult.InvocationConfiguration.Error,
                    cancellationToken));
            root.Subcommands.Add(init);

            var doctorWorkspace = CreateWorkspaceOption();
            var config = new Option<string?>("--config")
            {
                Description = "Use an additional JSONC configuration file.",
            };
            var set = new Option<string[]>("--set")
            {
                Description = "Override configuration with path=value; repeatable.",
            };
            var strictConfig = new Option<bool>("--strict-config")
            {
                Description = "Treat unknown configuration fields as failures.",
            };
            var json = new Option<bool>("--json")
            {
                Description = "Write the stable JSON result model.",
            };
            var doctor = new Command(
                "doctor",
                "Inspect runtime and workspace health without modifying state.")
            {
                doctorWorkspace,
                config,
                set,
                strictConfig,
                json,
            };
            doctor.SetAction((parseResult, cancellationToken) =>
                RunDoctorAsync(
                    new DoctorRequest(
                        workingDirectory,
                        userProfileDirectory,
                        product,
                        RuntimeCatalog.ConfigSections)
                    {
                        ExplicitWorkspace = parseResult.GetValue(doctorWorkspace),
                        ExplicitConfigPath = ResolveOptionalPath(
                            parseResult.GetValue(config),
                            workingDirectory),
                        Environment = ReadEnvironment(),
                        SetOverrides = parseResult.GetValue(set) ?? [],
                        StrictConfig = parseResult.GetValue(strictConfig),
                    },
                    parseResult.GetValue(json),
                    parseResult.InvocationConfiguration.Output,
                    parseResult.InvocationConfiguration.Error,
                    cancellationToken));
            root.Subcommands.Add(doctor);
            return root;
        }

        private static string? ResolveOptionalPath(string? path, string basePath) =>
            string.IsNullOrWhiteSpace(path)
                ? null
                : Path.GetFullPath(path, basePath);

        private static Option<string?> CreateWorkspaceOption() =>
            new("--workspace", "-w", "/workspace")
            {
                Description = "Workspace root; defaults to the current directory.",
            };

        private static async Task<int> RunInitAsync(
            string? explicitWorkspace,
            string workingDirectory,
            TextWriter output,
            TextWriter error,
            CancellationToken cancellationToken)
        {
            try
            {
                var paths = WorkspaceDiscovery.Discover(
                    workingDirectory,
                    explicitWorkspace ?? workingDirectory);
                await WorkspaceInitializer.InitializeAsync(
                    paths,
                    new RuntimeConfig().State.BusyTimeout,
                    cancellationToken);
                await output.WriteLineAsync(
                    $"Initialized OpenCoWork workspace: {paths.WorkspaceRoot}");
                return 0;
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException)
            {
                await error.WriteLineAsync(
                    new SecretRedactor([]).RedactText(exception.Message));
                return 1;
            }
        }

        private static async Task<int> RunDoctorAsync(
            DoctorRequest request,
            bool json,
            TextWriter output,
            TextWriter error,
            CancellationToken cancellationToken)
        {
            try
            {
                var report = await DiagnosticRunner.RunAsync(
                    request,
                    cancellationToken);
                await output.WriteLineAsync(
                    json
                        ? DiagnosticRunner.FormatJson(report)
                        : DiagnosticRunner.FormatText(report));
                return report.HasFailures ? 1 : 0;
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException)
            {
                await error.WriteLineAsync(
                    new SecretRedactor([]).RedactText(exception.Message));
                return 3;
            }
        }

        private static IReadOnlyDictionary<string, string> ReadEnvironment()
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                if (entry.Key is string key &&
                    entry.Value is string value &&
                    key.StartsWith("OPENCOWORK__", StringComparison.Ordinal))
                {
                    result[key] = value;
                }
            }

            return result;
        }

        private sealed class ProductVersionAction(string productVersion)
            : SynchronousCommandLineAction
        {
            public override int Invoke(ParseResult parseResult)
            {
                parseResult.InvocationConfiguration.Output.WriteLine(
                    $"opencowork {productVersion}");
                return 0;
            }
        }
    }

    public static class OpenCoWorkCompositionRoot
    {
        public static IHost Build(string[] args)
        {
            ArgumentNullException.ThrowIfNull(args);

            var registry = new ModuleRegistry(RuntimeCatalog.Modules);
            var primaryHost = registry.SelectPrimaryModule();
            var builder = Host.CreateApplicationBuilder(args);
            builder.Services.AddOpenCoWorkRuntime(
                registry,
                primaryHost,
                new RuntimeConfig().StopTimeout);
            return builder.Build();
        }
    }

    [OpenCoWorkModule("cli", CanBePrimaryHost = true)]
    public sealed class CliModule : IOpenCoWorkModule
    {
        public void ConfigureServices(IServiceCollection services)
        {
        }

        public ValueTask StartAsync(
            IServiceProvider services,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask StopAsync(
            IServiceProvider services,
            CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }
}
