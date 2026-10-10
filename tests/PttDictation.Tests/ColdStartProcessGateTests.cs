using System.Diagnostics;

namespace PttDictation.Tests;

[TestClass]
public sealed class ColdStartProcessGateTests
{
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
