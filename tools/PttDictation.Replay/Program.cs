using System.Diagnostics;
using System.Text.Json;
using NAudio.Wave;
using PttDictation.App;
using PttDictation.Core;

if (args.Length == 3 && args[0] == "--qwen-probe")
    return await QwenIntegrationProbe.RunAsync(args[1], args[2]);

if (args.Length == 2 && args[0] == "--notepad-probe")
    return NativeInsertionProbe.Run(args[1]);
if (args.Length == 3 && args[0] == "--saved-notepad-recovery-probe")
    return NativeInsertionProbe.Run(args[2], savedTrace: args[1], injectClipboardReadFailure: true);
if (args.Length == 2 && args[0] == "--placeholder-probe")
    return NativeInsertionProbe.Run(args[1], "editor");
if (args.Length == 3 && args[0] == "--saved-insertion-probe")
    return NativeInsertionProbe.Run(args[2], "editor", args[1]);
if (args.Length == 5 && args[0] == "--context-probe")
    return await RecognitionContextProbe.RunAsync(args[1], args[2], args[3], args[4]);

// This harness drives the experimental production workflow. It never opens the microphone,
// creates a Windows text target, accesses the clipboard, or saves app settings.
if (args.Length != 2 && args.Length != 4)
{
    Console.Error.WriteLine("Usage: PttDictation.Replay --fixture <output-directory> | <audio.wav> <output-directory> <runtime.exe> <model.gguf>");
    return 2;
}
var fixture = args[0] == "--fixture";
if (fixture != (args.Length == 2))
    return 2;
var output = Path.GetFullPath(args[1]);
Directory.CreateDirectory(output);
using var diagnostics = DiagnosticTrace.Configure(output,
    "experimental-replay-" + typeof(DictationWorkflow).Assembly.ManifestModule.ModuleVersionId);
