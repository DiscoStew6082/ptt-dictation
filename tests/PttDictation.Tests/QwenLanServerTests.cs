using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Http;
using PttDictation.App;
using PttDictation.Core;

namespace PttDictation.Tests;

[TestClass]
public sealed class QwenLanServerTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";

    [TestMethod]
    public async Task AuthenticatedUploadReturnsUnicodeAndCleansAudioWithoutUsingClientFilename()
    {
        await using var fixture = await Fixture.Start(async (path, token) =>
        {
            Assert.AreEqual("recording", await File.ReadAllTextAsync(path, token));
            Assert.AreEqual("audio.wav", Path.GetFileName(path));
            return new TranscriptResult("Hello, café.", TimeSpan.Zero, null);
        });
        using var unauthorized = await fixture.Client.PostAsync("v1/audio/transcriptions", Upload());
        Assert.AreEqual(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.AreEqual(0, fixture.Decodes);
        fixture.Authorize();
        using var response = await fixture.Client.PostAsync("v1/audio/transcriptions", Upload());
        response.EnsureSuccessStatusCode();
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "Hello, caf");
        using var plain = await fixture.Client.PostAsync("v1/audio/transcriptions", Upload(format: "text"));
        Assert.AreEqual("Hello, café.", await plain.Content.ReadAsStringAsync());
        Assert.AreEqual(0, Directory.GetDirectories(fixture.Root).Length);
        using var models = await fixture.Client.GetAsync("v1/models");
        StringAssert.Contains(await models.Content.ReadAsStringAsync(), QwenLanServer.ModelId);
    }

    [TestMethod]
    public async Task InvalidModelFormatLanguageAndAudioFailBeforeTranscription()
    {
        var calls = 0;
        await using var fixture = await Fixture.Start((_, _) => { calls++; throw new Exception(); });
        fixture.Authorize();
        foreach (var upload in new[] { Upload(model: "wrong"), Upload(format: "srt"), Upload(language: "fr") })
        {
            using var response = await fixture.Client.PostAsync("v1/audio/transcriptions", upload);
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        }
        fixture.InvalidAudio = true;
        using var badAudio = await fixture.Client.PostAsync("v1/audio/transcriptions", Upload());
        Assert.AreEqual(HttpStatusCode.BadRequest, badAudio.StatusCode);
        Assert.AreEqual(0, calls);
        Assert.AreEqual(0, Directory.GetDirectories(fixture.Root).Length);
    }

    [TestMethod]
    public async Task BusyPhoneRequestIsRejectedAndFailureDoesNotLeakExceptionDetails()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await Fixture.Start(async (_, token) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            throw new IOException("private model path and transcript");
        });
        fixture.Authorize();
        var first = fixture.Client.PostAsync("v1/audio/transcriptions", Upload());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var second = await fixture.Client.PostAsync("v1/audio/transcriptions", Upload());
        Assert.AreEqual(HttpStatusCode.TooManyRequests, second.StatusCode);
        release.SetResult();
        using var failed = await first;
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.IsFalse((await failed.Content.ReadAsStringAsync()).Contains("private model"));
        Assert.AreEqual(0, Directory.GetDirectories(fixture.Root).Length);
    }

    [TestMethod]
    public async Task ServerShutdownCancelsInFlightRecognitionAndCleansAudio()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await Fixture.Start(async (_, token) =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { cancelled.SetResult(); }
            return new TranscriptResult("never", TimeSpan.Zero, null);
        });
        fixture.Authorize();
        var pending = fixture.Client.PostAsync("v1/audio/transcriptions", Upload());
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Server.DisposeAsync();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { using var response = await pending; Assert.AreEqual(HttpStatusCode.GatewayTimeout, response.StatusCode); }
        catch (HttpRequestException) { }
        Assert.AreEqual(0, Directory.GetDirectories(fixture.Root).Length);
    }

    [TestMethod]
    public void ConfigurationRejectsWildcardPublicAddressAndWeakKey()
    {
        foreach (var address in new[] { "0.0.0.0", "8.8.8.8", "::", "localhost" })
            Assert.Throws<InvalidDataException>(() => new QwenLanOptions(address, 8765, Key, Environment.ProcessPath!).Validate());
        Assert.Throws<InvalidDataException>(() => new QwenLanOptions("127.0.0.1", 8765, "weak", Environment.ProcessPath!).Validate());
    }

    [TestMethod]
    public void StartupCleanupDeletesOnlyRecognizedRequestAudio()
    {
        var root = Path.Combine(Path.GetTempPath(), "ptt-lan-cleanup-" + Guid.NewGuid().ToString("N"));
        var owned = Path.Combine(root, "request-" + Guid.NewGuid().ToString("N"));
        var other = Path.Combine(root, "request-unrecognized");
        Directory.CreateDirectory(owned);
        Directory.CreateDirectory(other);
        try
        {
            File.WriteAllText(Path.Combine(owned, "audio.wav"), "audio");
            File.WriteAllText(Path.Combine(owned, "upload.audio"), "audio");
            File.WriteAllText(Path.Combine(other, "audio.wav"), "keep");
            QwenLanServer.DeleteAbandonedAudio(root);
            Assert.IsFalse(Directory.Exists(owned));
            Assert.IsTrue(File.Exists(Path.Combine(other, "audio.wav")));
        }
        finally { Directory.Delete(root, true); }
    }

    private static MultipartFormDataContent Upload(string model = QwenLanServer.ModelId, string format = "json", string language = "en")
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent(model), "model");
        content.Add(new StringContent(format), "response_format");
        content.Add(new StringContent(language), "language");
        content.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("recording")), "file", "../../outside.wav");
        return content;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ptt-lan-test-" + Guid.NewGuid().ToString("N"));
        public QwenLanServer Server = null!;
        public HttpClient Client = null!;
        public int Decodes;
        public bool InvalidAudio;
        public static async Task<Fixture> Start(Func<string, CancellationToken, Task<TranscriptResult>> transcribe)
        {
            var fixture = new Fixture();
            Directory.CreateDirectory(fixture.Root);
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            fixture.Server = new(new("127.0.0.1", port, Key, Environment.ProcessPath!), fixture.Root, transcribe,
                async (file, destination, token) =>
                {
                    fixture.Decodes++;
                    if (fixture.InvalidAudio) throw new InvalidDataException();
                    await using var output = File.Create(destination);
                    await file.CopyToAsync(output, token);
                });
            await fixture.Server.StartAsync();
            fixture.Client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
            return fixture;
        }
        public void Authorize() => Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Key);
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Server.DisposeAsync();
            Directory.Delete(Root, true);
        }
    }
}
