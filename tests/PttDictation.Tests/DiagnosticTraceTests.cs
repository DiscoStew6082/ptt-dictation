using System.Diagnostics;
using System.Text.Json;
using PttDictation.Core;

namespace PttDictation.Tests;

[TestClass]
[DoNotParallelize]
public sealed class DiagnosticTraceTests
{
    private string directory = null!;

    [TestInitialize]
    public void Initialize() => directory = Path.Combine(Path.GetTempPath(), "ptt-diagnostic-tests-" + Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }

    [TestMethod]
    public void InvalidConfigurationCannotBreakApplicationStartup()
    {
        using var configured = DiagnosticTrace.Configure("\0invalid", "test");
        DiagnosticTrace.Write("application.continues");
    }

    [TestMethod]
    public async Task EventsIncludeBuildSessionTimelineAndOriginalException()
    {
        using var configured = DiagnosticTrace.Configure(directory, "stable-test");
        var id = DiagnosticTrace.BeginRecording("hotkey");
        Exception captured;
        try { throw new InvalidOperationException("chunk failed", new IOException("decoder closed")); }
        catch (Exception error) { captured = error; }
        DiagnosticTrace.Write("chunk.failed", new { text = "recognized words" }, captured);
        await DiagnosticTrace.FlushAsync();
        using var record = JsonDocument.Parse(File.ReadLines(Path.Combine(directory, "events.jsonl")).Last());
        var root = record.RootElement;
        Assert.AreEqual("stable-test", root.GetProperty("build").GetString());
        Assert.AreEqual(id, root.GetProperty("recordingId").GetString());
        Assert.AreEqual(2L, root.GetProperty("sessionSequence").GetInt64());
        Assert.IsGreaterThanOrEqualTo(0, root.GetProperty("elapsedMs").GetDouble());
        StringAssert.Contains(root.GetProperty("exception").GetString()!, "System.IO.IOException: decoder closed");
        StringAssert.Contains(root.GetProperty("exception").GetString()!, nameof(EventsIncludeBuildSessionTimelineAndOriginalException));
    }

    [TestMethod]
    public async Task ErrorIndexKeepsFailuresWithoutPreviewTextOrNormalCancellationNoise()
    {
        using var configured = DiagnosticTrace.Configure(directory, "error-index-test");
        var id = DiagnosticTrace.BeginRecording("test");
        DiagnosticTrace.Write("workflow.preview_text_stages", new { raw = "private preview" });
        DiagnosticTrace.Write("server.request_cancelled", error: new OperationCanceledException());
        DiagnosticTrace.Write("target.conflict", new { reason = "selection_acknowledgement_timeout" });
        DiagnosticTrace.Write("settings.save_failed", error: new IOException("disk unavailable"));
        DiagnosticTrace.Write("ui.warning", new { title = "Settings were not loaded", message = "Invalid settings" });
        await DiagnosticTrace.FlushAsync();

        var lines = File.ReadAllLines(Path.Combine(directory, "errors.jsonl"));
        Assert.HasCount(3, lines);
        Assert.IsFalse(string.Join("\n", lines).Contains("private preview"));
        using var conflict = JsonDocument.Parse(lines[0]);
        Assert.AreEqual(id, conflict.RootElement.GetProperty("recordingId").GetString());
        Assert.AreEqual("selection_acknowledgement_timeout",
            conflict.RootElement.GetProperty("data").GetProperty("reason").GetString());
        Assert.AreEqual(Environment.ProcessId, conflict.RootElement.GetProperty("processId").GetInt32());
    }

