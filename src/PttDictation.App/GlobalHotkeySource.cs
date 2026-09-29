using PttDictation.Core;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PttDictation.App;

internal sealed class GlobalHotkeySource : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;

    private readonly LowLevelKeyboardProc _callback;
    private readonly object _lifecycleGate = new();
    private KeyboardHookThread? _hookThread;
    private bool _disposed;
    private IntPtr _hookId;
    private HotkeyConfiguration _configuration = null!;
    private int _activeHoldVirtualKey;
    private int _activeToggleVirtualKey;
    private bool _holdPressed;
    private bool _togglePressed;

    public event Action? Pressed;
    public event Action? Released;
    public event Action? ToggleRequested;

    internal const int KeyDownMessageForTest = WmKeyDown;
    internal const int KeyUpMessageForTest = WmKeyUp;

    public GlobalHotkeySource()
        : this(AppSettings.Default.HoldHotkey, AppSettings.Default.ToggleHotkey)
    {
    }

    internal GlobalHotkeySource(DictationHotkey holdHotkey, DictationHotkey toggleHotkey)
    {
        _callback = HookCallback;
        Configure(holdHotkey, toggleHotkey);
    }

    public void Start()
    {
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _hookThread ??= new KeyboardHookThread(InstallHook, RemoveHook);
        }
    }

    private void InstallHook()
    {
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        _hookId = SetWindowsHookEx(WhKeyboardLl, _callback, GetModuleHandle(module?.ModuleName), 0);
        if (_hookId == IntPtr.Zero)
        {
            throw new InvalidOperationException("Could not install the global dictation keyboard hook.");
        }
        var configuration = Volatile.Read(ref _configuration);
        DiagnosticTrace.Write("hotkey.started", new { configuration.Hold, configuration.Toggle,
            threadId = Environment.CurrentManagedThreadId });
    }

    internal void Configure(DictationHotkey holdHotkey, DictationHotkey toggleHotkey)
    {
        if (holdHotkey == toggleHotkey)
        {
            throw new ArgumentException("Hold-to-talk and toggle-to-talk must use different keys.");
        }

        Volatile.Write(ref _configuration, new HotkeyConfiguration(
            DictationHotkeyCatalog.VirtualKey(holdHotkey), DictationHotkeyCatalog.VirtualKey(toggleHotkey)));
    }

    public void Dispose()
    {
        KeyboardHookThread? thread;
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            _disposed = true;
            thread = _hookThread;
            _hookThread = null;
        }
        thread?.Dispose();
    }

    private void RemoveHook()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && ProcessHookEvent(lParam, wParam.ToInt32(), trace: true))
        {
            return (IntPtr)1;
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    internal bool ProcessKeyEventForTest(int virtualKey, int message) => ProcessKeyEvent(virtualKey, message);

    internal bool ProcessHookEventForTest(IntPtr data, int message) => ProcessHookEvent(data, message);

    private bool ProcessHookEvent(IntPtr data, int message, bool trace = false)
    {
        var key = Marshal.PtrToStructure<KeyboardHookData>(data);
        // Our paste shortcut must reach the target without changing physical hold/toggle state.
        var configuration = Volatile.Read(ref _configuration);
        var virtualKey = (int)key.VirtualKey;
        var isHotkey = virtualKey == configuration.Hold || virtualKey == configuration.Toggle
            || (_holdPressed && virtualKey == _activeHoldVirtualKey)
            || (_togglePressed && virtualKey == _activeToggleVirtualKey);
        // This is the state before Windows processes this event, not a physical-key assertion.
        var windowsDownBeforeEvent = trace && isHotkey && (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
        var ownInput = key.ExtraInfo == WindowsPasteInput.InputMarker;
        var swallowed = !ownInput && ProcessKeyEvent(virtualKey, message);
        if (trace && isHotkey)
            DiagnosticTrace.Write("hotkey.input", new { virtualKey, message, injected = (key.Flags & 0x10) != 0,
                ownInput, swallowed, windowsDownBeforeEvent });
        return swallowed;
    }

    internal static int VirtualKeyForTest(DictationHotkey hotkey) => DictationHotkeyCatalog.VirtualKey(hotkey);

    private bool ProcessKeyEvent(int virtualKey, int message)
    {
        var configuration = Volatile.Read(ref _configuration);
        var isKeyDown = message is WmKeyDown or WmSysKeyDown;
        var isKeyUp = message is WmKeyUp or WmSysKeyUp;
        if (!isKeyDown && !isKeyUp)
        {
            return false;
        }

        if (isKeyUp && _holdPressed && virtualKey == _activeHoldVirtualKey)
        {
            _holdPressed = false;
            Released?.Invoke();
            return !IsControl(virtualKey);
        }

        if (isKeyUp && _togglePressed && virtualKey == _activeToggleVirtualKey)
        {
            _togglePressed = false;
            return !IsControl(virtualKey);
        }

        // A held key remains owned until its release, even if Settings changes the binding.
        if (isKeyDown && ((_holdPressed && virtualKey == _activeHoldVirtualKey)
            || (_togglePressed && virtualKey == _activeToggleVirtualKey))) return true;

        if (isKeyDown && virtualKey == configuration.Hold)
        {
            if (!_holdPressed)
            {
                _holdPressed = true;
                _activeHoldVirtualKey = virtualKey;
                Pressed?.Invoke();
            }

            return true;
        }

        if (isKeyDown && virtualKey == configuration.Toggle)
        {
            if (!_togglePressed)
            {
                _togglePressed = true;
                _activeToggleVirtualKey = virtualKey;
                ToggleRequested?.Invoke();
            }

            return true;
        }

        // Never swallow an unmatched release: its press may have reached Windows
        // before startup/reconfiguration. Matched Ctrl releases also pass through
        // above so a pre-existing down state cannot remain latched by this hook.
        return false;
    }

    private static bool IsControl(int virtualKey) => virtualKey is 0xA2 or 0xA3;
    private sealed record HotkeyConfiguration(int Hold, int Toggle);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardHookData
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
