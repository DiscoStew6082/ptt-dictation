namespace PttDictation.App;

/// <summary>Retries only bounded transient HTTP responses, respecting server retry timing.</summary>
internal static class QwenDownloadRetry
{
    internal static async Task<HttpResponseMessage> SendAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> send, CancellationToken token,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(send);
        delay ??= Task.Delay;
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var response = await send(token).ConfigureAwait(false);
            if (token.IsCancellationRequested)
            {
                response.Dispose();
                token.ThrowIfCancellationRequested();
            }
            if (attempt == 3 || (int)response.StatusCode is not (429 or 502 or 503 or 504)) return response;
            var retry = response.Headers.RetryAfter;
            var wait = retry?.Delta ?? (retry?.Date is { } date
                ? date - DateTimeOffset.UtcNow : TimeSpan.FromSeconds(2 << attempt));
            if (wait > TimeSpan.FromSeconds(60)) return response;
            if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
            // Round upward so a fractional millisecond never shortens Retry-After.
            wait = TimeSpan.FromMilliseconds(Math.Ceiling(wait.TotalMilliseconds));
            response.Dispose();
            await delay(wait, token).ConfigureAwait(false);
        }
    }
}
