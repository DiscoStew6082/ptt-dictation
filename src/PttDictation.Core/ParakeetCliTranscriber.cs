using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace PttDictation.Core;

public sealed record CliTranscriberOptions(
    string CliPath,
    string ModelPath,
    TimeSpan? Timeout = null);

public sealed class ParakeetCliTranscriber(CliTranscriberOptions options, IProcessRunner processRunner) : ITranscriber
{
    public async Task<TranscriptResult> TranscribeAsync(string wavPath, CancellationToken cancellationToken)
    {
        var cliDirectory = Path.GetDirectoryName(options.CliPath);
        var request = new ProcessRequest(
            options.CliPath,
            ["transcribe", "--model", options.ModelPath, "--input", wavPath, "--json"],
            options.Timeout ?? TimeSpan.FromMinutes(2),
            cliDirectory,
            RuntimePathBuilder.GetRuntimeSearchPaths(options.CliPath));

        var result = await processRunner.RunAsync(request, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"parakeet-cli failed with exit code {result.ExitCode}: {result.StandardError}");
        }

        return Parse(result.StandardOutput, result.Elapsed);
    }

    public static TranscriptResult Parse(string output, TimeSpan elapsed)
    {
        var text = output.Trim();
        double? confidence = null;
        IReadOnlyList<TranscriptWord> words = [];

        if (text.StartsWith('{'))
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            text = ReadString(root, "text")
                ?? ReadString(root, "transcript")
                ?? ReadString(root, "transcription")
                ?? string.Empty;

            if (root.TryGetProperty("confidence", out var confidenceElement)
                && confidenceElement.ValueKind == JsonValueKind.Number
                && confidenceElement.TryGetDouble(out var parsedConfidence))
            {
                confidence = parsedConfidence;
            }

            words = ReadWords(root);
        }

        return new TranscriptResult(text, elapsed, confidence, words);
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static IReadOnlyList<TranscriptWord> ReadWords(JsonElement root)
    {
        if (!root.TryGetProperty("words", out var wordsElement) || wordsElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var words = new List<TranscriptWord>();
        foreach (var wordElement in wordsElement.EnumerateArray())
        {
            if (wordElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var text = ReadString(wordElement, "w")
                ?? ReadString(wordElement, "word")
                ?? ReadString(wordElement, "text");
            if (string.IsNullOrWhiteSpace(text)
                || !TryReadSeconds(wordElement, "start", out var start)
                || !TryReadSeconds(wordElement, "end", out var end))
            {
                continue;
            }

            double? confidence = null;
            if (TryReadSeconds(wordElement, "conf", out var parsedConfidence)
                || TryReadSeconds(wordElement, "confidence", out parsedConfidence))
            {
                confidence = parsedConfidence;
            }

            words.Add(new TranscriptWord(text, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), confidence));
        }

        return words;
    }

    private static bool TryReadSeconds(JsonElement element, string propertyName, out double seconds)
    {
        seconds = 0;
        return element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDouble(out seconds);
    }
}

public sealed partial class ParakeetStreamingCliTranscriber(CliTranscriberOptions options, IProcessRunner processRunner) : ITranscriber
{
    private static readonly TimeSpan EndOfUtteranceTail = TimeSpan.FromSeconds(1);

    public async Task<TranscriptResult> TranscribeAsync(string wavPath, CancellationToken cancellationToken)
    {
        var preparedInput = await StreamingWaveTailPadder.PrepareAsync(
            wavPath,
            EndOfUtteranceTail,
            cancellationToken);
        try
        {
            var cliDirectory = Path.GetDirectoryName(options.CliPath);
            var request = new ProcessRequest(
                options.CliPath,
                ["transcribe", "--model", options.ModelPath, "--input", preparedInput.Path, "--stream", "--timestamps"],
                options.Timeout ?? TimeSpan.FromMinutes(2),
                cliDirectory,
                RuntimePathBuilder.GetRuntimeSearchPaths(options.CliPath));

            var result = await processRunner.RunAsync(request, cancellationToken);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"parakeet-cli streaming failed with exit code {result.ExitCode}: {result.StandardError}");
            }

