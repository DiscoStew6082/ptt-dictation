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

    [TestMethod]
    public async Task CaptureReusesTheThreadLocalClientInitializedByWarmup()
    {
        using var initialized = new ThreadLocal<bool>(() => false);
        using var worker = new ApartmentWorker(ApartmentState.MTA, "Shared accessibility client test");
        using var coldWorker = new ApartmentWorker(ApartmentState.MTA, "Uninitialized accessibility client test");
        var warmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int subscribeThread = 0, probeThread = 0, captureThread = 0, removeThread = 0, captures = 0, guards = 0;
        using var warmup = new WindowsAccessibilityWarmup(_ =>
        {
            subscribeThread = Environment.CurrentManagedThreadId;
            return new Subscription(() => removeThread = Environment.CurrentManagedThreadId);
        }, () =>
        {
            Assert.AreEqual(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());
            probeThread = Environment.CurrentManagedThreadId;
            initialized.Value = true;
            warmed.TrySetResult();
            return true;
        }, interval: TimeSpan.FromMinutes(1), providerWorker: worker);
        var originalTarget = new Target();
        using var output = new LiveClipboardPaster(() =>
        {
            captures++;
            throw new AssertFailedException("A prepared original target must not be recaptured.");
        }, (_, _) => Assert.Fail("Warmup and preparation never paste."), automationWorker: worker,
            acquireCapture: warmup.AcquireCaptureAsync,
            beginCaptureGuard: () => { guards++; return () => true; },
            createPreparedReadiness: () => new(() =>
            {
                captureThread = Environment.CurrentManagedThreadId;
                Assert.IsTrue(initialized.Value, "Capture must use the exact client initialized by warmup.");
                return true;
            }, () => originalTarget));
        try
        {
            await warmed.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await coldWorker.InvokeAsync(() => Assert.IsFalse(initialized.Value,
                "Another MTA thread still has an uninitialized client; warmup is not process-wide."));

            await output.PrepareCaptureAsync(CancellationToken.None);
            output.CaptureTarget();
            await worker.InvokeAsync(() => { }); // Complete exact prepared-target handoff.
            output.EndSession();
            await worker.InvokeAsync(() => { }); // Complete the queued capture-lease release.

            Assert.AreEqual(subscribeThread, probeThread);
            Assert.AreEqual(probeThread, captureThread);
            Assert.AreEqual(1, guards);
            Assert.AreEqual(0, captures);
        }
        finally { await LiveClipboardPaster.DisposeAutomationAfterWarmupAsync(warmup, worker); }
        Assert.AreEqual(subscribeThread, removeThread);
    }

    [TestMethod]
    public async Task SharedMtaCaptureLeasePreventsWarmupUntilTheLeaseIsReleased()
    {
        using var worker = new ApartmentWorker(ApartmentState.MTA, "Leased accessibility client test");
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0, probeThread = 0;
        using var warmup = new WindowsAccessibilityWarmup(_ => new Subscription(() => { }), () =>
        {
            probeThread = Environment.CurrentManagedThreadId;
            if (Interlocked.Increment(ref calls) == 1) first.TrySetResult();
            else second.TrySetResult();
            return true;
        }, interval: TimeSpan.FromMinutes(1), providerWorker: worker);
        try
        {
            await first.Task.WaitAsync(TimeSpan.FromSeconds(2));
            using (await warmup.AcquireCaptureAsync(CancellationToken.None))
            {
                warmup.Request();
                await Task.Delay(150);
                await worker.InvokeAsync(() =>
                {
                    Assert.AreEqual(probeThread, Environment.CurrentManagedThreadId);
                    Assert.AreEqual(1, Volatile.Read(ref calls), "Warmup cannot interleave with a capture lease.");
                });
            }
            warmup.Request();
            await second.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { await LiveClipboardPaster.DisposeAutomationAfterWarmupAsync(warmup, worker); }
    }

    [TestMethod]
    public async Task BlockedSharedProviderDisposalKeepsQueueOpenUntilSameOwnerUnsubscribes()
    {
        using var worker = new ApartmentWorker(ApartmentState.MTA, "Blocked accessibility client test");
        using var release = new ManualResetEvent(false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int subscribeThread = 0, removeThread = 0;
        using var warmup = new WindowsAccessibilityWarmup(_ =>
        {
            subscribeThread = Environment.CurrentManagedThreadId;
            return new Subscription(() => removeThread = Environment.CurrentManagedThreadId);
        }, () =>
        {
            entered.TrySetResult();
            release.WaitOne();
            return true;
        }, providerWorker: worker);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var shutdown = LiveClipboardPaster.DisposeAutomationAfterWarmupAsync(warmup, worker);
            Assert.IsFalse(shutdown.IsCompleted, "Disposal must return without waiting for a blocked provider.");
            Assert.IsFalse(warmup.Completion.IsCompleted);
            var queued = worker.InvokeAsync(() => { });
            Assert.IsFalse(queued.IsCompleted, "The queue stays open, but the provider still owns its worker.");

            release.Set();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(2));
            await queued.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(subscribeThread, removeThread);
            Assert.AreNotEqual(0, removeThread);
            Assert.IsTrue(worker.InvokeAsync(() => Assert.Fail("Closed worker cannot execute more provider work.")).IsCanceled);
        }
        finally
        {
            release.Set();
            await LiveClipboardPaster.DisposeAutomationAfterWarmupAsync(warmup, worker);
        }
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
