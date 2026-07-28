using System.Collections;
using System.CommandLine;
using System.CommandLine.Invocation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Agents;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Diagnostics;
using OpenCoWork.Core.Hosting;
using OpenCoWork.Core.Logging;
using OpenCoWork.Core.Sessions;
using OpenCoWork.Core.Workspaces;
using OpenCoWork.Generated;
using OpenCoWork.Protocol;

return await OpenCoWork.App.OpenCoWorkCli.RunAsync(args);

namespace OpenCoWork.App
{
    public static class OpenCoWorkCli
    {
        public static Task<int> RunAsync(
            string[] args,
            CancellationToken cancellationToken = default) =>
            RunCoreAsync(
                args,
                Console.In,
                Console.Out,
                Console.Error,
                Environment.CurrentDirectory,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                !Console.IsInputRedirected,
                configureServices: null,
                Console.OpenStandardInput(),
                Console.OpenStandardOutput(),
                cancellationToken);

        public static Task<int> RunAsync(
            string[] args,
            TextWriter output,
            TextWriter error,
            string workingDirectory,
            string userProfileDirectory,
            CancellationToken cancellationToken = default) =>
            RunCoreAsync(
                args,
                Console.In,
                output,
                error,
                workingDirectory,
                userProfileDirectory,
                !Console.IsInputRedirected,
                configureServices: null,
                protocolInput: null,
                protocolOutput: null,
                cancellationToken);

        public static Task<int> RunAsync(
            string[] args,
            TextReader input,
            TextWriter output,
            TextWriter error,
            string workingDirectory,
            string userProfileDirectory,
            bool isInteractive,
            Action<IServiceCollection>? configureServices = null,
            CancellationToken cancellationToken = default) =>
            RunCoreAsync(
                args,
                input,
                output,
                error,
                workingDirectory,
                userProfileDirectory,
                isInteractive,
                configureServices,
                protocolInput: null,
                protocolOutput: null,
                cancellationToken);

