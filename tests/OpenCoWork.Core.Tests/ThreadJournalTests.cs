using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenCoWork.Abstractions;
using OpenCoWork.Core.Sessions;
using OpenCoWork.Core.Workspaces;
using Xunit;

namespace OpenCoWork.Core.Tests;

public sealed class ThreadJournalTests
{
    [Fact]
    public async Task Thread_created_fact_replays_frozen_workspace_and_cowork_provenance()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var journal = new ThreadJournal(files.Paths);
        var threadId = Guid.CreateVersion7();
        var runId = Guid.CreateVersion7();
        var workspace = new ExecutionWorkspaceDescriptor(
            CoWorkWorkspaceMode.Worktree,
            files.Root,
            Path.Combine(files.Root, "scratchpad"),
            Guid.CreateVersion7(),
            Path.Combine(files.Root, "worktree"),
            new string('a', 40));
        var provenance = new CoWorkThreadProvenance(
            runId,
            CoWorkAgentRunKind.MissionTask,
            MissionId: Guid.CreateVersion7(),
            MissionTaskId: Guid.CreateVersion7());

        await journal.AppendAsync(
            ThreadJournalLocation.Active,
            Draft(
                threadId,
                sequence: 1,
                new ThreadCreatedFact(
                    "worker",
                    HistoryMode.Server,
                    FirstUserMessage: null,
                    new string('b', 64),
                    ExecutionWorkspace: workspace,
                    CoWorkProvenance: provenance)),
            cancellationToken);

