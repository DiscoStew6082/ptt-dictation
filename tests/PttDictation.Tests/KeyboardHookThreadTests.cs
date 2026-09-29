using PttDictation.App;

namespace PttDictation.Tests;

[TestClass]
public sealed class KeyboardHookThreadTests
{
    [TestMethod]
    public void MessagePumpRunsWhileOwnerIsBlockedAndUninstallsOnItsOwnThread()
    {
        var ownerThread = Environment.CurrentManagedThreadId;
        var installThread = 0;
        var uninstallThread = 0;
        using var delivered = new ManualResetEventSlim();
        var callbackThread = 0;
        using (var loop = new KeyboardHookThread(
            () => installThread = Environment.CurrentManagedThreadId,
            () => uninstallThread = Environment.CurrentManagedThreadId))
        {
            loop.Post(() => { callbackThread = Environment.CurrentManagedThreadId; delivered.Set(); });
            Assert.IsTrue(delivered.Wait(TimeSpan.FromSeconds(3)),
                "A blocked owner/UI thread must not prevent the keyboard message pump from dispatching.");
            Assert.AreNotEqual(ownerThread, installThread);
            Assert.AreEqual(installThread, callbackThread);
        }
        Assert.AreEqual(installThread, uninstallThread);
    }

    [TestMethod]
    public void InstallationFailurePropagatesAndDoesNotCallUninstall()
    {
        var uninstalled = false;
        var error = Assert.ThrowsExactly<InvalidOperationException>(() =>
            new KeyboardHookThread(() => throw new InvalidOperationException("install rejected"),
                () => uninstalled = true));
        Assert.AreEqual("install rejected", error.Message);
        Assert.IsFalse(uninstalled);
    }

    [TestMethod]
    public void RepeatedDisposeUninstallsOnceAndRejectsNewWork()
    {
        var uninstalls = 0;
        var loop = new KeyboardHookThread(() => { }, () => Interlocked.Increment(ref uninstalls));
        loop.Dispose();
        loop.Dispose();
        Assert.AreEqual(1, uninstalls);
        Assert.ThrowsExactly<ObjectDisposedException>(() => loop.Post(() => { }));
    }
}
