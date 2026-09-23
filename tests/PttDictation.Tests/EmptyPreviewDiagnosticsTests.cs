using System.Text.Json;
using PttDictation.Core;

namespace PttDictation.Tests;

[TestClass]
[DoNotParallelize]
public sealed class EmptyPreviewDiagnosticsTests
{
    private const string WarningStage = "recognition.preview_empty_warning";

    [TestMethod]
    public async Task MultipleSuccessfulEmptyPreviewsAndNonemptyFinalProduceMetadataOnlyIndexedWarning()
    {
        using var run = new Harness("private final words");
        await run.Start();
        await run.Chunk("");
        await run.Chunk(" \r\n\t");
        await run.Session.StopAsync(CancellationToken.None);
        var warning = (await run.Warnings()).Single();
        Assert.AreEqual(run.RecordingId, warning.GetProperty("recordingId").GetString());
        var data = warning.GetProperty("data");
        Assert.AreEqual(2, data.GetProperty("successfulPreviewCount").GetInt32());
        Assert.AreEqual(2, data.GetProperty("emptyPreviewCount").GetInt32());
        Assert.AreEqual(0, data.GetProperty("failedPreviewCount").GetInt32());
        Assert.AreEqual(0, data.GetProperty("cancelledPreviewCount").GetInt32());
        Assert.AreEqual(19, data.GetProperty("finalCharacterCount").GetInt32());
        Assert.AreEqual(9450d, data.GetProperty("finalAudioMilliseconds").GetDouble());
        Assert.AreEqual(2000d, data.GetProperty("minimumPreviewAudioMilliseconds").GetDouble());
        Assert.AreEqual(2000d, data.GetProperty("maximumPreviewAudioMilliseconds").GetDouble());
        Assert.IsTrue(data.GetProperty("previewProcessingMilliseconds").GetDouble() >= 0);
        Assert.IsFalse(warning.ToString().Contains("private final words", StringComparison.Ordinal));
        Assert.IsFalse(warning.ToString().Contains(run.Directory, StringComparison.Ordinal));
        Assert.IsEmpty(System.IO.Directory.GetFiles(run.Directory, "*.wav", SearchOption.AllDirectories));
        Assert.AreEqual(0, run.Published);
    }

