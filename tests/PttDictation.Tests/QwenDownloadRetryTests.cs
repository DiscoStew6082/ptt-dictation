using System.Net;
using System.Net.Http.Headers;
using PttDictation.App;

namespace PttDictation.Tests;

[TestClass]
public sealed class QwenDownloadRetryTests
{
    [TestMethod]
    public async Task ThrottleResponseIsDisposedBeforeRequestedDelayThenSuccessIsReturned()
    {
        var content = new TrackedContent();
        var throttle = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = content };
        throttle.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(9));
        using var success = new HttpResponseMessage(HttpStatusCode.OK);
        var calls = 0;
        var waited = false;
        var result = await QwenDownloadRetry.SendAsync(_ => Task.FromResult(++calls == 1 ? throttle : success), CancellationToken.None,
            (delay, _) =>
            {
                Assert.IsTrue(content.Disposed);
                Assert.AreEqual(TimeSpan.FromSeconds(9), delay);
                waited = true;
                return Task.CompletedTask;
            });
        Assert.AreSame(success, result);
        Assert.AreEqual(2, calls);
        Assert.IsTrue(waited);
    }

    [TestMethod]
    public async Task RetryAfterHttpDateUsesRemainingWaitWithoutRoundingDown()
    {
        var retryAt = DateTimeOffset.UtcNow.AddSeconds(20);
        var throttled = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        throttled.Headers.RetryAfter = new RetryConditionHeaderValue(retryAt);
        using var success = new HttpResponseMessage(HttpStatusCode.OK);
        var calls = 0;
        var waited = false;
        await QwenDownloadRetry.SendAsync(_ => Task.FromResult(++calls == 1 ? throttled : success), CancellationToken.None,
            (duration, _) =>
            {
                Assert.IsTrue(duration >= retryAt - DateTimeOffset.UtcNow, "HTTP-date must not cause an earlier retry.");
                Assert.IsTrue(duration <= TimeSpan.FromSeconds(20));
                waited = true;
                return Task.CompletedTask;
            });
        Assert.IsTrue(waited);
    }

    [TestMethod]
    [DataRow(429)]
    [DataRow(502)]
    [DataRow(503)]
    [DataRow(504)]
    public async Task ExhaustedTransientResponsesUseThreeBackoffsAndReturnFourthResponse(int status)
    {
        var calls = 0;
        var contents = new List<TrackedContent>();
        var delays = new List<TimeSpan>();
        using var result = await QwenDownloadRetry.SendAsync(_ =>
        {
            calls++;
            var content = new TrackedContent();
            contents.Add(content);
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = content });
        }, CancellationToken.None, (duration, _) => { delays.Add(duration); return Task.CompletedTask; });
        Assert.AreEqual(4, calls);
        CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8) }, delays);
        Assert.IsTrue(contents.Take(3).All(content => content.Disposed));
        Assert.IsFalse(contents[3].Disposed, "Caller must still own the final response for EnsureSuccessStatusCode.");
        Assert.AreEqual((HttpStatusCode)status, result.StatusCode);
    }

    [TestMethod]
    [DataRow(403)]
    [DataRow(404)]
    [DataRow(500)]
    public async Task PermanentOrUnspecifiedFailureIsNeverRetried(int status)
    {
        var calls = 0;
        var content = new TrackedContent();
        using var failure = new HttpResponseMessage((HttpStatusCode)status) { Content = content };
        var result = await QwenDownloadRetry.SendAsync(_ => { calls++; return Task.FromResult(failure); }, CancellationToken.None,
            (_, _) => throw new AssertFailedException("Unexpected retry delay."));
        Assert.AreSame(failure, result);
        Assert.AreEqual(1, calls);
        Assert.IsFalse(content.Disposed);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LongServerWaitReturnsResponseRatherThanRetryingEarly(bool httpDate)
    {
        var calls = 0;
        var content = new TrackedContent();
        using var failure = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = content };
        failure.Headers.RetryAfter = httpDate
            ? new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(2))
            : new RetryConditionHeaderValue(TimeSpan.FromSeconds(61));
        var result = await QwenDownloadRetry.SendAsync(_ => { calls++; return Task.FromResult(failure); }, CancellationToken.None,
            (_, _) => throw new AssertFailedException("A long Retry-After must not be shortened."));
        Assert.AreSame(failure, result);
        Assert.AreEqual(1, calls);
        Assert.IsFalse(content.Disposed);
    }

    [TestMethod]
    public async Task CancellationDuringDelayDisposesResponseAndPreventsAnotherSend()
    {
        using var cancellation = new CancellationTokenSource();
        var content = new TrackedContent();
        var calls = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() => QwenDownloadRetry.SendAsync(_ =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = content });
        }, cancellation.Token, (_, token) =>
        {
            Assert.IsTrue(content.Disposed);
            cancellation.Cancel();
            return Task.Delay(Timeout.InfiniteTimeSpan, token);
        }));
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task CancellationRacingResponseDisposesItWithoutRetry()
    {
        using var cancellation = new CancellationTokenSource();
        var content = new TrackedContent();
        var calls = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() => QwenDownloadRetry.SendAsync(_ =>
        {
            calls++;
            cancellation.Cancel();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }, cancellation.Token));
        Assert.IsTrue(content.Disposed);
        Assert.AreEqual(1, calls);
    }

    private sealed class TrackedContent : ByteArrayContent
    {
        public bool Disposed { get; private set; }
        public TrackedContent() : base([]) { }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