            return Parse(result.StandardOutput, result.Elapsed);
        }
        finally
        {
            preparedInput.Dispose();
        }
    }

    public static TranscriptResult Parse(string output, TimeSpan elapsed)
    {
        var finalText = ReadFinalText(output)
            ?? throw new InvalidOperationException("parakeet-cli streaming output did not include a final transcript.");
        return new TranscriptResult(finalText, elapsed, null, ReadTimestampWords(output));
    }

    private static string? ReadFinalText(string output)
    {
        foreach (var line in SplitLines(output).Reverse())
        {
            var match = FinalLineRegex().Match(line);
            if (match.Success)
            {
                return CleanSpecialTokens(match.Groups["text"].Value.Trim());
            }
        }

        return null;
    }

    private static IReadOnlyList<TranscriptWord> ReadTimestampWords(string output)
    {
        var words = new List<TranscriptWord>();
        foreach (var line in SplitLines(output))
        {
            var match = TimestampLineRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            if (!TryParseSeconds(match.Groups["start"].Value, out var start)
                || !TryParseSeconds(match.Groups["end"].Value, out var end))
            {
                continue;
            }

            double? confidence = TryParseSeconds(match.Groups["confidence"].Value, out var parsedConfidence)
                ? parsedConfidence
                : null;
            words.Add(new TranscriptWord(
                CleanSpecialTokens(match.Groups["word"].Value.Trim()),
                TimeSpan.FromSeconds(start),
                TimeSpan.FromSeconds(end),
                confidence));
        }

        return words;
    }

    private static bool TryParseSeconds(string value, out double seconds)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
    }

    private static string CleanSpecialTokens(string text)
    {
        return text.Replace("<EOU>", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
    }

    private static string[] SplitLines(string text)
    {
        return text.Split(["\r\n", "\n"], StringSplitOptions.None);
    }

    [GeneratedRegex(@"^\[stream:final\]\s*(?<text>.*)$")]
    private static partial Regex FinalLineRegex();

    [GeneratedRegex(@"^\s*(?<start>\d+(?:\.\d+)?)\-(?<end>\d+(?:\.\d+)?)\s+(?<word>.+?)\s+\((?<confidence>\d+(?:\.\d+)?)\)\s*$")]
    private static partial Regex TimestampLineRegex();
}

internal sealed class StreamingWaveTailPadder : IDisposable
{
    private StreamingWaveTailPadder(string path, bool deleteAfterUse)
    {
        Path = path;
        _deleteAfterUse = deleteAfterUse;
    }

    private readonly bool _deleteAfterUse;

    public string Path { get; }

    public static async Task<StreamingWaveTailPadder> PrepareAsync(
        string wavPath,
        TimeSpan tailDuration,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(wavPath) || tailDuration <= TimeSpan.Zero)
        {
            return new StreamingWaveTailPadder(wavPath, deleteAfterUse: false);
        }

        var source = await File.ReadAllBytesAsync(wavPath, cancellationToken);
        if (!TryDescribePcmWave(source, out var dataSizeOffset, out var dataOffset, out var dataSize, out var bytesPerSecond, out var blockAlign))
        {
            return new StreamingWaveTailPadder(wavPath, deleteAfterUse: false);
        }

        var unalignedTailBytes = checked((int)Math.Ceiling(bytesPerSecond * tailDuration.TotalSeconds));
        var tailBytes = checked((unalignedTailBytes + blockAlign - 1) / blockAlign * blockAlign);
        var dataEnd = checked(dataOffset + dataSize);
        var padded = new byte[checked(source.Length + tailBytes)];
        Buffer.BlockCopy(source, 0, padded, 0, dataEnd);
        Buffer.BlockCopy(source, dataEnd, padded, dataEnd + tailBytes, source.Length - dataEnd);
        BinaryPrimitives.WriteUInt32LittleEndian(padded.AsSpan(dataSizeOffset, sizeof(uint)), checked((uint)(dataSize + tailBytes)));
        BinaryPrimitives.WriteUInt32LittleEndian(padded.AsSpan(4, sizeof(uint)), checked((uint)(padded.Length - 8)));