    [TestMethod]
    public async Task ErrorsKeepTheirOriginalTargetWhenRecordingsOverlap()
    {
        using var configured = DiagnosticTrace.Configure(directory, "error-index-test");
        var first = DiagnosticTrace.BeginRecording("first");
        DiagnosticTrace.Write("focus_guard.begin", new { targetProcessId = 11 }, recordingId: first);
        var second = DiagnosticTrace.BeginRecording("second");
        DiagnosticTrace.Write("focus_guard.begin", new { targetProcessId = 22 }, recordingId: second);
        DiagnosticTrace.Write("target.process", new { processName = "first-editor" }, recordingId: first);
        DiagnosticTrace.Write("target.conflict", new { reason = "selection_acknowledgement_timeout" }, recordingId: first);
        DiagnosticTrace.Write("target.conflict", new { reason = "provider_failure" }, recordingId: second);
        await DiagnosticTrace.FlushAsync();

        var lines = File.ReadAllLines(Path.Combine(directory, "errors.jsonl"));
        using var original = JsonDocument.Parse(lines[0]);
        using var next = JsonDocument.Parse(lines[1]);
        Assert.AreEqual(first, original.RootElement.GetProperty("recordingId").GetString());
        Assert.AreEqual(11, original.RootElement.GetProperty("target").GetProperty("targetProcessId").GetInt32());
        Assert.AreEqual("first-editor", original.RootElement.GetProperty("targetProcess").GetProperty("processName").GetString());
        Assert.AreEqual(22, next.RootElement.GetProperty("target").GetProperty("targetProcessId").GetInt32());
        Assert.AreEqual(JsonValueKind.Null, next.RootElement.GetProperty("targetProcess").ValueKind);
    }

    [TestMethod]
    public async Task UnavailableErrorIndexDoesNotSuppressTheOriginalTimeline()
    {
        Directory.CreateDirectory(Path.Combine(directory, "errors.jsonl"));
        using var configured = DiagnosticTrace.Configure(directory, "error-index-test");
        DiagnosticTrace.Write("inline.insertion_failed", error: new IOException("original failure"));
        await DiagnosticTrace.FlushAsync();
        StringAssert.Contains(File.ReadAllText(Path.Combine(directory, "events.jsonl")), "original failure");
        Directory.Delete(Path.Combine(directory, "errors.jsonl"));
        DiagnosticTrace.Write("settings.save_failed", error: new IOException("second failure"));
        await DiagnosticTrace.FlushAsync();
        var errors = File.ReadAllText(Path.Combine(directory, "errors.jsonl"));
        StringAssert.Contains(errors, "diagnostics.loss");
        StringAssert.Contains(errors, "second failure");
    }

    [TestMethod]
    public async Task ErrorIndexSurvivesVerboseTimelineRotation()
    {
        using var configured = DiagnosticTrace.Configure(directory, "error-index-test");
        DiagnosticTrace.Write("inline.insertion_failed", error: new InvalidOperationException("editor unavailable"));
        await DiagnosticTrace.FlushAsync();
        for (var index = 0; index < 10; index++)
        {
            DiagnosticTrace.Write("preview", new { text = new string('x', 1_000_000) });
            await DiagnosticTrace.FlushAsync();
        }
        StringAssert.Contains(File.ReadAllText(Path.Combine(directory, "errors.jsonl")), "editor unavailable");
        Assert.IsFalse(File.ReadAllText(Path.Combine(directory, "events.jsonl")).Contains("editor unavailable"));
        Assert.IsFalse(File.ReadAllText(Path.Combine(directory, "events.previous.jsonl")).Contains("editor unavailable"));
    }

    [TestMethod]
    public async Task RetentionKeepsExactlyLastThreeFullRecordingsAfterOriginalDeletion()
    {
        Directory.CreateDirectory(directory);
        using var configured = DiagnosticTrace.Configure(Path.Combine(directory, "trace"), "experimental-test");
        var recorded = new List<(string Id, byte[] Bytes)>();
        for (var index = 0; index < 5; index++)
        {
            var id = DiagnosticTrace.BeginRecording("test");
            var source = Path.Combine(directory, "temporary.wav");
            var bytes = Enumerable.Repeat((byte)index, 100_000 + index).ToArray();
            File.WriteAllBytes(source, bytes);
            using var blocker = new BlockingData();
            DiagnosticTrace.Write("worker.block", blocker);
            Assert.IsTrue(blocker.Entered.Wait(TimeSpan.FromSeconds(5)));
            DiagnosticTrace.RetainRecording(id, source);
            File.Delete(source);
            Assert.IsFalse(File.Exists(source));
            blocker.Release.Set();
            await DiagnosticTrace.FlushAsync();
            recorded.Add((id, bytes));
        }
        var root = Path.Combine(directory, "trace");
        Assert.HasCount(3, Directory.GetDirectories(root));
        foreach (var recording in recorded.TakeLast(3))
            CollectionAssert.AreEqual(recording.Bytes, File.ReadAllBytes(Path.Combine(root, recording.Id, "audio.wav")));
        foreach (var recording in recorded.Take(2))
            Assert.IsFalse(Directory.Exists(Path.Combine(root, recording.Id)));
    }

