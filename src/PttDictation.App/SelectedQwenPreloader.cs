using System.Diagnostics;
using PttDictation.Core;

namespace PttDictation.App;

internal sealed class SelectedQwenPreloader(
    Func<CancellationToken, Task> warmUp,
    CancellationToken lifetime,
    Action<string, double, Exception?>? trace = null)
{
    private readonly object _gate = new();
    private FinalTranscriptionEngine _selection;
    private CancellationTokenSource? _active;
    private Task _pending = Task.CompletedTask;
    private bool _running;
    private bool _ready;

    // Called after settings publication on the UI thread. All model/manifest work
    // runs on the pool; repeated settings saves share one pending warmup.
    public Task ApplySelection(FinalTranscriptionEngine engine)
    {
        lock (_gate)
        {
            if (lifetime.IsCancellationRequested) return _pending;
            if (_selection != engine) _ready = false;
            _selection = engine;
            if (engine != FinalTranscriptionEngine.Qwen)
            {
                // The existing final-transcriber gate separates this warmup from
                // real requests. Cancelling its gate wait cannot cancel a request
                // already transcribing, and a ready worker is not disposed here.
                _active?.Cancel();
                return _pending;
            }
            if (_running || _ready) return _pending;
            _running = true;
            _pending = Task.Run(RunAsync);
            return _pending;
        }
    }

    private async Task RunAsync()
    {
        while (true)
        {
            CancellationTokenSource operation;
            lock (_gate)
            {
                if (_selection != FinalTranscriptionEngine.Qwen || lifetime.IsCancellationRequested)
                {
                    _running = false;
                    return;
                }
                operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                _active = operation;
            }

            var elapsed = Stopwatch.StartNew();
            Report("started", 0);
            var completed = false;
            try
            {
                operation.Token.ThrowIfCancellationRequested();
                await warmUp(operation.Token).ConfigureAwait(false);
                operation.Token.ThrowIfCancellationRequested();
                completed = true;
                Report("completed", elapsed.Elapsed.TotalMilliseconds);
            }
            catch (Exception) when (operation.IsCancellationRequested)
            {
                Report("cancelled", elapsed.Elapsed.TotalMilliseconds);
            }
            catch (Exception error)
            {
                // A preload failure must not open UI, change settings, or prevent
                // the normal per-session warmup from retrying when needed.
                Report("failed", elapsed.Elapsed.TotalMilliseconds, error);
            }

            lock (_gate)
            {
                _active = null;
                var restart = operation.IsCancellationRequested && !lifetime.IsCancellationRequested
                    && _selection == FinalTranscriptionEngine.Qwen;
                _ready = completed && !operation.IsCancellationRequested
                    && _selection == FinalTranscriptionEngine.Qwen;
                operation.Dispose();
                if (restart) continue;
                _running = false;
                return;
            }
        }
    }

    private void Report(string stage, double elapsedMilliseconds, Exception? error = null)
    {
        try
        {
            if (trace is not null) trace(stage, elapsedMilliseconds, error);
            else DiagnosticTrace.Write($"qwen.preload_{stage}", new { elapsedMilliseconds }, error);
        }
        catch { /* Diagnostics must not change the preload lifecycle. */ }
    }
}
