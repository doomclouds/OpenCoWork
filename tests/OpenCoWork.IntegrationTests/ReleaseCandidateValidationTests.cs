using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenCoWork.App;
using OpenCoWork.Core.Tools;
using Xunit;

namespace OpenCoWork.IntegrationTests;

public sealed class ReleaseCandidateValidationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ReleaseCandidate_short_soak_runs_protocol_mcp_lsp_and_cleans_up()
    {
        var app = Environment.GetEnvironmentVariable("OPENCOWORK_RC_APP_PATH") ??
                  Path.Combine(
                      Path.GetDirectoryName(typeof(OpenCoWorkCli).Assembly.Location)!,
                      OperatingSystem.IsWindows() ? "opencowork.exe" : "opencowork");
        Assert.True(File.Exists(app), $"Release candidate App was not found: {app}");
        var duration = ReleaseCandidateSoakRunner.ReadDuration(
            "OPENCOWORK_SOAK_DURATION",
            TimeSpan.Zero,
            TimeSpan.FromHours(2));
        var phaseTimeout = ReleaseCandidateSoakRunner.ReadDuration(
            "OPENCOWORK_SOAK_PHASE_TIMEOUT",
            TimeSpan.FromMinutes(2),
            TimeSpan.FromMinutes(10));
        var runner = new ReleaseCandidateSoakRunner(TimeProvider.System);

        var report = await runner.RunAsync(
            duration,
            phaseTimeout,
            [
                new ReleaseCandidatePhase(
                    "cli-wire-acp-automation-cowork-gateway-hub-operations",
                    async token =>
                    {
                        token.ThrowIfCancellationRequested();
                        if (await ProtocolTestClient.RunAsync(["--server", app]) != 0)
                        {
                            throw new ReleaseCandidatePhaseException();
                        }
                    }),
                new ReleaseCandidatePhase(
                    "mcp-fixture",
                    _ => new McpCapabilityIntegrationTests()
                        .Stdio_process_honors_cancel_sanitizes_errors_and_is_killed_on_stop()),
                new ReleaseCandidatePhase(
                    "lsp-fixture",
                    _ => new LspCapabilityIntegrationTests()
                        .External_file_uri_is_rejected_and_restart_advances_generation()),
            ],
            TestContext.Current.CancellationToken);

        await ReleaseValidationOutput.WriteAsync(
            "release-candidate-soak.json",
            report,
            output,
            TestContext.Current.CancellationToken);
        Assert.True(report.Passed, string.Join(',', report.ErrorCodes));
        Assert.True(report.CompletedIterations >= 1);
        Assert.Equal(3, report.Phases.Count);
        Assert.Equal(0, report.FinalResources.ChildProcessCount);
        Assert.Equal(0, report.FinalResources.WalBytes);
    }

    [Fact]
    public async Task Soak_timeout_is_hard_failure_and_cleanup_still_runs()
    {
        var cleanupCount = 0;
        var report = await new ReleaseCandidateSoakRunner(TimeProvider.System)
            .RunAsync(
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(20),
                [new ReleaseCandidatePhase(
                    "timeout",
                    token => Task.Delay(Timeout.InfiniteTimeSpan, token))],
                TestContext.Current.CancellationToken,
                cleanup: () =>
                {
                    cleanupCount++;
                    return ValueTask.CompletedTask;
                });

        Assert.False(report.Passed);
        Assert.Contains("phase.timeout", report.ErrorCodes);
        Assert.Equal(1, cleanupCount);
    }

    [Fact]
    public async Task Soak_cancellation_is_reported_and_cleanup_still_runs()
    {
        using var cancellation = new CancellationTokenSource();
        var cleanupCount = 0;
        var report = await new ReleaseCandidateSoakRunner(TimeProvider.System)
            .RunAsync(
                TimeSpan.FromMinutes(1),
                TimeSpan.FromMinutes(1),
                [new ReleaseCandidatePhase(
                    "cancel",
                    _ =>
                    {
                        cancellation.Cancel();
                        return Task.FromCanceled(cancellation.Token);
                    })],
                cancellation.Token,
                cleanup: () =>
                {
                    cleanupCount++;
                    return ValueTask.CompletedTask;
                });

        Assert.False(report.Passed);
        Assert.Contains("runner.cancelled", report.ErrorCodes);
        Assert.Equal(1, cleanupCount);
    }

    [Fact]
    public void ReleaseCandidate_baseline_rejects_only_comparable_phases_over_two_times()
    {
        var environment = ReleaseValidationEnvironment.Create() with
        {
            MachineIdSha256 = new string('a', 64),
        };
        var baseline = new ReleaseCandidateReport(
            1,
            "soak",
            Passed: true,
            environment,
            DateTimeOffset.UnixEpoch,
            100,
            0,
            1,
            0,
            [],
            [new ReleaseCandidatePhaseReport("phase", 1, 1, 0, 100, 100)],
            [],
            ReleaseResourceSample.Empty,
            new ReleaseCandidateBaseline(false, true, 2, []));
        var current = baseline with
        {
            Phases = [new ReleaseCandidatePhaseReport("phase", 1, 1, 0, 201, 201)],
        };

        Assert.Equal(
            ["phase"],
            ReleaseCandidateSoakRunner.CompareBaseline(current, baseline).ExceededPhases);
        Assert.Empty(
            ReleaseCandidateSoakRunner.CompareBaseline(
                current,
                baseline with
                {
                    Environment = environment with
                    {
                        ProcessorCount = environment.ProcessorCount + 1,
                    },
                }).ExceededPhases);
    }
}

