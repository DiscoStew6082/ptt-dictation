using PttDictation.App;

namespace PttDictation.Tests;

[TestClass]
public sealed class SingleInstanceGuardTests
{
    [TestMethod]
    public void NamedGuardAllowsOnlyOneOwnerUntilDisposed()
    {
        var guardName = "Local\\PttDictation.Tests.SingleInstanceGuard." + Guid.NewGuid();

        using var first = SingleInstanceGuard.TryAcquire(guardName);
        using var second = SingleInstanceGuard.TryAcquire(guardName);

        Assert.IsNotNull(first);
        Assert.IsNull(second);

        first.Dispose();

        using var third = SingleInstanceGuard.TryAcquire(guardName);
        Assert.IsNotNull(third);
    }

    [TestMethod]
    public void NamedGuardAcquiresExistingUnownedMutex()
    {
        var guardName = "Local\\PttDictation.Tests.SingleInstanceGuard." + Guid.NewGuid();
        using var existingMutex = new Mutex(initiallyOwned: false, name: guardName);

        using var guard = SingleInstanceGuard.TryAcquire(guardName);
        using var blocked = SingleInstanceGuard.TryAcquire(guardName);

        Assert.IsNotNull(guard);
        Assert.IsNull(blocked);
    }

    [TestMethod]
    public void NamedGuardReturnsNullWhenAnotherThreadOwnsMutex()
    {
        var guardName = "Local\\PttDictation.Tests.SingleInstanceGuard." + Guid.NewGuid();
        using var ownerReady = new ManualResetEventSlim();
        using var releaseOwner = new ManualResetEventSlim();
        Exception? ownerException = null;
        var ownerThread = new Thread(() =>
        {
            try
            {
                using var ownedMutex = new Mutex(initiallyOwned: true, name: guardName);
                ownerReady.Set();
                releaseOwner.Wait();
                ownedMutex.ReleaseMutex();
            }
            catch (Exception ex)
            {
                ownerException = ex;
            }
        });

        ownerThread.Start();
        Assert.IsTrue(ownerReady.Wait(TimeSpan.FromSeconds(5)));

        using var guard = SingleInstanceGuard.TryAcquire(guardName);

        releaseOwner.Set();
        ownerThread.Join();
        if (ownerException is not null)
        {
            throw ownerException;
        }

        Assert.IsNull(guard);
    }
}
