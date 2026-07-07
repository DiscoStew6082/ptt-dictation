using ParakeetPtt.Core;
using System.IO.Compression;

namespace ParakeetPtt.Tests;

[TestClass]
public sealed class CoreBehaviorTests
{
    [TestMethod]
    public void TranscriptCorrectionsApplyPhrasesAndWholeWords()
    {
        var corrections = new TranscriptCorrectionDictionary(
            [
                new TranscriptCorrection("kuda", "CUDA"),
                new TranscriptCorrection("parakeet p t t", "Parakeet PTT"),
                new TranscriptCorrection("c sharp", "C#")
            ]);

        var corrected = corrections.Apply("kuda is not a barracuda. parakeet p t t likes c sharp");

        Assert.AreEqual("CUDA is not a barracuda. Parakeet PTT likes C#", corrected);
    }

    [TestMethod]
    public async Task ReleaseTranscribesAndPastesCleanedText()
    {
        var recorder = new FakeAudioRecorder("utterance.wav");
        var transcriber = new FakeTranscriber("  hello parakeet  \n\n");
        var paster = new FakeClipboardPaster();
        var controller = new DictationController(recorder, transcriber, paster, new SessionHistory());

        var started = await controller.HandleHotkeyDownAsync(CancellationToken.None);
        var outcome = await controller.HandleHotkeyUpAsync(CancellationToken.None);

        Assert.IsTrue(started);
        Assert.AreEqual(DictationOutcome.Pasted, outcome);
        Assert.AreEqual(1, recorder.StartCount);
        Assert.AreEqual(1, recorder.StopCount);
        Assert.AreEqual("utterance.wav", transcriber.LastAudioPath);
        Assert.AreEqual("Hello parakeet.", paster.PastedText);
    }

    [TestMethod]
    public async Task ReleasePublishesCleanedTranscriptPreviewBeforePaste()
    {
        string? preview = null;
        string? previewAtPaste = null;
        var paster = new FakeClipboardPaster(() => previewAtPaste = preview);
        var controller = new DictationController(
            new FakeAudioRecorder("utterance.wav"),
            new FakeTranscriber("  preview this  "),
            paster,
            new SessionHistory(),
            text => preview = text);

        await controller.HandleHotkeyDownAsync(CancellationToken.None);
        var outcome = await controller.HandleHotkeyUpAsync(CancellationToken.None);

        Assert.AreEqual(DictationOutcome.Pasted, outcome);
        Assert.AreEqual("Preview this.", preview);
        Assert.AreEqual("Preview this.", previewAtPaste);
    }

    [TestMethod]
    public async Task ReleaseAppliesTranscriptCorrectionsBeforePreviewHistoryAndPaste()
    {
        string? preview = null;
        var history = new SessionHistory();
        var paster = new FakeClipboardPaster();
        var controller = new DictationController(
            new FakeAudioRecorder("utterance.wav"),
            new FakeTranscriber("  kuda likes c sharp  "),
            paster,
            history,
            text => preview = text,
            getTranscriptCorrections: () =>
            [
                new TranscriptCorrection("kuda", "CUDA"),
                new TranscriptCorrection("c sharp", "C#")
            ]);

        await controller.HandleHotkeyDownAsync(CancellationToken.None);
        var outcome = await controller.HandleHotkeyUpAsync(CancellationToken.None);

        Assert.AreEqual(DictationOutcome.Pasted, outcome);
        Assert.AreEqual("CUDA likes C#.", preview);
        Assert.AreEqual("CUDA likes C#.", paster.PastedText);
        CollectionAssert.AreEqual(new[] { "CUDA likes C#." }, history.Items.ToArray());
    }

    [TestMethod]
    public async Task IncrementalSessionPublishesPartialTextButOnlyPastesFinalTranscript()
    {
        var session = new FakeDictationSession("  final text  ");
        var updates = new List<TranscriptUpdate>();
        var history = new SessionHistory();
        var paster = new FakeClipboardPaster();
        var controller = new DictationController(
            new FakeDictationSessionFactory(session),
            paster,
            history,
            transcriptUpdateReady: updates.Add);

        var started = await controller.HandleHotkeyDownAsync(CancellationToken.None);
        session.PublishPartial("partial text");

        Assert.IsTrue(started);
        Assert.IsNull(paster.PastedText);
        Assert.AreEqual(0, history.Items.Count);
        Assert.AreEqual(1, updates.Count);
        Assert.AreEqual(TranscriptUpdateKind.Partial, updates[0].Kind);
        Assert.AreEqual("partial text", updates[0].StableText);

        var outcome = await controller.HandleHotkeyUpAsync(CancellationToken.None);

        Assert.AreEqual(DictationOutcome.Pasted, outcome);
        Assert.AreEqual("Final text.", paster.PastedText);
        CollectionAssert.AreEqual(new[] { "Final text." }, history.Items.ToArray());
        Assert.AreEqual(1, session.StartCount);
        Assert.AreEqual(1, session.StopCount);
    }

    [TestMethod]
    public async Task IncrementalSessionPublishesCorrectedPartialTextForPreview()
    {
        var session = new FakeDictationSession("final text");
        var updates = new List<TranscriptUpdate>();
        var controller = new DictationController(
            new FakeDictationSessionFactory(session),
            new FakeClipboardPaster(),
            new SessionHistory(),
            transcriptUpdateReady: updates.Add,
            getTranscriptCorrections: () =>
            [
                new TranscriptCorrection("kuda", "CUDA"),
                new TranscriptCorrection("c sharp", "C#")
            ]);

        await controller.HandleHotkeyDownAsync(CancellationToken.None);
        session.PublishPartial("kuda likes c sharp");

        Assert.AreEqual(1, updates.Count);
        Assert.AreEqual(TranscriptUpdateKind.Partial, updates[0].Kind);
        Assert.AreEqual("CUDA likes C#", updates[0].StableText);
    }