internal sealed class ReleaseCandidateSoakRunner(TimeProvider timeProvider)
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(30);

    public async Task<ReleaseCandidateReport> RunAsync(
        TimeSpan duration,
        TimeSpan phaseTimeout,
        IReadOnlyList<ReleaseCandidatePhase> phases,
        CancellationToken cancellationToken,
        Func<ValueTask>? cleanup = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            phaseTimeout,
            TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            duration,
            TimeSpan.FromHours(2));
        ArgumentNullException.ThrowIfNull(phases);
        if (phases.Count == 0)
        {
            throw new ArgumentException("At least one phase is required.", nameof(phases));
        }

        var startedAt = timeProvider.GetUtcNow();
        var elapsed = Stopwatch.StartNew();
        var samples = new ConcurrentQueue<ReleaseResourceSample>();
        using var sampling = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        var sampler = SampleAsync(elapsed, samples, sampling.Token);
        var accumulators = phases.ToDictionary(
            phase => phase.Name,
            phase => new PhaseAccumulator(phase.Name),
            StringComparer.Ordinal);
        var errors = new List<string>();
        var sqliteBusy = 0;
        var iterations = 0;
        try
        {
            do
            {
                foreach (var phase in phases)
                {
                    var phaseWatch = Stopwatch.StartNew();
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);
                    timeout.CancelAfter(phaseTimeout);
                    try
                    {
                        await phase.Execute(timeout.Token);
                        accumulators[phase.Name].Complete(phaseWatch.ElapsedMilliseconds);
                    }
                    catch (OperationCanceledException) when (
                        !cancellationToken.IsCancellationRequested &&
                        timeout.IsCancellationRequested)
                    {
                        accumulators[phase.Name].Fail(phaseWatch.ElapsedMilliseconds);
                        errors.Add("phase.timeout");
                        break;
                    }
                    catch (OperationCanceledException) when (
                        cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (SqliteException exception) when (
                        exception.SqliteErrorCode is 5 or 6)
                    {
                        sqliteBusy++;
                        accumulators[phase.Name].Fail(phaseWatch.ElapsedMilliseconds);
                        errors.Add("sqlite.busy");
                        break;
                    }
                    catch (Exception)
                    {
                        accumulators[phase.Name].Fail(phaseWatch.ElapsedMilliseconds);
                        errors.Add("phase.failed");
                        break;
                    }
                }

                if (errors.Count == 0)
                {
                    iterations++;
                }
            }
            while (errors.Count == 0 && elapsed.Elapsed < duration);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            errors.Add("runner.cancelled");
        }
        finally
        {
            sampling.Cancel();
            try
            {
                await sampler;
            }
            catch (OperationCanceledException)
            {
            }

            if (cleanup is not null)
            {
                try
                {
                    await cleanup();
                }
                catch (Exception)
                {
                    errors.Add("cleanup.failed");
                }
            }
        }

        var finalResources = await ReleaseResourceSample.CaptureAsync(
            elapsed.ElapsedMilliseconds);
        var resourceSamples = samples.ToArray();
        errors.AddRange(ResourceErrors(resourceSamples, finalResources));
        var report = new ReleaseCandidateReport(
            1,
            "soak",
            errors.Count == 0,
            ReleaseValidationEnvironment.Create(),
            startedAt,
            elapsed.ElapsedMilliseconds,
            (long)duration.TotalMilliseconds,
            iterations,
            sqliteBusy,
            errors.Distinct(StringComparer.Ordinal).ToArray(),
            accumulators.Values.Select(value => value.ToReport()).ToArray(),
            resourceSamples,
            finalResources,
            new ReleaseCandidateBaseline(false, false, 2, []));
        var baselinePath = Environment.GetEnvironmentVariable(
            "OPENCOWORK_RC_BASELINE_REPORT");
        if (string.IsNullOrWhiteSpace(baselinePath))
        {
            return report;
        }

        var baseline = JsonSerializer.Deserialize<ReleaseCandidateReport>(
            await File.ReadAllTextAsync(baselinePath, cancellationToken),
            ReleaseValidationOutput.JsonOptions)
            ?? throw new InvalidDataException("Release candidate baseline is invalid.");
        var comparison = CompareBaseline(report, baseline);
        return report with
        {
            Passed = report.Passed && comparison.ExceededPhases.Count == 0,
            ErrorCodes = comparison.ExceededPhases.Count == 0
                ? report.ErrorCodes
                : [.. report.ErrorCodes, "baseline.exceeded"],
            Baseline = comparison,
        };
    }

    public static TimeSpan ReadDuration(
        string environmentVariable,
        TimeSpan fallback,
        TimeSpan maximum)
    {
        var value = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }
        if (!TimeSpan.TryParse(
                value,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed) ||
            parsed <= TimeSpan.Zero ||
            parsed > maximum)
        {
            throw new InvalidOperationException(
                $"{environmentVariable} must be a positive duration no greater than {maximum}.");
        }
        return parsed;
    }

    public static ReleaseCandidateBaseline CompareBaseline(
        ReleaseCandidateReport current,
        ReleaseCandidateReport baseline)
    {
        var comparable =
            current.Environment.MachineIdSha256 is not null &&
            string.Equals(
                current.Environment.MachineIdSha256,
                baseline.Environment.MachineIdSha256,
                StringComparison.Ordinal) &&
            current.Environment.RuntimeIdentifier == baseline.Environment.RuntimeIdentifier &&
            current.Environment.OsDescription == baseline.Environment.OsDescription &&
            current.Environment.Architecture == baseline.Environment.Architecture &&
            current.Environment.FrameworkDescription ==
            baseline.Environment.FrameworkDescription &&
            current.Environment.ProcessorCount == baseline.Environment.ProcessorCount;
        if (!comparable)
        {
            return new ReleaseCandidateBaseline(true, false, 2, []);
        }

        var baselinePhases = baseline.Phases.ToDictionary(
            phase => phase.Name,
            StringComparer.Ordinal);
        var exceeded = current.Phases
            .Where(phase =>
                baselinePhases.TryGetValue(phase.Name, out var previous) &&
                previous.AverageElapsedMilliseconds > 0 &&
                phase.AverageElapsedMilliseconds >
                previous.AverageElapsedMilliseconds * 2)
            .Select(phase => phase.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new ReleaseCandidateBaseline(true, true, 2, exceeded);
    }

    private static async Task SampleAsync(
        Stopwatch elapsed,
        ConcurrentQueue<ReleaseResourceSample> samples,
        CancellationToken cancellationToken)
    {
        samples.Enqueue(await ReleaseResourceSample.CaptureAsync(
            elapsed.ElapsedMilliseconds));
        using var timer = new PeriodicTimer(SampleInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            samples.Enqueue(await ReleaseResourceSample.CaptureAsync(
                elapsed.ElapsedMilliseconds));
        }
    }

    private static IEnumerable<string> ResourceErrors(
        IReadOnlyList<ReleaseResourceSample> samples,
        ReleaseResourceSample final)
    {
        if (final.ChildProcessCount != 0)
        {
            yield return "resource.childProcessLeak";
        }
        if (final.WalBytes != 0)
        {
            yield return "resource.walLeak";
        }
        var all = samples.Append(final).ToArray();
        if (GrowsMonotonically(
                all.Select(sample => sample.ManagedMemoryBytes),
                64L * 1024 * 1024))
        {
            yield return "resource.memoryGrowth";
        }
        if (GrowsMonotonically(
                all.Select(sample => (long)sample.HandleOrDescriptorCount),
                32))
        {
            yield return "resource.handleGrowth";
        }
        if (GrowsMonotonically(
                all.Select(sample => (long)sample.ThreadCount),
                8))
        {
            yield return "resource.threadGrowth";
        }
    }

    private static bool GrowsMonotonically(
        IEnumerable<long> source,
        long minimumGrowth)
    {
        var values = source.ToArray();
        return values.Length >= 5 &&
               values[^1] - values[0] > minimumGrowth &&
               values.Zip(values.Skip(1)).All(pair => pair.Second > pair.First);
    }

    private sealed class PhaseAccumulator(string name)
    {
        private int _attempted;
        private int _completed;
        private int _failed;
        private long _totalMilliseconds;
        private long _maximumMilliseconds;

        public void Complete(long elapsedMilliseconds)
        {
            _attempted++;
            _completed++;
            Record(elapsedMilliseconds);
        }

        public void Fail(long elapsedMilliseconds)
        {
            _attempted++;
            _failed++;
            Record(elapsedMilliseconds);
        }

        public ReleaseCandidatePhaseReport ToReport() => new(
            name,
            _attempted,
            _completed,
            _failed,
            _attempted == 0 ? 0 : _totalMilliseconds / _attempted,
            _maximumMilliseconds);

        private void Record(long elapsedMilliseconds)
        {
            _totalMilliseconds += elapsedMilliseconds;
            _maximumMilliseconds = Math.Max(_maximumMilliseconds, elapsedMilliseconds);
        }
    }
}

