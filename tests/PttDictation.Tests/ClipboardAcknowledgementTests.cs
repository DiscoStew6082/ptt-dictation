using PttDictation.App;

namespace PttDictation.Tests;

[TestClass]
public sealed class ClipboardAcknowledgementTests
{
    [TestMethod]
    public void ClipboardPayloadSurvivesRestoreTimerUntilAcknowledged()
    {
        var clipboard = new ClipboardBackend();
        var queue = new RestoreQueue();
        var paster = Create(clipboard, queue);
        var lease = paster.PasteToCurrentTargetAwaitingAcknowledgement("delayed words", () => true);

        queue.RunAll(); // All timer work has become due, but the editor has not read it yet.
        Assert.AreEqual("delayed words", clipboard.Text);
        Assert.AreEqual(0, clipboard.Restores);
        Assert.AreEqual(1, clipboard.Sends);

        lease.Acknowledge();
        lease.Dispose();
        queue.RunAll();
        Assert.AreSame(clipboard.Original, clipboard.Current);
        Assert.AreEqual(1, clipboard.Restores);
        Assert.AreEqual(1, clipboard.Sends);
    }

    [TestMethod]
    public void AcknowledgementCannotOverwriteANewerUserClipboard()
    {
        var clipboard = new ClipboardBackend();
        var queue = new RestoreQueue();
        var lease = Create(clipboard, queue).PasteToCurrentTargetAwaitingAcknowledgement("words", () => true);
        clipboard.SetText("new user copy");
        lease.Acknowledge();
        queue.RunAll();
        Assert.AreEqual("new user copy", clipboard.Text);
        Assert.AreEqual(0, clipboard.Restores);
    }

    [TestMethod]
    public void ConsecutiveAcknowledgementsPreserveOriginalSnapshot()
    {
        var clipboard = new ClipboardBackend();
        var queue = new RestoreQueue();
        var paster = Create(clipboard, queue);
        var first = paster.PasteToCurrentTargetAwaitingAcknowledgement("first", () => true);
        first.Acknowledge();
        var second = paster.PasteToCurrentTargetAwaitingAcknowledgement("second", () => true);
        queue.RunAll();
        Assert.AreEqual("second", clipboard.Text, "An older lease must not restore over the current write.");
        second.Acknowledge();
        queue.RunAll();
        Assert.AreSame(clipboard.Original, clipboard.Current);
        Assert.AreEqual(2, clipboard.Sends);
        Assert.AreEqual(1, clipboard.Restores);
    }

