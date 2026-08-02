using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using OpenCoWork.Abstractions;
using OpenCoWork.Automations;
using OpenCoWork.Core.Capabilities;
using OpenCoWork.Core.Configuration;
using OpenCoWork.Core.Gateway;
using OpenCoWork.Core.Logging;
using OpenCoWork.Core.State;
using OpenCoWork.Core.Workspaces;
using OpenCoWork.Teams;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class GatewayOutboxTests
{
    [Fact]
    public async Task Channel_runtime_projects_digest_bound_trust_and_releases_secret_leases()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var files = await OutboxWorkspace.CreateAsync(cancellationToken);
        var paths = new OpenCoWorkPaths(files.Root);
        var user = Path.Combine(files.Root, "user");
        Directory.CreateDirectory(user);
        var persistencePaths = new CapabilityPersistencePaths(paths, user);
        var trust = new CapabilityFileStore(persistencePaths);
        var alpha = files.Config.Channels.Single(channel => channel.Id == "alpha");
        await trust.SaveTrustDecisionsAsync(
            new TrustDecisionsDocument(
                1,
                [
                    new CapabilityTrustDecision(
                        files.Root,
                        CapabilitySourceKind.Workspace,
                        "channel/alpha",
                        "1",
                        GatewayConfig.ComputeChannelSha256(alpha),
                        [CapabilityTrustScope.ExternalChannel],
                        []),
                ]),
            cancellationToken);
        var runtime = new GatewayChannelRuntime(
            files.State,
            files.Config,
            trust,
            paths,
            files.Credentials,
            files.Time);

        await runtime.ReconcileAsync(cancellationToken);

        Assert.True(runtime.HasEnabledChannels);
        Assert.NotNull(runtime.AcquireInboundSecret("alpha"));
        Assert.Null(runtime.AcquireInboundSecret("beta"));
        Assert.Equal("ready", await files.ScalarAsync<string>(
            "SELECT runtime_status FROM channels WHERE channel_id = 'alpha';",
            cancellationToken));
        Assert.Equal("pendingTrust", await files.ScalarAsync<string>(
            "SELECT runtime_status FROM channels WHERE channel_id = 'beta';",
            cancellationToken));

        await runtime.StopAsync(cancellationToken);

        Assert.Null(runtime.AcquireInboundSecret("alpha"));
        Assert.Equal("stopped", await files.ScalarAsync<string>(
            "SELECT runtime_status FROM channels WHERE channel_id = 'alpha';",
            cancellationToken));
    }

    [Theory]
    [InlineData((int)GatewayOutboxFaultPoint.OutboxCommitted)]
    [InlineData((int)GatewayOutboxFaultPoint.SendCompleted)]
    [InlineData((int)GatewayOutboxFaultPoint.SentCommitted)]
    public async Task Crash_windows_reuse_one_delivery_and_one_body(int faultPointValue)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var files = await OutboxWorkspace.CreateAsync(cancellationToken);
        await files.AddTerminalAsync(
            "alpha",
            "conversation-1",
            "message-1",
            1,
            "completed",
            "done",
            cancellationToken);
        var injected = false;
        files.ReplaceReconciler(point =>
        {
            if (!injected && point == (GatewayOutboxFaultPoint)faultPointValue)
            {
                injected = true;
                throw new InjectedCrashException();
            }
        });

        try
        {
            await files.Reconciler.ReconcileOutboxAsync(
                files.RuntimeInstanceId,
                4,
                cancellationToken);
        }
        catch (InjectedCrashException)
        {
        }
        files.Advance(TimeSpan.FromMinutes(11));
        files.ReplaceReconciler();
        await files.Reconciler.ReconcileOutboxAsync(
            files.RuntimeInstanceId,
            4,
            cancellationToken);

        Assert.True(injected);
        Assert.Equal(1L, await files.ScalarAsync<long>(
            "SELECT count(*) FROM channel_outbox;",
            cancellationToken));
        Assert.Equal("sent", await files.ScalarAsync<string>(
            "SELECT status FROM channel_outbox;",
            cancellationToken));
        Assert.Single(files.Sender.Requests.Select(request => request.Envelope.DeliveryId).Distinct());
        Assert.Single(files.Sender.Requests.Select(request => request.BodySha256).Distinct());
    }

    [Fact]
    public async Task Retry_after_is_capped_then_fifth_failure_dead_letters_without_blocking_channel()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var files = await OutboxWorkspace.CreateAsync(cancellationToken);
        await files.AddTerminalAsync(
            "alpha", "bad-conversation", "bad-1", 1, "failed", null,
            cancellationToken);
        await files.AddTerminalAsync(
            "beta", "good-conversation", "good-1", 1, "completed", "ok",
            cancellationToken);
        files.Sender.Result = request => request.ChannelId == "alpha"
            ? new ChannelSendResult(
                false,
                true,
                TimeSpan.FromHours(1),
                ChannelErrorCodes.RateLimited)
            : new ChannelSendResult(true, false);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await files.Reconciler.ReconcileOutboxAsync(
                files.RuntimeInstanceId,
                8,
                cancellationToken);
            if (attempt == 0)
            {
                Assert.Equal(600_000L, await files.ScalarAsync<long>(
                    "SELECT next_attempt_utc - updated_utc FROM channel_outbox WHERE channel_id = 'alpha';",
                    cancellationToken));
                Assert.Equal("sent", await files.ScalarAsync<string>(
                    "SELECT status FROM channel_outbox WHERE channel_id = 'beta';",
                    cancellationToken));
            }

            files.Advance(TimeSpan.FromMinutes(10));
        }

        Assert.Equal("deadLettered", await files.ScalarAsync<string>(
            "SELECT status FROM channel_outbox WHERE channel_id = 'alpha';",
            cancellationToken));
        Assert.Equal(5, files.Sender.Requests.Count(request => request.ChannelId == "alpha"));
        Assert.Equal(1, files.Sender.Requests.Count(request => request.ChannelId == "beta"));

        var outboxId = Guid.Parse(await files.ScalarAsync<string>(
            "SELECT outbox_message_id FROM channel_outbox WHERE channel_id = 'alpha';",
            cancellationToken));
        var deliveryId = Guid.Parse(await files.ScalarAsync<string>(
            "SELECT delivery_id FROM channel_outbox WHERE channel_id = 'alpha';",
            cancellationToken));
        var revision = await files.ScalarAsync<long>(
            "SELECT revision FROM channel_outbox WHERE channel_id = 'alpha';",
            cancellationToken);
        var retryKey = Guid.CreateVersion7();
        var retriedRevision = await files.Reconciler.RetryOutboxAsync(
            outboxId,
            revision,
            retryKey,
            cancellationToken);
        Assert.Equal(retriedRevision, await files.Reconciler.RetryOutboxAsync(
            outboxId,
            revision,
            retryKey,
            cancellationToken));
        var conflict = await Assert.ThrowsAsync<ChannelServiceException>(() =>
            files.Reconciler.RetryOutboxAsync(
                outboxId,
                revision,
                Guid.CreateVersion7(),
                cancellationToken));
        Assert.Equal(ChannelErrorCodes.RevisionConflict, conflict.Code);
        files.Sender.Result = _ => new ChannelSendResult(true, false);
        await files.Reconciler.ReconcileOutboxAsync(
            files.RuntimeInstanceId,
            8,
            cancellationToken);
        Assert.Equal(deliveryId, files.Sender.Requests.Last().Envelope.DeliveryId);
        Assert.Equal(retriedRevision, await files.Reconciler.RetryOutboxAsync(
            outboxId,
            revision,
            retryKey,
            cancellationToken));
    }

    [Fact]
    public async Task Same_conversation_sends_one_partition_at_a_time()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var files = await OutboxWorkspace.CreateAsync(cancellationToken);
        await files.AddTerminalAsync(
            "alpha", "same", "message-1", 1, "completed", "one",
            cancellationToken);
        await files.AddTerminalAsync(
            "alpha", "same", "message-2", 2, "completed", "two",
            cancellationToken);

        await files.Reconciler.ReconcileOutboxAsync(
            files.RuntimeInstanceId,
            8,
            cancellationToken);
        Assert.Single(files.Sender.Requests);
        Assert.Equal("message-1", files.Sender.Requests[0].Envelope.SourceMessageId);

        await files.Reconciler.ReconcileOutboxAsync(
            files.RuntimeInstanceId,
            8,
            cancellationToken);
        Assert.Equal(
            ["message-1", "message-2"],
            files.Sender.Requests.Select(request => request.Envelope.SourceMessageId));
    }

    [Fact]
    public async Task Untrusted_or_changed_channel_never_reaches_the_sender()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var files = await OutboxWorkspace.CreateAsync(cancellationToken);
        await files.AddTerminalAsync(
            "alpha", "conversation-a", "message-a", 1, "completed", "a",
            cancellationToken);
        await files.AddTerminalAsync(
            "beta", "conversation-b", "message-b", 1, "completed", "b",
            cancellationToken);
        await files.ExecuteSqlAsync(
            """
            UPDATE channels
            SET definition_sha256 =
                'cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc'
            WHERE channel_id = 'alpha';
            UPDATE channels SET trust_status = 'pending' WHERE channel_id = 'beta';
            """,
            cancellationToken);

        await files.Reconciler.ReconcileOutboxAsync(
            files.RuntimeInstanceId,
            8,
            cancellationToken);

        Assert.Empty(files.Sender.Requests);
        Assert.Equal(2L, await files.ScalarAsync<long>(
            "SELECT count(*) FROM channel_outbox WHERE status = 'pending';",
            cancellationToken));
    }

    [Fact]
    public async Task Channel_concurrency_and_minimum_interval_do_not_serialize_other_channels()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var files = await OutboxWorkspace.CreateAsync(
            cancellationToken,
            alphaMaxConcurrentSends: 1,
            alphaMinimumSendIntervalMs: 30);
        await files.AddTerminalAsync(
            "alpha", "a-1", "alpha-1", 1, "completed", "one",
            cancellationToken);
        await files.AddTerminalAsync(
            "alpha", "a-2", "alpha-2", 1, "completed", "two",
            cancellationToken);
        await files.AddTerminalAsync(
            "beta", "b-1", "beta-1", 1, "completed", "three",
            cancellationToken);
        var startedChannels = new ConcurrentDictionary<string, byte>(
            StringComparer.Ordinal);
        var twoChannelsStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        files.Sender.Handler = async (request, token) =>
        {
            if (startedChannels.TryAdd(request.ChannelId, 0) &&
                startedChannels.Count == 2)
            {
                twoChannelsStarted.TrySetResult();
            }
            await twoChannelsStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), token);
            return new ChannelSendResult(true, false);
        };

        await files.Reconciler.ReconcileOutboxAsync(
            files.RuntimeInstanceId,
            8,
            cancellationToken);

        Assert.Equal(1, files.Sender.MaximumConcurrentByChannel["alpha"]);
        Assert.True(files.Sender.MaximumConcurrentTotal > 1);
        var alphaStarts = files.Sender.StartedAt["alpha"].Order().ToArray();
        Assert.Equal(2, alphaStarts.Length);
        Assert.True(alphaStarts[1] - alphaStarts[0] >= TimeSpan.FromMilliseconds(25));
    }

    [Fact]
    public async Task Removed_session_queue_item_becomes_terminal_dead_letter()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var files = await OutboxWorkspace.CreateAsync(cancellationToken);
        await files.AddTerminalAsync(
            "alpha", "removed", "removed-1", 1, "completed", "unused",
            cancellationToken);
        await files.ExecuteSqlAsync(
            """
            UPDATE channel_inbound_messages SET turn_id = NULL;
            DELETE FROM turns;
            """,
            cancellationToken);

        await files.Reconciler.ReconcileOutboxAsync(
            files.RuntimeInstanceId,
            1,
            cancellationToken);

        Assert.Equal("deadLettered", await files.ScalarAsync<string>(
            "SELECT status FROM channel_inbound_messages;",
            cancellationToken));
        Assert.Equal(ChannelErrorCodes.TurnRemoved, await files.ScalarAsync<string>(
            "SELECT error_code FROM channel_inbound_messages;",
            cancellationToken));
        Assert.Empty(files.Sender.Requests);
    }

    [Fact]
    public async Task Oversized_terminal_text_is_explicitly_truncated_below_wire_limit()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var files = await OutboxWorkspace.CreateAsync(cancellationToken);
        await files.AddTerminalAsync(
            "alpha",
            "large",
            "large-1",
            1,
            "completed",
            new string('\u754c', 300_000),
            cancellationToken);

        await files.Reconciler.ReconcileOutboxAsync(
            files.RuntimeInstanceId,
            1,
            cancellationToken);

        var envelope = Assert.Single(files.Sender.Requests).Envelope;
        Assert.True(envelope.Truncated);
        Assert.True(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            envelope,
            new System.Text.Json.JsonSerializerOptions(
                System.Text.Json.JsonSerializerDefaults.Web)).Length <= 256 * 1024);
    }

    [Fact]
    public async Task Expired_lease_owner_cannot_overwrite_newer_send_result()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var files = await OutboxWorkspace.CreateAsync(cancellationToken);
        await files.AddTerminalAsync(
            "alpha", "lease", "lease-1", 1, "completed", "done",
            cancellationToken);
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sends = 0;
        files.Sender.Handler = async (_, token) =>
        {
            if (Interlocked.Increment(ref sends) == 1)
            {
                firstStarted.SetResult();
                await releaseFirst.Task.WaitAsync(token);
                return new ChannelSendResult(false, true);
            }
            return new ChannelSendResult(true, false);
        };

        var stale = files.Reconciler.ReconcileOutboxAsync(
            files.RuntimeInstanceId,
            1,
            cancellationToken);
        await firstStarted.Task.WaitAsync(cancellationToken);
        files.Advance(TimeSpan.FromMinutes(3));
        files.ReplaceReconciler();
        await files.Reconciler.ReconcileOutboxAsync(
            Guid.CreateVersion7(),
            1,
            cancellationToken);
        releaseFirst.SetResult();
        await stale;

        Assert.Equal("sent", await files.ScalarAsync<string>(
            "SELECT status FROM channel_outbox;",
            cancellationToken));
        Assert.Equal(2, files.Sender.Requests.Count);
    }

    private sealed class OutboxWorkspace : IAsyncDisposable
    {
        private readonly StateRuntime _state;
        private readonly GatewayConfig _config;
        private readonly ChannelCredentialService _credentials;
        private readonly MutableTimeProvider _time = new(
            DateTimeOffset.FromUnixTimeSeconds(1_786_070_400));

        private OutboxWorkspace(
            string root,
            StateRuntime state,
            GatewayConfig config,
            ChannelCredentialService credentials)
        {
            Root = root;
            _state = state;
            _config = config;
            _credentials = credentials;
            Sender = new RecordingSender();
            Reconciler = CreateReconciler();
        }

        public string Root { get; }
        public StateRuntime State => _state;
        public GatewayConfig Config => _config;
        public ChannelCredentialService Credentials => _credentials;
        public TimeProvider Time => _time;
        public RecordingSender Sender { get; }
        public GatewayReconciler Reconciler { get; private set; }
        public Guid RuntimeInstanceId { get; } = Guid.CreateVersion7();

        public static async Task<OutboxWorkspace> CreateAsync(
            CancellationToken cancellationToken,
            int alphaMaxConcurrentSends = 2,
            int alphaMinimumSendIntervalMs = 0)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"opencowork-gateway-outbox-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var paths = new OpenCoWorkPaths(root);
            var state = new StateRuntime(
                paths,
                TimeSpan.FromSeconds(2),
                [
                    .. GatewayStateMigrationContributors.Create(),
                    .. TeamsStateMigrationContributors.Create(),
                    .. AutomationsStateMigrationContributors.Create(),
                ]);
            await state.InitializeAsync(cancellationToken);
            var config = new GatewayConfig
            {
                Channels =
                [
                    Channel(
                        "alpha",
                        alphaMaxConcurrentSends,
                        alphaMinimumSendIntervalMs),
                    Channel("beta", 2, 0),
                ],
            };
            var credentials = new ChannelCredentialService(
                new InMemoryOsSecretStore(),
                new SecretRedactor([]),
                paths,
                _ => "test-secret");
            await state.WriteAsync(
                async (connection, transaction, token) =>
                {
                    foreach (var channel in config.Channels)
                    {
                        await using var command = connection.CreateCommand();
                        command.Transaction = transaction;
                        command.CommandText =
                            """
                            INSERT INTO channels (
                                channel_id, kind, enabled, definition_sha256,
                                trust_status, runtime_status, revision, created_utc,
                                updated_utc)
                            VALUES ($id, 'webhook', 1, $sha, 'trusted', 'ready', 1, 1, 1);
                            """;
                        Add(command, "$id", channel.Id);
                        Add(command, "$sha", GatewayConfig.ComputeChannelSha256(channel));
                        await command.ExecuteNonQueryAsync(token);
                    }
                    return true;
                },
                cancellationToken);
            return new OutboxWorkspace(root, state, config, credentials);
        }

        public void ReplaceReconciler(
            Action<GatewayOutboxFaultPoint>? faultInjector = null) =>
            Reconciler = CreateReconciler(faultInjector);

        public void Advance(TimeSpan duration) => _time.Advance(duration);

        public async Task AddTerminalAsync(
            string channelId,
            string conversationId,
            string messageId,
            long partitionSequence,
            string status,
            string? text,
            CancellationToken cancellationToken)
        {
            var threadId = Guid.CreateVersion7();
            var turnId = Guid.CreateVersion7();
            var correlationId = Guid.CreateVersion7();
            var inboundId = Guid.CreateVersion7();
            var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
            await _state.WriteAsync(
                async (connection, transaction, token) =>
                {
                    await ExecuteAsync(
                        connection,
                        transaction,
                        """
                        INSERT INTO threads (
                            thread_id, display_name, display_name_search, status,
                            availability, history_mode, current_sequence,
                            last_applied_sequence, created_utc, updated_utc)
                        VALUES ($threadId, 'gateway', 'gateway', 'active', 'available',
                                'server', 1, 1, $now, $now);
                        """,
                        token,
                        ("$threadId", threadId.ToString("D")),
                        ("$now", now));
                    await ExecuteAsync(
                        connection,
                        transaction,
                        """
                        INSERT INTO turns (
                            turn_id, thread_id, status, error_code, error_message,
                            created_utc, updated_utc, completed_utc,
                            effective_agent_mode, correlation_id)
                        VALUES ($turnId, $threadId, $status, $errorCode, NULL,
                                $now, $now, $now, 'agent', $correlationId);
                        """,
                        token,
                        ("$turnId", turnId.ToString("D")),
                        ("$threadId", threadId.ToString("D")),
                        ("$status", status),
                        ("$errorCode", status == "completed" ? DBNull.Value : "runtime.failed"),
                        ("$correlationId", correlationId.ToString("D")),
                        ("$now", now));
                    if (text is not null)
                    {
                        await ExecuteAsync(
                            connection,
                            transaction,
                            """
                            INSERT INTO items (
                                item_id, thread_id, turn_id, sequence, item_type, status,
                                payload_json, content_text, content_length, content_sha256,
                                created_utc, updated_utc)
                            VALUES ($itemId, $threadId, $turnId, 1, 'agentMessage',
                                    'completed', '{}', $text, length(CAST($text AS BLOB)),
                                    'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                                    $now, $now);
                            """,
                            token,
                            ("$itemId", Guid.CreateVersion7().ToString("D")),
                            ("$threadId", threadId.ToString("D")),
                            ("$turnId", turnId.ToString("D")),
                            ("$text", text),
                            ("$now", now));
                    }
                    await ExecuteAsync(
                        connection,
                        transaction,
                        """
                        INSERT INTO channel_inbound_messages (
                            inbound_message_id, channel_id, external_message_id,
                            external_conversation_id, partition_sequence, payload_json,
                            body_sha256, session_create_idempotency_key,
                            session_submit_idempotency_key, session_expected_sequence,
                            session_queue_item_id, correlation_id, thread_id, turn_id,
                            status, attempt_count, next_attempt_utc, error_code,
                            diagnostic, revision, created_utc, updated_utc, delivered_utc)
                        VALUES ($inboundId, $channelId, $messageId, $conversationId,
                                $partitionSequence, '{}',
                                'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
                                $createKey, $submitKey, 1, $queueItemId, $correlationId,
                                $threadId, $turnId, 'delivered', 1, $now, NULL, NULL,
                                1, $now, $now, $now);
                        """,
                        token,
                        ("$inboundId", inboundId.ToString("D")),
                        ("$channelId", channelId),
                        ("$messageId", messageId),
                        ("$conversationId", conversationId),
                        ("$partitionSequence", partitionSequence),
                        ("$createKey", Guid.CreateVersion7().ToString("D")),
                        ("$submitKey", Guid.CreateVersion7().ToString("D")),
                        ("$queueItemId", Guid.CreateVersion7().ToString("D")),
                        ("$correlationId", correlationId.ToString("D")),
                        ("$threadId", threadId.ToString("D")),
                        ("$turnId", turnId.ToString("D")),
                        ("$now", now));
                    return true;
                },
                cancellationToken);
        }

        public async Task<T> ScalarAsync<T>(string sql, CancellationToken cancellationToken)
        {
            await using var connection =
                await _state.OpenReadWriteConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            var value = await command.ExecuteScalarAsync(cancellationToken);
            return (T)Convert.ChangeType(
                value,
                typeof(T),
                System.Globalization.CultureInfo.InvariantCulture)!;
        }

        public async Task ExecuteSqlAsync(string sql, CancellationToken cancellationToken) =>
            _ = await _state.WriteAsync(
                async (connection, transaction, token) =>
                {
                    await ExecuteAsync(connection, transaction, sql, token);
                    return true;
                },
                cancellationToken);

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
            return ValueTask.CompletedTask;
        }

        private GatewayReconciler CreateReconciler(
            Action<GatewayOutboxFaultPoint>? faultInjector = null) =>
            new(
                _state,
                _config,
                _credentials,
                Sender,
                gateway: null,
                _time,
                faultInjector);

        private static GatewayChannelConfig Channel(
            string id,
            int maxConcurrentSends,
            int minimumSendIntervalMs) =>
            new()
            {
                Id = id,
                CallbackUrl = $"https://{id}.example.test/result",
                Credential = new GatewayCredentialConfig
                {
                    Source = GatewayCredentialSource.Environment,
                    EnvironmentVariable = "TEST_SECRET",
                },
                MaxConcurrentSends = maxConcurrentSends,
                MinimumSendIntervalMs = minimumSendIntervalMs,
            };

        private static async ValueTask ExecuteAsync(
            DbConnection connection,
            DbTransaction transaction,
            string sql,
            CancellationToken cancellationToken,
            params (string Name, object Value)[] parameters)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            foreach (var (name, value) in parameters)
            {
                Add(command, name, value);
            }
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static void Add(DbCommand command, string name, object value)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = name;
            parameter.Value = value;
            command.Parameters.Add(parameter);
        }
    }

    private sealed class RecordingSender : IChannelSender
    {
        private readonly ConcurrentQueue<ChannelSendRequest> _requests = [];
        private readonly ConcurrentDictionary<string, int> _activeByChannel = [];
        private readonly ConcurrentDictionary<string, int> _maximumByChannel = [];
        private readonly ConcurrentDictionary<string, ConcurrentQueue<TimeSpan>> _startedAt = [];
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private int _activeTotal;
        private int _maximumTotal;

        public Func<ChannelSendRequest, ChannelSendResult> Result { get; set; } =
            _ => new ChannelSendResult(true, false);
        public Func<ChannelSendRequest, CancellationToken, ValueTask<ChannelSendResult>>?
            Handler
        { get; set; }
        public IReadOnlyList<ChannelSendRequest> Requests => _requests.ToArray();
        public IReadOnlyDictionary<string, int> MaximumConcurrentByChannel =>
            _maximumByChannel;
        public int MaximumConcurrentTotal => Volatile.Read(ref _maximumTotal);
        public IReadOnlyDictionary<string, IReadOnlyList<TimeSpan>> StartedAt =>
            _startedAt.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<TimeSpan>)pair.Value.ToArray(),
                StringComparer.Ordinal);

        public async ValueTask<ChannelSendResult> SendAsync(
            ChannelSendRequest request,
            ReadOnlyMemory<byte> secret,
            CancellationToken cancellationToken = default)
        {
            Assert.False(secret.IsEmpty);
            _requests.Enqueue(request);
            _startedAt.GetOrAdd(request.ChannelId, _ => []).Enqueue(_clock.Elapsed);
            var channelActive = _activeByChannel.AddOrUpdate(
                request.ChannelId,
                1,
                (_, active) => active + 1);
            UpdateMaximum(_maximumByChannel, request.ChannelId, channelActive);
            var totalActive = Interlocked.Increment(ref _activeTotal);
            UpdateMaximum(ref _maximumTotal, totalActive);
            try
            {
                return Handler is null
                    ? Result(request)
                    : await Handler(request, cancellationToken);
            }
            finally
            {
                _activeByChannel.AddOrUpdate(
                    request.ChannelId,
                    0,
                    (_, active) => active - 1);
                Interlocked.Decrement(ref _activeTotal);
            }
        }

        private static void UpdateMaximum(
            ConcurrentDictionary<string, int> values,
            string key,
            int value) =>
            values.AddOrUpdate(key, value, (_, current) => Math.Max(current, value));

        private static void UpdateMaximum(ref int location, int value)
        {
            var current = Volatile.Read(ref location);
            while (current < value)
            {
                var observed = Interlocked.CompareExchange(ref location, value, current);
                if (observed == current)
                {
                    return;
                }
                current = observed;
            }
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }

    private sealed class InjectedCrashException : Exception;
}
