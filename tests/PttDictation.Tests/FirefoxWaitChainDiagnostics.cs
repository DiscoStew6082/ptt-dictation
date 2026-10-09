using System.Diagnostics;
using System.Text.Json;

namespace PttDictation.Tests;

// Native diagnostics run in a disposable process, never inside the testhost.
internal static class FirefoxWaitChainDiagnostics
{
    internal sealed record ProcessTarget(int ProcessId, DateTime StartedUtc, uint PriorityThreadId);

    public static async Task CaptureAsync(ProcessTarget firefox, uint testUiThreadId, string path, bool dumpFirefox = false)
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true"
            || Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted"
            || Environment.GetEnvironmentVariable("PTT_RUN_FIREFOX_COLD_START_TESTS") != "1")
            throw new InvalidOperationException("Wait-chain diagnostics require the isolated Firefox CI test.");

        var root = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE")
            ?? throw new InvalidOperationException("GitHub workspace is required for the diagnostic worker.");
        var script = Path.Combine(root, "tests", "PttDictation.Tests", "FirefoxWaitChainWorker.ps1");
        if (!File.Exists(script)) throw new FileNotFoundException("Diagnostic worker script is missing.", script);
        using var host = Process.GetCurrentProcess();
        var targets = new[]
        {
            new { firefox.ProcessId, firefox.StartedUtc, firefox.PriorityThreadId, ProcessName = "firefox" },
            new { ProcessId = Environment.ProcessId, StartedUtc = host.StartTime.ToUniversalTime(),
                PriorityThreadId = testUiThreadId, ProcessName = host.ProcessName }
        }.Where(target => target.ProcessId > 0 && target.StartedUtc != default).ToArray();
        var targetsPath = Path.ChangeExtension(path, ".targets.json");
        var chainsPath = Path.ChangeExtension(path, ".chains.jsonl");
        await File.WriteAllTextAsync(targetsPath, JsonSerializer.Serialize(targets));
        var start = new ProcessStartInfo("pwsh.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-MTA", "-File", script,
                     "-TargetsPath", targetsPath, "-OutputPath", chainsPath })
            start.ArgumentList.Add(argument);
        var dumpPath = dumpFirefox && firefox.ProcessId > 0 ? Path.ChangeExtension(path, ".firefox.dmp") : null;
        if (dumpPath is not null)
        {
            start.ArgumentList.Add("-DumpPath"); start.ArgumentList.Add(dumpPath);
            start.ArgumentList.Add("-DumpProcessId"); start.ArgumentList.Add(firefox.ProcessId.ToString());
        }
        var startedUtc = DateTimeOffset.UtcNow;
        using var worker = Process.Start(start) ?? throw new InvalidOperationException("Diagnostic worker did not start.");
        var stdout = worker.StandardOutput.ReadToEndAsync();
        var stderr = worker.StandardError.ReadToEndAsync();
        var timedOut = false;
        try { await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)); }
        catch (TimeoutException)
        {
            timedOut = true;
            worker.Kill(entireProcessTree: true);
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            startedUtc, completedUtc = DateTimeOffset.UtcNow, timedOut, worker.ExitCode,
            stdout = await stdout, stderr = await stderr, chainsPath, dumpPath,
            limitations = "A diagnostic worker crash or timeout does not alter the failed test; a single-node chain does not prove responsiveness."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