    [TestMethod]
    [DataRow(0, "final speech")]
    [DataRow(1, "final speech")]
    [DataRow(2, "")]
    [DataRow(2, " \t")]
    public async Task NoChunksSingleChunkAndSilenceDoNotWarn(int chunks, string final)
    {
        using var run = new Harness(final);
        await run.Start();
        for (var i = 0; i < chunks; i++) await run.Chunk("");
        await run.Session.StopAsync(CancellationToken.None);
        Assert.IsEmpty(await run.Warnings());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AnyNonemptyPreviewPreventsAllEmptyWarning(bool wordsOnly)
    {
        using var run = new Harness("final speech");
        await run.Start();
        await run.Chunk("");
        await run.Chunk("");
        await run.Chunk(wordsOnly ? "" : "preview speech", wordsOnly
            ? [new TranscriptWord("recognized", TimeSpan.Zero, TimeSpan.FromSeconds(1), null)] : null);
        await run.Session.StopAsync(CancellationToken.None);
        Assert.IsEmpty(await run.Warnings());
        Assert.IsTrue(run.Published > 0);
    }

    [TestMethod]
    public async Task FailedPreviewIsReportedAsFailureInsteadOfAllSuccessfulEmptyWarning()
    {
        using var run = new Harness("final speech");
        await run.Start();
        await run.Chunk("");
        await run.Chunk("");
        await run.ChunkError(new IOException("preview provider failed"));
        await run.Session.StopAsync(CancellationToken.None);
        Assert.IsEmpty(await run.Warnings());
    }

    [TestMethod]
    public async Task CancelledPreviewDoesNotCountAsSuccessfulEmpty()
    {
        using var run = new Harness("final speech");
        await run.Start();
        await run.Chunk("");
        await run.ChunkError(new OperationCanceledException());
        await run.Session.StopAsync(CancellationToken.None);
        Assert.IsEmpty(await run.Warnings());
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    public async Task PreviewResultReturnedAfterStopCancellationIsExcludedFromSuccessfulCounts(int completedEmpties)
    {
        using var run = new Harness("final speech");
        await run.Start();
        for (var i = 0; i < completedEmpties; i++) await run.Chunk("");
        await run.BeginPendingChunk();
        var stop = run.Session.StopAsync(CancellationToken.None);
        Assert.IsTrue(run.Preview.LastToken.IsCancellationRequested);
        run.Preview.Pending!.SetResult(new TranscriptResult("late preview must be ignored", null, null));
        await stop.WaitAsync(TimeSpan.FromSeconds(2));
        var warnings = await run.Warnings();
        if (completedEmpties == 1) Assert.IsEmpty(warnings);
        else
        {
            var data = warnings.Single().GetProperty("data");
            Assert.AreEqual(2, data.GetProperty("successfulPreviewCount").GetInt32());
            Assert.AreEqual(1, data.GetProperty("cancelledPreviewCount").GetInt32());
        }
        Assert.AreEqual(0, run.Published);
    }

    [TestMethod]
    public async Task IncompletePreviewSettlementDoesNotProduceAnAllEmptyConclusion()
    {
        using var run = new Harness("final speech");
        await run.Start();
        await run.Chunk("");
        await run.Chunk("");
        await run.BeginPendingChunk();
        // Keep one preview unresolved until the existing settlement deadline
        // expires and final recognition begins. This is not a completed sample.
        run.Final.OnCall = () => run.Preview.Pending!.TrySetResult(new TranscriptResult("", null, null));
        await run.Session.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsEmpty(await run.Warnings());
    }

    [TestMethod]
    public async Task CancelledSessionAndFailedFinalDoNotWarn()
    {
        using var run = new Harness("final speech");
        await run.Start();
        await run.Chunk("");
        await run.Chunk("");
        await run.Session.CancelAsync(CancellationToken.None);
        Assert.IsEmpty(await run.Warnings());
        await run.Start();
        await run.Chunk("");
        await run.Chunk("");
        run.Final.Error = new IOException("final provider failed");
        await Assert.ThrowsAsync<IOException>(() => run.Session.StopAsync(CancellationToken.None));
        Assert.IsEmpty(await run.Warnings());
    }

    [TestMethod]
    public async Task CancelledFinalReturningTextDoesNotWarn()
    {
        using var run = new Harness("final speech");
        await run.Start();
        await run.Chunk("");
        await run.Chunk("");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        // This fake provider deliberately ignores cancellation and returns text.
        await run.Session.StopAsync(cancelled.Token);
        Assert.IsEmpty(await run.Warnings());
    }

    [TestMethod]
    public async Task SuccessfulEmptyCountsResetForReusedSession()
    {
        using var run = new Harness("final speech");
        await run.Start();
        await run.Chunk("");
        await run.Session.StopAsync(CancellationToken.None);
        await run.Start();
        await run.Chunk("");
        await run.Session.StopAsync(CancellationToken.None);
        Assert.IsEmpty(await run.Warnings());
    }

    private sealed class Harness : IDisposable
    {
        public readonly string Directory = Path.Combine(Path.GetTempPath(), "ptt-empty-preview-test-" + Guid.NewGuid().ToString("N"));
        public readonly ChunkedTranscribingDictationSession Session;
        public readonly Transcriber Preview = new();
        public readonly Transcriber Final;
        public string RecordingId = "";
        public int Published;
        private readonly Recorder _recorder = new();
        private readonly IDisposable _trace;
        public Harness(string final)
        {
            System.IO.Directory.CreateDirectory(Directory);
            _trace = DiagnosticTrace.Configure(Directory, "empty-preview-test");
            Final = new Transcriber { Text = final };
            Session = new(_recorder, Preview, Final);
            Session.TranscriptUpdated += update =>
            {
                if (!string.IsNullOrWhiteSpace(update.StableText)) Published++;
            };
        }
        public Task Start()
        {
            RecordingId = DiagnosticTrace.BeginRecording("test");
            return Session.StartAsync(CancellationToken.None);
        }
        public async Task Chunk(string text, IReadOnlyList<TranscriptWord>? words = null)
        {
            Preview.Text = text;
            Preview.Words = words;
            Preview.Error = null;
            await PublishAndWait();
        }
        public async Task ChunkError(Exception error)
        {
            Preview.Error = error;
            await PublishAndWait();
            Preview.Error = null;
        }
        public async Task BeginPendingChunk()
        {
            Preview.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Preview.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var path = Path.Combine(Directory, Guid.NewGuid().ToString("N") + ".wav");
            await File.WriteAllBytesAsync(path, []);
            _recorder.Publish(new(path, TimeSpan.FromSeconds(2), DeleteAfterUse: true));
            await Preview.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        private async Task PublishAndWait()
        {
            var path = Path.Combine(Directory, Guid.NewGuid().ToString("N") + ".wav");
            // Placeholder file for the production ownership/release signal; no microphone/audio data.
            await File.WriteAllBytesAsync(path, []);
            _recorder.Publish(new(path, TimeSpan.FromSeconds(2), DeleteAfterUse: true));
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (File.Exists(path) && DateTime.UtcNow < deadline) await Task.Delay(1);
            Assert.IsFalse(File.Exists(path), "Chunk processing must finish before the next fixture input.");
        }
        public async Task<JsonElement[]> Warnings()
        {
            await DiagnosticTrace.FlushAsync();
            var path = Path.Combine(Directory, "errors.jsonl");
            return File.Exists(path) ? File.ReadAllLines(path).Select(line =>
            {
                using var json = JsonDocument.Parse(line);
                return json.RootElement.Clone();
            }).Where(entry => entry.GetProperty("stage").GetString() == WarningStage).ToArray() : [];
        }
        public void Dispose()
        {
            _trace.Dispose();
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
        }
    }

    private sealed class Recorder : IChunkedAudioRecorder
    {
        public event Action<RecordedAudio>? AudioChunkReady;
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<RecordedAudio> StopAsync(CancellationToken cancellationToken)
            => Task.FromResult(new RecordedAudio("unused-final.wav", TimeSpan.FromMilliseconds(9450)));
        public void Publish(RecordedAudio audio) => AudioChunkReady?.Invoke(audio);
    }

    private sealed class Transcriber : ITranscriber
    {
        public string Text = "";
        public IReadOnlyList<TranscriptWord>? Words;
        public Exception? Error;
        public TaskCompletionSource<TranscriptResult>? Pending;
        public TaskCompletionSource? Started;
        public CancellationToken LastToken;
        public Action? OnCall;
        public Task<TranscriptResult> TranscribeAsync(string wavPath, CancellationToken cancellationToken)
        {
            LastToken = cancellationToken;
            OnCall?.Invoke();
            Started?.TrySetResult();
            return Pending?.Task ?? (Error is null ? Task.FromResult(new TranscriptResult(Text, null, null, Words))
                : Task.FromException<TranscriptResult>(Error));
        }
    }
}
