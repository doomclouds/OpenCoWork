using System.Data.Common;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using OpenCoWork.Abstractions;
using OpenCoWork.Automations;
using OpenCoWork.Core.Gateway;
using OpenCoWork.Core.State;
using OpenCoWork.Core.Workspaces;
using OpenCoWork.Teams;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class GatewayMediaStoreTests
{
    [Fact]
    public async Task Media_is_content_addressed_and_metadata_is_committed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var files = await MediaWorkspace.CreateAsync(cancellationToken);
        var contents = new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1, 2 };
        var input = new ChannelMediaInput(
            "image/png",
            "../../screenshot.png",
            Convert.ToBase64String(contents));

        var media = await files.Store.CommitAsync(
            "build-bot",
            files.InboundId,
            [input],
            cancellationToken);

        var expectedSha = Convert.ToHexString(SHA256.HashData(contents)).ToLowerInvariant();
        var item = Assert.Single(media);
        Assert.Equal(expectedSha, item.ContentSha256);
        Assert.Equal("../../screenshot.png", item.DisplayName);
        Assert.Equal(
            $"build-bot/{expectedSha[..2]}/{expectedSha}",
            item.RelativePath);
        Assert.True(File.Exists(Path.Combine(
            files.Paths.ExternalChannelMediaDirectory,
            item.RelativePath.Replace('/', Path.DirectorySeparatorChar))));
        Assert.False(File.Exists(Path.Combine(files.Root, "screenshot.png")));
        Assert.Equal(1L, await files.ScalarAsync<long>(
            "SELECT count(*) FROM channel_media;",
            cancellationToken));
    }

    [Fact]
    public async Task Media_rejects_type_spoofing_tampering_and_reparse_paths()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var files = await MediaWorkspace.CreateAsync(cancellationToken);
        var input = new ChannelMediaInput(
            "image/png",
            "fake.png",
            Convert.ToBase64String("not-png"u8));
        await Assert.ThrowsAsync<ChannelServiceException>(() =>
            files.Store.CommitAsync(
                "build-bot",
                files.InboundId,
                [input],
                cancellationToken));
        Assert.Empty(Directory.EnumerateFiles(
            files.Paths.ExternalChannelMediaDirectory,
            "*",
            SearchOption.AllDirectories));
        Assert.Equal(0L, await files.ScalarAsync<long>(
            "SELECT count(*) FROM channel_media;",
            cancellationToken));

        var validBytes = new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1 };
        var valid = input with { ContentBase64 = Convert.ToBase64String(validBytes) };
        var stored = Assert.Single(await files.Store.CommitAsync(
            "build-bot",
            files.InboundId,
            [valid],
            cancellationToken));
        var path = Path.Combine(
            files.Paths.ExternalChannelMediaDirectory,
            stored.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        await File.WriteAllTextAsync(path, "tampered", cancellationToken);
        await Assert.ThrowsAsync<ChannelServiceException>(() =>
            files.Store.CommitAsync(
                "build-bot",
                files.SecondInboundId,
                [valid],
                cancellationToken));

        if (!OperatingSystem.IsWindows())
        {
            Directory.Delete(files.Paths.ExternalChannelMediaDirectory, recursive: true);
            var outside = Directory.CreateDirectory(Path.Combine(files.Root, "outside")).FullName;
            Directory.CreateSymbolicLink(files.Paths.ExternalChannelMediaDirectory, outside);
            await Assert.ThrowsAnyAsync<IOException>(() =>
                files.Store.CommitAsync(
                    "build-bot",
                    files.SecondInboundId,
                    [valid],
                    cancellationToken));
            Assert.Empty(Directory.EnumerateFiles(outside));
        }
    }

    [Fact]
    public async Task Media_count_item_and_total_limits_leave_no_files_or_rows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var files = await MediaWorkspace.CreateAsync(cancellationToken);
        var small = new ChannelMediaInput("text/plain", "small.txt", "AA==");

        await Assert.ThrowsAsync<ChannelServiceException>(() =>
            files.Store.CommitAsync(
                "build-bot",
                files.InboundId,
                Enumerable.Repeat(small, 9).ToArray(),
                cancellationToken));
        var oversized = small with
        {
            ContentBase64 = Convert.ToBase64String(new byte[(8 * 1024 * 1024) + 1]),
        };
        await Assert.ThrowsAsync<ChannelServiceException>(() =>
            files.Store.CommitAsync(
                "build-bot",
                files.InboundId,
                [oversized],
                cancellationToken));

        var eightMiB = Convert.ToBase64String(new byte[8 * 1024 * 1024]);
        await Assert.ThrowsAsync<ChannelServiceException>(() =>
            files.Store.CommitAsync(
                "build-bot",
                files.InboundId,
                [
                    small with { ContentBase64 = eightMiB },
                    small with { ContentBase64 = eightMiB },
                    small,
                ],
                cancellationToken));

        Assert.Empty(Directory.EnumerateFiles(
            files.Paths.ExternalChannelMediaDirectory,
            "*",
            SearchOption.AllDirectories));
        Assert.Equal(0L, await files.ScalarAsync<long>(
            "SELECT count(*) FROM channel_media;",
            cancellationToken));
    }

    [Fact]
    public async Task Orphan_cleanup_deletes_only_old_unreferenced_internal_files()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var files = await MediaWorkspace.CreateAsync(cancellationToken);
        var registered = Assert.Single(await files.Store.CommitAsync(
            "build-bot",
            files.InboundId,
            [new ChannelMediaInput("text/plain", "kept.txt", "a2VwdA==")],
            cancellationToken));
        var prefix = Path.Combine(
            files.Paths.ExternalChannelMediaDirectory,
            "build-bot",
            "ff");
        Directory.CreateDirectory(prefix);
        var oldOrphan = Path.Combine(prefix, new string('f', 64));
        var recentOrphan = Path.Combine(prefix, new string('e', 64));
        await File.WriteAllTextAsync(oldOrphan, "old", cancellationToken);
        await File.WriteAllTextAsync(recentOrphan, "recent", cancellationToken);
        File.SetLastWriteTimeUtc(oldOrphan, DateTime.UtcNow.AddHours(-2));

        var removed = await files.Store.CleanupOrphansAsync(
            DateTimeOffset.UtcNow.AddHours(-1),
            cancellationToken);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(oldOrphan));
        Assert.True(File.Exists(recentOrphan));
        Assert.True(File.Exists(Path.Combine(
            files.Paths.ExternalChannelMediaDirectory,
            registered.RelativePath)));
    }

    private sealed class MediaWorkspace : IAsyncDisposable
    {
        private readonly StateRuntime _state;

        private MediaWorkspace(string root, OpenCoWorkPaths paths, StateRuntime state)
        {
            Root = root;
            Paths = paths;
            _state = state;
            Store = new GatewayMediaStore(paths, state);
        }

        public string Root { get; }
        public OpenCoWorkPaths Paths { get; }
        public GatewayMediaStore Store { get; }
        public Guid InboundId { get; } = Guid.CreateVersion7();
        public Guid SecondInboundId { get; } = Guid.CreateVersion7();

        public static async Task<MediaWorkspace> CreateAsync(
            CancellationToken cancellationToken)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"opencowork-gateway-media-{Guid.NewGuid():N}");
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
            var result = new MediaWorkspace(root, paths, state);
            await state.WriteAsync(
                async (connection, transaction, token) =>
                {
                    await ExecuteAsync(
                        connection,
                        transaction,
                        """
                        INSERT INTO channels (
                            channel_id, kind, enabled, definition_sha256,
                            trust_status, runtime_status, revision, created_utc, updated_utc)
                        VALUES (
                            'build-bot', 'webhook', 1,
                            'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                            'trusted', 'ready', 1, 1, 1);
                        """,
                        token);
                    await ExecuteAsync(
                        connection,
                        transaction,
                        InboundInsert(result.InboundId, 1),
                        token);
                    await ExecuteAsync(
                        connection,
                        transaction,
                        InboundInsert(result.SecondInboundId, 2),
                        token);
                    return true;
                },
                cancellationToken);
            return result;
        }

        public async Task<T> ScalarAsync<T>(string sql, CancellationToken cancellationToken)
        {
            await using var connection =
                await _state.OpenReadWriteConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return (T)Convert.ChangeType(
                await command.ExecuteScalarAsync(cancellationToken),
                typeof(T),
                System.Globalization.CultureInfo.InvariantCulture)!;
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root))
            {
                if (Directory.Exists(Paths.ExternalChannelMediaDirectory) &&
                    (File.GetAttributes(Paths.ExternalChannelMediaDirectory) &
                     FileAttributes.ReparsePoint) != 0)
                {
                    Directory.Delete(Paths.ExternalChannelMediaDirectory);
                }

                Directory.Delete(Root, recursive: true);
            }

            return ValueTask.CompletedTask;
        }

        private static string InboundInsert(Guid inboundId, int sequence) =>
            $$"""
              INSERT INTO channel_inbound_messages (
                  inbound_message_id, channel_id, external_message_id,
                  external_conversation_id, partition_sequence, payload_json,
                  body_sha256, session_create_idempotency_key,
                  session_submit_idempotency_key, correlation_id, status,
                  attempt_count, next_attempt_utc, revision, created_utc, updated_utc)
              VALUES (
                  '{{inboundId:D}}', 'build-bot', 'message-{{sequence}}', 'conversation-1',
                  {{sequence}}, '{}',
                  'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
                  '{{Guid.CreateVersion7():D}}', '{{Guid.CreateVersion7():D}}',
                  '{{Guid.CreateVersion7():D}}', 'pending', 0, 1, 1, 1, 1);
              """;

        private static async ValueTask ExecuteAsync(
            DbConnection connection,
            DbTransaction transaction,
            string sql,
            CancellationToken cancellationToken)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