    [TestMethod]
    public async Task ChunkedSessionPublishesMergedPartialsAndDeletesChunkFiles()
    {
        var chunkOne = Path.Combine(Path.GetTempPath(), $"parakeet-chunk-{Guid.NewGuid():N}-1.wav");
        var chunkTwo = Path.Combine(Path.GetTempPath(), $"parakeet-chunk-{Guid.NewGuid():N}-2.wav");
        var finalAudio = Path.Combine(Path.GetTempPath(), $"parakeet-final-{Guid.NewGuid():N}.wav");
        await File.WriteAllTextAsync(chunkOne, "chunk one");
        await File.WriteAllTextAsync(chunkTwo, "chunk two");
        await File.WriteAllTextAsync(finalAudio, "final");
        var recorder = new FakeChunkedAudioRecorder(finalAudio);
        var transcriber = new FakeMappedTranscriber(new Dictionary<string, string>
        {
            [chunkOne] = "hello brave",
            [chunkTwo] = "brave new world",
            [finalAudio] = "hello brave new world"
        });
        var session = new ChunkedTranscribingDictationSession(recorder, transcriber);
        var updates = new List<TranscriptUpdate>();
        session.TranscriptUpdated += updates.Add;

        await session.StartAsync(CancellationToken.None);
        recorder.PublishChunk(chunkOne);
        recorder.PublishChunk(chunkTwo);
        await WaitUntilAsync(() => updates.Count == 2);
        var result = await session.StopAsync(CancellationToken.None);

        Assert.AreEqual("hello brave new world", result.Transcript.Text);
        CollectionAssert.AreEqual(
            new[] { "hello brave", "hello brave new world" },
            updates.Select(update => update.StableText).ToArray());
        Assert.IsFalse(File.Exists(chunkOne));
        Assert.IsFalse(File.Exists(chunkTwo));
        Assert.IsTrue(File.Exists(finalAudio));
        File.Delete(finalAudio);
    }

    [TestMethod]
    public async Task ChunkedSessionUsesWordTimestampsToDropOverlappedPartialWords()
    {
        var chunkOne = Path.Combine(Path.GetTempPath(), $"parakeet-chunk-{Guid.NewGuid():N}-1.wav");
        var chunkTwo = Path.Combine(Path.GetTempPath(), $"parakeet-chunk-{Guid.NewGuid():N}-2.wav");
        var finalAudio = Path.Combine(Path.GetTempPath(), $"parakeet-final-{Guid.NewGuid():N}.wav");
        await File.WriteAllTextAsync(chunkOne, "chunk one");
        await File.WriteAllTextAsync(chunkTwo, "chunk two");
        await File.WriteAllTextAsync(finalAudio, "final");
        var recorder = new FakeChunkedAudioRecorder(finalAudio);
        var transcriber = new FakeResultTranscriber(new Dictionary<string, TranscriptResult>
        {
            [chunkOne] = new(
                "hello brave",
                TimeSpan.FromMilliseconds(10),
                null,
                [
                    new TranscriptWord("hello", TimeSpan.FromSeconds(0.1), TimeSpan.FromSeconds(0.3), 0.9),
                    new TranscriptWord("brave", TimeSpan.FromSeconds(0.4), TimeSpan.FromSeconds(0.7), 0.9)
                ]),
            [chunkTwo] = new(
                "brave new world",
                TimeSpan.FromMilliseconds(10),
                null,
                [
                    new TranscriptWord("brave", TimeSpan.FromSeconds(0.1), TimeSpan.FromSeconds(0.6), 0.8),
                    new TranscriptWord("new", TimeSpan.FromSeconds(0.9), TimeSpan.FromSeconds(1.1), 0.9),
                    new TranscriptWord("world", TimeSpan.FromSeconds(1.2), TimeSpan.FromSeconds(1.5), 0.9)
                ]),
            [finalAudio] = new("hello brave new world", TimeSpan.FromMilliseconds(10), null)
        });
        var session = new ChunkedTranscribingDictationSession(recorder, transcriber);
        var updates = new List<TranscriptUpdate>();
        session.TranscriptUpdated += updates.Add;

        await session.StartAsync(CancellationToken.None);
        recorder.PublishChunk(chunkOne);
        recorder.PublishChunk(chunkTwo, TimeSpan.FromSeconds(0.8));
        await WaitUntilAsync(() => updates.Count == 2);
        var result = await session.StopAsync(CancellationToken.None);

        Assert.AreEqual("hello brave new world", result.Transcript.Text);
        CollectionAssert.AreEqual(
            new[] { "hello brave", "hello brave new world" },
            updates.Select(update => update.StableText).ToArray());
        File.Delete(finalAudio);
    }

