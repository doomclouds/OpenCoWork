using System.Collections;
using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Agents;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Diagnostics;
using OpenCoWork.Core.Hosting;
using OpenCoWork.Core.Logging;
using OpenCoWork.Core.Sessions;
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
                Console.In,
                Console.Out,
                Console.Error,
                Environment.CurrentDirectory,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                !Console.IsInputRedirected,
                configureServices: null,
                cancellationToken);

        public static Task<int> RunAsync(
            string[] args,
            TextWriter output,
            TextWriter error,
            string workingDirectory,
            string userProfileDirectory,
            CancellationToken cancellationToken = default) =>
            RunAsync(
                args,
                Console.In,
                output,
                error,
                workingDirectory,
                userProfileDirectory,
                !Console.IsInputRedirected,
                configureServices: null,
                cancellationToken);

        public static async Task<int> RunAsync(
            string[] args,
            TextReader input,
            TextWriter output,
            TextWriter error,
            string workingDirectory,
            string userProfileDirectory,
            bool isInteractive,
            Action<IServiceCollection>? configureServices = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(args);
            ArgumentNullException.ThrowIfNull(input);
            ArgumentNullException.ThrowIfNull(output);
            ArgumentNullException.ThrowIfNull(error);
            ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
            ArgumentException.ThrowIfNullOrWhiteSpace(userProfileDirectory);

            var product = ProductMetadata.FromAssembly(typeof(OpenCoWorkCli).Assembly);
            var root = CreateRootCommand(
                product,
                workingDirectory,
                userProfileDirectory,
                input,
                isInteractive,
                configureServices);
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
            string userProfileDirectory,
            TextReader input,
            bool isInteractive,
            Action<IServiceCollection>? configureServices)
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
                      chat      Run a local multi-turn agent conversation.

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

            var chatWorkspace = CreateWorkspaceOption();
            var chatConfig = new Option<string?>("--config")
            {
                Description = "Use an additional JSONC configuration file.",
            };
            var chatSet = new Option<string[]>("--set")
            {
                Description = "Override configuration with path=value; repeatable.",
            };
            var chatStrictConfig = new Option<bool>("--strict-config")
            {
                Description = "Treat unknown configuration fields as failures.",
            };
            var thread = new Option<Guid?>("--thread")
            {
                Description = "Resume the exact existing thread ID.",
            };
            var provider = new Option<string?>("--provider")
            {
                Description = "Select an exact configured provider ID.",
            };
            var model = new Option<string?>("--model")
            {
                Description = "Select an exact configured model ID.",
            };
            var chat = new Command(
                "chat",
                "Run a local multi-turn agent conversation.")
            {
                chatWorkspace,
                chatConfig,
                chatSet,
                chatStrictConfig,
                thread,
                provider,
                model,
            };
            chat.SetAction((parseResult, cancellationToken) =>
                RunChatAsync(
                    parseResult.GetValue(chatWorkspace),
                    ResolveOptionalPath(
                        parseResult.GetValue(chatConfig),
                        workingDirectory),
                    parseResult.GetValue(chatSet) ?? [],
                    parseResult.GetValue(chatStrictConfig),
                    parseResult.GetValue(thread),
                    parseResult.GetValue(provider),
                    parseResult.GetValue(model),
                    workingDirectory,
                    userProfileDirectory,
                    input,
                    parseResult.InvocationConfiguration.Output,
                    parseResult.InvocationConfiguration.Error,
                    isInteractive,
                    configureServices,
                    cancellationToken));
            root.Subcommands.Add(chat);
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

        private static async Task<int> RunChatAsync(
            string? explicitWorkspace,
            string? explicitConfigPath,
            IReadOnlyList<string> setOverrides,
            bool strictConfig,
            Guid? threadId,
            string? providerId,
            string? modelId,
            string workingDirectory,
            string userProfileDirectory,
            TextReader input,
            TextWriter output,
            TextWriter error,
            bool isInteractive,
            Action<IServiceCollection>? configureServices,
            CancellationToken cancellationToken)
        {
            var redactor = new SecretRedactor([]);
            try
            {
                if (string.IsNullOrWhiteSpace(providerId) !=
                    string.IsNullOrWhiteSpace(modelId))
                {
                    await error.WriteLineAsync(
                        "--provider and --model must be specified together.");
                    return 2;
                }

                var paths = WorkspaceDiscovery.Discover(
                    workingDirectory,
                    explicitWorkspace);
                var loaded = ConfigLoader.Load(
                    new ConfigLoadRequest(RuntimeCatalog.ConfigSections)
                    {
                        UserConfigPath = Path.Combine(
                            userProfileDirectory,
                            ".opencowork",
                            "config.jsonc"),
                        WorkspaceConfigPath = paths.ConfigPath,
                        LocalConfigPath = paths.LocalConfigPath,
                        ExplicitConfigPath = explicitConfigPath,
                        Environment = ReadEnvironment(),
                        SetOverrides = setOverrides,
                        Strict = strictConfig,
                    });
                if (!loaded.Validation.IsValid || loaded.Snapshot is null)
                {
                    foreach (var diagnostic in loaded.Validation.Diagnostics)
                    {
                        await error.WriteLineAsync(
                            $"{diagnostic.Code}: {diagnostic.Message}");
                    }

                    return 3;
                }

                var snapshot = loaded.Snapshot;
                redactor = SecretRedactor.FromSnapshot(snapshot);
                var runtimeConfig = snapshot.GetRequiredSection<RuntimeConfig>();
                using var host = OpenCoWorkCompositionRoot.Build(
                    [],
                    paths.WorkspaceRoot,
                    services =>
                    {
                        services.AddSingleton(snapshot);
                        services.AddSingleton(
                            snapshot.GetRequiredSection<SessionConfig>());
                        services.AddSingleton(
                            snapshot.GetRequiredSection<ModelsConfig>());
                        services.AddSingleton(
                            snapshot.GetRequiredSection<ToolsConfig>());
                        configureServices?.Invoke(services);
                    },
                    runtimeConfig);
                redactor = host.Services.GetRequiredService<SecretRedactor>();
                await host.StartAsync(cancellationToken);
                try
                {
                    return await ChatCommandRunner.RunAsync(
                        host.Services,
                        threadId,
                        providerId,
                        modelId,
                        input,
                        output,
                        error,
                        isInteractive,
                        cancellationToken);
                }
                finally
                {
                    await host.StopAsync(CancellationToken.None);
                }
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException)
            {
                await error.WriteLineAsync(
                    redactor.RedactText(exception.Message));
                return 1;
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
        public static IHost Build(
            string[] args,
            string? workspaceRoot = null,
            Action<IServiceCollection>? configureServices = null,
            RuntimeConfig? effectiveRuntimeConfig = null)
        {
            ArgumentNullException.ThrowIfNull(args);

            var registry = new ModuleRegistry(RuntimeCatalog.Modules);
            var primaryHost = registry.SelectPrimaryModule();
            var builder = Host.CreateApplicationBuilder(args);
            var runtimeConfig = effectiveRuntimeConfig ?? new RuntimeConfig();
            builder.Services.AddSingleton(
                WorkspaceDiscovery.Discover(
                    Environment.CurrentDirectory,
                    workspaceRoot));
            builder.Services.AddSingleton(runtimeConfig);
            builder.Services.AddSingleton(new SessionConfig());
            configureServices?.Invoke(builder.Services);
            builder.Services.AddOpenCoWorkRuntime(
                registry,
                primaryHost,
                runtimeConfig.StopTimeout);
            return builder.Build();
        }
    }

    [OpenCoWorkModule("session")]
    public sealed class SessionModule : IOpenCoWorkModule
    {
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddOpenCoWorkAgentRuntime();
            services.AddOpenCoWorkSessionRuntime();
        }

        public async ValueTask StartAsync(
            IServiceProvider services,
            CancellationToken cancellationToken)
        {
            var session = services.GetRequiredService<SessionRuntime>();
            await session.StartAsync(cancellationToken);
            if (session.IsDegraded)
            {
                services.GetRequiredService<WorkspaceRuntime>().ReportDegraded(
                    "session",
                    "Session projection recovery is incomplete.");
            }
        }

        public ValueTask StopAsync(
            IServiceProvider services,
            CancellationToken cancellationToken) =>
            new(services.GetRequiredService<SessionRuntime>().StopAsync(cancellationToken));
    }

    [OpenCoWorkModule(
        "cli",
        Dependencies = ["session"],
        CanBePrimaryHost = true)]
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