internal sealed record ReleaseCandidatePhase(
    string Name,
    Func<CancellationToken, Task> Execute);

internal sealed record ReleaseCandidateReport(
    int SchemaVersion,
    string Kind,
    bool Passed,
    ReleaseValidationEnvironment Environment,
    DateTimeOffset StartedAtUtc,
    long ElapsedMilliseconds,
    long TargetDurationMilliseconds,
    int CompletedIterations,
    int SqliteBusyCount,
    IReadOnlyList<string> ErrorCodes,
    IReadOnlyList<ReleaseCandidatePhaseReport> Phases,
    IReadOnlyList<ReleaseResourceSample> ResourceSamples,
    ReleaseResourceSample FinalResources,
    ReleaseCandidateBaseline Baseline);

internal sealed record ReleaseCandidatePhaseReport(
    string Name,
    int Attempted,
    int Completed,
    int Failed,
    long AverageElapsedMilliseconds,
    long MaximumElapsedMilliseconds);

internal sealed record ReleaseCandidateBaseline(
    bool Applied,
    bool Comparable,
    int LimitMultiplier,
    IReadOnlyList<string> ExceededPhases);

internal sealed record ReleaseValidationEnvironment(
    string ProductVersion,
    string? ReleaseSourceCommit,
    string RuntimeIdentifier,
    string OsDescription,
    string Architecture,
    string FrameworkDescription,
    int ProcessorCount,
    string? MachineIdSha256)
{
    public static ReleaseValidationEnvironment Create()
    {
        var commit = Environment.GetEnvironmentVariable(
            "OPENCOWORK_RELEASE_SOURCE_COMMIT");
        if (!string.IsNullOrWhiteSpace(commit) &&
            (commit.Length != 40 || !commit.All(Uri.IsHexDigit)))
        {
            throw new InvalidOperationException(
                "OPENCOWORK_RELEASE_SOURCE_COMMIT must be a full Git SHA.");
        }
        var version = typeof(OpenCoWorkCli).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0]
            ?? throw new InvalidOperationException("Product version is unavailable.");
        var machineId = Environment.GetEnvironmentVariable(
            "OPENCOWORK_VALIDATION_MACHINE_ID");
        return new ReleaseValidationEnvironment(
            version,
            commit?.ToLowerInvariant(),
            RuntimeInformation.RuntimeIdentifier,
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription,
            Environment.ProcessorCount,
            string.IsNullOrWhiteSpace(machineId)
                ? null
                : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(machineId)))
                    .ToLowerInvariant());
    }
}