    [TestMethod]
    public async Task ChunkedSessionStillMergesTimestampWordsThatStraddleOverlapBoundary()
    {
        var chunkOne = Path.Combine(Path.GetTempPath(), $"parakeet-chunk-{Guid.NewGuid():N}-1.wav");
        var chunkTwo = Path.Combine(Path.GetTempPath(), $"parakeet-chunk-{Guid.NewGuid():N}-2.wav");
        var finalAudio = Path.Combine(Path.GetTempPath(), $"parakeet-final-{Guid.NewGuid():N}.wav");
        await File.WriteAllTextAsync(chunkOne, "chunk one");
        await File.WriteAllTextAsync(chunkTwo, "chunk two");
        await File.WriteAllTextAsync(finalAudio, "final");
        var recorder = new FakeChunkedAudioRecorder(finalAudio);
        var transcriber = new FakeResultTranscriber(new Dictionary<string, TranscriptResult>
        {
            [chunkOne] = new(
                "hello brave",
                TimeSpan.FromMilliseconds(10),
                null,
                [
                    new TranscriptWord("hello", TimeSpan.FromSeconds(0.1), TimeSpan.FromSeconds(0.3), 0.9),
                    new TranscriptWord("brave", TimeSpan.FromSeconds(0.4), TimeSpan.FromSeconds(0.7), 0.9)
                ]),
            [chunkTwo] = new(
                "brave new",
                TimeSpan.FromMilliseconds(10),
                null,
                [
                    new TranscriptWord("brave", TimeSpan.FromSeconds(0.4), TimeSpan.FromSeconds(0.9), 0.8),
                    new TranscriptWord("new", TimeSpan.FromSeconds(1.0), TimeSpan.FromSeconds(1.2), 0.9)
                ]),
            [finalAudio] = new("hello brave new", TimeSpan.FromMilliseconds(10), null)
        });
        var session = new ChunkedTranscribingDictationSession(recorder, transcriber);
        var updates = new List<TranscriptUpdate>();
        session.TranscriptUpdated += updates.Add;

        await session.StartAsync(CancellationToken.None);
        recorder.PublishChunk(chunkOne);
        recorder.PublishChunk(chunkTwo, TimeSpan.FromSeconds(0.8));
        await WaitUntilAsync(() => updates.Count == 2);
        var result = await session.StopAsync(CancellationToken.None);

        Assert.AreEqual("hello brave new", result.Transcript.Text);
        CollectionAssert.AreEqual(
            new[] { "hello brave", "hello brave new" },
            updates.Select(update => update.StableText).ToArray());
        File.Delete(finalAudio);
    }

    [TestMethod]
    public async Task ChunkedSessionStartFailureUnsubscribesFromRecorderChunks()
    {
        var recorder = new FailingStartChunkedAudioRecorder();
        var session = new ChunkedTranscribingDictationSession(recorder, new FakeTranscriber("unused"));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => session.StartAsync(CancellationToken.None));

