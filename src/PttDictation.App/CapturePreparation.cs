namespace PttDictation.App;

internal sealed class CapturePreparation : IDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _cancellation;
    private readonly Action<IDisposable> _release;
    private readonly Func<long> _tickCount;
    private readonly long _startedAt;
    private long _deadlineMilliseconds = 5000;
    private bool _finalValidationStarted;
    private IDisposable? _lease;
    private bool _disposed;
    internal Func<bool> Unchanged { get; }
    internal CancellationToken Token { get; }
    internal Func<bool>? ValidateIdentity { get; set; }
    internal IWindowsTextTarget? PreparedTarget { get; set; }
    internal Func<bool>? ConfirmedIntent { get; set; }

    internal CapturePreparation(CancellationToken cancellationToken, Func<bool> unchanged, Action<IDisposable> release,
        Func<long>? tickCount = null)
    {
        _tickCount = tickCount ?? (() => Environment.TickCount64);
        _startedAt = _tickCount();
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _cancellation.CancelAfter(TimeSpan.FromSeconds(5));
        Token = _cancellation.Token;
        Unchanged = unchanged;
        _release = release;
    }

    internal long ElapsedMilliseconds => _tickCount() - _startedAt;
    internal bool IsExpired => ElapsedMilliseconds >= Volatile.Read(ref _deadlineMilliseconds);
    internal string Phase => _finalValidationStarted ? "final_validation" : "readiness";

    internal void ThrowIfExpired()
    {
        Token.ThrowIfCancellationRequested();
        // A native call or a delayed timer must not accept a result beyond
        // either phase's deadline, even if cancellation has not been delivered.
        if (IsExpired)
            throw new OperationCanceledException(Token);
    }

    internal void BeginFinalValidation()
    {
        lock (_gate)
        {
            ThrowIfExpired();
            if (_disposed || _finalValidationStarted)
                throw new InvalidOperationException("Capture final validation cannot restart.");
            var elapsed = ElapsedMilliseconds;
            // A prepared original target must become ready within the initial
            // five seconds. Its one final validation gets a separate five
            // seconds, capped at ten seconds from the original preparation.
            if (elapsed >= 5000) throw new OperationCanceledException(Token);
            _finalValidationStarted = true;
            Volatile.Write(ref _deadlineMilliseconds, Math.Min(elapsed + 5000, 10000));
            _cancellation.CancelAfter(TimeSpan.FromMilliseconds(_deadlineMilliseconds - elapsed));
            Token.ThrowIfCancellationRequested();
        }
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
