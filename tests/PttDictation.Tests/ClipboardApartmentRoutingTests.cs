using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using PttDictation.App;

namespace PttDictation.Tests;

[TestClass]
public sealed class ClipboardApartmentRoutingTests
{
    [TestMethod]
    public async Task TargetValidationAndSubmissionStayOnMtaWhileClipboardUsesSta()
    {
        using var automation = new ApartmentWorker(ApartmentState.MTA, "Routing automation test");
        using var clipboardWorker = new ApartmentWorker(ApartmentState.STA, "Routing clipboard test");
        var backend = new Backend();
        var target = new Target();
        var queue = new RestoreQueue();
        var clipboard = new ClipboardPaster(backend, queue, new Foreground(), clipboardWorker);
        using var output = new LiveClipboardPaster(() => target, clipboard.PasteToCurrentTarget,
            automationWorker: automation, clipboardWorker: clipboardWorker,
            pasteAwaitingAcknowledgement: clipboard.PasteToCurrentTargetAwaitingAcknowledgement);
        output.CaptureTarget();
        await automation.InvokeAsync(() => { });
        output.UpdatePreview("words");
        output.Pump();
        await automation.InvokeAsync(() => { });
        Assert.AreEqual(1, backend.Sends);
        Assert.IsTrue(target.Apartments.Count > 0);
        Assert.IsTrue(target.Apartments.All(apartment => apartment == ApartmentState.MTA),
            "No UIA target access may occur on the clipboard STA.");
        Assert.IsTrue(backend.Calls.Where(call => call.Name != "sequence" && call.Name != "send")
            .All(call => call.Apartment == ApartmentState.STA));
        Assert.AreEqual(ApartmentState.MTA, backend.Calls.Single(call => call.Name == "send").Apartment);
        target.Acknowledged = true;
        output.Pump();
        await automation.InvokeAsync(() => { });
        queue.RunAll();
        Assert.AreSame(backend.Original, backend.Current);
        Assert.AreEqual(ApartmentState.STA, backend.Calls.Single(call => call.Name == "restore").Apartment);
    }

    [TestMethod]
    public async Task IdentityFactoryRunsOnAutomationWorkerAfterNativeBoundary()
    {
        using var automation = new ApartmentWorker(ApartmentState.MTA, "Identity test");
        var workerId = 0;
        await automation.InvokeAsync(() => workerId = Environment.CurrentManagedThreadId);
        var callerId = Environment.CurrentManagedThreadId;
        var observed = new ConcurrentQueue<(string Stage, int Thread)>();
        var target = new Target();
        using var output = new LiveClipboardPaster(() => target, (_, _) => { },
            automationWorker: automation,
            beginCaptureGuard: () => { observed.Enqueue(("guard", Environment.CurrentManagedThreadId)); return () => true; },
            prepareCapture: () => { observed.Enqueue(("identity", Environment.CurrentManagedThreadId)); return () => target; });
        output.CaptureTarget();
        await automation.InvokeAsync(() => { });
        var events = observed.ToArray();
        Assert.AreEqual(("guard", callerId), events[0]);
        Assert.AreEqual(("identity", workerId), events[1]);
    }

    [TestMethod]
    public async Task CancellationAfterClipboardPreparationCannotSubmitInput()
    {
        using var automation = new ApartmentWorker(ApartmentState.MTA, "Cancellation automation test");
        using var clipboardWorker = new ApartmentWorker(ApartmentState.STA, "Cancellation clipboard test");
        var backend = new Backend();
        var queue = new RestoreQueue();
        var clipboard = new ClipboardPaster(backend, queue, new Foreground(), clipboardWorker);
        using var output = new LiveClipboardPaster(() => new Target(), clipboard.PasteToCurrentTarget,
            automationWorker: automation, clipboardWorker: clipboardWorker,
            pasteAwaitingAcknowledgement: clipboard.PasteToCurrentTargetAwaitingAcknowledgement);
        backend.AfterSet = output.EndSession;
        output.CaptureTarget();
        await automation.InvokeAsync(() => { });
        output.UpdatePreview("words");
        output.Pump();
        await automation.InvokeAsync(() => { });
        queue.RunAll();
        Assert.AreEqual(0, backend.Sends);
        Assert.AreSame(backend.Original, backend.Current);
    }

