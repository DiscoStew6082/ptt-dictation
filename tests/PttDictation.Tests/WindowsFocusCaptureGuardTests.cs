using PttDictation.App;

namespace PttDictation.Tests;

[TestClass]
public sealed class WindowsFocusCaptureGuardTests
{
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

    private sealed class Events : IFocusCaptureEvents
    {
        public bool Available { get; set; } = true;
        public FocusCapturePosition Position { get; set; } = new(1, 2);
        public event Action<uint, IntPtr, int>? Changed;
        public FocusCapturePosition ReadFocus() => Position;
        public bool IsInFocusedHierarchy(IntPtr window) => window == Position.Focused;
        public void Raise(uint kind, int window, int objectId = 0) => Changed?.Invoke(kind, new IntPtr(window), objectId);
        public void Dispose() { }
    }
}
