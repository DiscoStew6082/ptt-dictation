using System.Diagnostics;
using System.Text.Json;
using NAudio.Wave;
using PttDictation.App;
using PttDictation.Core;

// Real recognizer/pipeline verification. No microphone, UI, clipboard or settings writes.
internal static class QwenIntegrationProbe
{
    public static async Task<int> RunAsync(string configurationPath, string outputDirectory)
    {
        var config = JsonSerializer.Deserialize<Configuration>(File.ReadAllText(configurationPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Missing Qwen probe configuration.");
        var output = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(output);
        var report = new List<object>();
        using var trace = DiagnosticTrace.Configure(output, "qwen-production-pipeline-probe");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        using var preview = new PersistentParakeetServerTranscriber(
            new(config.ParakeetRuntimePath, config.ParakeetModelPath, TimeSpan.FromMinutes(2)),
            Path.Combine(Path.GetDirectoryName(config.ParakeetRuntimePath)!, "parakeet-server.exe"));
        var qwenOptions = new QwenTranscriberOptions(config.PythonPath, config.QwenModelPath, config.WorkerPath);
        using var final = new ConfiguredFinalTranscriber(preview,
            () => AppSettings.Default with { FinalTranscriptionEngine = FinalTranscriptionEngine.Qwen },
            () => qwenOptions, options => new PersistentQwenTranscriber(options));
        void Record(object row)
        {
            report.Add(row);
            File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(report,
                new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(row));
        }
        try
        {
            var clock = Stopwatch.StartNew();
            await final.WarmUpAsync(deadline.Token);
            Record(new { check = "real_qwen_warmup", seconds = clock.Elapsed.TotalSeconds });
            foreach (var sample in config.Samples)
            {
                using var reader = new WaveFileReader(sample.Path);
                var recorder = new SavedRecorder(sample.Path, reader.TotalTime);
                var session = new ChunkedTranscribingDictationSession(recorder, preview, final.CreateSessionTranscriber());
                var previewReady = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                session.TranscriptUpdated += update => previewReady.TrySetResult(update.StableText + update.UnstableText);
                await session.StartAsync(deadline.Token);
                recorder.Publish();
                var previewText = await previewReady.Task.WaitAsync(TimeSpan.FromSeconds(90), deadline.Token);
                clock.Restart();
                var result = await session.StopAsync(deadline.Token);
                var passes = sample.ExpectedFragment is null || result.Transcript.Text.Contains(sample.ExpectedFragment,
                    StringComparison.OrdinalIgnoreCase);
                Record(new { check = "real_preview_then_qwen_final", sample.Id, preview = previewText,
                    final = result.Transcript.Text, secondsAfterStop = clock.Elapsed.TotalSeconds,
                    expectedFragmentPresent = passes });
                if (!passes) throw new InvalidOperationException("Expected speech fragment missing for " + sample.Id);
            }
            foreach (var peak in new short[] { 0, 1 })
            {
                var path = Path.Combine(output, $"silence-{peak}.wav");
                using (var writer = new WaveFileWriter(path, new WaveFormat(16000, 16, 1)))
                {
                    var data = new byte[16000 * 2 * 3];
                    if (peak != 0)
                        for (var offset = 0; offset < data.Length; offset += 2)
                            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(offset, 2),
                                (short)(offset % 4 == 0 ? peak : -peak));
                    writer.Write(data, 0, data.Length);
                }
                var result = await final.TranscribeAsync(path, deadline.Token);
                Record(new { check = "near_silence", peak, text = result.Text });
                if (result.Text.Length != 0) throw new InvalidOperationException("Silence produced words.");
            }
            using (var cancel = new CancellationTokenSource())
            {
                var request = final.TranscribeAsync(config.Samples.Last().Path, cancel.Token);
                await Task.Delay(250, deadline.Token);
                clock.Restart();
                cancel.Cancel();
                try
                {
                    await request;
                    throw new InvalidOperationException("Real inference completed before the cancellation probe.");
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested)
                {
                    Record(new { check = "cancel_real_inference", seconds = clock.Elapsed.TotalSeconds });
                }
            }
            clock.Restart();
            var retry = await final.TranscribeAsync(config.Samples.First().Path, deadline.Token);
            Record(new { check = "fresh_worker_after_cancellation", seconds = clock.Elapsed.TotalSeconds, text = retry.Text });
            if (string.IsNullOrWhiteSpace(retry.Text)) throw new InvalidOperationException("Recovery returned no speech.");

            var invalid = Path.Combine(output, "invalid.wav");
            File.WriteAllText(invalid, "invalid wav");
            try
            {
                await final.TranscribeAsync(invalid, deadline.Token);
                throw new InvalidDataException("Invalid WAV accepted.");
            }
            catch (InvalidOperationException)
            {
                Record(new { check = "worker_error_propagated", passed = true });
            }
            var recovered = await final.TranscribeAsync(config.Samples.First().Path, deadline.Token);
            if (string.IsNullOrWhiteSpace(recovered.Text)) throw new InvalidOperationException("Recovery after error returned no text.");
            Record(new { check = "fresh_worker_after_error", text = recovered.Text });
            final.Dispose();
            preview.Dispose();
            await DiagnosticTrace.FlushAsync();
            Record(new { check = "complete", passed = true });
            return 0;
        }
        catch (Exception error)
        {
            Record(new { check = "failed", error = error.ToString() });
            return 1;
        }
    }

    private sealed record Configuration(string PythonPath, string QwenModelPath, string WorkerPath,
        string ParakeetRuntimePath, string ParakeetModelPath, Sample[] Samples);
    private sealed record Sample(string Id, string Path, string? ExpectedFragment);
    private sealed class SavedRecorder(string path, TimeSpan duration) : IChunkedAudioRecorder
    {
        public event Action<RecordedAudio>? AudioChunkReady;
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public void Publish() => AudioChunkReady?.Invoke(new(path, duration, IsCumulative: true));
        public Task<RecordedAudio> StopAsync(CancellationToken token) => Task.FromResult(new RecordedAudio(path, duration));
    }
}
