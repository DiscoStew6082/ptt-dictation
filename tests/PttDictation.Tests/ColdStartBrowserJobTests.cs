using System.Runtime.InteropServices;
using System.Diagnostics;

namespace PttDictation.Tests;

[TestClass]
public sealed class ColdStartBrowserJobTests
{
    [TestMethod]
    [DataRow("")]
    [DataRow("--no-first-run")]
    [DataRow("--user-data-dir=C:\\profile folder\\")]
    [DataRow("C:\\Program Files\\Browser\\browser.exe")]
    [DataRow("argument with \\\"quotes\\\" and \\slashes\\")]
    public void NativeLaunchArgumentRoundTripsWithoutSplittingOrChangingProfile(string argument)
    {
        var parsed = CommandLineToArgvW("test.exe " + ColdStartBrowserJob.QuoteArgument(argument), out var count);
        Assert.AreNotEqual(IntPtr.Zero, parsed);
        try
        {
            Assert.AreEqual(2, count);
            Assert.AreEqual(argument, Marshal.PtrToStringUni(Marshal.ReadIntPtr(parsed, IntPtr.Size)));
        }
        finally { LocalFree(parsed); }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task OwnedParentAndChildHaveSignaledHandlesBeforeCleanupCompletes()
    {
        var evidence = new List<ColdStartBrowserJob.CleanupObservation>();
        var processes = new List<Process>();
        var independentSignalsProven = false;
        var expectIndependentProof = false;
        using var host = Process.GetCurrentProcess();
        using var job = new ColdStartBrowserJob(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            ["/d", "/c", "ping.exe -n 60 127.0.0.1 > nul"], observation =>
            {
                evidence.Add(observation);
                if (observation.Stage != "exit_signals_proven" || !expectIndependentProof) return;
                foreach (var process in processes)
                {
                    Assert.IsTrue(process.WaitForExit(0), "Independent retained handle must already be signaled.");
                    // External handles can keep job accounting nonzero. Release
                    // only after verifying the exact process exit, as cleanup does.
                    process.Dispose();
                }
                independentSignalsProven = true;
            });
        Assert.IsFalse(job.Contains(host), "The test host must not belong to the owned browser job.");
        try
        {
            var deadline = Stopwatch.StartNew();
            while (true)
            {
                foreach (var pid in job.GetOwnedProcessIds())
                {
                    if (processes.Any(process => process.Id == pid)) continue;
                    var process = Process.GetProcessById(pid);
                    // Retain the exact native handle while the child is alive.
                    _ = process.SafeHandle;
                    if (job.Contains(process)) processes.Add(process);
                    else process.Dispose();
                }
                if (processes.Any(process => process.Id == job.LauncherId)
                    && processes.Any(process => process.ProcessName.Equals("ping", StringComparison.OrdinalIgnoreCase))) break;
                Assert.IsLessThan(5000L, deadline.ElapsedMilliseconds, "Owned cmd parent did not create its ping child.");
                await Task.Delay(25);
            }
            var parent = processes.Single(process => process.Id == job.LauncherId);
            var child = processes.Single(process => process.ProcessName.Equals("ping", StringComparison.OrdinalIgnoreCase));
            Assert.IsFalse(parent.WaitForExit(0));
            Assert.IsFalse(child.WaitForExit(0));
            var parentId = parent.Id;
            var childId = child.Id;
            var expected = new[] { parent, child }.Select(process =>
                (Id: process.Id, StartedUtc: process.StartTime.ToUniversalTime())).ToArray();

            expectIndependentProof = true;
            job.Dispose();

            Assert.IsTrue(independentSignalsProven, "Both retained handles must prove exit before final accounting.");
            Assert.IsFalse(host.HasExited, "Cleanup must preserve the unrelated test host.");
            var before = evidence.Single(value => value.Stage == "before_termination");
            var completed = evidence.Single(value => value.Stage == "cleanup_completed");
            Assert.AreEqual(0u, completed.ActiveProcesses);
            Assert.IsLessThan(5000L, completed.ElapsedMilliseconds);
            foreach (var process in expected)
            {
                var initial = before.Members.Single(member => member.ProcessId == process.Id);
                Assert.AreEqual(true, initial.IsJobMember);
                Assert.AreEqual(false, initial.ExitSignaled);
                Assert.AreEqual(process.StartedUtc, initial.StartedUtc);
                Assert.IsTrue(Path.IsPathFullyQualified(initial.Executable!));
                Assert.AreEqual(true, completed.Members.Single(member => member.ProcessId == process.Id).ExitSignaled);
            }
            var signals = evidence.FindIndex(value => value.Stage == "exit_signals_proven");
            var released = evidence.FindIndex(value => value.Stage == "retained_handles_released");
            var finished = evidence.FindIndex(value => value.Stage == "cleanup_completed");
            Assert.IsTrue(signals < released && released < finished);
            Assert.AreEqual(parentId, before.Members.Single(member => member.ProcessId == childId).ParentProcessIdAtCreation);
            job.Dispose(); // Repeated Dispose cannot restart cleanup or emit another proof.
            Assert.HasCount(1, evidence.Where(value => value.Stage == "cleanup_completed").ToArray());
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
