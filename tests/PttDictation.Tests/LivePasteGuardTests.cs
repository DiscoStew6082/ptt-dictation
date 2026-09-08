using PttDictation.App;

namespace PttDictation.Tests;

[TestClass]
public sealed class LivePasteGuardTests
{
    [TestMethod]
    public void PermissionRevokedDuringClipboardVerificationPreventsKeyboardInjection()
    {
        var permitted = true;
        var clipboard = new ClipboardBackend { Verify = () => { permitted = false; return true; } };
        var paster = new ClipboardPaster(clipboard, new RestoreQueue(), new Foreground());
        Assert.Throws<InvalidOperationException>(() => paster.PasteToCurrentTarget("dictation", () => permitted));
        Assert.IsFalse(clipboard.Sent);
    }

    private sealed class ClipboardBackend : IClipboardPasteBackend
    {
        public required Func<bool> Verify { get; init; }
        public bool Sent { get; private set; }
        public IDataObject? GetDataObject() => null;
        public uint SetText(string text) => 1;
        public bool IsSequenceCurrent(uint expectedSequence) => true;
        public bool IsCurrent(uint expectedSequence, string pastedText) => Verify();
        public void SendPaste() => Sent = true;
        public void RestoreIfCurrent(uint expectedSequence, IDataObject? previous) { }
    }
    private sealed class RestoreQueue : IClipboardRestoreQueue
    {
        public void Enqueue(Action restore) { }
        public void EnqueueImmediate(Action restore) { }
    }
    private sealed class Foreground : IForegroundWindowBackend
    {
        public IntPtr GetForegroundWindow() => (IntPtr)1;
        public bool IsWindow(IntPtr window) => true;
        public bool SetForegroundWindow(IntPtr window) => throw new AssertFailedException("Live insertion must not force focus.");
    }
}