        var replay = await journal.ReplayAsync(
            ThreadJournalLocation.Active,
            threadId,
            cancellationToken);
        var fact = Assert.Single(replay.Entries).Payload.Deserialize<ThreadCreatedFact>(
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters =
                {
                    new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
                },
            });
        Assert.Equal(workspace, fact!.ExecutionWorkspace);
        Assert.Equal(provenance, fact.CoWorkProvenance);
    }

    [Fact]
    public async Task Append_and_replay_use_the_canonical_jsonl_contract()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var journal = new ThreadJournal(files.Paths);
        var threadId = Guid.CreateVersion7();
        var draft = Draft(
            threadId,
            sequence: 1,
            new
            {
                Zeta = "last",
                Values = new Dictionary<string, int>
                {
                    ["z"] = 2,
                    ["a"] = 1,
                },
            });

        var appended = await journal.AppendAsync(
            ThreadJournalLocation.Active,
            draft,
            cancellationToken);
        var path = journal.GetPath(ThreadJournalLocation.Active, threadId);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var json = Encoding.UTF8.GetString(bytes.AsSpan(0, bytes.Length - 1));

        Assert.Equal((byte)'\n', bytes[^1]);
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.True(json.IndexOf("\"schemaVersion\"", StringComparison.Ordinal) <
                    json.IndexOf("\"threadId\"", StringComparison.Ordinal));
        Assert.True(json.IndexOf("\"payload\"", StringComparison.Ordinal) <
                    json.IndexOf("\"checksum\"", StringComparison.Ordinal));
        Assert.True(json.IndexOf("\"a\":1", StringComparison.Ordinal) <
                    json.IndexOf("\"z\":2", StringComparison.Ordinal));
        Assert.Equal(64, appended.Checksum.Length);
        Assert.Equal(appended.Checksum, appended.Checksum.ToLowerInvariant());
        using (var document = JsonDocument.Parse(json))
        {
            Assert.Equal(
                [
                    "schemaVersion",
                    "threadId",
                    "sequence",
                    "entryId",
                    "timestamp",
                    "entryType",
                    "idempotencyKey",
                    "payload",
                    "checksum",
                ],
                document.RootElement.EnumerateObject()
                    .Select(property => property.Name)
                    .ToArray());
        }

        var checksumProperty = json.LastIndexOf(",\"checksum\"", StringComparison.Ordinal);
        var unsigned = Encoding.UTF8.GetBytes(json[..checksumProperty] + "}");
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(unsigned)).ToLowerInvariant(),
            appended.Checksum);

        var replay = await journal.ReplayAsync(
            ThreadJournalLocation.Active,
            threadId,
            cancellationToken);

        Assert.Equal(ThreadJournalHealth.Healthy, replay.Health);
        var entry = Assert.Single(replay.Entries);
        Assert.Equal(draft.EntryId, entry.EntryId);
        Assert.Equal("last", entry.Payload.GetProperty("zeta").GetString());
        Assert.Equal(1, entry.Payload.GetProperty("values").GetProperty("a").GetInt32());
    }

    [Fact]
    public async Task Oversized_payloads_are_rejected_before_the_journal_or_sequence_exists()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var journal = new ThreadJournal(files.Paths);
        var threadId = Guid.CreateVersion7();

        var textError = await Assert.ThrowsAsync<ThreadJournalException>(
            () => journal.AppendAsync(
                ThreadJournalLocation.Active,
                Draft(
                    threadId,
                    sequence: 1,
                    new TextItemContent(new string('x', 256 * 1024 + 1))),
                cancellationToken));
        Assert.Equal(SessionErrorCodes.JournalEntryTooLarge, textError.Code);

        var entryError = await Assert.ThrowsAsync<ThreadJournalException>(
            () => journal.AppendAsync(
                ThreadJournalLocation.Active,
                Draft(
                    threadId,
                    sequence: 1,
                    new
                    {
                        Values = Enumerable.Repeat("small", 180_000).ToArray(),
                    }),
                cancellationToken));
        Assert.Equal(SessionErrorCodes.JournalEntryTooLarge, entryError.Code);
        Assert.False(File.Exists(
            journal.GetPath(ThreadJournalLocation.Active, threadId)));

        await journal.AppendAsync(
            ThreadJournalLocation.Active,
            Draft(threadId, sequence: 1, new { Text = "fits" }),
            cancellationToken);
        var replay = await journal.ReplayAsync(
            ThreadJournalLocation.Active,
            threadId,
            cancellationToken);
        Assert.Equal(1, Assert.Single(replay.Entries).Sequence);
    }

    [Fact]
    public async Task Lf_terminated_checksum_corruption_is_backed_up_and_isolated()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var journal = new ThreadJournal(files.Paths);
        var threadId = Guid.CreateVersion7();
        await journal.AppendAsync(
            ThreadJournalLocation.Active,
            Draft(threadId, sequence: 1, new { Text = "committed" }),
            cancellationToken);
        var path = journal.GetPath(ThreadJournalLocation.Active, threadId);
        var original = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken);
        var marker = "\"checksum\":\"";
        var checksumStart =
            original.LastIndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var tamperedCharacters = original.ToCharArray();
        tamperedCharacters[checksumStart] =
            original[checksumStart] == '0' ? '1' : '0';
        var tampered = new string(tamperedCharacters);
        await File.WriteAllTextAsync(path, tampered, new UTF8Encoding(false), cancellationToken);

        var replay = await journal.ReplayAsync(
            ThreadJournalLocation.Active,
            threadId,
            cancellationToken);

        Assert.Equal(ThreadJournalHealth.RecoveryRequired, replay.Health);
        Assert.Equal(SessionErrorCodes.JournalCorrupt, replay.DiagnosticCode);
        Assert.NotNull(replay.BackupPath);
        Assert.True(File.Exists(replay.BackupPath));
        Assert.Equal(
            tampered,
            await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken));
    }

    [Fact]
    public async Task Incomplete_tail_uses_recovery_intent_and_resumes_after_interruption()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var threadId = Guid.CreateVersion7();
        var journal = new ThreadJournal(files.Paths);
        await journal.AppendAsync(
            ThreadJournalLocation.Active,
            Draft(threadId, sequence: 1, new { Text = "committed" }),
            cancellationToken);
        var path = journal.GetPath(ThreadJournalLocation.Active, threadId);
        await using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write))
        {
            await stream.WriteAsync(
                "{\"schemaVersion\":1"u8.ToArray(),
                cancellationToken);
            stream.Flush(flushToDisk: true);
        }

        var interrupted = new ThreadJournal(
            files.Paths,
            point =>
            {
                if (point == ThreadJournalFaultPoint.AfterRecoveryTruncate)
                {
                    throw new InjectedJournalFaultException();
                }
            });
        await Assert.ThrowsAsync<InjectedJournalFaultException>(
            () => interrupted.ReplayAsync(
                ThreadJournalLocation.Active,
                threadId,
                cancellationToken));
        Assert.Single(Directory.EnumerateFiles(
            files.Paths.ThreadRecoveryDirectory,
            "*.intent.json",
            SearchOption.AllDirectories));

        var resumed = await new ThreadJournal(files.Paths).ReplayAsync(
            ThreadJournalLocation.Active,
            threadId,
            cancellationToken);

        Assert.Equal(ThreadJournalHealth.Repaired, resumed.Health);
        Assert.Equal(2, resumed.Entries.Count);
        Assert.Equal(
            SessionEventType.ThreadJournalRecovered,
            resumed.Entries[^1].EntryType);
        Assert.Empty(Directory.EnumerateFiles(
            files.Paths.ThreadRecoveryDirectory,
            "*.intent.json",
            SearchOption.AllDirectories));
        Assert.Single(Directory.EnumerateFiles(
            files.Paths.ThreadRecoveryDirectory,
            "*.backup",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Sequence_gap_and_thread_filename_mismatch_are_never_repaired()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var journal = new ThreadJournal(files.Paths);
        var threadId = Guid.CreateVersion7();
        await journal.AppendAsync(
            ThreadJournalLocation.Active,
            Draft(threadId, sequence: 1, new { Text = "one" }),
            cancellationToken);
        await journal.AppendAsync(
            ThreadJournalLocation.Active,
            Draft(threadId, sequence: 3, new { Text = "gap" }),
            cancellationToken);

        var gap = await journal.ReplayAsync(
            ThreadJournalLocation.Active,
            threadId,
            cancellationToken);
        Assert.Equal(ThreadJournalHealth.RecoveryRequired, gap.Health);

        var otherThreadId = Guid.CreateVersion7();
        var otherPath = journal.GetPath(ThreadJournalLocation.Active, otherThreadId);
        File.Move(
            journal.GetPath(ThreadJournalLocation.Active, threadId),
            otherPath);
        var mismatch = await journal.ReplayAsync(
            ThreadJournalLocation.Active,
            otherThreadId,
            cancellationToken);
        Assert.Equal(ThreadJournalHealth.RecoveryRequired, mismatch.Health);
    }

    [Fact]
    public async Task Unknown_schema_is_rejected_even_with_a_valid_checksum()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var journal = new ThreadJournal(files.Paths);
        var threadId = Guid.CreateVersion7();
        await journal.AppendAsync(
            ThreadJournalLocation.Active,
            Draft(threadId, sequence: 1, new { Text = "fact" }),
            cancellationToken);
        var path = journal.GetPath(ThreadJournalLocation.Active, threadId);
        var json = (await File.ReadAllTextAsync(
            path,
            Encoding.UTF8,
            cancellationToken)).TrimEnd('\n');
        json = json.Replace(
            "\"schemaVersion\":1",
            "\"schemaVersion\":2",
            StringComparison.Ordinal);
        var checksumProperty = json.LastIndexOf(",\"checksum\"", StringComparison.Ordinal);
        var checksumStart =
            json.IndexOf("\"checksum\":\"", StringComparison.Ordinal) +
            "\"checksum\":\"".Length;
        var checksum = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(json[..checksumProperty] + "}")))
            .ToLowerInvariant();
        json = json[..checksumStart] + checksum + json[(checksumStart + 64)..];
        await File.WriteAllTextAsync(
            path,
            json + "\n",
            new UTF8Encoding(false),
            cancellationToken);

        var replay = await journal.ReplayAsync(
            ThreadJournalLocation.Active,
            threadId,
            cancellationToken);

        Assert.Equal(ThreadJournalHealth.RecoveryRequired, replay.Health);
        Assert.Equal(SessionErrorCodes.JournalUnsupportedSchema, replay.DiagnosticCode);
    }

    [Theory]
    [InlineData((int)ThreadJournalFaultPoint.BeforeWrite, false)]
    [InlineData((int)ThreadJournalFaultPoint.HalfLineWritten, false)]
    [InlineData((int)ThreadJournalFaultPoint.BeforeFlush, true)]
    [InlineData((int)ThreadJournalFaultPoint.AfterFlushBeforeMemory, true)]
    public async Task Write_fault_points_preserve_the_commit_boundary(
        int faultPointValue,
        bool entryIsRecoverable)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var threadId = Guid.CreateVersion7();
        var faultPoint = (ThreadJournalFaultPoint)faultPointValue;
        var faulted = new ThreadJournal(
            files.Paths,
            point =>
            {
                if (point == faultPoint)
                {
                    throw new InjectedJournalFaultException();
                }
            });

        if (faultPoint == ThreadJournalFaultPoint.AfterFlushBeforeMemory)
        {
            var committed = await Assert.ThrowsAsync<ThreadJournalCommittedException>(
                () => faulted.AppendAsync(
                    ThreadJournalLocation.Active,
                    Draft(threadId, sequence: 1, new { Text = "fact" }),
                    cancellationToken));
            Assert.Equal(1, committed.Entry.Sequence);
        }
        else
        {
            await Assert.ThrowsAsync<InjectedJournalFaultException>(
                () => faulted.AppendAsync(
                    ThreadJournalLocation.Active,
                    Draft(threadId, sequence: 1, new { Text = "fact" }),
                    cancellationToken));
        }
        var path = faulted.GetPath(ThreadJournalLocation.Active, threadId);

        if (faultPoint == ThreadJournalFaultPoint.BeforeWrite)
        {
            Assert.False(File.Exists(path));
            return;
        }

        var replay = await new ThreadJournal(files.Paths).ReplayAsync(
            ThreadJournalLocation.Active,
            threadId,
            cancellationToken);
        Assert.Equal(entryIsRecoverable ? 1 : 0, replay.Entries.Count);
    }

    [Fact]
    public async Task Invalid_recovery_intent_is_isolated_to_its_thread()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        var threadId = Guid.CreateVersion7();
        var journal = new ThreadJournal(files.Paths);
        await journal.AppendAsync(
            ThreadJournalLocation.Active,
            Draft(threadId, sequence: 1, new { Text = "committed" }),
            cancellationToken);
        var path = journal.GetPath(ThreadJournalLocation.Active, threadId);
        await File.AppendAllTextAsync(
            path,
            "{\"schemaVersion\":1",
            new UTF8Encoding(false),
            cancellationToken);

        var interrupted = new ThreadJournal(
            files.Paths,
            point =>
            {
                if (point == ThreadJournalFaultPoint.AfterRecoveryTruncate)
                {
                    throw new InjectedJournalFaultException();
                }
            });
        await Assert.ThrowsAsync<InjectedJournalFaultException>(
            () => interrupted.ReplayAsync(
                ThreadJournalLocation.Active,
                threadId,
                cancellationToken));
        var intentPath = Assert.Single(Directory.EnumerateFiles(
            files.Paths.ThreadRecoveryDirectory,
            "*.intent.json",
            SearchOption.AllDirectories));
        await File.WriteAllTextAsync(
            intentPath,
            "{}",
            new UTF8Encoding(false),
            cancellationToken);

        var replay = await journal.ReplayAsync(
            ThreadJournalLocation.Active,
            threadId,
            cancellationToken);

        Assert.Equal(ThreadJournalHealth.RecoveryRequired, replay.Health);
        Assert.Equal(SessionErrorCodes.JournalCorrupt, replay.DiagnosticCode);
    }

    [Fact]
    public async Task Journal_write_rejects_a_thread_directory_link_outside_the_workspace()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var files = new TempWorkspace();
        Directory.CreateDirectory(files.Paths.ThreadsDirectory);
        var outside = $"{files.Root}-outside";
        Directory.CreateDirectory(outside);
        CreateDirectoryLink(files.Paths.ActiveThreadsDirectory, outside);

        try
        {
            var journal = new ThreadJournal(files.Paths);
            await Assert.ThrowsAsync<WorkspacePathEscapeException>(
                () => journal.AppendAsync(
                    ThreadJournalLocation.Active,
                    Draft(Guid.CreateVersion7(), sequence: 1, new { Text = "blocked" }),
                    cancellationToken));
            Assert.Empty(Directory.EnumerateFiles(outside));
        }
        finally
        {
            Directory.Delete(files.Paths.ActiveThreadsDirectory);
            Directory.Delete(outside);
        }
    }

    private static ThreadJournalDraft Draft(
        Guid threadId,
        long sequence,
        object payload) =>
        new(
            threadId,
            sequence,
            Guid.CreateVersion7(),
            new DateTimeOffset(2026, 7, 26, 8, 30, 0, TimeSpan.Zero),
            SessionEventType.ThreadCreated,
            Guid.CreateVersion7(),
            payload);

    private sealed class TempWorkspace : IDisposable
    {
        public TempWorkspace()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"opencowork-journal-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            Paths = new OpenCoWorkPaths(Root);
        }

        public string Root { get; }

        public OpenCoWorkPaths Paths { get; }

        public void Dispose()
        {
            Directory.Delete(Root, recursive: true);
        }
    }

    private static void CreateDirectoryLink(string path, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(path, target);
            return;
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/d /c mklink /J \"{path}\" \"{target}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("Could not start mklink.");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new IOException(process.StandardError.ReadToEnd());
        }
    }

    private sealed class InjectedJournalFaultException : Exception;
}
