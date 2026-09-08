using PttDictation.App;
using PttDictation.Core;
using System.Text.Json;

namespace PttDictation.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ExperimentalTracingTests
{
    [TestMethod]
    public async Task SwallowedChunkFailureIsRecordedWithoutChangingPreviewOrFinalResult()
    {
        var withoutTrace = await RunChunkFailureAsync();
        using var folder = new TraceFolder();
        using var trace = DiagnosticTrace.Configure(folder.Path, "experimental-test");
        DiagnosticTrace.BeginRecording("chunk-failure-test");
        var withTrace = await RunChunkFailureAsync();
        Assert.AreEqual(withoutTrace, withTrace);
        Assert.AreEqual("surviving preview|final recognition", withTrace);
        var events = await folder.ReadAsync();
        StringAssert.Contains(events, "chunk.failed");
        StringAssert.Contains(events, "injected chunk failure");
        StringAssert.Contains(events, "InvalidOperationException");
        StringAssert.Contains(events, "recognition.chunk_raw");
        StringAssert.Contains(events, "recognition.final_raw");
    }

    [TestMethod]
    public async Task SubscriberFailureAndCancellationRemainSwallowedButHaveDistinctTraceEvents()
    {
        using var folder = new TraceFolder();
        using var trace = DiagnosticTrace.Configure(folder.Path, "experimental-test");
        DiagnosticTrace.BeginRecording("subscriber-test");
        var recorder = new Recorder();
        var preview = new Preview(new OperationCanceledException("injected chunk cancellation"));
        var session = new ChunkedTranscribingDictationSession(recorder, preview, new Final());
        session.TranscriptUpdated += _ => throw new InvalidOperationException("injected subscriber failure");
        await session.StartAsync(CancellationToken.None);
        recorder.Emit("first");
        recorder.Emit("second");
        await preview.SecondCall.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // A third chunk confirms the previous callback returned despite its exception.
        recorder.Emit("third");
        await preview.ThirdCall.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var result = await session.StopAsync(CancellationToken.None);
        Assert.AreEqual("final recognition", result.Transcript.Text);
        var events = await folder.ReadAsync();
        StringAssert.Contains(events, "chunk.cancelled");
        StringAssert.Contains(events, "preview.subscriber_failed");
        StringAssert.Contains(events, "injected subscriber failure");
    }