var scratch = Path.Combine(output, "temporary-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
ITranscriber? transcriber = null;
DictationWorkflow? workflow = null;
try
{
    var audio = Path.Combine(scratch, "input.wav");
    if (fixture)
    {
        using var writer = new WaveFileWriter(audio, new WaveFormat(16000, 16, 1));
        writer.Write(new byte[32000 * 4], 0, 32000 * 4);
    }
    else
    {
        // A transient copy permits retention to prune its own files while replay is running.
        File.Copy(Path.GetFullPath(args[0]), audio);
    }
    var settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PttDictation", "settings.json");
    var settings = fixture ? AppSettings.Default : new AppSettingsStore(settingsPath).Load();
    if (fixture)
        transcriber = new FixtureTranscriber(audio);
    else
    {
        var runtime = Path.GetFullPath(args[2]);
        var model = Path.GetFullPath(args[3]);
        if (!File.Exists(runtime) || !File.Exists(model))
            throw new FileNotFoundException("Replay requires existing runtime and model files; it does not download assets.");
        var options = new CliTranscriberOptions(runtime, model, TimeSpan.FromMinutes(5));
        var server = Path.Combine(Path.GetDirectoryName(runtime)!, "parakeet-server.exe");
        var registry = ModelRegistry.CreateDefault();
        var selectedModel = registry.Find(settings.SelectedModelId) ?? registry.DefaultModel;
        transcriber = File.Exists(server)
            ? new PersistentParakeetServerTranscriber(options, server)
            : TranscriberSelection.Resolve(settings, selectedModel) == TranscriberKind.Streaming
                ? new ParakeetStreamingCliTranscriber(options, new SystemProcessRunner())
                : new ParakeetCliTranscriber(options, new SystemProcessRunner());
        DiagnosticTrace.Write("replay.runtime", new { runtime, model, implementation = transcriber.GetType().Name });
    }
    var recorder = new ReplayRecorder(audio, scratch);
    var target = new ReplayTextOutput();
    var history = new SessionHistory();
    workflow = new DictationWorkflow(
        new ChunkedTranscribingDictationSessionFactory(recorder, transcriber), target, history,
        () => settings.TranscriptCorrections);
    await workflow.HandleAsync(DictationIntent.BeginHold, cancellation.Token);
    DiagnosticTrace.Write("replay.input", new { source = fixture ? "synthetic-silence-with-fixture-recognizer" : Path.GetFullPath(args[0]),
        nativeTextTarget = false, productionChunkTiming = true, automaticDeviceRetry = false, recorder.Duration });
    await recorder.EmitAsync(cancellation.Token);
    await workflow.HandleAsync(DictationIntent.EndHold, cancellation.Token);
    var state = workflow.CurrentState;
    DiagnosticTrace.Write("replay.completed", new { phase = state.Phase.ToString(), state.ErrorMessage, previewCount = target.Previews.Count });
    await File.WriteAllTextAsync(Path.Combine(output, "comparison.json"), JsonSerializer.Serialize(new
    {
        phase = state.Phase.ToString(), state.ErrorMessage, previews = target.Previews,
        finalText = target.FinalText, history = history.Entries,
        limitation = "Audio/chunk/correction workflow replay only. No microphone, native textbox, clipboard, or hotkey interaction."
    }, new JsonSerializerOptions { WriteIndented = true }));
    if (fixture && (target.Previews.Count == 0 || target.FinalText != "Fixture final transcription."))
        throw new InvalidOperationException("Fixture failed to exercise preview and final output.");
    Console.WriteLine(JsonSerializer.Serialize(new { phase = state.Phase.ToString(), previewCount = target.Previews.Count, output }));
    return state.Phase is DictationWorkflowPhase.Pasted or DictationWorkflowPhase.Empty ? 0 : 1;
}
catch (Exception error)
{
    DiagnosticTrace.Write("replay.failed", error: error);
    Console.Error.WriteLine(error.Message);
    return 1;
}
finally
{
    try
    {
        // Stop/cancel production work before disposing its runtime or deleting its input.
        if (workflow is not null)
            await workflow.HandleAsync(DictationIntent.Cancel, CancellationToken.None);
    }
    catch (Exception error) { DiagnosticTrace.Write("replay.cancel_failed", error: error); }
    try { (transcriber as IDisposable)?.Dispose(); }
    catch (Exception error) { DiagnosticTrace.Write("replay.dispose_failed", error: error); }
    try
    {
        // Only files created in this run's unique temporary directory are removed, without recursion.
        foreach (var file in Directory.EnumerateFiles(scratch)) File.Delete(file);
        Directory.Delete(scratch, recursive: false);
    }
    catch (Exception error) { DiagnosticTrace.Write("replay.cleanup_failed", error: error); }
    finally { await DiagnosticTrace.FlushAsync(); }
}

sealed class ReplayTextOutput : ILiveClipboardPaster
{
    public event Action<Exception>? InsertionFailed { add { } remove { } }
    public List<string> Previews { get; } = [];
    public string? FinalText { get; private set; }
    public void CaptureTarget() => DiagnosticTrace.Write("replay.output_captured");
    public void UpdatePreview(string text) { Previews.Add(text); DiagnosticTrace.Write("replay.preview_output", new { text }); }
    public Task PasteAsync(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FinalText = text;
        DiagnosticTrace.Write("replay.final_output", new { text });
        return Task.CompletedTask;
    }
    public void EndSession() { }
}

sealed class FixtureTranscriber(string finalPath) : ITranscriber
{
    private int chunks;
    public Task<TranscriptResult> TranscribeAsync(string wavPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var text = wavPath == finalPath ? "Fixture final transcription." : "Fixture preview " + Interlocked.Increment(ref chunks);
        return Task.FromResult(new TranscriptResult(text, null, null));
    }
}

sealed class ReplayRecorder : IChunkedAudioRecorder
{
    private readonly string audio;
    private readonly string scratch;
    private readonly byte[] pcm;
    public TimeSpan Duration { get; }
    public event Action<RecordedAudio>? AudioChunkReady;

    public ReplayRecorder(string audio, string scratch)
    {
        this.audio = audio;
        this.scratch = scratch;
        using var reader = new WaveFileReader(audio);
        if (reader.WaveFormat.Encoding != WaveFormatEncoding.Pcm || reader.WaveFormat.SampleRate != 16000
            || reader.WaveFormat.BitsPerSample != 16 || reader.WaveFormat.Channels != 1)
            throw new InvalidDataException("Replay expects the app's 16 kHz mono 16-bit PCM WAV format.");
        using var bytes = new MemoryStream();
        reader.CopyTo(bytes);
        pcm = bytes.ToArray();
        Duration = TimeSpan.FromSeconds(pcm.Length / 32000d);
    }
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<RecordedAudio> StopAsync(CancellationToken cancellationToken) => Task.FromResult(new RecordedAudio(audio, Duration));
    public async Task EmitAsync(CancellationToken cancellationToken)
    {
        const int bytesPerSecond = 32000;
        var chunkBytes = (int)(bytesPerSecond * WasapiAudioRecorder.ChunkDurationForTest.TotalSeconds);
        var overlapBytes = (int)(bytesPerSecond * WasapiAudioRecorder.ChunkOverlapForTest.TotalSeconds);
        using var buffer = new PcmChunkBuffer(bytesPerSecond, chunkBytes, overlapBytes, cumulative: true);
        buffer.Append(pcm);
        var stopwatch = Stopwatch.StartNew();
        var sequence = 0;
        while (buffer.TryCreateChunk(Path.Combine(scratch, $"chunk-{sequence:0000}.wav")) is { } chunk)
        {
            var due = TimeSpan.FromSeconds((chunkBytes + sequence * (chunkBytes - overlapBytes)) / (double)bytesPerSecond);
            if (due > stopwatch.Elapsed) await Task.Delay(due - stopwatch.Elapsed, cancellationToken);
            using (var writer = new WaveFileWriter(chunk.Path, new WaveFormat(16000, 16, 1)))
                writer.Write(chunk.Pcm, 0, chunk.Pcm.Length);
            AudioChunkReady?.Invoke(new RecordedAudio(chunk.Path, chunk.Duration, true, chunk.OverlapDuration, chunk.IsCumulative));
            sequence++;
        }
        if (Duration > stopwatch.Elapsed) await Task.Delay(Duration - stopwatch.Elapsed, cancellationToken);
    }
}