    [TestMethod]
    public async Task ClipboardChangeDuringFinalTargetValidationCannotSubmitInput()
    {
        using var clipboardWorker = new ApartmentWorker(ApartmentState.STA, "Final guard clipboard test");
        var backend = new Backend();
        var queue = new RestoreQueue();
        var clipboard = new ClipboardPaster(backend, queue, new Foreground(), clipboardWorker);
        var validations = 0;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Task.Run(() =>
            clipboard.PasteToCurrentTargetAwaitingAcknowledgement("words", () =>
            {
                if (++validations == 2) backend.ReplaceExternally("new copy");
                return true;
            })));
        queue.RunAll();
        Assert.AreEqual(0, backend.Sends);
        Assert.AreEqual("new copy", backend.Current?.GetData(DataFormats.UnicodeText));
    }

    [TestMethod]
    public async Task IdleClipboardApartmentDispatchesNativeMessages()
    {
        using var worker = new ApartmentWorker(ApartmentState.STA, "STA message test");
        MessageWindow? window = null;
        try
        {
            await worker.InvokeAsync(() => window = new MessageWindow());
            Assert.IsNotNull(window);
            Assert.IsTrue(PostMessage(window.Handle, MessageWindow.ProbeMessage, IntPtr.Zero, IntPtr.Zero));
            await window.Received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            if (window is not null) await worker.InvokeAsync(window.DestroyHandle);
        }
    }

    [TestMethod]
    public async Task ClipboardApartmentDoesNotReenterQueuedWorkWhilePumpingMessages()
    {
        using var worker = new ApartmentWorker(ApartmentState.STA, "Reentrant clipboard test");
        using var entered = new ManualResetEventSlim();
        using var nextQueued = new ManualResetEventSlim();
        var events = new ConcurrentQueue<string>();
        var first = worker.InvokeAsync(() =>
        {
            events.Enqueue("first start");
            entered.Set();
            Assert.IsTrue(nextQueued.Wait(TimeSpan.FromSeconds(2)));
            Application.DoEvents();
            events.Enqueue("first end");
        });
        Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(2)));
        var second = worker.InvokeAsync(() => events.Enqueue("second"));
        nextQueued.Set();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        CollectionAssert.AreEqual(new[] { "first start", "first end", "second" }, events.ToArray());
    }

    [TestMethod]
    public async Task AcknowledgedRestorationSurvivesClipboardWorkerDisposal()
    {
        using var worker = new ApartmentWorker(ApartmentState.STA, "Retiring clipboard test");
        using var restoreQueue = new ClipboardRestoreQueue(TimeSpan.Zero);
        using var queueBlocked = new ManualResetEventSlim();
        using var allowRestore = new ManualResetEventSlim();
        restoreQueue.EnqueueImmediate(() => { queueBlocked.Set(); allowRestore.Wait(TimeSpan.FromSeconds(5)); });
        Assert.IsTrue(queueBlocked.Wait(TimeSpan.FromSeconds(2)));
        var backend = new Backend();
        try
        {
            var clipboard = new ClipboardPaster(backend, restoreQueue, new Foreground(), worker);
            var lease = await Task.Run(() => clipboard.PasteToCurrentTargetAwaitingAcknowledgement("words", () => true));
            lease.Acknowledge();
            worker.Dispose();
            allowRestore.Set();
            await backend.Restored.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreSame(backend.Original, backend.Current);
            Assert.AreEqual(ApartmentState.STA, backend.Calls.Single(call => call.Name == "restore").Apartment);
        }
        finally { allowRestore.Set(); }
    }

    private sealed class MessageWindow : NativeWindow
    {
        public const int ProbeMessage = 0x8001;
        public TaskCompletionSource Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public MessageWindow() => CreateHandle(new CreateParams { Parent = (IntPtr)(-3), Caption = "PTT owned STA message test" });
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == ProbeMessage) Received.TrySetResult();
            base.WndProc(ref message);
        }
    }

    private sealed class Target : IWindowsTextTarget
    {
        public ConcurrentQueue<ApartmentState> Apartments { get; } = new();
        private bool Observe(bool result) { Apartments.Enqueue(Thread.CurrentThread.GetApartmentState()); return result; }
        public bool Acknowledged;
        public bool IsFocused => Observe(true);
        public bool SupportsReplacement => Observe(true);
        public bool CanPasteFallback => Observe(true);
        public bool IsPreparedSelectionCurrent => Observe(true);
        public bool HasAttemptedWrite { get; private set; }
        public TextTargetUpdateResult TryReplace(string previous, string replacement, Action<string> paste)
        {
            Observe(true);
            if (HasAttemptedWrite) return Acknowledged ? TextTargetUpdateResult.Success : TextTargetUpdateResult.Pending;
            HasAttemptedWrite = true;
            paste(replacement);
            return TextTargetUpdateResult.Pending;
        }
    }

    private sealed class Backend : IClipboardPasteBackend
    {
        private uint _sequence = 1;
        public ConcurrentQueue<(string Name, ApartmentState Apartment)> Calls { get; } = new();
        public IDataObject Original { get; } = new DataObject(DataFormats.UnicodeText, "original");
        public IDataObject? Current;
        public Action? AfterSet;
        public int Sends;
        public TaskCompletionSource Restored { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private void Record(string name) => Calls.Enqueue((name, Thread.CurrentThread.GetApartmentState()));
        public IDataObject? GetDataObject() { Record("snapshot"); return Current ??= Original; }
        public uint SetText(string text)
        {
            Record("set");
            Current = new DataObject(DataFormats.UnicodeText, text);
            var sequence = ++_sequence;
            AfterSet?.Invoke();
            return sequence;
        }
        public void ReplaceExternally(string text) { Current = new DataObject(DataFormats.UnicodeText, text); _sequence++; }
        public bool IsSequenceCurrent(uint sequence) { Record("sequence"); return sequence == _sequence; }
        public bool IsCurrent(uint sequence, string text) { Record("validate"); return sequence == _sequence && Equals(text, Current?.GetData(DataFormats.UnicodeText)); }
        public void SendPaste() { Record("send"); Sends++; }
        public void RestoreIfCurrent(uint sequence, IDataObject? previous)
        {
            Record("restore");
            if (_sequence != sequence) return;
            Current = previous;
            _sequence++;
            Restored.TrySetResult();
        }
    }

    private sealed class RestoreQueue : IClipboardRestoreQueue
    {
        private readonly ConcurrentQueue<Action> _work = new();
        public void Enqueue(Action restore) => _work.Enqueue(restore);
        public void EnqueueImmediate(Action restore) => _work.Enqueue(restore);
        public void RunAll() { while (_work.TryDequeue(out var restore)) restore(); }
    }

    private sealed class Foreground : IForegroundWindowBackend
    {
        public IntPtr GetForegroundWindow() => (IntPtr)1;
        public bool IsWindow(IntPtr window) => true;
        public bool SetForegroundWindow(IntPtr window) => true;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