    [TestMethod]
    public async Task BlockedBackgroundWriterDoesNotBlockRecordingAndReportsQueueLoss()
    {
        using var configured = DiagnosticTrace.Configure(directory, "test");
        using var blocker = new BlockingData();
        DiagnosticTrace.Write("worker.block", blocker);
        Assert.IsTrue(blocker.Entered.Wait(TimeSpan.FromSeconds(5)));
        var timer = Stopwatch.StartNew();
        for (var index = 0; index < 2_000; index++)
            DiagnosticTrace.Write("transcript", new { index });
        Assert.IsLessThan(TimeSpan.FromSeconds(2), timer.Elapsed);
        blocker.Release.Set();
        await DiagnosticTrace.FlushAsync();
        StringAssert.Contains(File.ReadAllText(Path.Combine(directory, "events.jsonl")), "diagnostics.loss");
    }

    [TestMethod]
    public async Task UnwritableDestinationDoesNotThrowAndWriterRecoversWithFailureEvidence()
    {
        Directory.CreateDirectory(directory);
        var root = Path.Combine(directory, "occupied");
        File.WriteAllText(root, "A file prevents creating the diagnostics directory.");
        using var configured = DiagnosticTrace.Configure(root, "test");
        DiagnosticTrace.BeginRecording("test");
        DiagnosticTrace.Write("chunk.failed", error: new IOException("original failure"));
        await DiagnosticTrace.FlushAsync();
        File.Delete(root);
        DiagnosticTrace.Write("after.recovery");
        await DiagnosticTrace.FlushAsync();
        var log = File.ReadAllText(Path.Combine(root, "events.jsonl"));
        StringAssert.Contains(log, "sinkFailures");
        StringAssert.Contains(log, "after.recovery");
    }

    [TestMethod]
    public async Task MissingAudioIsReportedAndInvalidIdentifiersCannotEscapeRoot()
    {
        Directory.CreateDirectory(directory);
        using var configured = DiagnosticTrace.Configure(directory, "test");
        var id = DiagnosticTrace.BeginRecording("test");
        DiagnosticTrace.RetainRecording(id, Path.Combine(directory, "missing.wav"));
        DiagnosticTrace.RetainRecording("../outside", Path.Combine(directory, "missing.wav"));
        await DiagnosticTrace.FlushAsync();
        var log = File.ReadAllText(Path.Combine(directory, "events.jsonl"));
        StringAssert.Contains(log, "audio.retention_failed");
        StringAssert.Contains(log, "FileNotFoundException");
        StringAssert.Contains(log, "Invalid recording identifier");
        Assert.HasCount(0, Directory.GetDirectories(directory));
    }

