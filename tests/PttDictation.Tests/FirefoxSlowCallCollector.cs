using System.Diagnostics;
using System.Text.Json;
using PttDictation.App;

namespace PttDictation.Tests;

// Temporary opt-in diagnostics: one capture per editor case, entirely outside
// the provider/UI apartments. Disabled for ordinary acceptance runs.
internal sealed class FirefoxSlowCallCollector : IAsyncDisposable
{
    private static readonly HashSet<string> FocusOperations = new(StringComparer.Ordinal)
    {
        "focus.original_element", "focus.compare_original",
        "preparation.focused_element", "preparation.original_element"
    };
    private readonly Process _worker;
    private readonly Task<string> _stdout;
    private readonly Task<string> _stderr;
    private readonly string _requestPath;
    private readonly string _statusPath;
    private int _triggered;

    private FirefoxSlowCallCollector(Process worker, string requestPath, string statusPath)
    {
        _worker = worker;
        _requestPath = requestPath;
        _statusPath = statusPath;
        _stdout = worker.StandardOutput.ReadToEndAsync();
        _stderr = worker.StandardError.ReadToEndAsync();
    }

    public static async Task<FirefoxSlowCallCollector?> StartIfEnabledAsync(string output)
    {
        if (Environment.GetEnvironmentVariable("PTT_CAPTURE_SLOW_FIREFOX") != "1") return null;
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true"
            || Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted"
            || Environment.GetEnvironmentVariable("PTT_RUN_FIREFOX_COLD_START_TESTS") != "1")
            throw new InvalidOperationException("Proactive Firefox diagnostics require the isolated CI test.");

        var root = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE")
            ?? throw new InvalidOperationException("GitHub workspace is required.");
        var script = Path.Combine(root, "tests", "PttDictation.Tests", "FirefoxWaitChainWorker.ps1");
        var readyPath = Path.Combine(output, "slow-provider-worker.ready");
        var requestPath = Path.Combine(output, "slow-provider-worker.request.json");
        if (File.Exists(readyPath) || File.Exists(requestPath))
            throw new InvalidOperationException("Refusing stale diagnostic worker state.");
        var start = new ProcessStartInfo("pwsh.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-MTA", "-File", script,
            "-RequestPath", requestPath, "-ReadyPath", readyPath,
            "-OutputPath", Path.Combine(output, "slow-provider.chains.jsonl") })
            start.ArgumentList.Add(argument);
        var worker = Process.Start(start) ?? throw new InvalidOperationException("Diagnostic worker did not start.");
        var collector = new FirefoxSlowCallCollector(worker, requestPath, Path.Combine(output, "slow-provider-worker.json"));
        try
        {
            var watch = Stopwatch.StartNew();
            while (!File.Exists(readyPath))
            {
                if (worker.HasExited) throw new InvalidOperationException("Prepared diagnostic worker exited: " + await collector._stderr);
                if (watch.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Diagnostic worker did not prepare before Firefox launch.");
                await Task.Delay(20);
            }
            return collector;
        }
        catch
        {
            await collector.DisposeAsync();
            throw;
        }
    }

    public Task WatchTrialAsync(FirefoxWaitChainDiagnostics.ProcessTarget firefox, uint uiThreadId,
        string output, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        if (Volatile.Read(ref _triggered) != 0) return;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pending = TextProviderDiagnostics.SnapshotPendingOperations()
                    .Where(operation => FocusOperations.Contains(operation.Name)
                        && operation.ElapsedMilliseconds >= 1000).ToArray();
                var pendingTimestamp = Stopwatch.GetTimestamp();
                if (pending.Length > 0)
                {
                    if (Interlocked.Exchange(ref _triggered, 1) != 0) return;
                    var triggeredUtc = DateTimeOffset.UtcNow;
                    using var host = Process.GetCurrentProcess();
                    var request = new
                    {
                        Targets = new[]
                        {
                            new { ProcessId = Environment.ProcessId, StartedUtc = host.StartTime.ToUniversalTime(),
                                PriorityThreadId = uiThreadId, ProcessName = host.ProcessName },
                            new { firefox.ProcessId, firefox.StartedUtc, firefox.PriorityThreadId, ProcessName = "firefox" }
                        },
                        DumpProcessId = firefox.ProcessId, DumpPath = Path.Combine(output, "slow-provider.dmp"),
                        DumpPairs = 2, DumpIntervalMilliseconds = 350
                    };
                    // Atomic publication: the already-prepared worker polls only
                    // this file; there is no UIA read, startup, or compilation here.
                    await File.WriteAllTextAsync(_requestPath + ".tmp", JsonSerializer.Serialize(request));
                    await File.WriteAllTextAsync(Path.Combine(output, "slow-provider-trigger.json"),
                        JsonSerializer.Serialize(new { triggeredUtc, timestamp = pendingTimestamp,
                            timestampFrequency = Stopwatch.Frequency, pending, targets = request.Targets,
                            request.DumpPairs, request.DumpIntervalMilliseconds }));
                    File.Move(_requestPath + ".tmp", _requestPath);
                    var timedOut = false;
                    // Metadata-only samples establish whether the same slow
                    // call remained pending while each native dump was written.
                    // They never call UIA, inspect another process, or send input.
                    using (var samples = new StreamWriter(Path.Combine(output, "slow-provider-pending.jsonl")) { AutoFlush = true })
                    {
                        var workerExit = _worker.WaitForExitAsync();
                        var watch = Stopwatch.StartNew();
                        while (true)
                        {
                            var current = TextProviderDiagnostics.SnapshotPendingOperations();
                            var timestamp = Stopwatch.GetTimestamp();
                            var sinceTrigger = Stopwatch.GetElapsedTime(pendingTimestamp, timestamp).TotalMilliseconds;
                            await samples.WriteLineAsync(JsonSerializer.Serialize(new
                            {
                                utc = DateTimeOffset.UtcNow, timestamp,
                                timestampFrequency = Stopwatch.Frequency, pending = current,
                                triggeringCalls = pending.Select(original => new
                                {
                                    original.Name, original.ManagedThreadId,
                                    stillPending = current.Any(call => call.Name == original.Name
                                        && call.ManagedThreadId == original.ManagedThreadId
                                        // The elapsed clock must still imply
                                        // the same start, not a later call with
                                        // the same name on the serial worker.
                                        && Math.Abs(call.ElapsedMilliseconds
                                            - original.ElapsedMilliseconds - sinceTrigger) < 25)
                                })
                            }));
                            if (workerExit.IsCompleted) { await workerExit; break; }
                            if (watch.Elapsed >= TimeSpan.FromSeconds(8))
                            { timedOut = true; await StopWorkerAsync(); break; }
                            await Task.WhenAny(workerExit, Task.Delay(50));
                        }
                    }
                    await File.WriteAllTextAsync(_statusPath, JsonSerializer.Serialize(new
                    {
                        triggeredUtc, completedUtc = DateTimeOffset.UtcNow, timedOut,
                        _worker.ExitCode, stdout = await _stdout, stderr = await _stderr
                    }));
                    return;
                }
                await Task.Delay(10, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    });

    private async Task StopWorkerAsync()
    {
        if (_worker.HasExited) return;
        try { _worker.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) when (_worker.HasExited) { }
        await _worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
    }

    public async ValueTask DisposeAsync()
    {
        try { await StopWorkerAsync(); }
        finally { _worker.Dispose(); }
    }
}
