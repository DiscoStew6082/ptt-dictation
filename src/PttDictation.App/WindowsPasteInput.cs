using System.Runtime.InteropServices;

namespace PttDictation.App;

internal static class WindowsPasteInput
{
    // An app-specific tag, not a security boundary. Other injected keys keep their normal hotkey behavior.
    internal static readonly nuint InputMarker = 0x50545444;
    private static readonly IWindowsPasteInputBackend NativeBackend = new NativeInputBackend();
    private const ushort LeftControl = 0xA2;
    private const ushort V = 0x56;
    private const uint KeyUp = 2;

    public static bool CanPasteNow => CanPaste(NativeBackend);

    public static void SendPaste() => SendPaste(NativeBackend);

    internal static bool CanPaste(IWindowsPasteInputBackend backend) =>
        !new[] { 0x10, 0xA0, 0xA1, 0x12, 0xA4, 0xA5, 0x5B, 0x5C, V }.Any(backend.IsKeyDown);

    internal static void SendPaste(IWindowsPasteInputBackend backend)
    {
        if (!CanPaste(backend))
        {
            throw new InvalidOperationException("Waiting for Shift, Alt, Windows, or V to be released before inserting dictation.");
        }

        var controlDown = backend.IsKeyDown(0x11) || backend.IsKeyDown(LeftControl) || backend.IsKeyDown(0xA3);
        NativeInput[] inputs = controlDown
            ? [Key(V), Key(V, KeyUp)]
            : [Key(LeftControl), Key(V), Key(V, KeyUp), Key(LeftControl, KeyUp)];
        var sent = backend.Send(inputs);
        if (sent != inputs.Length)
        {
            // Release only keys introduced by the accepted prefix; never release a pre-existing Ctrl.
            var releases = new List<NativeInput>();
            var vDownIndex = controlDown ? 0 : 1;
            if (sent > vDownIndex && sent <= vDownIndex + 1) releases.Add(Key(V, KeyUp));
            if (!controlDown && sent > 0 && sent < inputs.Length) releases.Add(Key(LeftControl, KeyUp));
            if (releases.Count > 0) backend.Send(releases.ToArray());
            throw new InvalidOperationException("Windows did not accept the complete dictation paste shortcut.");
        }
    }

    private static NativeInput Key(ushort virtualKey, uint flags = 0) => new()
    {
        Type = 1,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInput { VirtualKey = virtualKey, Flags = flags, ExtraInfo = InputMarker }
        }
    };

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeInput
    {
        public uint Type;
        public InputUnion Data;
    }

    // Including MOUSEINPUT gives INPUT the correct union size/alignment on both x86 and x64.
    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    private sealed class NativeInputBackend : IWindowsPasteInputBackend
    {
        public bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
        public uint Send(NativeInput[] inputs) => SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>());
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] NativeInput[] inputs, int size);
}

internal interface IWindowsPasteInputBackend
{
    bool IsKeyDown(int virtualKey);
    uint Send(WindowsPasteInput.NativeInput[] inputs);
}