        private static async Task<int> RunCoreAsync(
            string[] args,
            TextReader input,
            TextWriter output,
            TextWriter error,
            string workingDirectory,
            string userProfileDirectory,
            bool isInteractive,
            Action<IServiceCollection>? configureServices,
            Stream? protocolInput,
            Stream? protocolOutput,
            CancellationToken cancellationToken)
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
                configureServices,
                protocolInput,
                protocolOutput);
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
            Action<IServiceCollection>? configureServices,
            Stream? protocolInput,
            Stream? protocolOutput)
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
                      app-server  Serve the Desktop wire protocol.
                      acp       Serve the ACP v1 bridge over stdio.

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

            var serverWorkspace = CreateWorkspaceOption();
            var serverConfig = new Option<string?>("--config")
            {
                Description = "Use an additional JSONC configuration file.",
            };
            var serverSet = new Option<string[]>("--set")
            {
                Description = "Override configuration with path=value; repeatable.",
            };
            var serverStrictConfig = new Option<bool>("--strict-config")
            {
                Description = "Treat unknown configuration fields as failures.",
            };
            var transport = new Option<string?>("--transport")
            {
                Description = "Protocol transport: stdio (default) or websocket.",
            };
            var port = new Option<int?>("--port")
            {
                Description = "Loopback port required by the websocket transport.",
            };
            var appServer = new Command(
                "app-server",
                "Serve the Desktop wire protocol.")
            {
                serverWorkspace,
                serverConfig,
                serverSet,
                serverStrictConfig,
                transport,
                port,
            };
            appServer.SetAction((parseResult, cancellationToken) =>
                RunProtocolServerAsync(
                    parseResult.GetValue(serverWorkspace),
                    ResolveOptionalPath(
                        parseResult.GetValue(serverConfig),
                        workingDirectory),
                    parseResult.GetValue(serverSet) ?? [],
                    parseResult.GetValue(serverStrictConfig),
                    parseResult.GetValue(transport),
                    parseResult.GetValue(port),
                    workingDirectory,
                    userProfileDirectory,
                    input,
                    parseResult.InvocationConfiguration.Output,
                    parseResult.InvocationConfiguration.Error,
                    configureServices,
                    protocolInput,
                    protocolOutput,
                    acp: false,
                    cancellationToken));
            root.Subcommands.Add(appServer);

            var acpWorkspace = CreateWorkspaceOption();
            var acpConfig = new Option<string?>("--config")
            {
                Description = "Use an additional JSONC configuration file.",
            };
            var acpSet = new Option<string[]>("--set")
            {
                Description = "Override configuration with path=value; repeatable.",
            };
            var acpStrictConfig = new Option<bool>("--strict-config")
            {
                Description = "Treat unknown configuration fields as failures.",
            };
            var acp = new Command(
                "acp",
                "Serve the ACP v1 bridge over stdio.")
            {
                acpWorkspace,
                acpConfig,
                acpSet,
                acpStrictConfig,
            };
            acp.SetAction((parseResult, cancellationToken) =>
                RunProtocolServerAsync(
                    parseResult.GetValue(acpWorkspace),
                    ResolveOptionalPath(
                        parseResult.GetValue(acpConfig),
                        workingDirectory),
                    parseResult.GetValue(acpSet) ?? [],
                    parseResult.GetValue(acpStrictConfig),
                    requestedTransport: null,
                    port: null,
                    workingDirectory,
                    userProfileDirectory,
                    input,
                    parseResult.InvocationConfiguration.Output,
                    parseResult.InvocationConfiguration.Error,
                    configureServices,
                    protocolInput,
                    protocolOutput,
                    acp: true,
                    cancellationToken));
            root.Subcommands.Add(acp);
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
                var loaded = LoadConfig(
                    paths,
                    explicitConfigPath,
                    setOverrides,
                    strictConfig,
                    userProfileDirectory);
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
                    services => ConfigureRuntimeServices(
                        services,
                        snapshot,
                        configureServices),
                    runtimeConfig,
                    "cli");
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

        private static async Task<int> RunProtocolServerAsync(
            string? explicitWorkspace,
            string? explicitConfigPath,
            IReadOnlyList<string> setOverrides,
            bool strictConfig,
            string? requestedTransport,
            int? port,
            string workingDirectory,
            string userProfileDirectory,
            TextReader input,
            TextWriter output,
            TextWriter error,
            Action<IServiceCollection>? configureServices,
            Stream? protocolInput,
            Stream? protocolOutput,
            bool acp,
            CancellationToken cancellationToken)
        {
            var redactor = new SecretRedactor([]);
            try
            {
                var transport = acp ? "stdio" : requestedTransport ?? "stdio";
                if (transport is not ("stdio" or "websocket"))
                {
                    await error.WriteLineAsync(
                        "--transport must be 'stdio' or 'websocket'.");
                    return 2;
                }

                if (transport == "websocket" &&
                    (port is null or < 1 or > 65_535))
                {
                    await error.WriteLineAsync(
                        "--port must be between 1 and 65535 for websocket.");
                    return 2;
                }

                var token = transport == "websocket"
                    ? Environment.GetEnvironmentVariable(
                        OpenCoWorkProtocolServer.WebSocketTokenEnvironment)
                    : null;
                if (transport == "websocket" && string.IsNullOrWhiteSpace(token))
                {
                    await error.WriteLineAsync(
                        $"{OpenCoWorkProtocolServer.WebSocketTokenEnvironment} is required for websocket.");
                    return 2;
                }

                var paths = WorkspaceDiscovery.Discover(
                    workingDirectory,
                    explicitWorkspace);
                var loaded = LoadConfig(
                    paths,
                    explicitConfigPath,
                    setOverrides,
                    strictConfig,
                    userProfileDirectory);
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
                using var host = OpenCoWorkCompositionRoot.Build(
                    [],
                    paths.WorkspaceRoot,
                    services => ConfigureRuntimeServices(
                        services,
                        snapshot,
                        configureServices),
                    snapshot.GetRequiredSection<RuntimeConfig>(),
                    acp ? "acp" : "app-server");
                redactor = host.Services.GetRequiredService<SecretRedactor>();
                await host.StartAsync(cancellationToken);
                try
                {
                    var sessions = host.Services.GetRequiredService<ISessionService>();
                    if (acp)
                    {
                        var models = snapshot.GetRequiredSection<ModelsConfig>();
                        if (protocolInput is not null && protocolOutput is not null)
                        {
                            await OpenCoWorkProtocolServer.RunAcpStdioAsync(
                                sessions,
                                paths.WorkspaceRoot,
                                models.DefaultProvider,
                                models.DefaultModel,
                                protocolInput,
                                protocolOutput,
                                cancellationToken);
                        }
                        else
                        {
                            await OpenCoWorkProtocolServer.RunAcpJsonLinesAsync(
                                sessions,
                                paths.WorkspaceRoot,
                                models.DefaultProvider,
                                models.DefaultModel,
                                input,
                                output,
                                cancellationToken);
                        }
                    }
                    else if (transport == "websocket")
                    {
                        await OpenCoWorkProtocolServer.RunWebSocketAsync(
                            sessions,
                            paths.WorkspaceRoot,
                            port!.Value,
                            token!,
                            cancellationToken);
                    }
                    else if (protocolInput is not null && protocolOutput is not null)
                    {
                        await OpenCoWorkProtocolServer.RunStdioAsync(
                            sessions,
                            paths.WorkspaceRoot,
                            protocolInput,
                            protocolOutput,
                            cancellationToken);
                    }
                    else
                    {
                        await OpenCoWorkProtocolServer.RunJsonLinesAsync(
                            sessions,
                            paths.WorkspaceRoot,
                            input,
                            output,
                            cancellationToken);
                    }

                    return 0;
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

        private static ConfigLoadResult LoadConfig(
            OpenCoWorkPaths paths,
            string? explicitConfigPath,
            IReadOnlyList<string> setOverrides,
            bool strictConfig,
            string userProfileDirectory) =>
            ConfigLoader.Load(
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

        private static void ConfigureRuntimeServices(
            IServiceCollection services,
            EffectiveConfigSnapshot snapshot,
            Action<IServiceCollection>? configureServices)
        {
            services.AddSingleton(snapshot);
            services.AddSingleton(snapshot.GetRequiredSection<SessionConfig>());
            services.AddSingleton(snapshot.GetRequiredSection<ModelsConfig>());
            services.AddSingleton(snapshot.GetRequiredSection<ToolsConfig>());
            configureServices?.Invoke(services);
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
            RuntimeConfig? effectiveRuntimeConfig = null,
            string? primaryModuleId = null)
        {
            ArgumentNullException.ThrowIfNull(args);

            var registry = new ModuleRegistry(RuntimeCatalog.Modules);
            var primaryHost = registry.SelectPrimaryModule(
                primaryModuleId ?? "cli");
            var builder = Host.CreateApplicationBuilder(args);
            if (primaryHost.Id != "cli")
            {
                builder.Logging.ClearProviders();
                builder.Logging.AddConsole(options =>
                    options.LogToStandardErrorThreshold = LogLevel.Trace);
            }

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

    [OpenCoWorkModule(
        "app-server",
        Dependencies = ["session"],
        CanBePrimaryHost = true)]
    public sealed class AppServerModule : IOpenCoWorkModule
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

    [OpenCoWorkModule(
        "acp",
        Dependencies = ["session"],
        CanBePrimaryHost = true)]
    public sealed class AcpModule : IOpenCoWorkModule
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