        Assert.AreEqual(0, recorder.ChunkSubscriptionCount);
    }

    [TestMethod]
    public async Task ChunkedSessionStopCancelsPartialWorkBeforeFinalTranscription()
    {
        var chunk = Path.Combine(Path.GetTempPath(), $"parakeet-chunk-{Guid.NewGuid():N}.wav");
        var finalAudio = Path.Combine(Path.GetTempPath(), $"parakeet-final-{Guid.NewGuid():N}.wav");
        await File.WriteAllTextAsync(chunk, "chunk");
        await File.WriteAllTextAsync(finalAudio, "final");
        var recorder = new FakeChunkedAudioRecorder(finalAudio);
        var transcriber = new BlockingChunkTranscriber(chunk, finalAudio, "final wins");
        var session = new ChunkedTranscribingDictationSession(recorder, transcriber);

        await session.StartAsync(CancellationToken.None);
        recorder.PublishChunk(chunk);
        await transcriber.ChunkStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var result = await session.StopAsync(CancellationToken.None);

        Assert.AreEqual("final wins", result.Transcript.Text);
        Assert.IsTrue(transcriber.ChunkCancellationObserved.Task.IsCompleted);
        Assert.IsFalse(File.Exists(chunk));
        File.Delete(finalAudio);
    }

    [TestMethod]
    public async Task ChunkedSessionStopFailureCancelsPartialWork()
    {
        var chunk = Path.Combine(Path.GetTempPath(), $"parakeet-chunk-{Guid.NewGuid():N}.wav");
        await File.WriteAllTextAsync(chunk, "chunk");
        var recorder = new FailingStopChunkedAudioRecorder();
        var transcriber = new BlockingChunkTranscriber(chunk, "unused-final.wav", "unused");
        var session = new ChunkedTranscribingDictationSession(recorder, transcriber);

        await session.StartAsync(CancellationToken.None);
        recorder.PublishChunk(chunk);
        await transcriber.ChunkStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => session.StopAsync(CancellationToken.None));

        Assert.IsTrue(transcriber.ChunkCancellationObserved.Task.IsCompleted);
        Assert.IsFalse(File.Exists(chunk));
    }

    [TestMethod]
    public async Task PreviewFailureDoesNotBlockPaste()
    {
        var paster = new FakeClipboardPaster();
        var controller = new DictationController(
            new FakeAudioRecorder("utterance.wav"),
            new FakeTranscriber("still paste this"),
            paster,
            new SessionHistory(),
            _ => throw new InvalidOperationException("preview failed"));

        await controller.HandleHotkeyDownAsync(CancellationToken.None);
        var outcome = await controller.HandleHotkeyUpAsync(CancellationToken.None);

        Assert.AreEqual(DictationOutcome.Pasted, outcome);
        Assert.AreEqual("Still paste this.", paster.PastedText);
    }

    [TestMethod]
    public async Task DuplicateKeydownDoesNotStartSecondRecording()
    {
        var recorder = new FakeAudioRecorder("utterance.wav");
        var controller = new DictationController(
            recorder,
            new FakeTranscriber("hello"),
            new FakeClipboardPaster(),
            new SessionHistory());

        var firstStarted = await controller.HandleHotkeyDownAsync(CancellationToken.None);
        var duplicateStarted = await controller.HandleHotkeyDownAsync(CancellationToken.None);
        var outcome = await controller.HandleHotkeyUpAsync(CancellationToken.None);

        Assert.IsTrue(firstStarted);
        Assert.IsFalse(duplicateStarted);
        Assert.AreEqual(DictationOutcome.Pasted, outcome);
        Assert.AreEqual(1, recorder.StartCount);
        Assert.AreEqual(1, recorder.StopCount);
    }

    [TestMethod]
    public async Task FailedRecordingStartDoesNotLeaveControllerRecording()
    {
        var failingSession = new FailingStartDictationSession();
        var succeedingSession = new FakeDictationSession("hello");
        var controller = new DictationController(
            new SequenceDictationSessionFactory(failingSession, succeedingSession),
            new FakeClipboardPaster(),
            new SessionHistory());

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => controller.HandleHotkeyDownAsync(CancellationToken.None));
        var startedAfterFailure = await controller.HandleHotkeyDownAsync(CancellationToken.None);

        Assert.IsTrue(startedAfterFailure);
        Assert.AreEqual(1, failingSession.StartCount);
        Assert.AreEqual(1, succeedingSession.StartCount);
    }

    [TestMethod]
    public async Task EmptyTranscriptDoesNotPasteOrEnterHistory()
    {
        var history = new SessionHistory();
        var paster = new FakeClipboardPaster();
        var controller = new DictationController(
            new FakeAudioRecorder("utterance.wav"),
            new FakeTranscriber("   "),
            paster,
            history);

        await controller.HandleHotkeyDownAsync(CancellationToken.None);
        var outcome = await controller.HandleHotkeyUpAsync(CancellationToken.None);

        Assert.AreEqual(DictationOutcome.EmptyTranscript, outcome);
        Assert.IsNull(paster.PastedText);
        Assert.AreEqual(0, history.Items.Count);
    }

    [TestMethod]
    public async Task TemporaryRecordingIsDeletedAfterTranscription()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"parakeet-ptt-{Guid.NewGuid():N}.wav");
        await File.WriteAllTextAsync(tempPath, "fake wav");
        var controller = new DictationController(
            new FakeAudioRecorder(tempPath, deleteAfterUse: true),
            new FakeTranscriber("hello"),
            new FakeClipboardPaster(),
            new SessionHistory());

        await controller.HandleHotkeyDownAsync(CancellationToken.None);
        var outcome = await controller.HandleHotkeyUpAsync(CancellationToken.None);

        Assert.AreEqual(DictationOutcome.Pasted, outcome);
        Assert.IsFalse(File.Exists(tempPath));
    }

    [TestMethod]
    public async Task TemporaryRecordingDeleteFailurePublishesCleanupWarning()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"parakeet-ptt-{Guid.NewGuid():N}.wav");
        await File.WriteAllTextAsync(tempPath, "fake wav");
        File.SetAttributes(tempPath, FileAttributes.ReadOnly);
        string? warningPath = null;
        var controller = new DictationController(
            new FakeAudioRecorder(tempPath, deleteAfterUse: true),
            new FakeTranscriber("hello"),
            new FakeClipboardPaster(),
            new SessionHistory(),
            cleanupWarningReady: path => warningPath = path);

        var outcome = await controller.HandleHotkeyUpAsync(CancellationToken.None);

        Assert.AreEqual(DictationOutcome.NotRecording, outcome);
        Assert.IsNull(warningPath);

        await controller.HandleHotkeyDownAsync(CancellationToken.None);
        outcome = await controller.HandleHotkeyUpAsync(CancellationToken.None);

        Assert.AreEqual(DictationOutcome.Pasted, outcome);
        Assert.AreEqual(tempPath, warningPath);
        File.SetAttributes(tempPath, FileAttributes.Normal);
        File.Delete(tempPath);
    }

    [TestMethod]
    public async Task ParakeetCliTranscriberConstructsCommandAndParsesJson()
    {
        var runner = new FakeProcessRunner("""{"text":"hello from cli","duration":1.25,"confidence":0.88,"words":[{"w":"hello","start":0.160,"end":0.480,"conf":0.9848},{"w":"from","start":0.560,"end":0.720,"conf":0.75},{"w":"cli","start":0.800,"end":1.040}]}""");
        var transcriber = new ParakeetCliTranscriber(
            new CliTranscriberOptions("C:\\tools\\parakeet-cli.exe", "C:\\models\\tdt_ctc-110m-f16.gguf"),
            runner);

        var result = await transcriber.TranscribeAsync("C:\\temp\\speech.wav", CancellationToken.None);

        Assert.AreEqual("hello from cli", result.Text);
        Assert.AreEqual(0.88, result.Confidence);
        Assert.AreEqual(3, result.Words.Count);
        Assert.AreEqual("hello", result.Words[0].Text);
        Assert.AreEqual(TimeSpan.FromSeconds(0.160), result.Words[0].Start);
        Assert.AreEqual(TimeSpan.FromSeconds(0.480), result.Words[0].End);
        Assert.AreEqual(0.9848, result.Words[0].Confidence);
        Assert.AreEqual("cli", result.Words[2].Text);
        Assert.IsNull(result.Words[2].Confidence);
        Assert.AreEqual("C:\\tools\\parakeet-cli.exe", runner.LastRequest?.FileName);
        Assert.AreEqual("C:\\tools", runner.LastRequest?.WorkingDirectory);
        CollectionAssert.AreEqual(
            new[] { "transcribe", "--model", "C:\\models\\tdt_ctc-110m-f16.gguf", "--input", "C:\\temp\\speech.wav", "--json" },
            runner.LastRequest?.Arguments.ToArray());
    }

    [TestMethod]
    public void ParakeetCliParserAcceptsAlternateWordFieldNamesAndSkipsInvalidWords()
    {
        var result = ParakeetCliTranscriber.Parse(
            """{"text":"hello world","words":[null,"skip",{"word":"hello","start":0.1,"end":0.2,"confidence":0.9},{"text":"world","start":0.3,"end":0.4,"conf":0.8},{"w":"skip me","start":0.5}]}""",
            TimeSpan.FromMilliseconds(12));

        Assert.AreEqual(2, result.Words.Count);
        Assert.AreEqual("hello", result.Words[0].Text);
        Assert.AreEqual(0.9, result.Words[0].Confidence);
        Assert.AreEqual("world", result.Words[1].Text);
        Assert.AreEqual(0.8, result.Words[1].Confidence);
    }

    [TestMethod]
    public void TranscriptResultStillSupportsOriginalDeconstructionShape()
    {
        var result = new TranscriptResult("hello", TimeSpan.FromMilliseconds(12), 0.7);

        var (text, inferenceTime, confidence) = result;

        Assert.AreEqual("hello", text);
        Assert.AreEqual(TimeSpan.FromMilliseconds(12), inferenceTime);
        Assert.AreEqual(0.7, confidence);
    }

    [TestMethod]
    public void RecordedAudioStillSupportsOriginalDeconstructionShape()
    {
        var audio = new RecordedAudio("utterance.wav", TimeSpan.FromSeconds(2), DeleteAfterUse: true);

        var (path, duration, deleteAfterUse) = audio;

        Assert.AreEqual("utterance.wav", path);
        Assert.AreEqual(TimeSpan.FromSeconds(2), duration);
        Assert.IsTrue(deleteAfterUse);
    }

    [TestMethod]
    public void ParakeetCliParserReturnsEmptyWordsForPlainText()
    {
        var result = ParakeetCliTranscriber.Parse("plain transcript", TimeSpan.FromMilliseconds(12));

        Assert.AreEqual("plain transcript", result.Text);
        Assert.AreEqual(0, result.Words.Count);
    }

    [TestMethod]
    public async Task ParakeetStreamingCliTranscriberConstructsStreamCommandAndParsesFinalTextWithTimestamps()
    {
        var output = """
            [stream] hello parakeet pushed to talk [EOU @ 2.56s]
            [stream:final] hello parakeet pushed to talk
            0.32-0.40  hello  (1.00)
            0.80-1.36  parakeet  (0.52)
            1.36-1.52  pushed  (0.83)
            1.60-1.68  to  (0.93)
            1.76-1.84  talk  (0.99)
            """;
        var runner = new FakeProcessRunner(output);
        var transcriber = new ParakeetStreamingCliTranscriber(
            new CliTranscriberOptions("C:\\tools\\parakeet-cli.exe", "C:\\models\\realtime_eou_120m-v1-q8_0.gguf"),
            runner);

        var result = await transcriber.TranscribeAsync("C:\\temp\\speech.wav", CancellationToken.None);

        Assert.AreEqual("hello parakeet pushed to talk", result.Text);
        Assert.AreEqual(5, result.Words.Count);
        Assert.AreEqual("hello", result.Words[0].Text);
        Assert.AreEqual(TimeSpan.FromSeconds(0.32), result.Words[0].Start);
        Assert.AreEqual(TimeSpan.FromSeconds(0.40), result.Words[0].End);
        Assert.AreEqual(1.00, result.Words[0].Confidence);
        Assert.AreEqual("talk", result.Words[4].Text);
        Assert.AreEqual("C:\\tools\\parakeet-cli.exe", runner.LastRequest?.FileName);
        CollectionAssert.AreEqual(
            new[] { "transcribe", "--model", "C:\\models\\realtime_eou_120m-v1-q8_0.gguf", "--input", "C:\\temp\\speech.wav", "--stream", "--timestamps" },
            runner.LastRequest?.Arguments.ToArray());
    }

    [TestMethod]
    public void ParakeetStreamingCliParserRejectsOutputWithoutFinalLine()
    {
        Assert.ThrowsExactly<InvalidOperationException>(
            () => ParakeetStreamingCliTranscriber.Parse("[stream] still unstable", TimeSpan.FromMilliseconds(12)));
    }

    [TestMethod]
    public void RuntimePathBuilderIncludesSiblingCudaDllDirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), $"parakeet-runtime-paths-{Guid.NewGuid():N}");
        var cliDir = Path.Combine(root, "parakeet-v0.4.0-bin-win-cuda-x64");
        var cudaDir = Path.Combine(root, "cudart-parakeet-bin-win-cuda-x64");
        Directory.CreateDirectory(cliDir);
        Directory.CreateDirectory(cudaDir);
        var cliPath = Path.Combine(cliDir, "parakeet-cli.exe");
        File.WriteAllText(cliPath, string.Empty);

        var paths = RuntimePathBuilder.GetRuntimeSearchPaths(cliPath);

        CollectionAssert.Contains(paths.ToArray(), cliDir);
        CollectionAssert.Contains(paths.ToArray(), cudaDir);
        Directory.Delete(root, recursive: true);
    }

    [TestMethod]
    public async Task ProcessRunnerPreservesCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var runner = new SystemProcessRunner();
        var request = new ProcessRequest(
            "cmd.exe",
            ["/c", "ping", "127.0.0.1", "-n", "6"],
            TimeSpan.FromSeconds(30));

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => runner.RunAsync(request, cancellation.Token));
    }

    [TestMethod]
    public async Task SettingsRoundtripKeepsSelectedModelAndDevicePreference()
    {
        var path = Path.Combine(Path.GetTempPath(), $"parakeet-settings-{Guid.NewGuid():N}.json");
        var store = new AppSettingsStore(path);
        var saved = AppSettings.Default with
        {
            SelectedModelId = "tdt-0.6b-v3-f16",
            TranscriptionMode = TranscriptionMode.Streaming,
            DevicePreference = DevicePreference.Cpu,
            NotificationsEnabled = false,
            AudibleStatusEnabled = false,
            TranscriptCorrections =
            [
                new TranscriptCorrection("kuda", "CUDA"),
                new TranscriptCorrection("c sharp", "C#")
            ]
        };

        await store.SaveAsync(saved, CancellationToken.None);
        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.AreEqual(saved.SelectedModelId, loaded.SelectedModelId);
        Assert.AreEqual(TranscriptionMode.Streaming, loaded.TranscriptionMode);
        Assert.AreEqual(DevicePreference.Cpu, loaded.DevicePreference);
        Assert.IsFalse(loaded.NotificationsEnabled);
        Assert.IsFalse(loaded.AudibleStatusEnabled);
        Assert.AreEqual(2, loaded.TranscriptCorrections.Count);
        Assert.AreEqual("kuda", loaded.TranscriptCorrections[0].HeardAs);
        Assert.AreEqual("CUDA", loaded.TranscriptCorrections[0].ReplaceWith);
        File.Delete(path);
    }

    [TestMethod]
    public void ModelRegistryExposesCuratedDefaultAndMultilingualModel()
    {
        var registry = ModelRegistry.CreateDefault();
        var multilingual = registry.Find("tdt-0.6b-v3-f16");

        Assert.AreEqual("tdt_ctc-110m-f16", registry.DefaultModel.Id);
        Assert.IsNotNull(multilingual);
        StringAssert.Contains(registry.DefaultModel.DownloadUrl.ToString(), "tdt_ctc-110m-f16.gguf");
        Assert.IsTrue(registry.DefaultModel.MinimumBytes > 0);
        Assert.AreEqual("7f9a6376edde6a74592ace48b2ebdc27a1ac972d0be9dfcc29e668d99381faf1", registry.DefaultModel.Sha256);
        Assert.AreEqual("8ba47343e1e919895aca90e099150a01ed203ee0942d8ed31e27295efc5abb22", multilingual?.Sha256);
    }

    [TestMethod]
    public void ModelRegistryExposesRealtimeStreamingModelsWithPinnedHashes()
    {
        var registry = ModelRegistry.CreateDefault();
        var q8 = registry.Find("realtime-eou-120m-v1-q8_0");
        var f16 = registry.Find("realtime-eou-120m-v1-f16");

        Assert.IsNotNull(q8);
        Assert.IsNotNull(f16);
        Assert.IsTrue(q8.SupportsStreaming);
        Assert.IsTrue(q8.SupportsBatch);
        Assert.AreEqual("q8_0", q8.Quantization);
        StringAssert.EndsWith(q8.DownloadUrl.ToString(), "realtime_eou_120m-v1-q8_0.gguf");
        Assert.AreEqual("62616b914d6f5a683a5dea672df055b57de5c49dddf871b8b44b9c814dc3d896", q8.Sha256);
        Assert.IsTrue(f16.SupportsStreaming);
        Assert.AreEqual("F16", f16.Quantization);
        Assert.AreEqual("d1a2b12f12b8a096a57499c9111ed13b442a2b786e17a292c168be45088f0edc", f16.Sha256);
    }

    [TestMethod]
    public void TranscriberSelectionUsesStreamingOnlyWhenModeAndModelAllowIt()
    {
        var registry = ModelRegistry.CreateDefault();
        var batch = registry.DefaultModel;
        var streaming = registry.Find("realtime-eou-120m-v1-q8_0")!;

        Assert.AreEqual(
            TranscriberKind.Batch,
            TranscriberSelection.Resolve(AppSettings.Default with { TranscriptionMode = TranscriptionMode.Auto }, batch));
        Assert.AreEqual(
            TranscriberKind.Streaming,
            TranscriberSelection.Resolve(AppSettings.Default with { TranscriptionMode = TranscriptionMode.Auto }, streaming));
        Assert.AreEqual(
            TranscriberKind.Batch,
            TranscriberSelection.Resolve(AppSettings.Default with { TranscriptionMode = TranscriptionMode.Batch }, streaming));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => TranscriberSelection.Resolve(AppSettings.Default with { TranscriptionMode = TranscriptionMode.Streaming }, batch));
    }

    [TestMethod]
    public async Task ChecksumVerifierRejectsMismatchedSha256()
    {
        var path = Path.Combine(Path.GetTempPath(), $"parakeet-checksum-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "hello");

        Assert.IsTrue(await ChecksumVerifier.VerifySha256Async(path, "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", CancellationToken.None));
        Assert.IsFalse(await ChecksumVerifier.VerifySha256Async(path, "0000000000000000000000000000000000000000000000000000000000000000", CancellationToken.None));
        File.Delete(path);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    [TestMethod]
    public void RuntimeRegistryPinsLatestWindowsCudaAndCpuAssets()
    {
        var registry = RuntimeAssetRegistry.CreateDefault();

        Assert.AreEqual("v0.4.0", registry.ReleaseTag);
        StringAssert.EndsWith(registry.Cuda.DownloadUrl.ToString(), "parakeet-v0.4.0-bin-win-cuda-x64.zip");
        Assert.AreEqual("2a377eeb7f92e0d0cd28df768750a8132296de0c07454ad908d5eaceeb9ad5e4", registry.Cuda.Sha256);
        Assert.AreEqual(1, registry.Cuda.AdditionalArchives.Count);
        StringAssert.EndsWith(registry.Cuda.AdditionalArchives[0].DownloadUrl.ToString(), "cudart-parakeet-bin-win-cuda-x64.zip");
        Assert.AreEqual("cc2b5fb99951720130e4a701e0978419d0a878e25c88bebc1416152616bd1d94", registry.Cuda.AdditionalArchives[0].Sha256);
        StringAssert.EndsWith(registry.Cpu.DownloadUrl.ToString(), "parakeet-v0.4.0-bin-win-cpu-x64.zip");
    }

    [TestMethod]
    public void DictationStatusCatalogProvidesVisibleTranscribingState()
    {
        var status = DictationStatusCatalog.Transcribing;

        Assert.AreEqual(DictationStatusKind.Transcribing, status.Kind);
        Assert.AreEqual("Transcribing", status.Title);
        Assert.AreEqual("Sending audio to local parakeet-cli.", status.Message);
        Assert.IsFalse(status.AutoHide);
    }

    [TestMethod]
    public void DictationStatusCatalogProvidesTranscriptPreviewText()
    {
        var status = DictationStatusCatalog.TranscriptPreview("Preview this.");

        Assert.AreEqual(DictationStatusKind.TranscriptPreview, status.Kind);
        Assert.AreEqual("Transcript", status.Title);
        Assert.AreEqual("Preview this.", status.Message);
        Assert.IsFalse(status.AutoHide);
    }

    [TestMethod]
    public async Task AssetManagerDownloadsVerifiesExtractsRuntimeAndFindsCli()
    {
        var root = Path.Combine(Path.GetTempPath(), $"parakeet-assets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var zipPath = Path.Combine(root, "runtime.zip");
        CreateRuntimeZip(zipPath);
        var sha = await ChecksumVerifier.ComputeSha256Async(zipPath, CancellationToken.None);
        var downloader = new FakeDownloader(await File.ReadAllBytesAsync(zipPath));
        var manager = new AssetManager(root, downloader);
        var runtime = new RuntimeAssetInfo(
            "test-runtime",
            new Uri("https://example.invalid/runtime.zip"),
            sha,
            "runtime.zip",
            DevicePreference.Cuda);

        var cliPath = await manager.EnsureRuntimeAsync(runtime, CancellationToken.None);

        Assert.IsTrue(File.Exists(cliPath));
        StringAssert.EndsWith(cliPath, "parakeet-cli.exe");
        Directory.Delete(root, recursive: true);
    }

    [TestMethod]
    public async Task AssetManagerRevalidatesExtractedRuntimeFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"parakeet-assets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var zipPath = Path.Combine(root, "runtime.zip");
        CreateRuntimeZip(zipPath);
        var sha = await ChecksumVerifier.ComputeSha256Async(zipPath, CancellationToken.None);
        var manager = new AssetManager(root, new FakeDownloader(await File.ReadAllBytesAsync(zipPath)));
        var runtime = new RuntimeAssetInfo(
            "test-runtime",
            new Uri("https://example.invalid/runtime.zip"),
            sha,
            "runtime.zip",
            DevicePreference.Cuda);
        var cliPath = await manager.EnsureRuntimeAsync(runtime, CancellationToken.None);
        await File.WriteAllTextAsync(cliPath, "tampered");

        var repairedCliPath = await manager.EnsureRuntimeAsync(runtime, CancellationToken.None);

        Assert.AreEqual(cliPath, repairedCliPath);
        Assert.AreEqual("fake exe", await File.ReadAllTextAsync(repairedCliPath));
        Directory.Delete(root, recursive: true);
    }

    [TestMethod]
    public async Task AssetManagerRejectsRuntimeArchiveEntriesOutsideRuntimeDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"parakeet-assets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var zipPath = Path.Combine(root, "runtime.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("../evil.txt");
            await using var stream = entry.Open();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync("escape");
        }

        var sha = await ChecksumVerifier.ComputeSha256Async(zipPath, CancellationToken.None);
        var manager = new AssetManager(root, new FakeDownloader(await File.ReadAllBytesAsync(zipPath)));
        var runtime = new RuntimeAssetInfo(
            "test-runtime",
            new Uri("https://example.invalid/runtime.zip"),
            sha,
            "runtime.zip",
            DevicePreference.Cuda);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => manager.EnsureRuntimeAsync(runtime, CancellationToken.None));
        Assert.IsFalse(File.Exists(Path.Combine(root, "evil.txt")));
        Directory.Delete(root, recursive: true);
    }

    private static void CreateRuntimeZip(string zipPath)
    {
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("bin/parakeet-cli.exe");
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream);
        writer.Write("fake exe");
    }
}