    [TestMethod]
    public void ValidationFailureRestoresWithoutSendingPaste()
    {
        var clipboard = new ClipboardBackend();
        var queue = new RestoreQueue();
        var validations = 0;
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            Create(clipboard, queue).PasteToCurrentTargetAwaitingAcknowledgement("words", () => ++validations == 1));
        queue.RunAll();
        Assert.AreSame(clipboard.Original, clipboard.Current);
        Assert.AreEqual(0, clipboard.Sends);
    }

    [TestMethod]
    public void AmbiguousSendFailureRetainsPayloadWithoutRetryingInput()
    {
        var clipboard = new ClipboardBackend { FailSend = true };
        var queue = new RestoreQueue();
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            Create(clipboard, queue).PasteToCurrentTargetAwaitingAcknowledgement("words", () => true));
        queue.RunAll();
        Assert.AreEqual("words", clipboard.Text, "A queued paste may still consume its payload after SendInput throws.");
        Assert.AreEqual(1, clipboard.Sends);
        Assert.AreEqual(0, clipboard.Restores);
    }

    [TestMethod]
    public void LiveReplacementReleasesOnlyAfterTargetAcknowledgement()
    {
        var clipboard = new ClipboardBackend();
        var queue = new RestoreQueue();
        var paster = Create(clipboard, queue);
        var target = new Target();
        using var output = Live(target, paster);
        output.CaptureTarget();
        output.UpdatePreview("delayed words");
        output.Pump();
        for (var attempt = 0; attempt < 5; attempt++) { queue.RunAll(); output.Pump(); }
        Assert.AreEqual("delayed words", clipboard.Text);
        Assert.AreEqual(1, clipboard.Sends, "Pending acknowledgement must never send another paste.");
        target.Acknowledged = true;
        output.Pump();
        queue.RunAll();
        Assert.AreSame(clipboard.Original, clipboard.Current);
        Assert.AreEqual(1, clipboard.Restores);
    }

    [TestMethod]
    [DataRow("cancel")]
    [DataRow("dispose")]
    [DataRow("conflict")]
    public void TerminalSessionCannotRestoreOriginalBeneathQueuedPaste(string terminal)
    {
        var clipboard = new ClipboardBackend();
        var queue = new RestoreQueue();
        var target = new Target();
        using var output = Live(target, Create(clipboard, queue));
        output.CaptureTarget();
        output.UpdatePreview("words");
        output.Pump();
        Assert.AreEqual(0, queue.PendingCount);
        switch (terminal)
        {
            case "cancel": output.EndSession(); break;
            case "dispose": output.Dispose(); break;
            case "conflict": target.Conflict = true; output.Pump(); break;
        }
        output.EndSession();
        queue.RunAll();
        Assert.AreEqual("words", clipboard.Text, "The delayed consumer must receive dictation, never the user's original clipboard.");
        Assert.AreEqual(0, clipboard.Restores);
        Assert.AreEqual(1, clipboard.Sends);
    }

    [TestMethod]
    [DataRow("cancel")]
    [DataRow("dispose")]
    [DataRow("watchdog")]
    public void TerminalStateBeforeLeasePublicationRetainsPayload(string terminal)
    {
        var clipboard = new ClipboardBackend();
        var queue = new RestoreQueue();
        var paster = Create(clipboard, queue);
        long now = 100;
        LiveClipboardPaster? current = null;
        using var output = new LiveClipboardPaster(() => new Target(), paster.PasteToCurrentTarget,
            tickCount: () => now,
            pasteAwaitingAcknowledgement: (text, guard) =>
            {
                var lease = paster.PasteToCurrentTargetAwaitingAcknowledgement(text, guard);
                switch (terminal)
                {
                    case "cancel": current!.EndSession(); break;
                    case "dispose": current!.Dispose(); break;
                    case "watchdog": now += 5001; current!.Pump(); break;
                }
                return lease;
            });
        current = output;
        output.CaptureTarget();
        output.UpdatePreview("words");
        output.Pump();
        queue.RunAll();
        Assert.AreEqual("words", clipboard.Text);
        Assert.AreEqual(0, clipboard.Restores);
        Assert.AreEqual(1, clipboard.Sends);
    }

    [TestMethod]
    public async Task FallbackKeepsItsExistingDelayedRestoration()
    {
        var clipboard = new ClipboardBackend();
        var queue = new RestoreQueue();
        var paster = Create(clipboard, queue);
        using var output = new LiveClipboardPaster(() => new Target { SupportsReplacement = false },
            paster.PasteToCurrentTarget,
            pasteAwaitingAcknowledgement: (_, _) => throw new AssertFailedException("Fallback has no replacement acknowledgement."));
        output.CaptureTarget();
        await output.PasteAsync("words", CancellationToken.None);
        Assert.AreEqual(1, queue.PendingCount);
        queue.RunAll();
        Assert.AreSame(clipboard.Original, clipboard.Current);
    }

    [TestMethod]
    public void AbandonedPasteIgnoresLateAcknowledgementAndChainsOriginalSnapshot()
    {
        var clipboard = new ClipboardBackend();
        var queue = new RestoreQueue();
        var paster = Create(clipboard, queue);
        var abandoned = paster.PasteToCurrentTargetAwaitingAcknowledgement("old words", () => true);
        abandoned.Dispose();
        abandoned.Acknowledge();
        queue.RunAll();
        Assert.AreEqual("old words", clipboard.Text);
        Assert.AreEqual(0, clipboard.Restores);

        var next = paster.PasteToCurrentTargetAwaitingAcknowledgement("next words", () => true);
        next.Acknowledge();
        queue.RunAll();
        Assert.AreSame(clipboard.Original, clipboard.Current, "The next acknowledged write must restore the initial snapshot.");
        Assert.AreEqual(1, clipboard.Restores);
    }

    [TestMethod]
    public void AbandonmentLeavesNewerUserClipboardUntouched()
    {
        var clipboard = new ClipboardBackend();
        var queue = new RestoreQueue();
        var lease = Create(clipboard, queue).PasteToCurrentTargetAwaitingAcknowledgement("words", () => true);
        clipboard.SetText("new user copy");
        lease.Dispose();
        lease.Acknowledge();
        queue.RunAll();
        Assert.AreEqual("new user copy", clipboard.Text);
        Assert.AreEqual(0, clipboard.Restores);
    }

    private static ClipboardPaster Create(ClipboardBackend clipboard, RestoreQueue queue) =>
        new(clipboard, queue, new Foreground());

    [TestMethod]
    public void CancellationDuringAcknowledgementAbandonsClipboardAndStopsFurtherWrites()
    {
        var clipboard = new ClipboardBackend();
        var queue = new RestoreQueue();
        var target = new Target();
        using var output = Live(target, Create(clipboard, queue));
        output.CaptureTarget();
        output.UpdatePreview("words");
        output.Pump();
        target.OnAcknowledgement = output.EndSession;
        target.Acknowledged = true;
        output.Pump();
        queue.RunAll();
        Assert.AreEqual("words", clipboard.Text);
        Assert.AreEqual(0, clipboard.Restores);
        Assert.AreEqual(1, clipboard.Sends);
        output.Pump();
        Assert.AreEqual(1, clipboard.Sends);
    }

    private static LiveClipboardPaster Live(Target target, ClipboardPaster clipboard) =>
        new(() => target, clipboard.PasteToCurrentTarget,
            pasteAwaitingAcknowledgement: clipboard.PasteToCurrentTargetAwaitingAcknowledgement);

    private sealed class RestoreQueue : IClipboardRestoreQueue
    {
        private readonly Queue<Action> _work = new();
        public int PendingCount => _work.Count;
        public void Enqueue(Action restore) => _work.Enqueue(restore);
        public void EnqueueImmediate(Action restore) => _work.Enqueue(restore);
        public void RunAll() { while (_work.TryDequeue(out var restore)) restore(); }
    }

    private sealed class ClipboardBackend : IClipboardPasteBackend
    {
        private uint _sequence = 1;
        public IDataObject Original { get; } = new DataObject(DataFormats.UnicodeText, "original");
        public IDataObject? Current { get; private set; }
        public int Sends { get; private set; }
        public int Restores { get; private set; }
        public bool FailSend;
        public string? Text => Current?.GetData(DataFormats.UnicodeText) as string;
        public IDataObject? GetDataObject() => Current ??= Original;
        public uint SetText(string text) { Current = new DataObject(DataFormats.UnicodeText, text); return ++_sequence; }
        public bool IsSequenceCurrent(uint sequence) => sequence == _sequence;
        public bool IsCurrent(uint sequence, string text) => IsSequenceCurrent(sequence) && Text == text;
        public void SendPaste()
        {
            Sends++;
            if (FailSend) throw new InvalidOperationException("Injected ambiguous SendInput failure.");
        }
        public void RestoreIfCurrent(uint sequence, IDataObject? previous)
        {
            if (!IsSequenceCurrent(sequence)) return;
            Current = previous;
            _sequence++;
            Restores++;
        }
    }

    private sealed class Foreground : IForegroundWindowBackend
    {
        public IntPtr GetForegroundWindow() => (IntPtr)1;
        public bool IsWindow(IntPtr window) => true;
        public bool SetForegroundWindow(IntPtr window) => true;
    }

    private sealed class Target : IWindowsTextTarget
    {
        public bool Acknowledged;
        public Action? OnAcknowledgement;
        public bool Conflict;
        public bool IsFocused => true;
        public bool SupportsReplacement { get; init; } = true;
        public bool CanPasteFallback => true;
        public bool IsPreparedSelectionCurrent => true;
        public bool HasAttemptedWrite { get; private set; }
        public TextTargetUpdateResult TryReplace(string previous, string replacement, Action<string> paste)
        {
            if (Conflict) return TextTargetUpdateResult.Conflict;
            if (HasAttemptedWrite)
            {
                OnAcknowledgement?.Invoke();
                return Acknowledged ? TextTargetUpdateResult.Success : TextTargetUpdateResult.Pending;
            }
            HasAttemptedWrite = true;
            paste(replacement);
            return TextTargetUpdateResult.Pending;
        }
    }
}
