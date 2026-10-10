namespace PttDictation.Tests;

[TestClass]
public sealed class ColdStartCleanupTests
{
    [TestMethod]
    public async Task ProfileCleanupWaitsForTransientSharingLockToClose()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ptt-cleanup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var stream = new FileStream(Path.Combine(directory, "Affiliation Database"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var release = Task.Run(async () => { await Task.Delay(250); stream.Dispose(); });
        try
        {
            await Task.Run(() => ColdStartCleanup.DeleteProfile(directory));
            Assert.IsFalse(Directory.Exists(directory));
        }
        finally
        {
            await release;
            stream.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public void PermanentlyLockedProfileFailsAtBoundedDeadline()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ptt-cleanup-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var stream = new FileStream(Path.Combine(directory, "Affiliation Database"), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        try
        {
            Assert.ThrowsExactly<IOException>(() => ColdStartCleanup.DeleteProfile(directory, TimeSpan.FromMilliseconds(100)));
            Assert.IsTrue(Directory.Exists(directory));
        }
        finally
        {
            stream.Dispose();
            Directory.Delete(directory, true);
        }
    }

    [TestMethod]
    public void SecondaryCleanupErrorCannotReplaceOriginalTrialFailure()
    {
        var original = new InvalidOperationException("browser activation failed");
        var reported = new List<string>();
        var cleanup = new ColdStartCleanup((stage, _) => reported.Add(stage));
        var observed = Assert.ThrowsExactly<InvalidOperationException>(() =>
        {
            Exception? primary = null;
            try { throw original; }
            catch (Exception error) { primary = error; throw; }
            finally
            {
                cleanup.Run("profile", () => throw new IOException("profile is locked"));
                cleanup.ThrowIfNeeded(primary);
            }
        });
        Assert.AreSame(original, observed);
        CollectionAssert.AreEqual(new[] { "profile" }, reported);
    }

    [TestMethod]
    public void CleanupErrorStillFailsOtherwiseSuccessfulTrial()
    {
        var error = new IOException("profile is locked");
        var cleanup = new ColdStartCleanup((_, _) => { });
        var observed = Assert.ThrowsExactly<IOException>(() =>
        {
            cleanup.Run("profile", () => throw error);
            cleanup.ThrowIfNeeded(null);
        });
        Assert.AreSame(error, observed);
    }
}
