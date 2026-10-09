namespace PttDictation.App;

internal sealed class CapturePreparation : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancellation;
    private readonly Action<IDisposable> _release;
    private IDisposable? _lease;
    private bool _disposed;
    internal Func<bool> Unchanged { get; }
    internal CancellationToken Token { get; }
    internal Func<bool>? ValidateIdentity { get; set; }

    internal CapturePreparation(CancellationToken cancellationToken, Func<bool> unchanged, Action<IDisposable> release)
    {
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cancellation.CancelAfter(TimeSpan.FromSeconds(5));
        Token = _cancellation.Token;
        Unchanged = unchanged;
        _release = release;
    }

    internal void SetLease(IDisposable lease)
    {
        lock (_gate)
        {
            if (!_disposed) { _lease = lease; return; }
        }
        _release(lease);
    }

    public void Dispose()
    {
        IDisposable? lease;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _cancellation.Cancel();
            _cancellation.Dispose();
            lease = _lease;
            _lease = null;
        }
        if (lease is not null) _release(lease);
    }
}
