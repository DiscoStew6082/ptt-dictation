using PttDictation.App;
using PttDictation.Core;
using System.Runtime.InteropServices;

namespace PttDictation.Tests;

[TestClass]
public sealed class PasteInputTests
{
    [TestMethod]
    public void PasteInjectsOnlyTaggedControlVAndPreservesAlreadyHeldControl()
    {
        var backend = new RecordingInputBackend();
        WindowsPasteInput.SendPaste(backend);
        CollectionAssert.AreEqual(new ushort[] { 0xA2, 0x56, 0x56, 0xA2 }, backend.Inputs.Select(input => input.Data.Keyboard.VirtualKey).ToArray());
        CollectionAssert.AreEqual(new uint[] { 0, 0, 2, 2 }, backend.Inputs.Select(input => input.Data.Keyboard.Flags).ToArray());
        Assert.IsTrue(backend.Inputs.All(input => input.Type == 1 && input.Data.Keyboard.ExtraInfo == WindowsPasteInput.InputMarker));
        Assert.AreEqual(IntPtr.Size == 8 ? 40 : 28, Marshal.SizeOf<WindowsPasteInput.NativeInput>());

        backend.DownKeys.Add(0xA3);
        WindowsPasteInput.SendPaste(backend);
        CollectionAssert.AreEqual(new ushort[] { 0x56, 0x56 }, backend.Inputs.Select(input => input.Data.Keyboard.VirtualKey).ToArray());
    }

    [TestMethod]
    public void OwnPasteCannotReleasePhysicalHoldOrToggleRecordingButOtherInjectedEventsStillWork()
    {
        using var source = new GlobalHotkeySource(DictationHotkey.LeftControl, DictationHotkey.RightControl);
        var presses = 0;
        var releases = 0;
        var toggles = 0;
        source.Pressed += () => presses++;
        source.Released += () => releases++;
        source.ToggleRequested += () => toggles++;
        Assert.IsTrue(HookEvent(source, 0xA2, false, 0));
        Assert.IsFalse(HookEvent(source, 0xA2, true, WindowsPasteInput.InputMarker));
        Assert.AreEqual(0, releases);
        Assert.IsFalse(HookEvent(source, 0xA3, false, WindowsPasteInput.InputMarker));
        Assert.IsFalse(HookEvent(source, 0xA3, true, WindowsPasteInput.InputMarker));
        Assert.AreEqual(0, toggles);
        Assert.IsTrue(HookEvent(source, 0xA2, true, 0));
        Assert.AreEqual(1, presses);
        Assert.AreEqual(1, releases);
        Assert.IsTrue(HookEvent(source, 0xA3, false, 12345));
        Assert.AreEqual(1, toggles, "Nonmatching remapper-injected events must keep working.");
    }

    [TestMethod]
    [DataRow(0x10)]
    [DataRow(0xA0)]
    [DataRow(0xA1)]
    [DataRow(0x12)]
    [DataRow(0xA4)]
    [DataRow(0xA5)]
    [DataRow(0x5B)]
    [DataRow(0x5C)]
    [DataRow(0x56)]
    public void HeldConflictingKeyDefersPasteWithoutReleasingIt(int key)
    {
        var backend = new RecordingInputBackend();
        backend.DownKeys.Add(key);
        Assert.IsFalse(WindowsPasteInput.CanPaste(backend));
        Assert.ThrowsExactly<InvalidOperationException>(() => WindowsPasteInput.SendPaste(backend));
        Assert.HasCount(0, backend.Batches);
    }

    [TestMethod]
    public void PartialPasteReleasesOnlyItsOwnAcceptedKeysAndReportsFailure()
    {
        var backend = new RecordingInputBackend();
        backend.Results.Enqueue(1);
        Assert.ThrowsExactly<InvalidOperationException>(() => WindowsPasteInput.SendPaste(backend));
        Assert.HasCount(2, backend.Batches);
        Assert.HasCount(1, backend.Batches[1]);
        Assert.AreEqual((ushort)0xA2, backend.Batches[1][0].Data.Keyboard.VirtualKey);
        Assert.AreEqual(2u, backend.Batches[1][0].Data.Keyboard.Flags);

        backend = new RecordingInputBackend();
        backend.DownKeys.Add(0xA3);
        backend.Results.Enqueue(1);
        Assert.ThrowsExactly<InvalidOperationException>(() => WindowsPasteInput.SendPaste(backend));
        Assert.HasCount(2, backend.Batches);
        Assert.IsTrue(backend.Batches.SelectMany(batch => batch).All(input => input.Data.Keyboard.VirtualKey == 0x56),
            "Cleanup must never release a Ctrl held before paste.");
    }

    private static bool HookEvent(GlobalHotkeySource source, int key, bool keyUp, nuint marker)
    {
        var data = Marshal.AllocHGlobal(16 + IntPtr.Size);
        try
        {
            for (var offset = 0; offset < 16 + IntPtr.Size; offset++) Marshal.WriteByte(data, offset, 0);
            Marshal.WriteInt32(data, key);
            Marshal.WriteInt32(data, 8, marker == 0 ? 0 : 0x10);
            Marshal.WriteIntPtr(data, 16, (nint)marker);
            return source.ProcessHookEventForTest(data, keyUp ? 0x101 : 0x100);
        }
        finally { Marshal.FreeHGlobal(data); }
    }

    private sealed class RecordingInputBackend : IWindowsPasteInputBackend
    {
        public HashSet<int> DownKeys { get; } = [];
        public WindowsPasteInput.NativeInput[] Inputs { get; private set; } = [];
        public List<WindowsPasteInput.NativeInput[]> Batches { get; } = [];
        public Queue<uint> Results { get; } = [];
        public bool IsKeyDown(int virtualKey) => DownKeys.Contains(virtualKey);
        public uint Send(WindowsPasteInput.NativeInput[] inputs)
        {
            Inputs = inputs;
            Batches.Add(inputs);
            return Results.Count > 0 ? Results.Dequeue() : (uint)inputs.Length;
        }
    }
}