internal sealed class FakeAudioRecorder(string path, bool deleteAfterUse = false) : IAudioRecorder
{
    public int StartCount { get; private set; }
    public int StopCount { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        StartCount++;
        return Task.CompletedTask;
    }

    public Task<RecordedAudio> StopAsync(CancellationToken cancellationToken)
    {
        StopCount++;
        return Task.FromResult(new RecordedAudio(path, TimeSpan.FromSeconds(1), deleteAfterUse));
    }
}

internal sealed class FakeChunkedAudioRecorder(string finalPath) : IChunkedAudioRecorder
{
    public event Action<RecordedAudio>? AudioChunkReady;

    public int StartCount { get; private set; }

    public int StopCount { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        StartCount++;
        return Task.CompletedTask;
    }

    public Task<RecordedAudio> StopAsync(CancellationToken cancellationToken)
    {
        StopCount++;
        return Task.FromResult(new RecordedAudio(finalPath, TimeSpan.FromSeconds(4), DeleteAfterUse: true));
    }

    public void PublishChunk(string path, TimeSpan? overlap = null)
    {
        AudioChunkReady?.Invoke(new RecordedAudio(
            path,
            TimeSpan.FromSeconds(4),
            DeleteAfterUse: true,
            OverlapDuration: overlap ?? TimeSpan.Zero));
    }
}

