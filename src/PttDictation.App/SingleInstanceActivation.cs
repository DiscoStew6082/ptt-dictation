namespace PttDictation.App;

internal sealed class SingleInstanceActivation : IDisposable
{
    internal const string DefaultEventName = "Local\\PttDictation.App.OpenSettings";

    private readonly EventWaitHandle _signal;
    private bool _disposed;

    private SingleInstanceActivation(string eventName)
    {
        _signal = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
    }

    public static SingleInstanceActivation Listen(string eventName = DefaultEventName)
    {
        return new SingleInstanceActivation(eventName);
    }

    public bool ConsumePending()
    {
        return !_disposed && _signal.WaitOne(0);
    }

    public static bool TryNotify(string eventName = DefaultEventName)
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting(eventName);
            return signal.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _signal.Dispose();
    }
}
