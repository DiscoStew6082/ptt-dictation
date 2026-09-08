using System.Text.Json;
using NAudio.Wave;
using PttDictation.App;
using PttDictation.Core;

internal static class RecognitionContextProbe
{
    public static async Task<int> RunAsync(string source, string output, string runtime, string model)
    {
        Directory.CreateDirectory(output);
        using var reader = new WaveFileReader(source);
        using var memory = new MemoryStream();
        reader.CopyTo(memory);
        var pcm = memory.ToArray();
        var format = reader.WaveFormat;
        var options = new CliTranscriberOptions(runtime, model, TimeSpan.FromMinutes(2));
        using var transcriber = new PersistentParakeetServerTranscriber(options,
            Path.Combine(Path.GetDirectoryName(runtime)!, "parakeet-server.exe"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var results = new List<object>();
        foreach (var end in new[] { 9.2, 10.0, 10.8, 11.6, 12.4, 13.2 })
        foreach (var window in new[] { 2.0, 4.0, 6.0, end })
        {
            var start = Math.Max(0, end - window);
            var startByte = (int)(start * format.AverageBytesPerSecond) / format.BlockAlign * format.BlockAlign;
            var endByte = Math.Min(pcm.Length, (int)(end * format.AverageBytesPerSecond) / format.BlockAlign * format.BlockAlign);
            var path = Path.Combine(output, $"window-{end:0.0}-{window:0.0}.wav");
            using (var writer = new WaveFileWriter(path, format)) writer.Write(pcm, startByte, endByte - startByte);
            try
            {
                var transcript = await transcriber.TranscribeAsync(path, timeout.Token);
                results.Add(new { start, end, window, transcript.Text, transcript.Words, transcript.InferenceTime });
            }
            finally { File.Delete(path); }
        }
        await File.WriteAllTextAsync(Path.Combine(output, "context-results.json"),
            JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Saved {results.Count} context comparisons to {output}");
        return 0;
    }
}