    [TestMethod]
    public async Task OpenStreamTransfersOwnershipAndRetainsDeletedSource()
    {
        Directory.CreateDirectory(directory);
        var root = Path.Combine(directory, "trace");
        using var configured = DiagnosticTrace.Configure(root, "test");
        var sourcePath = Path.Combine(directory, "source.wav");
        byte[] bytes = [7, 8, 9, 10];
        File.WriteAllBytes(sourcePath, bytes);
        var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        File.Delete(sourcePath);
        var id = DiagnosticTrace.BeginRecording("watcher");
        DiagnosticTrace.RetainRecording(id, source);
        await DiagnosticTrace.FlushAsync();
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Path.Combine(root, id, "audio.wav")));
        Assert.IsFalse(source.CanRead);
    }

    [TestMethod]
    public void DisabledSinkStillDisposesTransferredStream()
    {
        using (DiagnosticTrace.Configure(directory, "test")) { }
        var source = new MemoryStream([1, 2, 3]);
        DiagnosticTrace.RetainRecording("irrelevant", source);
        Assert.IsFalse(source.CanRead);
    }

    [TestMethod]
    public async Task AsyncRecordingScopeKeepsOverlappingSessionsCorrelated()
    {
        using var configured = DiagnosticTrace.Configure(directory, "test");
        var original = DiagnosticTrace.BeginRecording("first");
        var latest = DiagnosticTrace.BeginRecording("second");
        using (DiagnosticTrace.EnterRecording(original))
        {
            await Task.Run(() => DiagnosticTrace.Write("first.continuation"));
            Assert.AreEqual(original, DiagnosticTrace.CurrentRecordingId);
            using (DiagnosticTrace.EnterRecording(latest))
                Assert.AreEqual(latest, DiagnosticTrace.CurrentRecordingId);
            Assert.AreEqual(original, DiagnosticTrace.CurrentRecordingId);
        }
        Assert.AreEqual(latest, DiagnosticTrace.CurrentRecordingId);
        await DiagnosticTrace.FlushAsync();
        using var record = JsonDocument.Parse(File.ReadLines(Path.Combine(directory, "events.jsonl")).Last());
        Assert.AreEqual(original, record.RootElement.GetProperty("recordingId").GetString());
    }

    [TestMethod]
    public async Task DestinationAudioWriteFailureIsRecordedWithoutConsumingRetentionSlot()
    {
        Directory.CreateDirectory(directory);
        var root = Path.Combine(directory, "trace");
        using var configured = DiagnosticTrace.Configure(root, "test");
        var source = Path.Combine(directory, "source.wav");
        File.WriteAllBytes(source, [1, 2, 3, 4]);
        var id = DiagnosticTrace.BeginRecording("test");
        await DiagnosticTrace.FlushAsync();
        // A file in place of the session directory forces a real destination failure.
        File.WriteAllText(Path.Combine(root, id), "occupied");
        DiagnosticTrace.RetainRecording(id, source);
        File.Delete(source);
        await DiagnosticTrace.FlushAsync();
        var log = File.ReadAllText(Path.Combine(root, "events.jsonl"));
        StringAssert.Contains(log, "audio.retention_failed");
        StringAssert.Contains(log, "IOException");
        Assert.HasCount(0, Directory.GetDirectories(root));
    }

    [TestMethod]
    public async Task LogsRotateAndRemainBounded()
    {
        using var configured = DiagnosticTrace.Configure(directory, "test");
        for (var index = 0; index < 30; index++)
            DiagnosticTrace.Write("transcript", new { text = new string('a', 300_000) });
        await DiagnosticTrace.FlushAsync();
        var files = Directory.GetFiles(directory, "*.jsonl");
        Assert.HasCount(2, files);
        foreach (var file in files)
        {
            Assert.IsLessThanOrEqualTo(4 * 1024 * 1024L, new FileInfo(file).Length);
            foreach (var line in File.ReadLines(file))
                using (JsonDocument.Parse(line)) { }
        }
    }

    private sealed class BlockingData : IDisposable
    {
        [System.Text.Json.Serialization.JsonIgnore]
        public ManualResetEventSlim Entered { get; } = new();
        [System.Text.Json.Serialization.JsonIgnore]
        public ManualResetEventSlim Release { get; } = new();

        public string Value
        {
            get
            {
                Entered.Set();
                if (!Release.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("Test did not release the diagnostics worker.");
                return "released";
            }
        }

        public void Dispose()
        {
            Release.Set();
            // A failed test can leave the worker unwinding; events deliberately remain usable.
        }
    }
}
