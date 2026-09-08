using PttDictation.App;

namespace PttDictation.Tests;

[TestClass]
public sealed class LiveClipboardRecoveryTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UnsentClipboardReadFailureRecoversPreviewAndFinalWithoutLosingText(bool failFirstWrite)
    {
        using var run = new Harness();
        if (!failFirstWrite) run.Preview("first words");
        run.Backend.Readable = false;
        run.Output.UpdatePreview("revised words");
        run.Output.Pump();
        Assert.AreEqual(failFirstWrite ? 0 : 1, run.Backend.Writes.Count);
        run.Backend.Readable = true;
        for (var i = 0; i < 4; i++) run.Output.Pump();
        Assert.AreEqual("Before 👋 revised words after", run.Surface.Document);
        var finish = run.Output.PasteAsync("Complete final words.", CancellationToken.None);
        for (var i = 0; i < 4; i++) run.Output.Pump();
        await finish.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual("Before 👋 Complete final words. after", run.Surface.Document);
        CollectionAssert.AreEqual(failFirstWrite
            ? new[] { "revised words", "Complete final words." }
            : new[] { "first words", "revised words", "Complete final words." }, run.Backend.Writes);
    }

    [TestMethod]
    public async Task ActualClipboardReplacementStillStopsWithoutSendingOrAppending()
    {
        using var run = new Harness();
        run.Preview("first words");
        run.Backend.Readable = false;
        run.Backend.SequenceCurrent = false;
        var finish = run.Output.PasteAsync("Do not overwrite", CancellationToken.None);
        for (var i = 0; i < 4; i++) run.Output.Pump();
        await Assert.ThrowsAsync<InvalidOperationException>(() => finish);
        Assert.AreEqual("Before 👋 first words after", run.Surface.Document);
        Assert.AreEqual(1, run.Backend.Writes.Count);
    }

    [TestMethod]
    public async Task MovingCaretDuringUnsentRetryStopsWithoutChangingUserText()
    {
        using var run = new Harness();
        run.Preview("first words");
        run.Backend.Readable = false;
        run.Output.UpdatePreview("revised words");
        run.Output.Pump();
        run.Surface.Prefix = run.Surface.Document;
        run.Surface.Selection = run.Surface.Suffix = "";
        run.Backend.Readable = true;
        var finish = run.Output.PasteAsync("Do not overwrite", CancellationToken.None);
        for (var i = 0; i < 4; i++) run.Output.Pump();
        await Assert.ThrowsAsync<InvalidOperationException>(() => finish);
        Assert.AreEqual("Before 👋 first words after", run.Surface.Document);
        Assert.AreEqual(1, run.Backend.Writes.Count);
    }

    [TestMethod]
    public async Task PersistentUnreadableClipboardStopsAtExistingTwoSecondDeadline()
    {
        using var run = new Harness();
        run.Preview("first words");
        run.Backend.Readable = false;
        var finish = run.Output.PasteAsync("Final words", CancellationToken.None);
        Assert.IsFalse(finish.IsCompleted, "An unsent transient failure should remain pending.");
        run.Now += TimeSpan.FromSeconds(3);
        for (var i = 0; i < 4; i++) run.Output.Pump();
        await Assert.ThrowsAsync<InvalidOperationException>(() => finish);
        Assert.AreEqual(1, run.Backend.Writes.Count);
    }

    [TestMethod]
    public async Task AmbiguousFailureAfterKeyboardInputIsNeverRetried()
    {
        using var run = new Harness();
        run.Preview("first words");
        run.Backend.ThrowAfterInput = true;
        var finish = run.Output.PasteAsync("Final words", CancellationToken.None);
        for (var i = 0; i < 4; i++) run.Output.Pump();
        await Assert.ThrowsAsync<InvalidOperationException>(() => finish);
        Assert.AreEqual(2, run.Backend.Writes.Count);
    }

    private sealed class Harness : IDisposable
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public readonly Surface Surface = new();
        public readonly Backend Backend;
        public readonly LiveClipboardPaster Output;
        public Harness()
        {
            Backend = new Backend(Surface);
            var clipboard = new ClipboardPaster(Backend, new RestoreQueue(), new Foreground());
            var target = new WindowsTextTarget(Surface, () => Now);
            Output = new LiveClipboardPaster(() => target, clipboard.PasteToCurrentTarget);
            Output.CaptureTarget();
        }
        public void Preview(string text)
        {
            Output.UpdatePreview(text);
            Output.Pump();
            Output.Pump();
        }
        public void Dispose() => Output.Dispose();
    }

    private sealed class Surface : IWindowsTextSurface
    {
        public string Prefix = "Before 👋 ", Selection = "replace me", Suffix = " after";
        public string Document => Prefix + Selection + Suffix;
        public bool IsFocused => true;
        public bool SupportsReplacement => true;
        public bool CanPasteFallback => true;
        public TextTargetSnapshot Read() => new(Document, Prefix, Selection, Suffix);
        public bool Select(string prefix, string ownedText, string suffix)
        {
            if (Document != prefix + ownedText + suffix) return false;
            Prefix = prefix; Selection = ownedText; Suffix = suffix;
            return true;
        }
        public void Paste(string text) { Prefix += text; Selection = ""; }
        public void RevealCaret() { }
    }

    private sealed class Backend(Surface surface) : IClipboardPasteBackend
    {
        public bool Readable = true, SequenceCurrent = true, ThrowAfterInput;
        public List<string> Writes = [];
        private string _text = "";
        public IDataObject? GetDataObject() => null;
        public uint SetText(string text) { _text = text; return 7389; }
        public bool IsSequenceCurrent(uint expectedSequence) => SequenceCurrent;
        public bool IsCurrent(uint expectedSequence, string pastedText) => SequenceCurrent && Readable;
        public void SendPaste()
        {
            Writes.Add(_text);
            surface.Paste(_text);
            if (ThrowAfterInput) throw new InvalidOperationException("Input outcome is ambiguous.");
        }
        public void RestoreIfCurrent(uint expectedSequence, IDataObject? previous) { }
    }
    private sealed class RestoreQueue : IClipboardRestoreQueue
    {
        public void Enqueue(Action restore) => restore();
        public void EnqueueImmediate(Action restore) => restore();
    }
    private sealed class Foreground : IForegroundWindowBackend
    {
        public IntPtr GetForegroundWindow() => (IntPtr)1;
        public bool IsWindow(IntPtr window) => true;
        public bool SetForegroundWindow(IntPtr window) => throw new AssertFailedException("Do not steal focus.");
    }
}
