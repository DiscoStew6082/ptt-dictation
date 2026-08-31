using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PttDictation.App;

internal sealed class RightCtrlHotkeySource : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int VkRightControl = 0xA3;
    private const int VkRightShift = 0xA1;

    private readonly LowLevelKeyboardProc _callback;
    private IntPtr _hookId;
    private bool _rightShiftPressed;

    public event Action? Pressed;
    public event Action? Released;
    public event Action? ToggleRequested;

    internal const int KeyDownMessageForTest = WmKeyDown;
    internal const int KeyUpMessageForTest = WmKeyUp;
    internal const int RightControlVirtualKeyForTest = VkRightControl;
    internal const int RightShiftVirtualKeyForTest = VkRightShift;

    public RightCtrlHotkeySource()
    {
        _callback = HookCallback;
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
            throw new InvalidOperationException("Could not install global Right Ctrl keyboard hook.");
        }
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
        if (nCode >= 0)
        {
            ProcessKeyEvent(Marshal.ReadInt32(lParam), wParam.ToInt32());
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    internal void ProcessKeyEventForTest(int virtualKey, int message) => ProcessKeyEvent(virtualKey, message);

    private void ProcessKeyEvent(int virtualKey, int message)
    {
        if (virtualKey == VkRightControl)
        {
            if (message is WmKeyDown or WmSysKeyDown)
            {
                Pressed?.Invoke();
            }
            else if (message is WmKeyUp or WmSysKeyUp)
            {
                Released?.Invoke();
            }
        }
        else if (virtualKey == VkRightShift)
        {
            if (message is WmKeyDown or WmSysKeyDown)
            {
                if (!_rightShiftPressed)
                {
                    _rightShiftPressed = true;
                    ToggleRequested?.Invoke();
                }
            }
            else if (message is WmKeyUp or WmSysKeyUp)
            {
                _rightShiftPressed = false;
            }
        }
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
