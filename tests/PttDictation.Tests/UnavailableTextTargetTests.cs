using System.Windows.Automation;
using PttDictation.App;
using PttDictation.Core;

namespace PttDictation.Tests;

[TestClass]
public sealed class UnavailableTextTargetTests
{
    [TestMethod]
    public async Task FinishingWithoutFocusEventuallyReleasesWorkflowAndRetainsFinalTranscript()
    {
        var surface = new Surface();
        using var output = new LiveClipboardPaster(() => new WindowsTextTarget(surface),
            (text, current) => { Assert.IsTrue(current()); surface.Paste(text); });
        var session = new FakeDictationSession("complete final words");
        var history = new SessionHistory();
        var workflow = new DictationWorkflow(new FakeDictationSessionFactory(session), output, history);
        using var cancellation = new CancellationTokenSource();
        await workflow.HandleAsync(DictationIntent.Toggle, cancellation.Token);
        session.PublishPartial("first words");
        output.Pump();
        output.Pump();
        surface.Focused = false;
        var finish = workflow.HandleAsync(DictationIntent.Toggle, cancellation.Token);
        try
        {
            Assert.IsFalse(finish.IsCompleted);
            await Task.Delay(TimeSpan.FromMilliseconds(5100));
            output.Pump();
            await finish.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.AreEqual(DictationWorkflowPhase.Failed, workflow.CurrentState.Phase);
            Assert.AreEqual("Complete final words.", history.Items.Single());
            Assert.AreEqual("Existing text first words", surface.Document);
            Assert.AreEqual(1, surface.Writes);
            surface.Focused = true;
            await workflow.HandleAsync(DictationIntent.Toggle, cancellation.Token);
            Assert.AreEqual(DictationWorkflowPhase.Recording, workflow.CurrentState.Phase);
        }
        finally
        {
            await cancellation.CancelAsync();
            await workflow.HandleAsync(DictationIntent.Cancel, CancellationToken.None);
            await finish;
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task VanishedTextboxEndsProcessingRetainsTranscriptAndAllowsNextRecording(bool disappearWhileFinishing)
    {
        var surface = new Surface();
        using var output = new LiveClipboardPaster(() => new WindowsTextTarget(surface),
            (text, current) => { Assert.IsTrue(current()); surface.Paste(text); });
        var session = new FakeDictationSession("complete final words");
        var history = new SessionHistory();
        var workflow = new DictationWorkflow(new FakeDictationSessionFactory(session), output, history);
        using var cancellation = new CancellationTokenSource();
        await workflow.HandleAsync(DictationIntent.Toggle, cancellation.Token);
        session.PublishPartial("first words");
        output.Pump();
        output.Pump();
        Assert.AreEqual("Existing text first words", surface.Document);

        surface.Focused = false;
        if (!disappearWhileFinishing)
        {
            surface.Available = false;
            output.Pump();
        }
        var finish = workflow.HandleAsync(DictationIntent.Toggle, cancellation.Token);
        surface.Available = false;
        output.Pump();
        // A lost UIA element must complete the real workflow, not wait forever
        // as it does for a still-existing field in a background window.
        try
        {
            await finish.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.AreEqual(DictationWorkflowPhase.Failed, workflow.CurrentState.Phase);
            Assert.AreEqual("Complete final words.", history.Items.Single());
            StringAssert.Contains(workflow.CurrentState.ErrorMessage!, "no longer available");
            Assert.AreEqual("Existing text first words", surface.Document);
            Assert.AreEqual(1, surface.Writes);
            surface.Available = surface.Focused = true;
            await workflow.HandleAsync(DictationIntent.Toggle, cancellation.Token);
            Assert.AreEqual(DictationWorkflowPhase.Recording, workflow.CurrentState.Phase);
        }
        finally
        {
            await cancellation.CancelAsync();
            await workflow.HandleAsync(DictationIntent.Cancel, CancellationToken.None);
            await finish;
        }
    }

    [TestMethod]
    public async Task ExistingTextboxCanRegainFocusAndReceiveFinalReplacement()
    {
        var surface = new Surface();
        using var output = new LiveClipboardPaster(() => new WindowsTextTarget(surface),
            (text, current) => { Assert.IsTrue(current()); surface.Paste(text); });
        output.CaptureTarget();
        output.UpdatePreview("first words");
        output.Pump();
        output.Pump();
        surface.Focused = false;
        var finish = output.PasteAsync("Final words.", CancellationToken.None);
        output.Pump();
        Assert.IsFalse(finish.IsCompleted);
        surface.Focused = true;
        output.Pump();
        output.Pump();
        await finish.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.AreEqual("Existing text Final words.", surface.Document);
        Assert.AreEqual(2, surface.Writes);
    }

    private sealed class Surface : IWindowsTextSurface
    {
        public bool Available = true, Focused = true;
        private string _prefix = "Existing text ", _selection = "";
        public string Document => _prefix + _selection;
        public int Writes;
        public bool IsFocused => Available ? Focused : throw new ElementNotAvailableException();
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
        public void Paste(string text) { _prefix += text; _selection = ""; Writes++; }
        public void RevealCaret() { }
    }
}
