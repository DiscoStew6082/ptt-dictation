using System.Diagnostics;

namespace PttDictation.Tests;

[TestClass]
public sealed class ColdStartProcessGateTests
{
    [TestMethod]
    public async Task EmptyObservationAfterDeadlineCannotAuthorizeLaunch()
    {
        await Assert.ThrowsExactlyAsync<AssertFailedException>(() =>
            ColdStartProcessGate.WaitUntilNoneRunningAsync(() => [], "late empty",
                _ => Thread.Sleep(30), TimeSpan.FromMilliseconds(5)));
    }

    [TestMethod]
    public async Task PersistentProcessTimesOutWithoutBeingTerminated()
    {
        using var host = Process.GetCurrentProcess();
        var observations = new List<ColdStartProcessGate.Observation[]>();
        await Assert.ThrowsExactlyAsync<AssertFailedException>(() =>
            ColdStartProcessGate.WaitUntilNoneRunningAsync(() => [Process.GetCurrentProcess()],
                "test host", observations.Add, TimeSpan.FromMilliseconds(150)));
        Assert.IsFalse(host.HasExited);
        Assert.IsNotEmpty(observations);
        Assert.IsTrue(observations.All(snapshot => snapshot.Single().HasExited == false));
    }

    [TestMethod]
    public async Task UnknownProcessCannotPassTheWaitGate()
    {
        ColdStartProcessGate.Observation[]? evidence = null;
        await Assert.ThrowsExactlyAsync<AssertFailedException>(() =>
            ColdStartProcessGate.WaitUntilNoneRunningAsync(() => [new Process()], "unknown",
                value => evidence = value, TimeSpan.Zero));
        Assert.IsNotNull(evidence);
        Assert.IsNull(evidence.Single().HasExited);
        StringAssert.Contains(evidence[0].Error!, nameof(InvalidOperationException));
    }

    [TestMethod]
    public async Task CancellationCannotAuthorizeLaunchEvenWhenProcessesDisappear()
    {
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            ColdStartProcessGate.WaitUntilNoneRunningAsync(() => [], "empty",
                _ => cancellation.Cancel(), TimeSpan.FromSeconds(20), cancellation.Token));
    }

    [TestMethod]
    public async Task ReplacementProcessIsObservedAndStillBlocksLaunch()
    {
        using var child = Process.Start(new ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /c set /p gate=")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true })!;
        using var host = Process.GetCurrentProcess();
        var observations = new List<ColdStartProcessGate.Observation[]>();
        try
        {
            await Assert.ThrowsExactlyAsync<AssertFailedException>(() =>
                ColdStartProcessGate.WaitUntilNoneRunningAsync(
                    () => observations.Count == 0 ? [Process.GetProcessById(child.Id)] : [Process.GetCurrentProcess()],
                    "replacement", snapshot =>
                    {
                        observations.Add(snapshot);
                        if (observations.Count == 1) child.StandardInput.Close();
                    }, TimeSpan.FromSeconds(2)).WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.AreEqual(child.Id, observations[0].Single().ProcessId);
            Assert.AreEqual(host.Id, observations[^1].Single().ProcessId);
            Assert.IsFalse(host.HasExited);
        }
        finally
        {
            child.StandardInput.Close();
            if (!child.WaitForExit(5000)) { child.Kill(); child.WaitForExit(); }
        }
    }

    [TestMethod]
    public async Task TransientProcessExitsNaturallyBeforeColdStartContinues()
    {
        using var child = Process.Start(new ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /c set /p gate=")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true })!;
        _ = child.SafeHandle;
        var observations = new List<ColdStartProcessGate.Observation[]>();
        try
        {
            await ColdStartProcessGate.WaitUntilNoneRunningAsync(
                () => child.HasExited ? [] : [Process.GetProcessById(child.Id)], "test child", snapshot =>
                {
                    observations.Add(snapshot);
                    // Release only the fixture's stdin; the gate must never terminate it.
                    if (observations.Count == 1) child.StandardInput.Close();
                }, TimeSpan.FromSeconds(5));
            Assert.IsTrue(observations[0].Any(value => value.HasExited == false));
            Assert.IsTrue(observations[^1].All(value => value.HasExited == true));
            Assert.IsTrue(child.WaitForExit(0));
            Assert.IsTrue(observations.Count >= 2, "A live snapshot cannot authorize launch.");
        }
        finally
        {
            child.StandardInput.Close();
            if (!child.WaitForExit(5000)) { child.Kill(); child.WaitForExit(); }
        }
    }

    [TestMethod]
    public void ExitedProcessWithRetainedHandleDoesNotPreventColdStart()
    {
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /c exit 0")
        { UseShellExecute = false, CreateNoWindow = true })!;
        var handle = process.SafeHandle;
        Assert.IsTrue(process.WaitForExit(5000));
        Assert.IsFalse(handle.IsClosed);
        ColdStartProcessGate.Observation[]? evidence = null;

        ColdStartProcessGate.AssertNoneRunning([process], "test child", value => evidence = value);

        Assert.IsNotNull(evidence);
        Assert.HasCount(1, evidence);
        Assert.AreEqual(process.Id, evidence[0].ProcessId);
        Assert.AreEqual(true, evidence[0].HasExited);
    }

    [TestMethod]
    public void LiveProcessStillPreventsColdStartAndIsNotTerminated()
    {
        using var process = Process.GetCurrentProcess();
        ColdStartProcessGate.Observation[]? evidence = null;
        Assert.ThrowsExactly<AssertFailedException>(() =>
            ColdStartProcessGate.AssertNoneRunning([process], "test host", value => evidence = value));
        Assert.IsFalse(process.HasExited);
        Assert.AreEqual(false, evidence![0].HasExited);
    }

    [TestMethod]
    public void UnknownProcessStateFailsClosedAndRecordsObservationError()
    {
        using var process = new Process();
        ColdStartProcessGate.Observation[]? evidence = null;
        Assert.ThrowsExactly<AssertFailedException>(() =>
            ColdStartProcessGate.AssertNoneRunning([process], "unknown", value => evidence = value));
        Assert.IsNull(evidence![0].HasExited);
        StringAssert.Contains(evidence[0].Error!, nameof(InvalidOperationException));
    }
}