internal sealed record ReleaseResourceSample(
    long ElapsedMilliseconds,
    long ManagedMemoryBytes,
    long WorkingSetBytes,
    int HandleOrDescriptorCount,
    int ThreadCount,
    int ChildProcessCount,
    long WalBytes)
{
    public static ReleaseResourceSample Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);

    public static async Task<ReleaseResourceSample> CaptureAsync(long elapsedMilliseconds)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return new ReleaseResourceSample(
            elapsedMilliseconds,
            GC.GetTotalMemory(forceFullCollection: false),
            process.WorkingSet64,
            CountHandlesOrDescriptors(process),
            process.Threads.Count,
            await CountDescendantsAsync(process.Id),
            0);
    }

    private static async Task<int> CountDescendantsAsync(int rootProcessId)
    {
        ProcessStartInfo start;
        if (OperatingSystem.IsWindows())
        {
            start = new ProcessStartInfo
            {
                FileName = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "WindowsPowerShell",
                    "v1.0",
                    "powershell.exe"),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add(
                "Get-CimInstance Win32_Process | ForEach-Object { " +
                "\"$($_.ProcessId) $($_.ParentProcessId)\" }");
        }
        else
        {
            start = new ProcessStartInfo
            {
                FileName = "/bin/ps",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("-A");
            start.ArgumentList.Add("-o");
            start.ArgumentList.Add("pid=");
            start.ArgumentList.Add("-o");
            start.ArgumentList.Add("ppid=");
        }

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Process metrics could not start.");
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("Process metrics failed.");
        }

        var children = new Dictionary<int, List<int>>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var values = line.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (values.Length != 2 ||
                !int.TryParse(values[0], out var processId) ||
                !int.TryParse(values[1], out var parentProcessId) ||
                processId == process.Id)
            {
                continue;
            }
            if (!children.TryGetValue(parentProcessId, out var list))
            {
                list = [];
                children.Add(parentProcessId, list);
            }
            list.Add(processId);
        }

        var descendants = new HashSet<int>();
        var pending = new Stack<int>();
        pending.Push(rootProcessId);
        while (pending.TryPop(out var parent) && children.TryGetValue(parent, out var direct))
        {
            foreach (var child in direct)
            {
                if (descendants.Add(child))
                {
                    pending.Push(child);
                }
            }
        }
        return descendants.Count;
    }

    private static int CountHandlesOrDescriptors(Process process)
    {
        if (OperatingSystem.IsWindows())
        {
            return process.HandleCount;
        }

        var path = OperatingSystem.IsLinux() ? "/proc/self/fd" : "/dev/fd";
        return Directory.EnumerateFileSystemEntries(path).Count();
    }
}