    [TestMethod]
    public async Task RangeConflictAndSuccessfulInsertionKeepTheirOutcomesAndDoNotLogSurroundingText()
    {
        var withoutTrace = RunTargetCases();
        using var folder = new TraceFolder();
        using var trace = DiagnosticTrace.Configure(folder.Path, "experimental-test");
        DiagnosticTrace.BeginRecording("range-test");
        CollectionAssert.AreEqual(withoutTrace, RunTargetCases());
        var events = await folder.ReadAsync();
        StringAssert.Contains(events, "acknowledgement_timeout");
        StringAssert.Contains(events, "document_partition_or_surroundings_changed");
        StringAssert.Contains(events, "target.write_acknowledged");
        Assert.IsFalse(events.Contains("private surrounding content", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task LiveInsertionProviderExceptionPreservesFailureWithTracingEnabledOrDisabled()
    {
        var withoutTrace = await RunInsertionFailureAsync();
        using var folder = new TraceFolder();
        using var trace = DiagnosticTrace.Configure(folder.Path, "experimental-test");
        DiagnosticTrace.BeginRecording("insertion-failure-test");
        Assert.AreEqual(withoutTrace, await RunInsertionFailureAsync());
        var events = await folder.ReadAsync();
        StringAssert.Contains(events, "inline.update_failed");
        StringAssert.Contains(events, "injected provider failure");
    }

    [TestMethod]
    public async Task DelayedChunkAndNestedRuntimeTraceRemainAttachedToOriginalRecording()
    {
        using var folder = new TraceFolder();
        using var trace = DiagnosticTrace.Configure(folder.Path, "experimental-test");
        var original = DiagnosticTrace.BeginRecording("original");
        var recorder = new Recorder();
        var preview = new DeferredPreview();
        var session = new ChunkedTranscribingDictationSession(recorder, preview, new Final());
        var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.TranscriptUpdated += _ => published.TrySetResult();
        await session.StartAsync(CancellationToken.None);
        recorder.Emit("delayed");
        await preview.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        DiagnosticTrace.BeginRecording("later");
        preview.Release.TrySetResult();
        await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await session.StopAsync(CancellationToken.None);
        var events = await folder.ReadAsync();
        foreach (var line in events.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var json = JsonDocument.Parse(line);
            var stage = json.RootElement.GetProperty("stage").GetString();
            if (stage is "test.nested_runtime_completed" or "recognition.chunk_raw" or "recognition.final_raw")
                Assert.AreEqual(original, json.RootElement.GetProperty("recordingId").GetString());
        }
        StringAssert.Contains(events, "test.nested_runtime_completed");
    }

    private static async Task<string> RunChunkFailureAsync()
    {
        var recorder = new Recorder();
        var preview = new Preview(new InvalidOperationException("injected chunk failure"));
        var session = new ChunkedTranscribingDictationSession(recorder, preview, new Final());
        var published = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.TranscriptUpdated += update => published.TrySetResult(update.StableText);
        await session.StartAsync(CancellationToken.None);
        recorder.Emit("first");
        recorder.Emit("second");
        var text = await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var result = await session.StopAsync(CancellationToken.None);
        return text + "|" + result.Transcript.Text;
    }

    private static TextTargetUpdateResult[] RunTargetCases()
    {
        var surface = new Surface();
        var time = DateTimeOffset.UtcNow;
        var target = new WindowsTextTarget(surface, () => time);
        var pending = target.TryReplace("", "spoken words", text => surface.Document = Surface.Prefix + text);
        var success = target.TryReplace("", "spoken words", _ => Assert.Fail("Acknowledgement must not paste again."));
        surface.Document += "user edit";
        var conflict = target.TryReplace("spoken words", "revised words", _ => Assert.Fail("Conflicting text must not paste."));
        var stale = new WindowsTextTarget(new Surface(), () => time);
        stale.TryReplace("", "unacknowledged", _ => { });
        time += TimeSpan.FromSeconds(3);
        var timeout = stale.TryReplace("", "unacknowledged", _ => Assert.Fail("Timeout must not paste again."));
        Assert.AreEqual(TextTargetUpdateResult.Pending, pending);
        Assert.AreEqual(TextTargetUpdateResult.Success, success);
        Assert.AreEqual(TextTargetUpdateResult.Conflict, conflict);
        Assert.AreEqual(TextTargetUpdateResult.Conflict, timeout);
        return [pending, success, conflict, timeout];
    }

    private static async Task<string> RunInsertionFailureAsync()
    {
        using var paster = new LiveClipboardPaster(() => new ThrowingTarget(), (_, _) => Assert.Fail("No paste expected."));
        paster.CaptureTarget();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => paster.PasteAsync("retained final text", CancellationToken.None));
        return failure.Message;
    }

    private sealed class Recorder : IChunkedAudioRecorder
    {
        public event Action<RecordedAudio>? AudioChunkReady;
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public Task<RecordedAudio> StopAsync(CancellationToken token)
            => Task.FromResult(new RecordedAudio("unused-final.wav", TimeSpan.FromSeconds(10)));
        public void Emit(string name) => AudioChunkReady?.Invoke(new RecordedAudio(name, TimeSpan.FromSeconds(3), OverlapDuration: TimeSpan.FromMilliseconds(250)));
    }

    private sealed class Preview(Exception firstFailure) : ITranscriber
    {
        private int _calls;
        public TaskCompletionSource SecondCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ThirdCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<TranscriptResult> TranscribeAsync(string path, CancellationToken token)
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == 1) return Task.FromException<TranscriptResult>(firstFailure);
            if (call == 2) SecondCall.TrySetResult();
            if (call == 3) ThirdCall.TrySetResult();
            return Task.FromResult(new TranscriptResult("surviving preview", null, null));
        }
    }

    private sealed class Final : ITranscriber
    {
        public Task<TranscriptResult> TranscribeAsync(string path, CancellationToken token)
            => Task.FromResult(new TranscriptResult("final recognition", null, null));
    }

    private sealed class DeferredPreview : ITranscriber
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<TranscriptResult> TranscribeAsync(string path, CancellationToken token)
        {
            Started.TrySetResult();
            await Release.Task;
            DiagnosticTrace.Write("test.nested_runtime_completed");
            return new TranscriptResult("late words", null, null);
        }
    }

    private sealed class Surface : IWindowsTextSurface
    {
        public const string Prefix = "private surrounding content ";
        public string Document = Prefix;
        public bool IsFocused => true;
        public bool SupportsReplacement => true;
        public bool CanPasteFallback => true;
        public TextTargetSnapshot Read() => new(Document, Prefix, Document[Prefix.Length..], "");
        public bool Select(string prefix, string ownedText, string suffix) => true;
        public void RevealCaret() { }
    }

    private sealed class ThrowingTarget : IWindowsTextTarget
    {
        public bool IsFocused => true;
        public bool SupportsReplacement => true;
        public bool CanPasteFallback => true;
        public bool IsPreparedSelectionCurrent => true;
        public bool HasAttemptedWrite => false;
        public TextTargetUpdateResult TryReplace(string previousText, string replacementText, Action<string> paste)
            => throw new InvalidOperationException("injected provider failure");
    }

    private sealed class TraceFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ptt-experimental-trace-test-" + Guid.NewGuid().ToString("N"));
        public async Task<string> ReadAsync()
        {
            await DiagnosticTrace.FlushAsync();
            return string.Join("\n", Directory.GetFiles(Path, "*.jsonl", SearchOption.AllDirectories).Select(File.ReadAllText));
        }
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
