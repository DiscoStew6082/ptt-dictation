using PttDictation.Core;
using System.Runtime.InteropServices;

namespace PttDictation.App;

internal sealed class ClipboardPaster : IClipboardPaster
{
    private IntPtr _capturedTarget;

    public void CaptureTarget()
    {
        _capturedTarget = GetForegroundWindow();
    }

    public async Task PasteAsync(string text, CancellationToken cancellationToken)
    {
        var previous = Clipboard.GetDataObject();
        try
        {
            Clipboard.SetText(text);
            if (_capturedTarget != IntPtr.Zero && IsWindow(_capturedTarget))
            {
                SetForegroundWindow(_capturedTarget);
                await Task.Delay(75, cancellationToken);
            }

            SendKeys.SendWait("^v");
            await Task.Delay(750, cancellationToken);
        }
        finally
        {
            TryRestore(text, previous);
        }
    }

    private static void TryRestore(string pastedText, IDataObject? previous)
    {
        if (previous is null)
        {
            return;
        }

        try
        {
            if (Clipboard.ContainsText() && Clipboard.GetText() == pastedText)
            {
                Clipboard.SetDataObject(previous, copy: true);
            }
        }
        catch (ExternalException)
        {
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