internal sealed class FailingStartChunkedAudioRecorder : IChunkedAudioRecorder
{
    private Action<RecordedAudio>? _audioChunkReady;

    public event Action<RecordedAudio>? AudioChunkReady
    {
        add
        {
            _audioChunkReady += value;
            ChunkSubscriptionCount++;
        }
        remove
        {
            _audioChunkReady -= value;
            ChunkSubscriptionCount--;
        }
    }

    public int ChunkSubscriptionCount { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("start failed");
    }

    public Task<RecordedAudio> StopAsync(CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }
}

internal sealed class FailingStopChunkedAudioRecorder : IChunkedAudioRecorder
{
    public event Action<RecordedAudio>? AudioChunkReady;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task<RecordedAudio> StopAsync(CancellationToken cancellationToken)
    {
        throw new InvalidOperationException("stop failed");
    }

    public void PublishChunk(string path)
    {
        AudioChunkReady?.Invoke(new RecordedAudio(path, TimeSpan.FromSeconds(4), DeleteAfterUse: true));
    }
}

internal sealed class FakeDictationSessionFactory(FakeDictationSession session) : IDictationSessionFactory
{
    public IDictationSession CreateSession()
    {
        return session;
    }
}

internal sealed class FakeDictationSession(string transcript) : IDictationSession
{
    public event Action<TranscriptUpdate>? TranscriptUpdated;