internal static class ReleaseValidationOutput
{
    private static readonly UTF8Encoding Utf8 = new(false);
    internal static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static async Task WriteAsync(
        string fileName,
        object report,
        ITestOutputHelper output,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(report, JsonOptions);
        using var reportDocument = JsonDocument.Parse(json);
        using var schemaDocument = JsonDocument.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "Snapshots",
                    "release-validation-report.schema.json"),
                cancellationToken));
        if (!new JsonSchemaValidationService().Evaluate(
                schemaDocument.RootElement,
                reportDocument.RootElement))
        {
            throw new InvalidDataException("Validation report does not match schema v1.");
        }
        var canary = Environment.GetEnvironmentVariable(
            "OPENCOWORK_VALIDATION_SECRET_CANARY");
        if (!string.IsNullOrEmpty(canary) &&
            json.Contains(canary, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Validation report contains the secret canary.");
        }
        output.WriteLine(json);
        var directory = Environment.GetEnvironmentVariable(
            "OPENCOWORK_VALIDATION_REPORT_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }
        var root = Path.GetFullPath(directory);
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(
            Path.Combine(root, fileName),
            json + Environment.NewLine,
            Utf8,
            cancellationToken);
    }
}

internal sealed class ReleaseCandidatePhaseException : Exception;
