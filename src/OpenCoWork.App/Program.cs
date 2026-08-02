using System.Collections;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenCoWork.Abstractions;
using OpenCoWork.Automations;
using OpenCoWork.Core.Agents;
using OpenCoWork.Core.Capabilities;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Diagnostics;
using OpenCoWork.Core.Gateway;
using OpenCoWork.Core.Hosting;
using OpenCoWork.Core.Logging;
using OpenCoWork.Core.Operations;
using OpenCoWork.Core.Sessions;
using OpenCoWork.Core.State;
using OpenCoWork.Core.Workspaces;
using OpenCoWork.Generated;
using OpenCoWork.Protocol;
using OpenCoWork.Teams;

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
                ResolveUserProfileDirectory(),
                !Console.IsInputRedirected,
                configureServices: null,
                Console.OpenStandardInput(),
                Console.OpenStandardOutput(),
                cancellationToken);

        private static string ResolveUserProfileDirectory()
        {
            var validationProfile = Environment.GetEnvironmentVariable(
                "OPENCOWORK_VALIDATION_USER_PROFILE");
            return string.IsNullOrWhiteSpace(validationProfile)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : Path.GetFullPath(validationProfile);
        }

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
                      gateway   Run the local external-channel host.
                      channel   Inspect and administer local channels.
                      hub       Inspect registered workspaces.
                      ops       Query local operations data.

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

            var gatewayWorkspace = CreateWorkspaceOption();
            var gatewayConfig = new Option<string?>("--config")
            {
                Description = "Use an additional JSONC configuration file.",
            };
            var gatewaySet = new Option<string[]>("--set")
            {
                Description = "Override configuration with path=value; repeatable.",
            };
            var gatewayStrictConfig = new Option<bool>("--strict-config")
            {
                Description = "Treat unknown configuration fields as failures.",
            };
            var gatewayPort = new Option<int?>("--port")
            {
                Description = "Override the loopback Webhook port.",
            };
            var gateway = new Command(
                "gateway",
                "Run the local external-channel host.")
            {
                gatewayWorkspace,
                gatewayConfig,
                gatewaySet,
                gatewayStrictConfig,
                gatewayPort,
            };
            gateway.SetAction((parseResult, cancellationToken) =>
                RunGatewayAsync(
                    parseResult.GetValue(gatewayWorkspace),
                    ResolveOptionalPath(
                        parseResult.GetValue(gatewayConfig),
                        workingDirectory),
                    parseResult.GetValue(gatewaySet) ?? [],
                    parseResult.GetValue(gatewayStrictConfig),
                    parseResult.GetValue(gatewayPort),
                    workingDirectory,
                    userProfileDirectory,
                    parseResult.InvocationConfiguration.Error,
                    configureServices,
                    cancellationToken));
            root.Subcommands.Add(gateway);
            AddOperationsCommands(
                root,
                workingDirectory,
                userProfileDirectory,
                input,
                isInteractive,
                configureServices);
            return root;
        }

        private static void AddOperationsCommands(
            RootCommand root,
            string workingDirectory,
            string userProfileDirectory,
            TextReader input,
            bool isInteractive,
            Action<IServiceCollection>? configureServices)
        {
            var channel = new Command("channel", "Inspect and administer local channels.");
            var channelOptions = AddServiceOptions(channel);

            var channelList = new Command("list", "List configured channels.");
            var channelStatus = new Option<ChannelRuntimeStatus?>("--status");
            var channelPageSize = new Option<int?>("--page-size");
            var channelCursor = new Option<string?>("--cursor");
            channelList.Add(channelStatus);
            channelList.Add(channelPageSize);
            channelList.Add(channelCursor);
            channelList.SetAction((result, token) => RunServiceCommandAsync(
                channelOptions, result, workingDirectory, userProfileDirectory,
                configureServices,
                services => services.GetRequiredService<IChannelService>().ListChannelsAsync(
                    new ChannelListQuery(
                        result.GetValue(channelStatus),
                        result.GetValue(channelPageSize) ?? 100,
                        result.GetValue(channelCursor)),
                    token),
                token));
            channel.Subcommands.Add(channelList);

            AddChannelQueueCommand(
                channel,
                "inbound",
                channelOptions,
                workingDirectory,
                userProfileDirectory,
                configureServices,
                static (service, channelId, status, pageSize, cursor, token) =>
                    service.ListInboundAsync(
                        new ChannelInboundQuery(
                            channelId,
                            ParseOptional<ChannelInboundStatus>(status),
                            pageSize,
                            cursor),
                        token));
            AddChannelQueueCommand(
                channel,
                "outbox",
                channelOptions,
                workingDirectory,
                userProfileDirectory,
                configureServices,
                static (service, channelId, status, pageSize, cursor, token) =>
                    service.ListOutboxAsync(
                        new ChannelOutboxQuery(
                            channelId,
                            ParseOptional<ChannelOutboxStatus>(status),
                            pageSize,
                            cursor),
                        token));

            var retry = new Command("retry", "Retry one dead-lettered outbox message.");
            var retryId = new Option<Guid?>("--id");
            var retryRevision = new Option<long?>("--revision");
            retry.Add(retryId);
            retry.Add(retryRevision);
            retry.SetAction((result, token) => RunServiceCommandAsync(
                channelOptions, result, workingDirectory, userProfileDirectory,
                configureServices,
                services => services.GetRequiredService<IChannelService>()
                    .RetryDeadLetterAsync(
                        new ChannelDeadLetterRetryRequest(
                            result.GetValue(retryId)
                                ?? throw new ArgumentException("--id is required."),
                            Guid.CreateVersion7(),
                            result.GetValue(retryRevision)
                                ?? throw new ArgumentException("--revision is required.")),
                        token),
                token));
            channel.Subcommands.Add(retry);

            var secret = new Command("secret", "Set or clear a local channel secret.");
            var secretSet = new Command("set", "Read and store a secret from secure input.");
            var secretSetId = new Option<string?>("--channel-id");
            secretSet.Add(secretSetId);
            secretSet.SetAction((result, token) => RunServiceCommandAsync(
                channelOptions, result, workingDirectory, userProfileDirectory,
                configureServices,
                async services =>
                {
                    var channelId = result.GetValue(secretSetId)
                                    ?? throw new ArgumentException("--channel-id is required.");
                    var value = await ReadSecretAsync(input, isInteractive, token);
                    services.GetRequiredService<IChannelCredentialAdmin>()
                        .Set(channelId, value);
                    return new { channelId, changed = true };
                },
                token));
            var secretClear = new Command("clear", "Clear a local channel secret.");
            var secretClearId = new Option<string?>("--channel-id");
            secretClear.Add(secretClearId);
            secretClear.SetAction((result, token) => RunServiceCommandAsync(
                channelOptions, result, workingDirectory, userProfileDirectory,
                configureServices,
                services =>
                {
                    var channelId = result.GetValue(secretClearId)
                                    ?? throw new ArgumentException("--channel-id is required.");
                    services.GetRequiredService<IChannelCredentialAdmin>()
                        .Clear(channelId);
                    return Task.FromResult(new { channelId, changed = true });
                },
                token));
            secret.Subcommands.Add(secretSet);
            secret.Subcommands.Add(secretClear);
            channel.Subcommands.Add(secret);
            root.Subcommands.Add(channel);

            AddHubCommands(root, workingDirectory, userProfileDirectory, configureServices);
            AddOpsCommands(root, workingDirectory, userProfileDirectory, configureServices);
        }

        private static ServiceCommandOptions AddServiceOptions(Command command)
        {
            var options = new ServiceCommandOptions(
                Recursive(CreateWorkspaceOption()),
                Recursive(new Option<string?>("--config")),
                Recursive(new Option<string[]>("--set")),
                Recursive(new Option<bool>("--strict-config")),
                Recursive(new Option<bool>("--json")));
            command.Add(options.Workspace);
            command.Add(options.Config);
            command.Add(options.Set);
            command.Add(options.StrictConfig);
            command.Add(options.Json);
            return options;
        }

        private static Option<T> Recursive<T>(Option<T> option)
        {
            option.Recursive = true;
            return option;
        }

        private static void AddChannelQueueCommand<T>(
            Command parent,
            string name,
            ServiceCommandOptions options,
            string workingDirectory,
            string userProfileDirectory,
            Action<IServiceCollection>? configureServices,
            Func<IChannelService, string?, string?, int, string?, CancellationToken, Task<T>> query)
        {
            var command = new Command(name, $"List channel {name} records.");
            var channelId = new Option<string?>("--channel-id");
            var status = new Option<string?>("--status");
            var pageSize = new Option<int?>("--page-size");
            var cursor = new Option<string?>("--cursor");
            command.Add(channelId);
            command.Add(status);
            command.Add(pageSize);
            command.Add(cursor);
            command.SetAction((result, token) => RunServiceCommandAsync(
                options, result, workingDirectory, userProfileDirectory,
                configureServices,
                services => query(
                    services.GetRequiredService<IChannelService>(),
                    result.GetValue(channelId),
                    result.GetValue(status),
                    result.GetValue(pageSize) ?? 100,
                    result.GetValue(cursor),
                    token),
                token));
            parent.Subcommands.Add(command);
        }

        private static void AddHubCommands(
            RootCommand root,
            string workingDirectory,
            string userProfileDirectory,
            Action<IServiceCollection>? configureServices)
        {
            var hub = new Command("hub", "Inspect registered workspaces.");
            var options = AddServiceOptions(hub);
            var list = new Command("list", "List registered workspaces.");
            var pageSize = new Option<int?>("--page-size");
            var cursor = new Option<string?>("--cursor");
            list.Add(pageSize);
            list.Add(cursor);
            list.SetAction((result, token) => RunServiceCommandAsync(
                options, result, workingDirectory, userProfileDirectory,
                configureServices,
                services => services.GetRequiredService<IHubService>().ListWorkspacesAsync(
                    new HubWorkspaceQuery(
                        result.GetValue(pageSize) ?? 100,
                        result.GetValue(cursor)),
                    token),
                token));
            hub.Subcommands.Add(list);

            var dashboard = new Command("dashboard", "Read one workspace dashboard.");
            var workspaceId = new Option<Guid?>("--workspace-id");
            dashboard.Add(workspaceId);
            dashboard.SetAction((result, token) => RunServiceCommandAsync(
                options, result, workingDirectory, userProfileDirectory,
                configureServices,
                async services =>
                    await services.GetRequiredService<IHubService>()
                        .GetDashboardAsync(
                            result.GetValue(workspaceId)
                                ?? throw new ArgumentException(
                                    "--workspace-id is required."),
                            token)
                    ?? throw new KeyNotFoundException("Workspace was not found."),
                token));
            hub.Subcommands.Add(dashboard);
            root.Subcommands.Add(hub);
        }

        private static void AddOpsCommands(
            RootCommand root,
            string workingDirectory,
            string userProfileDirectory,
            Action<IServiceCollection>? configureServices)
        {
            var ops = new Command("ops", "Query local operations data.");
            var options = AddServiceOptions(ops);

            var usage = new Command("usage", "Query provider usage.");
            var from = new Option<DateTimeOffset?>("--from");
            var to = new Option<DateTimeOffset?>("--to");
            var bucket = new Option<OperationsTimeBucket?>("--bucket");
            usage.Add(from);
            usage.Add(to);
            usage.Add(bucket);
            usage.SetAction((result, token) =>
            {
                var end = result.GetValue(to) ?? DateTimeOffset.UtcNow;
                return RunServiceCommandAsync(
                    options, result, workingDirectory, userProfileDirectory,
                    configureServices,
                    services => services.GetRequiredService<IOperationsQueryService>()
                        .QueryUsageAsync(
                            new UsageQuery(
                                result.GetValue(from) ?? end.AddHours(-24),
                                end,
                                result.GetValue(bucket) ?? OperationsTimeBucket.Hour),
                            token),
                    token);
            });
            ops.Subcommands.Add(usage);

            var trace = new Command("trace", "List or read traces.");
            var traceList = new Command("list", "List trace summaries.");
            var tracePageSize = new Option<int?>("--page-size");
            var traceCursor = new Option<string?>("--cursor");
            traceList.Add(tracePageSize);
            traceList.Add(traceCursor);
            traceList.SetAction((result, token) => RunServiceCommandAsync(
                options, result, workingDirectory, userProfileDirectory,
                configureServices,
                services => services.GetRequiredService<IOperationsQueryService>()
                    .ListTracesAsync(
                        new TraceListQuery(
                            PageSize: result.GetValue(tracePageSize) ?? 100,
                            Cursor: result.GetValue(traceCursor)),
                        token),
                token));
            var traceGet = new Command("get", "Read one trace.");
            var traceId = new Option<string?>("--trace-id");
            traceGet.Add(traceId);
            traceGet.SetAction((result, token) => RunServiceCommandAsync(
                options, result, workingDirectory, userProfileDirectory,
                configureServices,
                services => services.GetRequiredService<IOperationsQueryService>()
                    .GetTraceAsync(
                        result.GetValue(traceId)
                            ?? throw new ArgumentException("--trace-id is required."),
                        token),
                token));
            trace.Subcommands.Add(traceList);
            trace.Subcommands.Add(traceGet);
            ops.Subcommands.Add(trace);

            var heartbeat = new Command("heartbeat", "Read workspace heartbeat.");
            heartbeat.SetAction((result, token) => RunServiceCommandAsync(
                options, result, workingDirectory, userProfileDirectory,
                configureServices,
                async services =>
                    await services.GetRequiredService<IOperationsQueryService>()
                        .GetHeartbeatAsync(token)
                    ?? throw new KeyNotFoundException("Heartbeat is unavailable."),
                token));
            ops.Subcommands.Add(heartbeat);

            AddInsightCommands(
                ops, options, workingDirectory, userProfileDirectory, configureServices);
            root.Subcommands.Add(ops);
        }

        private static void AddInsightCommands(
            Command ops,
            ServiceCommandOptions options,
            string workingDirectory,
            string userProfileDirectory,
            Action<IServiceCollection>? configureServices)
        {
            var insight = new Command("insight", "Run or inspect workspace insights.");
            var list = new Command("list", "List proposals or runs.");
            var kind = new Option<string?>("--kind");
            var pageSize = new Option<int?>("--page-size");
            var cursor = new Option<string?>("--cursor");
            list.Add(kind);
            list.Add(pageSize);
            list.Add(cursor);
            list.SetAction((result, token) => RunServiceCommandAsync(
                options, result, workingDirectory, userProfileDirectory,
                configureServices,
                async services =>
                {
                    var service = services.GetRequiredService<IWorkspaceInsightService>();
                    return string.Equals(
                        result.GetValue(kind), "runs", StringComparison.OrdinalIgnoreCase)
                        ? (object)await service.ListRunsAsync(
                            result.GetValue(pageSize) ?? 100,
                            result.GetValue(cursor), token)
                        : await service.ListAsync(
                            result.GetValue(pageSize) ?? 100,
                            result.GetValue(cursor), token);
                },
                token));
            var run = new Command("run", "Run local deterministic insight rules.");
            run.SetAction((result, token) => RunServiceCommandAsync(
                options, result, workingDirectory, userProfileDirectory,
                configureServices,
                services => services.GetRequiredService<IWorkspaceInsightService>()
                    .RunAsync(
                        new InsightRunRequest(
                            Guid.CreateVersion7(),
                            InsightRunTrigger.Manual),
                        token),
                token));

            var get = new Command("get", "Read one proposal.");
            var getId = new Option<Guid?>("--proposal-id");
            get.Add(getId);
            get.SetAction((result, token) => RunServiceCommandAsync(
                options, result, workingDirectory, userProfileDirectory,
                configureServices,
                async services =>
                    await services.GetRequiredService<IWorkspaceInsightService>()
                        .GetAsync(
                            result.GetValue(getId)
                                ?? throw new ArgumentException(
                                    "--proposal-id is required."),
                            token)
                    ?? throw new KeyNotFoundException("Proposal was not found."),
                token));
            var archive = new Command("archive", "Archive one proposal.");
            var archiveId = new Option<Guid?>("--proposal-id");
            var revision = new Option<long?>("--revision");
            archive.Add(archiveId);
            archive.Add(revision);
            archive.SetAction((result, token) => RunServiceCommandAsync(
                options, result, workingDirectory, userProfileDirectory,
                configureServices,
                services => services.GetRequiredService<IWorkspaceInsightService>()
                    .ArchiveAsync(
                        result.GetValue(archiveId)
                            ?? throw new ArgumentException("--proposal-id is required."),
                        result.GetValue(revision)
                            ?? throw new ArgumentException("--revision is required."),
                        token),
                token));
            insight.Subcommands.Add(list);
            insight.Subcommands.Add(run);
            insight.Subcommands.Add(get);
            insight.Subcommands.Add(archive);
            ops.Subcommands.Add(insight);
        }

        private static async Task<int> RunServiceCommandAsync<T>(
            ServiceCommandOptions options,
            ParseResult result,
            string workingDirectory,
            string userProfileDirectory,
            Action<IServiceCollection>? configureServices,
            Func<IServiceProvider, Task<T>> action,
            CancellationToken cancellationToken)
        {
            var error = result.InvocationConfiguration.Error;
            var redactor = new SecretRedactor([]);
            try
            {
                var paths = WorkspaceDiscovery.Discover(
                    workingDirectory,
                    result.GetValue(options.Workspace));
                var loaded = LoadConfig(
                    paths,
                    ResolveOptionalPath(result.GetValue(options.Config), workingDirectory),
                    result.GetValue(options.Set) ?? [],
                    result.GetValue(options.StrictConfig),
                    userProfileDirectory);
                if (!loaded.Validation.IsValid || loaded.Snapshot is null)
                {
                    foreach (var diagnostic in loaded.Validation.Diagnostics)
                    {
                        await error.WriteLineAsync($"{diagnostic.Code}: {diagnostic.Message}");
                    }
                    return 3;
                }

                var snapshot = loaded.Snapshot;
                redactor = SecretRedactor.FromSnapshot(snapshot);
                using var host = OpenCoWorkCompositionRoot.Build(
                    [], paths.WorkspaceRoot,
                    services => ConfigureRuntimeServices(
                        services,
                        snapshot,
                        configureServices,
                        userProfileDirectory),
                    snapshot.GetRequiredSection<RuntimeConfig>(),
                    "cli");
                await host.StartAsync(cancellationToken);
                try
                {
                    var value = await action(host.Services);
                    await result.InvocationConfiguration.Output.WriteLineAsync(
                        JsonSerializer.Serialize(
                            value,
                            new JsonSerializerOptions(JsonSerializerDefaults.Web)
                            {
                                WriteIndented = !result.GetValue(options.Json),
                            }));
                    return 0;
                }
                finally
                {
                    await host.StopAsync(CancellationToken.None);
                }
            }
            catch (ArgumentException exception)
            {
                await error.WriteLineAsync(exception.Message);
                return 2;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await error.WriteLineAsync(redactor.RedactText(exception.Message));
                return 1;
            }
        }

        private static T? ParseOptional<T>(string? value)
            where T : struct, Enum =>
            value is null
                ? null
                : Enum.TryParse<T>(value, ignoreCase: true, out var parsed)
                    ? parsed
                    : throw new ArgumentException($"Invalid {typeof(T).Name} value.");

        private static async Task<string> ReadSecretAsync(
            TextReader input,
            bool isInteractive,
            CancellationToken cancellationToken)
        {
            if (!isInteractive)
            {
                throw new ArgumentException(
                    "Secret input requires an interactive terminal.");
            }

            string? value;
            if (ReferenceEquals(input, Console.In) && !Console.IsInputRedirected)
            {
                var buffer = new StringBuilder();
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var key = Console.ReadKey(intercept: true);
                    if (key.Key == ConsoleKey.Enter)
                    {
                        break;
                    }
                    if (key.Key == ConsoleKey.Backspace)
                    {
                        if (buffer.Length > 0)
                        {
                            buffer.Length--;
                        }
                    }
                    else if (!char.IsControl(key.KeyChar))
                    {
                        buffer.Append(key.KeyChar);
                    }
                }
                value = buffer.ToString();
            }
            else
            {
                value = await input.ReadLineAsync(cancellationToken);
            }

            return string.IsNullOrWhiteSpace(value)
                ? throw new ArgumentException("Secret input is empty.")
                : value;
        }

        private sealed record ServiceCommandOptions(
            Option<string?> Workspace,
            Option<string?> Config,
            Option<string[]> Set,
            Option<bool> StrictConfig,
            Option<bool> Json);

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
                    StateContributors(),
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
                    StateContributors(),
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

        private static IReadOnlyList<IWorkspaceStateMigrationContributor>
            StateContributors() =>
            [
                .. GatewayStateMigrationContributors.Create(),
                .. TeamsStateMigrationContributors.Create(),
                .. AutomationsStateMigrationContributors.Create(),
            ];

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
                        configureServices,
                        userProfileDirectory),
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

        private static async Task<int> RunGatewayAsync(
            string? explicitWorkspace,
            string? explicitConfigPath,
            IReadOnlyList<string> setOverrides,
            bool strictConfig,
            int? port,
            string workingDirectory,
            string userProfileDirectory,
            TextWriter error,
            Action<IServiceCollection>? configureServices,
            CancellationToken cancellationToken)
        {
            var redactor = new SecretRedactor([]);
            try
            {
                if (port is < 1 or > 65_535)
                {
                    await error.WriteLineAsync("--port must be between 1 and 65535.");
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
                    userProfileDirectory,
                    port is null
                        ? null
                        : new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            ["gateway.listenPort"] = port.Value.ToString(
                                System.Globalization.CultureInfo.InvariantCulture),
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
                using var host = OpenCoWorkCompositionRoot.Build(
                    [],
                    paths.WorkspaceRoot,
                    services => ConfigureRuntimeServices(
                        services,
                        snapshot,
                        configureServices,
                        userProfileDirectory),
                    snapshot.GetRequiredSection<RuntimeConfig>(),
                    "gateway");
                redactor = host.Services.GetRequiredService<SecretRedactor>();
                await host.StartAsync(cancellationToken);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
                finally
                {
                    await host.StopAsync(CancellationToken.None);
                }

                return 0;
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException)
            {
                await error.WriteLineAsync(redactor.RedactText(exception.Message));
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
                        configureServices,
                        userProfileDirectory),
                    snapshot.GetRequiredSection<RuntimeConfig>(),
                    acp ? "acp" : "app-server");
                redactor = host.Services.GetRequiredService<SecretRedactor>();
                await host.StartAsync(cancellationToken);
                try
                {
                    var sessions = host.Services.GetRequiredService<ISessionService>();
                    var capabilities =
                        host.Services.GetRequiredService<ICapabilityService>();
                    var coWork = host.Services.GetService<ICoWorkService>();
                    var automations =
                        host.Services.GetService<IAutomationService>();
                    var channels = host.Services.GetService<IChannelService>();
                    var hub = host.Services.GetService<IHubService>();
                    var operations = host.Services.GetService<IOperationsQueryService>();
                    var insights = host.Services.GetService<IWorkspaceInsightService>();
                    var changes = host.Services.GetService<IOperationsChangeSource>();
                    if (acp)
                    {
                        var models = snapshot.GetRequiredSection<ModelsConfig>();
                        if (protocolInput is not null && protocolOutput is not null)
                        {
                            await OpenCoWorkProtocolServer.RunAcpStdioAsync(
                                sessions,
                                paths.WorkspaceRoot,
                                ModelsConfig.ProviderId,
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
                                ModelsConfig.ProviderId,
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
                            capabilities,
                            coWork,
                            automations,
                            channels,
                            hub,
                            operations,
                            insights,
                            changes,
                            paths.WorkspaceRoot,
                            port!.Value,
                            token!,
                            cancellationToken);
                    }
                    else if (protocolInput is not null && protocolOutput is not null)
                    {
                        await OpenCoWorkProtocolServer.RunStdioAsync(
                            sessions,
                            capabilities,
                            coWork,
                            automations,
                            channels,
                            hub,
                            operations,
                            insights,
                            changes,
                            paths.WorkspaceRoot,
                            protocolInput,
                            protocolOutput,
                            cancellationToken);
                    }
                    else
                    {
                        await OpenCoWorkProtocolServer.RunJsonLinesAsync(
                            sessions,
                            capabilities,
                            coWork,
                            automations,
                            channels,
                            hub,
                            operations,
                            insights,
                            changes,
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
            string userProfileDirectory,
            IReadOnlyDictionary<string, string>? dedicatedOptions = null) =>
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
                    DedicatedOptions = dedicatedOptions ??
                        new Dictionary<string, string>(StringComparer.Ordinal),
                    Strict = strictConfig,
                });

        private static void ConfigureRuntimeServices(
            IServiceCollection services,
            EffectiveConfigSnapshot snapshot,
            Action<IServiceCollection>? configureServices,
            string? userProfileDirectory = null)
        {
            services.AddSingleton(snapshot);
            services.AddSingleton(snapshot.GetRequiredSection<SessionConfig>());
            services.AddSingleton(snapshot.GetRequiredSection<ModelsConfig>());
            services.AddSingleton(snapshot.GetRequiredSection<ToolsConfig>());
            services.AddSingleton(snapshot.GetRequiredSection<GatewayConfig>());
            services.AddSingleton(snapshot.GetRequiredSection<CoWorkConfig>());
            services.AddSingleton(snapshot.GetRequiredSection<AutomationsConfig>());
            if (!string.IsNullOrWhiteSpace(userProfileDirectory))
            {
                services.AddSingleton(new WorkspaceRegistryRoot(userProfileDirectory));
            }
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
            builder.Logging.ClearProviders();
            if (primaryHost.Id != "cli")
            {
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
            builder.Services.AddSingleton(new GatewayConfig());
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
            services.AddOpenCoWorkCapabilityRuntime();
            services.AddOpenCoWorkSessionRuntime();
        }

        public async ValueTask StartAsync(
            IServiceProvider services,
            CancellationToken cancellationToken)
        {
            var capabilities =
                services.GetRequiredService<WorkspaceCapabilityRuntime>();
            var session = services.GetRequiredService<SessionRuntime>();
            try
            {
                await capabilities.StartAsync(cancellationToken);
                await session.StartAsync(cancellationToken);
            }
            catch (Exception startupError)
            {
                try
                {
                    if (capabilities.Status != CapabilityRuntimeState.Stopped)
                    {
                        await capabilities.StopAsync(CancellationToken.None);
                    }
                }
                catch (Exception cleanupError)
                {
                    throw new AggregateException(
                        "Capability startup failed and cleanup reported an error.",
                        startupError,
                        cleanupError);
                }

                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(startupError)
                    .Throw();
                throw;
            }

            var degradedReasons = new List<string>();
            if (capabilities.Status == CapabilityRuntimeState.Degraded)
            {
                degradedReasons.Add("Capability recovery is incomplete.");
            }

            if (session.IsDegraded)
            {
                degradedReasons.Add("Session projection recovery is incomplete.");
            }

            if (degradedReasons.Count != 0)
            {
                services.GetRequiredService<WorkspaceRuntime>().ReportDegraded(
                    "session",
                    string.Join(' ', degradedReasons));
            }
        }

        public async ValueTask StopAsync(
            IServiceProvider services,
            CancellationToken cancellationToken)
        {
            Exception? sessionError = null;
            try
            {
                await services
                    .GetRequiredService<SessionRuntime>()
                    .StopAsync(cancellationToken);
            }
            catch (Exception error)
            {
                sessionError = error;
            }

            try
            {
                await services
                    .GetRequiredService<WorkspaceCapabilityRuntime>()
                    .StopAsync(cancellationToken);
            }
            catch (Exception capabilityError) when (sessionError is not null)
            {
                throw new AggregateException(
                    "Session and capability runtime cleanup failed.",
                    sessionError,
                    capabilityError);
            }

            if (sessionError is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(sessionError)
                    .Throw();
            }
        }
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

        public async ValueTask StartAsync(
            IServiceProvider services,
            CancellationToken cancellationToken)
        {
            if (services.GetRequiredService<WorkspaceRuntime>()
                .IsPrimaryHost("app-server"))
            {
                await services.GetRequiredService<OperationsRuntime>()
                    .StartAsync(cancellationToken);
            }
        }

        public async ValueTask StopAsync(
            IServiceProvider services,
            CancellationToken cancellationToken)
        {
            var operations = services.GetRequiredService<OperationsRuntime>();
            if (operations.IsRunning)
            {
                await operations.StopAsync(cancellationToken);
            }
        }
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
        "gateway",
        Dependencies = ["session"],
        CanBePrimaryHost = true)]
    public sealed class GatewayModule : IOpenCoWorkModule
    {
        private CancellationTokenSource? _intakeLifetime;
        private Task? _intake;

        public void ConfigureServices(IServiceCollection services)
        {
            foreach (var contributor in GatewayStateMigrationContributors.Create())
            {
                services.AddSingleton(contributor);
            }

            services.TryAddSingleton<GatewayMediaStore>();
            services.TryAddSingleton(services => new GatewayService(
                services.GetRequiredService<IWorkspaceStateStore>(),
                services.GetRequiredService<GatewayMediaStore>(),
                services.GetRequiredService<ISessionService>(),
                services.GetRequiredService<TimeProvider>(),
                services.GetRequiredService<OperationsChangeHub>()));
            services.TryAddSingleton<GatewayChannelRuntime>();
            services.TryAddSingleton<GatewayReconciler>();
            services.TryAddSingleton<ChannelOperationsService>();
            services.TryAddSingleton<IChannelService>(services =>
                services.GetRequiredService<ChannelOperationsService>());
            services.TryAddSingleton<IChannelInboundSink>(services =>
                services.GetRequiredService<GatewayService>());
            services.TryAddSingleton<IChannelSender, WebhookChannelSender>();
        }

        public async ValueTask StartAsync(
            IServiceProvider services,
            CancellationToken cancellationToken)
        {
            if (!services.GetRequiredService<WorkspaceRuntime>()
                    .IsPrimaryHost("gateway"))
            {
                return;
            }

            var operations = services.GetRequiredService<OperationsRuntime>();
            await operations.StartAsync(cancellationToken);
            var reconciler = services.GetRequiredService<GatewayReconciler>();
            try
            {
                await reconciler.StartAsync(cancellationToken);
            }
            catch (Exception startupError)
            {
                try
                {
                    await operations.StopAsync(CancellationToken.None);
                }
                catch (Exception cleanupError)
                {
                    throw new AggregateException(
                        "Gateway startup failed and operations cleanup reported an error.",
                        startupError,
                        cleanupError);
                }

                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(startupError)
                    .Throw();
                throw;
            }
            if (!reconciler.HasEnabledChannels)
            {
                return;
            }

            var lifetime = new CancellationTokenSource();
            var started = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var sink = services.GetRequiredService<IChannelInboundSink>();
            var port = services.GetRequiredService<GatewayConfig>().ListenPort;
            var intake = WebhookChannelServer.RunAsync(
                port,
                channelId => reconciler.AcquireInboundSecret(channelId) is { } secret
                    ? new WebhookChannelBinding(ready: true, secret)
                    : null,
                sink,
                services.GetRequiredService<TimeProvider>(),
                lifetime.Token,
                () => started.TrySetResult());
            _intakeLifetime = lifetime;
            _intake = intake;
            try
            {
                var completed = await Task.WhenAny(started.Task, intake);
                if (completed == intake)
                {
                    await intake;
                }
                await started.Task.WaitAsync(cancellationToken);
            }
            catch (Exception startupError)
            {
                await lifetime.CancelAsync();
                try
                {
                    await intake;
                }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                {
                }
                catch
                {
                }
                _intake = null;
                _intakeLifetime = null;
                lifetime.Dispose();
                var cleanupErrors = new List<Exception>();
                try
                {
                    await reconciler.StopAsync(CancellationToken.None);
                }
                catch (Exception cleanupError)
                {
                    cleanupErrors.Add(cleanupError);
                }
                try
                {
                    await operations.StopAsync(CancellationToken.None);
                }
                catch (Exception cleanupError)
                {
                    cleanupErrors.Add(cleanupError);
                }
                if (cleanupErrors.Count != 0)
                {
                    throw new AggregateException(
                        "Gateway intake startup failed and cleanup reported errors.",
                        new[] { startupError }.Concat(cleanupErrors));
                }

                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(startupError)
                    .Throw();
                throw;
            }
        }

        public async ValueTask StopAsync(
            IServiceProvider services,
            CancellationToken cancellationToken)
        {
            var errors = new List<Exception>();
            var lifetime = Interlocked.Exchange(ref _intakeLifetime, null);
            var intake = Interlocked.Exchange(ref _intake, null);
            if (lifetime is not null)
            {
                await lifetime.CancelAsync();
                if (intake is not null)
                {
                    try
                    {
                        await intake.WaitAsync(cancellationToken);
                    }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
                    {
                    }
                    catch (Exception error)
                    {
                        errors.Add(error);
                    }
                }
                lifetime.Dispose();
            }

            try
            {
                var reconciler = services.GetRequiredService<GatewayReconciler>();
                if (reconciler.IsRunning)
                {
                    await reconciler.StopAsync(cancellationToken);
                }
            }
            catch (Exception reconcilerError)
            {
                errors.Add(reconcilerError);
            }

            try
            {
                var operations = services.GetRequiredService<OperationsRuntime>();
                if (operations.IsRunning)
                {
                    await operations.StopAsync(cancellationToken);
                }
            }
            catch (Exception operationsError)
            {
                errors.Add(operationsError);
            }

            if (errors.Count != 0)
            {
                throw new AggregateException("Gateway runtime cleanup failed.", errors);
            }
        }
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