    public int StartCount { get; private set; }

    public int StopCount { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        StartCount++;
        return Task.CompletedTask;
    }

    public Task<DictationSessionResult> StopAsync(CancellationToken cancellationToken)
    {
        StopCount++;
        return Task.FromResult(new DictationSessionResult(new TranscriptResult(transcript, null, null)));
    }

    public void PublishPartial(string text)
    {
        TranscriptUpdated?.Invoke(new TranscriptUpdate(TranscriptUpdateKind.Partial, text));
    }
}

internal sealed class SequenceDictationSessionFactory(params IDictationSession[] sessions) : IDictationSessionFactory
{
    private int _index;

    public IDictationSession CreateSession()
    {
        return sessions[_index++];
    }
}

internal sealed class FailingStartDictationSession : IDictationSession
{
    public event Action<TranscriptUpdate>? TranscriptUpdated
    {
        add { }
        remove { }
    }

    public int StartCount { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        StartCount++;
        throw new InvalidOperationException("start failed");
    }

    public Task<DictationSessionResult> StopAsync(CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }
}

internal sealed class FakeTranscriber(string transcript) : ITranscriber
{
    public string? LastAudioPath { get; private set; }

    public Task<TranscriptResult> TranscribeAsync(string wavPath, CancellationToken cancellationToken)
    {
        LastAudioPath = wavPath;
        return Task.FromResult(new TranscriptResult(transcript, TimeSpan.FromMilliseconds(500), null));
    }
}

