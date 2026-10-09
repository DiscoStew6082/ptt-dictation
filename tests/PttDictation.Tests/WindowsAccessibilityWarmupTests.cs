using PttDictation.App;

namespace PttDictation.Tests;

[TestClass]
public sealed class WindowsAccessibilityWarmupTests
{
    [TestMethod]
    public async Task RequestsDuringAProviderReadAreCoalescedButNotLost()
    {
        using var release = new ManualResetEvent(false);
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action? request = null;
        int calls = 0;
        using var warmup = new WindowsAccessibilityWarmup(wake =>
        {
            request = wake;
            return new Subscription(() => { });
        }, () =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                first.SetResult();
                release.WaitOne();
            }
            else second.TrySetResult();
            return true;
        }, interval: TimeSpan.FromMinutes(1));
        try
        {
            await first.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.IsNotNull(request);
            for (var i = 0; i < 100; i++) request();
            Assert.AreEqual(1, Volatile.Read(ref calls));
            release.Set();
            await second.Task.WaitAsync(TimeSpan.FromSeconds(2));
            warmup.Dispose();
            await warmup.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(2, calls);
        }
        finally { release.Set(); }
    }

    [TestMethod]
    public async Task ProbeFailuresRecoverOnLaterRequestsAndActiveDictationSkipsReads()
    {
        var subscribed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int idle = 0, calls = 0;
        using var warmup = new WindowsAccessibilityWarmup(_ =>
        {
            subscribed.SetResult();
            return new Subscription(() => { });
        }, () =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                failed.SetResult();
                throw new System.Windows.Automation.ElementNotAvailableException();
            }
            recovered.TrySetResult();
            return true;
        }, () => Volatile.Read(ref idle) == 1, TimeSpan.FromMilliseconds(20));
        await subscribed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(250);
        Assert.AreEqual(0, Volatile.Read(ref calls), "Do not warm providers during dictation.");
        Volatile.Write(ref idle, 1);
        warmup.Request();
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        warmup.Request();
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        warmup.Dispose();
        await warmup.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task CoarseProviderGetsOnlyThreeReadsPerCycle()
    {
        var third = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        using var warmup = new WindowsAccessibilityWarmup(_ => new Subscription(() => { }), () =>
        {
            if (Interlocked.Increment(ref calls) == 3) third.TrySetResult();
            return false;
        }, interval: TimeSpan.FromMinutes(1));
        await third.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(250);
        Assert.AreEqual(3, Volatile.Read(ref calls));
        warmup.Dispose();
        await warmup.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task ColdProviderIsInitializedBeforeCaptureWithoutRelaxingFocusGuard()
    {
        var events = new Events();
        long now = 900;
        using var guard = new WindowsFocusCaptureGuard(events, () => Interlocked.Read(ref now));
        var initialized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = false;
        using var warmup = new WindowsAccessibilityWarmup(_ => new Subscription(() => { }), () =>
        {
            ready = true;
            events.Raise(WindowsFocusCaptureGuard.Ia2CaretEvent, 900);
            events.Raise(WindowsFocusCaptureGuard.FocusEvent, 900);
            initialized.TrySetResult();
            return true;
        }, interval: TimeSpan.FromMinutes(1));
        Assert.AreSame(initialized.Task, await Task.WhenAny(initialized.Task, Task.Delay(1500)),
            "The cold browser must be probed before the user presses the hotkey.");
        Interlocked.Exchange(ref now, 1000);
        var unchanged = guard.BeginCapture();
        var generation = guard.Generation;
        var original = new object();
        var target = new Target();
        var captured = InitialTextTargetCapture.Capture(original,
            _ => new(ready ? target : new Target { Supported = false }, !ready),
            () => original, (_, _) => true, () => guard.Generation == generation,
            _ => Assert.Fail("A warm provider should not need initial container recovery."));
        Assert.AreSame(target, captured);
        Assert.IsTrue(unchanged());
        // The same native events after the boundary must still reject capture.
        events.Raise(WindowsFocusCaptureGuard.FocusEvent, 1000);
        Assert.IsFalse(unchanged());
        warmup.Dispose();
        await warmup.Completion.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [TestMethod]
    public async Task SubscriptionAndRemovalShareAMtaThreadAndDisposalDoesNotWaitForProvider()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEvent(false);
        int subscribeThread = 0, removeThread = 0;
        ApartmentState apartment = ApartmentState.Unknown;
        using var warmup = new WindowsAccessibilityWarmup(_ =>
        {
            subscribeThread = Environment.CurrentManagedThreadId;
            apartment = Thread.CurrentThread.GetApartmentState();
            entered.SetResult();
            release.WaitOne();
            return new Subscription(() => removeThread = Environment.CurrentManagedThreadId);
        }, () => throw new AssertFailedException("No probe after disposal."));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        warmup.Dispose();
        warmup.Request();
        Assert.IsFalse(warmup.Completion.IsCompleted);
        release.Set();
        await warmup.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(ApartmentState.MTA, apartment);
        Assert.AreEqual(subscribeThread, removeThread);
        Assert.AreNotEqual(0, removeThread);
        warmup.Request();
    }

    private sealed class Subscription(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
    private sealed class Events : IFocusCaptureEvents
    {
        public bool Available => true;
        public event Action<FocusCaptureEvent>? Changed;
        public FocusCapturePosition ReadFocus() => new(1, 2);
        public bool IsInFocusedHierarchy(IntPtr window) => window == 2;
        public void Raise(uint kind, uint time) => Changed?.Invoke(new(kind, 2, -4, -1188, time));
        public void Dispose() { }
    }
    private sealed class Target : IWindowsTextTarget
    {
        public bool Supported { get; init; } = true;
        public bool IsFocused => true;
        public bool SupportsReplacement => Supported;
        public bool CanPasteFallback => Supported;
        public bool IsPreparedSelectionCurrent => true;
        public bool HasAttemptedWrite => false;
        public TextTargetUpdateResult TryReplace(string previousText, string replacementText, Action<string> paste)
            => throw new AssertFailedException("Warmup must never insert text.");
    }
}
