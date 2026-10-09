using PttDictation.App;
using PttDictation.Core;
using System.Text.Json;

namespace PttDictation.Tests;

[TestClass]
public sealed class WindowsFocusCaptureGuardTests
{
    [TestMethod]
    public void PreparationIgnoresOldForegroundDeliveryButRejectsNewRedirection()
    {
        var events = new Events();
        long now = 1000, input = 0;
        using var guard = new WindowsFocusCaptureGuard(events, () => now);
        var unchanged = guard.BeginPreparation(() => input);
        now = 1080;
        events.Raise(WindowsFocusCaptureGuard.ForegroundEvent, 1, timestamp: 990);
        Assert.IsTrue(unchanged(), "A delayed pre-intent foreground event must not reject cold startup.");
        events.Raise(WindowsFocusCaptureGuard.Ia2CaretEvent, 2, timestamp: 1001);
        Assert.IsTrue(unchanged(), "Preparation permits provider initialization; capture has its own strict boundary.");
        input++;
        Assert.IsFalse(unchanged());
        unchanged = guard.BeginPreparation(() => input);
        events.Raise(WindowsFocusCaptureGuard.ForegroundEvent, 99, timestamp: 1080);
        events.Raise(WindowsFocusCaptureGuard.ForegroundEvent, 1, timestamp: 1080);
        Assert.IsFalse(unchanged(), "Switching away and back must retire preparation.");
    }
    [TestMethod]
    [DoNotParallelize]
    public async Task AbandonedCaptureStopsEventDiagnosticsButStillTracksRealChanges()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ptt-focus-events-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var configured = DiagnosticTrace.Configure(directory, "focus-test");
            var events = new Events();
            long now = 1000;
            using var guard = new WindowsFocusCaptureGuard(events, () => now);
            var unchanged = guard.BeginCapture();
            now = 1080;
            events.Raise(WindowsFocusCaptureGuard.FocusEvent, 2, timestamp: 990);
            // Confirmation never runs: the capture was cancelled or timed out.
            now = 6001;
            events.Raise(WindowsFocusCaptureGuard.FocusEvent, 2, timestamp: 6001);
            Assert.IsFalse(unchanged());
            await DiagnosticTrace.FlushAsync();
            var eventLines = File.ReadLines(Path.Combine(directory, "events.jsonl"))
                .Where(line => line.Contains("\"stage\":\"focus_guard.event\"", StringComparison.Ordinal)).ToArray();
            Assert.HasCount(1, eventLines, "Abandoned capture must not log later editor activity indefinitely.");
            using var recorded = JsonDocument.Parse(eventLines[0]);
            var data = recorded.RootElement.GetProperty("data");
            Assert.IsTrue(data.GetProperty("ignoredBeforeCapture").GetBoolean());
            Assert.AreEqual(90U, data.GetProperty("ageMilliseconds").GetUInt32());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    public async Task DeferredCaptureConfirmationAcceptsOldEventsButRejectsCurrentChanges()
    {
        foreach (var eventTime in new uint[] { 990, 1000, 1001 })
        {
            var events = new Events();
            long now = 1000;
            using var guard = new WindowsFocusCaptureGuard(events, () => now);
            var writes = new List<string>();
            Action? confirm = null;
            using var output = new LiveClipboardPaster(() => new FallbackTarget(),
                (text, allowed) => { Assert.IsTrue(allowed()); writes.Add(text); },
                beginCaptureGuard: guard.BeginCapture, confirmCapture: action => confirm = action);
            output.CaptureTarget();
            now = 1080;
            events.Raise(WindowsFocusCaptureGuard.Ia2CaretEvent, 2, timestamp: eventTime);
            Assert.IsNotNull(confirm);
            confirm();
            var paste = output.PasteAsync("Final words.", CancellationToken.None);
            if (eventTime < 1000)
            {
                await paste.WaitAsync(TimeSpan.FromSeconds(1));
                CollectionAssert.AreEqual(new[] { "Final words." }, writes);
            }
            else
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => paste);
                Assert.HasCount(0, writes);
            }
        }
    }

    [TestMethod]
    public void ContainerRecoveryAndUiConfirmationUseTheSameOccurrenceBoundary()
    {
        foreach (var eventTime in new uint[] { 990, 1001 })
        {
            var events = new Events();
            long now = 1000;
            using var guard = new WindowsFocusCaptureGuard(events, () => now);
            var unchanged = guard.BeginCapture();
            var generation = guard.Generation;
            var original = new object();
            var editor = new object();
            var unsupported = new FallbackTarget { Writable = false };
            var writable = new ReplacementTarget();
            var target = InitialTextTargetCapture.Capture(original,
                identity => ReferenceEquals(identity, original) ? new(unsupported, true) : new(writable, false),
                () => editor, (_, _) => true,
                () => generation == guard.Generation,
                _ => { now = 1080; events.Raise(WindowsFocusCaptureGuard.FocusEvent, 2, timestamp: eventTime); });
            Assert.AreSame(eventTime < 1000 ? writable : (IWindowsTextTarget)unsupported, target);
            Assert.AreEqual(eventTime < 1000, unchanged());
        }
    }

    [TestMethod]
    public void DelayedNotificationsGeneratedBeforeCaptureDoNotRejectTheTextbox()
    {
        foreach (var kind in new[] { WindowsFocusCaptureGuard.ForegroundEvent,
            WindowsFocusCaptureGuard.FocusEvent, WindowsFocusCaptureGuard.TextSelectionEvent,
            WindowsFocusCaptureGuard.Ia2CaretEvent, WindowsFocusCaptureGuard.Ia2SelectionEvent,
            WindowsFocusCaptureGuard.LocationEvent })
        {
            var events = new Events();
            long now = 1000;
            using var guard = new WindowsFocusCaptureGuard(events, () => now);
            var unchanged = guard.BeginCapture();
            var generation = guard.Generation;
            now = 1080;
            events.Raise(kind, 2, WindowsFocusCaptureGuard.CaretObject, timestamp: 990);
            Assert.IsTrue(unchanged(), $"Queued event {kind:X} predates capture.");
            Assert.AreEqual(generation, guard.Generation,
                "The worker recovery and final confirmation must see the same unchanged generation.");
        }
    }

    [TestMethod]
    public void NotificationsAtOrAfterCaptureStillRejectAndStaleEventsCannotReviveIt()
    {
        foreach (var timestamp in new uint[] { 1000, 1001, 1080 })
        {
            var events = new Events();
            long now = 1000;
            using var guard = new WindowsFocusCaptureGuard(events, () => now);
            var unchanged = guard.BeginCapture();
            now = 1080;
            events.Raise(WindowsFocusCaptureGuard.FocusEvent, 2, timestamp: timestamp);
            events.Raise(WindowsFocusCaptureGuard.FocusEvent, 2, timestamp: 990);
            Assert.IsFalse(unchanged());
        }
    }

    [TestMethod]
    public void DelayedEventCannotHideChangedNativeFocusOrForegroundAwayAndBack()
    {
        var events = new Events();
        long now = 1000;
        using var guard = new WindowsFocusCaptureGuard(events, () => now);
        var unchanged = guard.BeginCapture();
        now = 1080;
        events.Position = new(1, 3);
        events.Raise(WindowsFocusCaptureGuard.FocusEvent, 3, timestamp: 990);
        Assert.IsFalse(unchanged());
        events.Position = new(1, 2);
        unchanged = guard.BeginCapture();
        now = 1100;
        events.Raise(WindowsFocusCaptureGuard.ForegroundEvent, 99, timestamp: 1081);
        events.Raise(WindowsFocusCaptureGuard.ForegroundEvent, 1, timestamp: 1082);
        Assert.IsFalse(unchanged());
    }

    [TestMethod]
    public void NativeTimestampWraparoundPreservesEventOrdering()
    {
        foreach (var start in new long[] { uint.MaxValue - 5L, uint.MaxValue + 5L })
        {
            var events = new Events();
            var now = start;
            using var guard = new WindowsFocusCaptureGuard(events, () => now);
            var unchanged = guard.BeginCapture();
            now += 80;
            events.Raise(WindowsFocusCaptureGuard.FocusEvent, 2, timestamp: unchecked((uint)(start - 10)));
            Assert.IsTrue(unchanged());
            events.Raise(WindowsFocusCaptureGuard.FocusEvent, 2, timestamp: unchecked((uint)(start + 1)));
            Assert.IsFalse(unchanged());
        }
    }

    [TestMethod]
    public void AmbiguousFutureEventTimestampIsNotTreatedAsAnOldNotification()
    {
        var events = new Events();
        using var guard = new WindowsFocusCaptureGuard(events, () => 1000);
        var unchanged = guard.BeginCapture();
        events.Raise(WindowsFocusCaptureGuard.FocusEvent, 2, timestamp: 1001);
        Assert.IsFalse(unchanged());
    }

    [TestMethod]
    public void SameNativeWindowFocusChangeInvalidatesPendingCapture()
    {
        var events = new Events();
        using var guard = new WindowsFocusCaptureGuard(events);
        var unchanged = guard.BeginCapture();
        Assert.IsTrue(unchanged());
        events.Raise(WindowsFocusCaptureGuard.FocusEvent, 2);
        Assert.IsFalse(unchanged());
    }

    [TestMethod]
    public void SelectionChangesIncludingChromiumCaretInvalidateCapture()
    {
        foreach (var kind in new[] { WindowsFocusCaptureGuard.TextSelectionEvent,
            WindowsFocusCaptureGuard.Ia2CaretEvent, WindowsFocusCaptureGuard.Ia2SelectionEvent })
        {
            var events = new Events();
            using var guard = new WindowsFocusCaptureGuard(events);
            var unchanged = guard.BeginCapture();
            events.Raise(kind, 2);
            Assert.IsFalse(unchanged());
        }
    }

    [TestMethod]
    public void BackgroundTextAndNonCaretLocationChangesDoNotInvalidateCapture()
    {
        var events = new Events();
        using var guard = new WindowsFocusCaptureGuard(events);
        var unchanged = guard.BeginCapture();
        events.Raise(WindowsFocusCaptureGuard.TextSelectionEvent, 99);
        events.Raise(WindowsFocusCaptureGuard.LocationEvent, 2, 0);
        Assert.IsTrue(unchanged());
        events.Raise(WindowsFocusCaptureGuard.LocationEvent, 2, WindowsFocusCaptureGuard.CaretObject);
        Assert.IsFalse(unchanged());
    }

    [TestMethod]
    public void ForegroundChangesAndReturningCannotReviveOldCapture()
    {
        var events = new Events();
        using var guard = new WindowsFocusCaptureGuard(events);
        var unchanged = guard.BeginCapture();
        events.Raise(WindowsFocusCaptureGuard.ForegroundEvent, 99);
        events.Raise(WindowsFocusCaptureGuard.ForegroundEvent, 1);
        Assert.IsFalse(unchanged());
        Assert.IsTrue(guard.BeginCapture()());
    }

    [TestMethod]
    public void ChangedFocusedHandleMissingHooksAndDisposalRejectCapture()
    {
        var events = new Events();
        var guard = new WindowsFocusCaptureGuard(events);
        var unchanged = guard.BeginCapture();
        events.Position = new(1, 3);
        Assert.IsFalse(unchanged());
        events.Available = false;
        Assert.IsFalse(guard.BeginCapture()());
        events.Available = true;
        var beforeDispose = guard.BeginCapture();
        guard.Dispose();
        Assert.IsFalse(beforeDispose());
    }

    private class FallbackTarget : IWindowsTextTarget
    {
        public bool Writable { get; init; } = true;
        public bool IsFocused => true;
        public virtual bool SupportsReplacement => false;
        public bool CanPasteFallback => Writable;
        public bool IsPreparedSelectionCurrent => true;
        public bool HasAttemptedWrite => false;
        public TextTargetUpdateResult TryReplace(string previousText, string replacementText, Action<string> paste)
            => throw new AssertFailedException("Capture must not perform range replacement.");
    }

    private sealed class ReplacementTarget : FallbackTarget
    {
        public override bool SupportsReplacement => true;
    }

    private sealed class Events : IFocusCaptureEvents
    {
        public bool Available { get; set; } = true;
        public FocusCapturePosition Position { get; set; } = new(1, 2);
        public event Action<FocusCaptureEvent>? Changed;
        public FocusCapturePosition ReadFocus() => Position;
        public bool IsInFocusedHierarchy(IntPtr window) => window == Position.Focused;
        public void Raise(uint kind, int window, int objectId = 0, uint? timestamp = null) =>
            Changed?.Invoke(new(kind, new IntPtr(window), objectId, 0,
                timestamp ?? unchecked((uint)Environment.TickCount64)));
        public void Dispose() { }
    }
}