internal sealed class FakeMappedTranscriber(IReadOnlyDictionary<string, string> transcripts) : ITranscriber
{
    public Task<TranscriptResult> TranscribeAsync(string wavPath, CancellationToken cancellationToken)
    {
        return Task.FromResult(new TranscriptResult(transcripts[wavPath], TimeSpan.FromMilliseconds(10), null));
    }
}

internal sealed class FakeResultTranscriber(IReadOnlyDictionary<string, TranscriptResult> transcripts) : ITranscriber
{
    public Task<TranscriptResult> TranscribeAsync(string wavPath, CancellationToken cancellationToken)
    {
        return Task.FromResult(transcripts[wavPath]);
    }
}

internal sealed class BlockingChunkTranscriber(
    string chunkPath,
    string finalPath,
    string finalTranscript) : ITranscriber
{
    public TaskCompletionSource ChunkStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource ChunkCancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<TranscriptResult> TranscribeAsync(string wavPath, CancellationToken cancellationToken)
    {
        if (wavPath == finalPath)
        {
            return new TranscriptResult(finalTranscript, TimeSpan.FromMilliseconds(10), null);
        }

        if (wavPath != chunkPath)
        {
            throw new InvalidOperationException("Unexpected path.");
        }

        ChunkStarted.SetResult();
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            ChunkCancellationObserved.SetResult();
            throw;
        }

        throw new InvalidOperationException("Chunk transcription should not complete.");
    }
}

internal sealed class FakeClipboardPaster(Action? beforePaste = null) : IClipboardPaster
{
    public string? PastedText { get; private set; }

    public Task PasteAsync(string text, CancellationToken cancellationToken)
    {
        beforePaste?.Invoke();
        PastedText = text;
        return Task.CompletedTask;
    }
}

internal sealed class FakeProcessRunner(string standardOutput) : IProcessRunner
{
    public ProcessRequest? LastRequest { get; private set; }

    public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
    {
        LastRequest = request;
        return Task.FromResult(new ProcessResult(0, standardOutput, string.Empty, TimeSpan.FromMilliseconds(10)));
    }
}

internal sealed class FakeDownloader(byte[] bytes) : IFileDownloader
{
    public Task DownloadAsync(Uri source, string destinationPath, CancellationToken cancellationToken)
    {
        File.WriteAllBytes(destinationPath, bytes);
        return Task.CompletedTask;
    }
}