        var directory = System.IO.Path.GetDirectoryName(wavPath) ?? System.IO.Path.GetTempPath();
        var preparedPath = System.IO.Path.Combine(
            directory,
            $"{System.IO.Path.GetFileNameWithoutExtension(wavPath)}-stream-tail-{Guid.NewGuid():N}.wav");
        try
        {
            await File.WriteAllBytesAsync(preparedPath, padded, cancellationToken);
            return new StreamingWaveTailPadder(preparedPath, deleteAfterUse: true);
        }
        catch
        {
            TryDelete(preparedPath);
            throw;
        }
    }

    public void Dispose()
    {
        if (_deleteAfterUse)
        {
            TryDelete(Path);
        }
    }

    private static bool TryDescribePcmWave(
        byte[] source,
        out int dataSizeOffset,
        out int dataOffset,
        out int dataSize,
        out int bytesPerSecond,
        out int blockAlign)
    {
        dataSizeOffset = 0;
        dataOffset = 0;
        dataSize = 0;
        bytesPerSecond = 0;
        blockAlign = 0;
        if (source.Length < 44
            || !source.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            || !source.AsSpan(8, 4).SequenceEqual("WAVE"u8))
        {
            return false;
        }

        var offset = 12;
        while (offset <= source.Length - 8)
        {
            var chunkSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(offset + 4, sizeof(uint))));
            var chunkDataOffset = offset + 8;
            if (chunkSize < 0 || chunkDataOffset > source.Length - chunkSize)
            {
                return false;
            }

            var chunkId = source.AsSpan(offset, 4);
            if (chunkId.SequenceEqual("fmt "u8) && chunkSize >= 16)
            {
                var format = BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(chunkDataOffset, sizeof(ushort)));
                if (format != 1)
                {
                    return false;
                }

                bytesPerSecond = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(chunkDataOffset + 8, sizeof(uint))));
                blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(chunkDataOffset + 12, sizeof(ushort)));
            }
            else if (chunkId.SequenceEqual("data"u8))
            {
                dataSizeOffset = offset + 4;
                dataOffset = chunkDataOffset;
                dataSize = chunkSize;
            }

            if (bytesPerSecond > 0 && blockAlign > 0 && dataOffset > 0)
            {
                return true;
            }

            offset = checked(chunkDataOffset + chunkSize + (chunkSize & 1));
        }

        return false;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken);
}

public sealed record ProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout,
    string? WorkingDirectory = null,
    IReadOnlyList<string>? PathDirectories = null);

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, TimeSpan Elapsed);

public sealed class SystemProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(request.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var stopwatch = Stopwatch.StartNew();
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            ErrorDialog = false
        };

        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            process.StartInfo.WorkingDirectory = request.WorkingDirectory;
        }

        ApplyPathDirectories(process.StartInfo, request.PathDirectories);

        foreach (var argument in request.Arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(linked.Token);
            var stderr = process.StandardError.ReadToEndAsync(linked.Token);
            await process.WaitForExitAsync(linked.Token);
            stopwatch.Stop();
            return new ProcessResult(process.ExitCode, await stdout, await stderr, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (!process.HasExited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException($"parakeet-cli did not finish within {request.Timeout}.");
        }
    }

    private static void ApplyPathDirectories(ProcessStartInfo startInfo, IReadOnlyList<string>? pathDirectories)
    {
        if (pathDirectories is null || pathDirectories.Count == 0)
        {
            return;
        }

        var existingPath = startInfo.Environment.TryGetValue("PATH", out var path)
            ? path
            : Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        startInfo.Environment["PATH"] = string.Join(Path.PathSeparator, pathDirectories.Concat([existingPath]));
    }
}

public static class RuntimePathBuilder
{
    public static IReadOnlyList<string> GetRuntimeSearchPaths(string executablePath)
    {
        var executableDirectory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(executableDirectory))
        {
            return [];
        }

        var searchPaths = new List<string> { executableDirectory };
        var runtimeRoot = Directory.GetParent(executableDirectory)?.FullName;
        var driveRoot = runtimeRoot is null ? null : Path.GetPathRoot(runtimeRoot);
        if (runtimeRoot is not null
            && Directory.Exists(runtimeRoot)
            && !string.Equals(driveRoot, runtimeRoot, StringComparison.OrdinalIgnoreCase))
        {
            searchPaths.AddRange(Directory.EnumerateDirectories(runtimeRoot, "cudart-*", SearchOption.TopDirectoryOnly));
        }

        return searchPaths
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
