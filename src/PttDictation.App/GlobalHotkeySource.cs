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
    private IntPtr _hookId;
    private int _holdVirtualKey;
    private int _toggleVirtualKey;
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
        if (_hookId != IntPtr.Zero)
        {
            return;
        }

        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        _hookId = SetWindowsHookEx(WhKeyboardLl, _callback, GetModuleHandle(module?.ModuleName), 0);
        if (_hookId == IntPtr.Zero)
        {
            throw new InvalidOperationException("Could not install the global dictation keyboard hook.");
        }
    }

    internal void Configure(DictationHotkey holdHotkey, DictationHotkey toggleHotkey)
    {
        if (holdHotkey == toggleHotkey)
        {
            throw new ArgumentException("Hold-to-talk and toggle-to-talk must use different keys.");
        }

        _holdVirtualKey = DictationHotkeyCatalog.VirtualKey(holdHotkey);
        _toggleVirtualKey = DictationHotkeyCatalog.VirtualKey(toggleHotkey);
    }

    public void Dispose()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && ProcessKeyEvent(Marshal.ReadInt32(lParam), wParam.ToInt32()))
        {
            return (IntPtr)1;
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    internal bool ProcessKeyEventForTest(int virtualKey, int message) => ProcessKeyEvent(virtualKey, message);

    internal static int VirtualKeyForTest(DictationHotkey hotkey) => DictationHotkeyCatalog.VirtualKey(hotkey);

    private bool ProcessKeyEvent(int virtualKey, int message)
    {
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
            return true;
        }

        if (isKeyUp && _togglePressed && virtualKey == _activeToggleVirtualKey)
        {
            _togglePressed = false;
            return true;
        }

        if (isKeyDown && virtualKey == _holdVirtualKey)
        {
            if (!_holdPressed)
            {
                _holdPressed = true;
                _activeHoldVirtualKey = virtualKey;
                Pressed?.Invoke();
            }

            return true;
        }

        if (isKeyUp && virtualKey == _holdVirtualKey)
        {
            return true;
        }

        if (isKeyDown && virtualKey == _toggleVirtualKey)
        {
            if (!_togglePressed)
            {
                _togglePressed = true;
                _activeToggleVirtualKey = virtualKey;
                ToggleRequested?.Invoke();
            }

            return true;
        }

        if (isKeyUp && virtualKey == _toggleVirtualKey)
        {
            return true;
        }

        return false;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
