using System.Windows.Automation;
using PttDictation.App;
using PttDictation.Core;

namespace PttDictation.Tests;

[TestClass]
public sealed class LiveInsertionFailureWorkflowTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task AlreadyAcknowledgedWordsDoNotBecomeFailureWhenEditorReferenceRetires(int failureStage)
    {
        var surface = new VisibleSurface();
        using var output = new LiveClipboardPaster(() => new WindowsTextTarget(surface),
            (text, current) => { Assert.IsTrue(current()); surface.Paste(text); });
        var session = new FinishingSession();
        var history = new SessionHistory();
        var workflow = new DictationWorkflow(new SingleDictationSessionFactory(session), output, history);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        workflow.StateChanged += state =>
        {
            if (state.Phase is DictationWorkflowPhase.Failed or DictationWorkflowPhase.InsertedPreview)
                ended.TrySetResult();
        };
        await workflow.HandleAsync(DictationIntent.Toggle, CancellationToken.None);
        session.Preview("all words already inserted");
        output.Pump();
        output.Pump(); // Provider confirms the actual document contains the write.
        var finish = failureStage > 0 ? workflow.HandleAsync(DictationIntent.Toggle, CancellationToken.None) : Task.CompletedTask;
        surface.Accessible = false;
        if (failureStage < 2) output.Pump(); // Stage 2 loses access inside the final PasteAsync.
        await session.StopRequested.Task.WaitAsync(TimeSpan.FromSeconds(1));
        session.Final.TrySetResult("all words already inserted");
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await finish.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.AreEqual(DictationWorkflowPhase.InsertedPreview, workflow.CurrentState.Phase);
        Assert.IsNull(workflow.CurrentState.ErrorMessage);
        Assert.AreEqual("all words already inserted", workflow.CurrentState.Transcript,
            "Report the text actually delivered; final capitalization and punctuation were not pasted.");
        Assert.AreEqual("all words already inserted", history.Items.Single());
        Assert.AreEqual("All words already inserted.", history.Entries.Single().Comparison!.FinalText);
        Assert.AreEqual("Existing all words already inserted", surface.Document);
        Assert.AreEqual(1, surface.Writes, "Never send a second paste to a retired or replacement editor.");
        Assert.IsFalse(session.Recording);
    }

    [TestMethod]
    public async Task CancellationDiscardsEligibleDeliveryAndNextCaptureStartsWithoutIt()
    {
        var surface = new VisibleSurface();
        using var output = new LiveClipboardPaster(() => new WindowsTextTarget(surface),
            (text, current) => { Assert.IsTrue(current()); surface.Paste(text); });
        var first = new FinishingSession();
        var next = new FinishingSession();
        var history = new SessionHistory();
        var workflow = new DictationWorkflow(new SequenceDictationSessionFactory(first, next), output, history);
        await workflow.HandleAsync(DictationIntent.Toggle, CancellationToken.None);
        first.Preview("already inserted");
        output.Pump();
        output.Pump();
        var finish = workflow.HandleAsync(DictationIntent.Toggle, CancellationToken.None);
        surface.Accessible = false;
        output.Pump();
        Assert.IsTrue(output.FailureDelivery!.CanCompleteFromAcknowledgedText);
        await workflow.HandleAsync(DictationIntent.Cancel, CancellationToken.None);
        first.Final.TrySetResult("already inserted");
        await finish.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.AreEqual(DictationWorkflowPhase.Cancelled, workflow.CurrentState.Phase);
        Assert.AreEqual(0, history.Items.Count);
        Assert.IsNull(output.FailureDelivery);

        surface.Accessible = true;
        await workflow.HandleAsync(DictationIntent.Toggle, CancellationToken.None);
        Assert.IsNull(output.FailureDelivery);
        Assert.AreEqual(DictationWorkflowPhase.Recording, workflow.CurrentState.Phase);
        await workflow.HandleAsync(DictationIntent.Cancel, CancellationToken.None);
        Assert.AreEqual(1, surface.Writes);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HistoricalAcknowledgementCannotHideAnAmbiguousLaterWriteOrDocumentConflict(bool pendingWrite)
    {
        var surface = new VisibleSurface();
        using var output = new LiveClipboardPaster(() => new WindowsTextTarget(surface),
            (text, current) => { Assert.IsTrue(current()); surface.Paste(text); });
        var session = new FinishingSession();
        var workflow = new DictationWorkflow(new SingleDictationSessionFactory(session), output, new SessionHistory());
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        workflow.StateChanged += state => { if (state.Phase == DictationWorkflowPhase.Failed) ended.TrySetResult(); };
        await workflow.HandleAsync(DictationIntent.Toggle, CancellationToken.None);
        session.Preview("old acknowledged words");
        output.Pump();
        output.Pump();
        if (pendingWrite)
        {
            surface.DelayNextPaste = true;
            session.Preview("different words not yet acknowledged");
            output.Pump();
            surface.Accessible = false;
        }
        else surface.AppendUserEdit();
        output.Pump();
        await session.StopRequested.Task.WaitAsync(TimeSpan.FromSeconds(1));
        session.Final.TrySetResult("old acknowledged words");
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.AreEqual(DictationWorkflowPhase.Failed, workflow.CurrentState.Phase);
        Assert.AreEqual(pendingWrite ? 2 : 1, surface.Writes);
    }

    [TestMethod]
    public async Task DelayedFailureCannotStopOrOverwriteTheNextRecording()
    {
        var dispatcher = new QueuedContext();
        var first = new FinishingSession();
        var next = new FinishingSession();
        var output = new SignallingOutput();
        var workflow = new DictationWorkflow(new SequenceDictationSessionFactory(first, next),
            output, new SessionHistory(), synchronizationContext: dispatcher);
        await workflow.HandleAsync(DictationIntent.Toggle, CancellationToken.None);
        var oldPreview = first.PendingPreview("old words must never reach the new textbox");
        output.Fail();
        Assert.AreEqual(1, dispatcher.Count);
        await workflow.HandleAsync(DictationIntent.Cancel, CancellationToken.None);
        await workflow.HandleAsync(DictationIntent.Toggle, CancellationToken.None);
        try
        {
            oldPreview();
            dispatcher.RunAll();
            Assert.IsTrue(next.Recording);
            Assert.AreEqual(0, next.StopCount);
            Assert.AreEqual(DictationWorkflowPhase.Recording, workflow.CurrentState.Phase);
            Assert.AreEqual("", workflow.CurrentState.Transcript);
            Assert.IsNull(output.PreviewText);
        }
        finally
        {
            first.Final.TrySetResult("first");
            next.Final.TrySetResult("next");
            await workflow.HandleAsync(DictationIntent.Cancel, CancellationToken.None);
        }
    }

    [TestMethod]
    public async Task FailureDuringRecorderStartupStopsAfterStartupAndRetainsRecognition()
    {
        var session = new FinishingSession { StartRelease = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        session.Final.TrySetResult("completed words");
        var output = new SignallingOutput();
        var history = new SessionHistory();
        var workflow = new DictationWorkflow(new SingleDictationSessionFactory(session), output, history);
        var start = workflow.HandleAsync(DictationIntent.Toggle, CancellationToken.None);
        output.Fail();
        Assert.AreEqual(0, session.StopCount);
        session.StartRelease.TrySetResult();
        await start.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.IsFalse(session.Recording);
        Assert.AreEqual(1, session.StopCount);
        Assert.AreEqual(DictationWorkflowPhase.Failed, workflow.CurrentState.Phase);
        Assert.AreEqual("Completed words.", history.Items.Single());
        Assert.AreEqual(0, output.Pastes);
    }

    [TestMethod]
    public async Task UserStopAndQueuedFailureShareOneStopAndRemainCancellable()
    {
        var dispatcher = new QueuedContext();
        var session = new FinishingSession();
        var output = new SignallingOutput();
        var history = new SessionHistory();
        var workflow = new DictationWorkflow(new SingleDictationSessionFactory(session), output, history,
            synchronizationContext: dispatcher);
        await workflow.HandleAsync(DictationIntent.Toggle, CancellationToken.None);
        output.Fail();
        var finish = workflow.HandleAsync(DictationIntent.Toggle, CancellationToken.None);
        await session.StopRequested.Task.WaitAsync(TimeSpan.FromSeconds(1));
        dispatcher.RunAll();
        Assert.AreEqual(1, session.StopCount);
        await workflow.HandleAsync(DictationIntent.Cancel, CancellationToken.None);
        await finish.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.AreEqual(0, session.CancelCount, "StopAsync already owns releasing the recorder.");
        Assert.AreEqual(DictationWorkflowPhase.Cancelled, workflow.CurrentState.Phase);
        Assert.AreEqual(0, history.Items.Count);
        Assert.AreEqual(0, output.Pastes);
    }

    [TestMethod]
    public async Task ANewRecordingWorksAfterAutomaticFailureFinishes()
    {
        var dispatcher = new QueuedContext();
        var first = new FinishingSession();
        var next = new FinishingSession();
        first.Final.TrySetResult("preserved old words");
        next.Final.TrySetResult("new successful words");
        var output = new SignallingOutput();
        var history = new SessionHistory();
        var workflow = new DictationWorkflow(new SequenceDictationSessionFactory(first, next), output, history,
            synchronizationContext: dispatcher);
        await workflow.HandleAsync(DictationIntent.Toggle, CancellationToken.None);
        output.Fail();
        dispatcher.RunAll();
        Assert.AreEqual(DictationWorkflowPhase.Failed, workflow.CurrentState.Phase);
        await workflow.HandleAsync(DictationIntent.Toggle, CancellationToken.None);
        Assert.IsTrue(next.Recording);
        await workflow.HandleAsync(DictationIntent.Toggle, CancellationToken.None);
        Assert.AreEqual(DictationWorkflowPhase.Pasted, workflow.CurrentState.Phase);
        Assert.AreEqual(1, output.Pastes);
        CollectionAssert.AreEquivalent(new[] { "New successful words.", "Preserved old words." }, history.Items.ToArray());
    }

    [TestMethod]
    public async Task UnavailableInsertionDuringRecordingStopsAudioAndRetainsFinalTextWithoutAnotherHotkey()
    {
        var surface = new VisibleSurface();
        using var output = new LiveClipboardPaster(() => new WindowsTextTarget(surface),
            (text, current) => { Assert.IsTrue(current()); surface.Paste(text); });
        var session = new FinishingSession();
        var history = new SessionHistory();
        var workflow = new DictationWorkflow(new SingleDictationSessionFactory(session), output, history);
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        workflow.StateChanged += state => { if (state.Phase == DictationWorkflowPhase.Failed) failed.TrySetResult(); };
        await workflow.HandleAsync(DictationIntent.Toggle, CancellationToken.None);
        session.Preview("words already inserted");
        output.Pump();
        output.Pump();
        Assert.AreEqual("Existing words already inserted", surface.Document);

        try
        {
            surface.Accessible = false; // Visible editor and caret remain; OS access fails.
            output.Pump();
            await session.StopRequested.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.IsFalse(session.Recording, "An output failure must stop microphone recording without another hotkey.");
            Assert.AreEqual(DictationWorkflowPhase.Processing, workflow.CurrentState.Phase);
            output.Pump();
            session.Preview("late preview must not revive recording");
            Assert.AreEqual(1, session.StopCount);
            session.Final.TrySetResult("all completed spoken words");
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.AreEqual("All completed spoken words.", history.Items.Single());
            Assert.AreEqual("Existing words already inserted", surface.Document);
            Assert.AreEqual(1, surface.Writes);
            StringAssert.Contains(workflow.CurrentState.ErrorMessage!, "Session History");
        }
        finally
        {
            session.Final.TrySetResult("all completed spoken words");
            await workflow.HandleAsync(DictationIntent.Cancel, CancellationToken.None);
        }
    }

    private sealed class FinishingSession : IDictationSession
    {
        public event Action<TranscriptUpdate>? TranscriptUpdated;
        public TaskCompletionSource StopRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> Final { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Recording { get; private set; }
        public int StopCount { get; private set; }
        public int CancelCount { get; private set; }
        public TaskCompletionSource? StartRelease { get; init; }
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (StartRelease is not null) await StartRelease.Task.WaitAsync(cancellationToken);
            Recording = true;
        }
        public async Task<DictationSessionResult> StopAsync(CancellationToken cancellationToken)
        {
            Recording = false;
            StopCount++;
            StopRequested.TrySetResult();
            return new(new TranscriptResult(await Final.Task.WaitAsync(cancellationToken), null, null));
        }
        public Task CancelAsync(CancellationToken cancellationToken) { Recording = false; CancelCount++; return Task.CompletedTask; }
        public void Preview(string text) => TranscriptUpdated?.Invoke(new(TranscriptUpdateKind.Partial, text));
        public Action PendingPreview(string text)
        {
            var subscribers = TranscriptUpdated;
            return () => subscribers?.Invoke(new(TranscriptUpdateKind.Partial, text));
        }
    }

    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly Queue<Action> _pending = new();
        public int Count => _pending.Count;
        public override void Post(SendOrPostCallback callback, object? state) => _pending.Enqueue(() => callback(state));
        public void RunAll() { while (_pending.TryDequeue(out var work)) work(); }
    }

    private sealed class SignallingOutput : ILiveClipboardPaster
    {
        public event Action<Exception>? InsertionFailed;
        public string? PreviewText;
        public int Pastes;
        public void CaptureTarget() => PreviewText = null;
        public void EndSession() { }
        public void UpdatePreview(string text) => PreviewText = text;
        public Task PasteAsync(string text, CancellationToken cancellationToken) { Pastes++; return Task.CompletedTask; }
        public void Fail() => InsertionFailed?.Invoke(new InvalidOperationException("Automation reference is unavailable."));
    }

    private sealed class VisibleSurface : IWindowsTextSurface
    {
        public bool Accessible = true;
        public bool DelayNextPaste;
        private string _prefix = "Existing ", _selection = "";
        public string Document => _prefix + _selection;
        public int Writes;
        public bool IsFocused => Accessible ? true : throw new ElementNotAvailableException();
        public bool SupportsReplacement => true;
        public bool CanPasteFallback => true;
        public TextTargetSnapshot Read() => new(Document, _prefix, _selection, "");
        public bool Select(string prefix, string ownedText, string suffix)
        {
            if (Document != prefix + ownedText + suffix) return false;
            _prefix = prefix;
            _selection = ownedText;
            return true;
        }
        public void Paste(string text) { Writes++; if (DelayNextPaste) return; _prefix += text; _selection = ""; }
        public void AppendUserEdit() => _prefix += " user edit";
        public void RevealCaret() { }
    }
}
