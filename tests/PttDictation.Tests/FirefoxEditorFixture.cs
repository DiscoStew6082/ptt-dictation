using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace PttDictation.Tests;

// The page observes native insertion independently of UIA. It never injects text.
internal sealed class FirefoxEditorFixture : IAsyncDisposable
{
    internal sealed record State(string Text, bool Focused, long Sequence,
        string UserAgent, double Timestamp, double? FirstFocusedAt, bool Ready,
        long TrustedInputEvents, long TrustedPasteEvents, double ClickX, double ClickY,
        double PixelRatio, long TrustedClicks, int SelectionRangeCount, string? SelectionAnchor,
        int? SelectionOffset)
    {
        public DateTimeOffset ReceivedUtc { get; init; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    private readonly List<State> _snapshots = [];
    private readonly string _page;
    private readonly Task _server;
    private State? _latest;

    private FirefoxEditorFixture(HttpListener listener, Uri url, string kind)
    {
        _listener = listener;
        Url = url;
        _page = BuildPage(kind);
        // DOM observations must not wait for the PTT test's UI/COM apartment.
        _server = Task.Run(ServeAsync);
    }

    public Uri Url { get; }
    public State? Latest { get { lock (_gate) return _latest; } }
    public IReadOnlyList<State> Snapshots { get { lock (_gate) return _snapshots.ToArray(); } }

    public static FirefoxEditorFixture Start(string kind)
    {
        if (kind is not ("textarea" or "contenteditable"))
            throw new ArgumentOutOfRangeException(nameof(kind));

        // Let the OS choose an unused port, then retry the small bind race.
        for (var attempt = 0; ; attempt++)
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var listener = new HttpListener();
            var origin = $"http://127.0.0.1:{port}/";
            listener.Prefixes.Add(origin);
            try
            {
                listener.Start();
                return new FirefoxEditorFixture(listener,
                    new Uri(origin + Guid.NewGuid().ToString("N") + "/"), kind);
            }
            catch (HttpListenerException error) when (attempt < 4 && error.ErrorCode != 5)
            {
                listener.Close();
            }
            catch
            {
                listener.Close();
                throw;
            }
        }
    }

    public async Task<State> WaitForAsync(Func<State, bool> predicate, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _shutdown.Token);
        deadline.CancelAfter(timeout);
        try
        {
            while (true)
            {
                if (Latest is { } state && predicate(state)) return state;
                if (_server.IsFaulted) await _server;
                await Task.Delay(25, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested
                                                && !_shutdown.IsCancellationRequested)
        {
            throw new TimeoutException($"Firefox page did not reach the expected state within {timeout}.");
        }
    }

    private async Task ServeAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync().WaitAsync(_shutdown.Token);
                try { await HandleAsync(context); }
                catch (Exception error) when (error is IOException or HttpListenerException
                                              or JsonException or InvalidDataException)
                {
                    // A closing browser may abandon its final report.
                    try { context.Response.StatusCode = 400; }
                    catch (ObjectDisposedException) { }
                }
                finally { context.Response.Close(); }
            }
        }
        catch (Exception error) when (_shutdown.IsCancellationRequested
            && error is OperationCanceledException or HttpListenerException or ObjectDisposedException) { }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        response.Headers["Cache-Control"] = "no-store";
        if (request.RemoteEndPoint is not { Address: var address } || !IPAddress.IsLoopback(address))
        {
            response.StatusCode = 403;
            return;
        }
        if (request.HttpMethod == "GET" && request.Url?.AbsolutePath == Url.AbsolutePath)
        {
            response.ContentType = "text/html; charset=utf-8";
            response.Headers["Content-Security-Policy"] =
                "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; frame-ancestors 'none'";
            var bytes = Encoding.UTF8.GetBytes(_page);
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, _shutdown.Token);
            return;
        }
        if (request.HttpMethod != "POST" || request.Url?.AbsolutePath != Url.AbsolutePath + "state")
        {
            response.StatusCode = 404;
            return;
        }
        if (request.Headers["Origin"] is { } origin && origin != Url.GetLeftPart(UriPartial.Authority))
        {
            response.StatusCode = 403;
            return;
        }
        const int maximumBytes = 16 * 1024;
        if (request.ContentLength64 > maximumBytes)
        {
            response.StatusCode = 413;
            return;
        }
        var buffer = new byte[maximumBytes + 1];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await request.InputStream.ReadAsync(buffer.AsMemory(count), _shutdown.Token);
            if (read == 0) break;
            count += read;
        }
        if (count > maximumBytes)
        {
            response.StatusCode = 413;
            return;
        }
        var state = JsonSerializer.Deserialize<State>(buffer.AsSpan(0, count), JsonOptions);
        if (state is null || state.Text is null || state.UserAgent is null || state.Sequence <= 0
            || !double.IsFinite(state.Timestamp) || state.TrustedInputEvents < 0 || state.TrustedPasteEvents < 0
            || !double.IsFinite(state.ClickX) || !double.IsFinite(state.ClickY)
            || !double.IsFinite(state.PixelRatio) || state.PixelRatio <= 0 || state.TrustedClicks < 0)
            throw new InvalidDataException("Invalid Firefox state report.");
        state = state with { ReceivedUtc = DateTimeOffset.UtcNow };
        lock (_gate)
        {
            if (_latest is null || state.Sequence > _latest.Sequence)
            {
                _latest = state;
                _snapshots.Add(state);
            }
        }
        response.StatusCode = 204;
    }

    private static string BuildPage(string kind)
    {
        var editor = kind == "textarea"
            ? "<textarea id=\"editor\" aria-label=\"PTT Firefox cold-start editor\" spellcheck=\"false\"></textarea>"
            : "<div id=\"editor\" contenteditable=\"true\" role=\"textbox\" aria-multiline=\"true\" aria-label=\"PTT Firefox cold-start editor\" spellcheck=\"false\"></div>";
        return $$"""
            <!doctype html>
            <html lang="en"><meta charset="utf-8">
            <meta name="color-scheme" content="dark">
            <title>PTT Firefox cold-start test</title>
            <style>
            body { background:#181818; color:#eee; font:18px system-ui; padding:32px; }
            #editor { display:block; box-sizing:border-box; width:100%; min-height:240px;
              background:#242424; color:#eee; border:2px solid #888; padding:16px;
              font:20px system-ui; white-space:pre-wrap; }
            </style>
            <h1>PTT Firefox cold-start test</h1>
            {{editor}}
            <script>
            const editor = document.getElementById('editor');
            let sequence = 0, firstFocusedAt = null, sending = false, ready = false;
            let trustedInputEvents = 0, trustedPasteEvents = 0, trustedClicks = 0;
            function observe() {
              const focused = document.activeElement === editor && document.hasFocus();
              if (focused && firstFocusedAt === null) firstFocusedAt = performance.now();
              const rect = editor.getBoundingClientRect();
              const selection = window.getSelection();
              const textarea = editor instanceof HTMLTextAreaElement;
              return {
                text: (textarea ? editor.value : editor.innerText)
                  .replace(/\r\n?/g, '\n'),
                focused, sequence: ++sequence, userAgent: navigator.userAgent,
                timestamp: performance.now(), firstFocusedAt, ready,
                trustedInputEvents, trustedPasteEvents,
                clickX: window.mozInnerScreenX + rect.left + Math.min(24, rect.width / 2),
                clickY: window.mozInnerScreenY + rect.top + Math.min(24, rect.height / 2),
                pixelRatio: window.devicePixelRatio, trustedClicks,
                selectionRangeCount: selection?.rangeCount ?? 0,
                selectionAnchor: selection?.anchorNode?.nodeName ?? null,
                selectionOffset: textarea ? editor.selectionStart : selection?.anchorOffset ?? null
              };
            }
            async function report() {
              if (sending) return;
              sending = true;
              try {
                await fetch('state', { method:'POST', headers:{'Content-Type':'application/json'},
                  body:JSON.stringify(observe()), cache:'no-store' });
              } finally { sending = false; }
            }
            function focusEditor() { editor.focus(); void report(); }
            window.addEventListener('focus', focusEditor);
            window.addEventListener('blur', () => void report());
            editor.addEventListener('input', event => {
              if (event.isTrusted) trustedInputEvents++;
              void report();
            });
            editor.addEventListener('paste', event => {
              if (event.isTrusted) trustedPasteEvents++;
              void report();
            });
            editor.addEventListener('click', event => {
              if (event.isTrusted) trustedClicks++;
              void report();
            });
            editor.addEventListener('focus', () => void report());
            editor.addEventListener('blur', () => void report());
            document.addEventListener('selectionchange', () => void report());
            requestAnimationFrame(() => requestAnimationFrame(() => { ready = true; focusEditor(); }));
            setInterval(() => void report(), 50);
            </script></html>
            """;
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        _listener.Close();
        try { await _server.WaitAsync(TimeSpan.FromSeconds(2)); }
        finally { _shutdown.Dispose(); }
    }
}
