using System.Diagnostics;
using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using PttDictation.Core;

namespace PttDictation.App;

internal sealed class PersistentParakeetServerTranscriber : ITranscriber, IWarmableTranscriber, IDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);
    private readonly CliTranscriberOptions _options;
    private readonly string _serverPath;
    private readonly SemaphoreSlim _startupLock = new(1, 1);
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly Func<int, Process> _serverProcessFactory;
    private readonly Func<int> _portProvider;
    private readonly HttpClient _httpClient;
    private Process? _serverProcess;
    private Uri? _endpoint;
    private Task<string>? _standardOutput;
    private Task<string>? _standardError;
    private bool _disposed;

    public PersistentParakeetServerTranscriber(CliTranscriberOptions options, string serverPath)
    {
        _options = options;
        _serverPath = serverPath;
        _serverProcessFactory = CreateServerProcess;
        _portProvider = ReserveAvailablePort;
        _httpClient = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = ConnectToExpectedServerAsync
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    internal PersistentParakeetServerTranscriber(
        CliTranscriberOptions options,
        string serverPath,
        Func<int, Process> serverProcessFactory,
        Func<int> portProvider)
        : this(options, serverPath)
    {
        _serverProcessFactory = serverProcessFactory;
        _portProvider = portProvider;
    }

    public async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        _ = await EnsureServerAsync(cancellationToken);
    }

    public async Task<TranscriptResult> TranscribeAsync(string wavPath, CancellationToken cancellationToken)
    {
        var endpoint = await EnsureServerAsync(cancellationToken);
        await _requestLock.WaitAsync(cancellationToken);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            var wav = await File.ReadAllBytesAsync(wavPath, cancellationToken);
            var result = await TranscribeAllUtterancesAsync(
                wav,
                (segment, token) => SendSegmentAsync(endpoint, Path.GetFileName(wavPath), segment, token),
                cancellationToken);
            stopwatch.Stop();
            return result with { InferenceTime = stopwatch.Elapsed };
        }
        finally
        {
            _requestLock.Release();
        }
    }

    internal static TranscriptResult ParseResponse(string json, TimeSpan elapsed)
    {
        return ParseSegmentResponse(json, elapsed).Transcript;
    }

    internal static async Task<TranscriptResult> TranscribeAllUtterancesAsync(
        byte[] wav,
        Func<byte[], CancellationToken, Task<ServerTranscriptSegment>> transcribeSegment,
        CancellationToken cancellationToken)
    {
        var audio = PcmWaveAudio.Parse(wav);
        var text = new List<string>();
        var words = new List<TranscriptWord>();
        var offset = TimeSpan.Zero;
        for (var iteration = 0; iteration < 64; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segmentWav = audio.CreateSegment(offset);
            var segment = await transcribeSegment(segmentWav, cancellationToken);
            if (!string.IsNullOrWhiteSpace(segment.Transcript.Text))
            {
                text.Add(segment.Transcript.Text.Trim());
            }

            words.AddRange(segment.Transcript.Words
                .Where(word => !string.IsNullOrWhiteSpace(word.Text))
                .Select(word => word with
                {
                    Start = word.Start + offset,
                    End = word.End + offset
                }));

            if (segment.EndOfUtterance is not { } endOfUtterance
                || endOfUtterance <= TimeSpan.FromMilliseconds(80))
            {
                return new TranscriptResult(string.Join(" ", text), null, null, words);
            }

            var nextOffset = offset + endOfUtterance;
            if (nextOffset >= audio.Duration - TimeSpan.FromMilliseconds(80))
            {
                return new TranscriptResult(string.Join(" ", text), null, null, words);
            }

            offset = nextOffset;
        }

        throw new InvalidOperationException("Parakeet returned too many end-of-utterance segments for one recording.");
    }

    private async Task<ServerTranscriptSegment> SendSegmentAsync(
        Uri endpoint,
        string fileName,
        byte[] wav,
        CancellationToken cancellationToken)
    {
        using var audioContent = new ByteArrayContent(wav);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        using var form = new MultipartFormDataContent();
        form.Add(audioContent, "file", fileName);
        form.Add(new StringContent("verbose_json"), "response_format");
        form.Add(new StringContent("word"), "timestamp_granularities[]");

        using var response = await _httpClient.PostAsync(endpoint, form, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Parakeet local server failed with HTTP {(int)response.StatusCode}: {body}");
        }

        return ParseSegmentResponse(body, null);
    }

    private static ServerTranscriptSegment ParseSegmentResponse(string json, TimeSpan? elapsed)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("text", out var textProperty))
        {
            throw new InvalidOperationException("Parakeet local server response did not include transcript text.");
        }

        var text = CleanSpecialTokens(textProperty.GetString() ?? string.Empty);
        var words = new List<TranscriptWord>();
        TimeSpan? endOfUtterance = null;
        if (root.TryGetProperty("words", out var wordsProperty)
            && wordsProperty.ValueKind == JsonValueKind.Array)
        {
            foreach (var word in wordsProperty.EnumerateArray())
            {
                if (!word.TryGetProperty("word", out var value)
                    || !word.TryGetProperty("start", out var start)
                    || !word.TryGetProperty("end", out var end)
                    || !start.TryGetDouble(out var startSeconds)
                    || !end.TryGetDouble(out var endSeconds))
                {
                    continue;
                }

                double? confidence = word.TryGetProperty("conf", out var confidenceProperty)
                    && confidenceProperty.TryGetDouble(out var parsedConfidence)
                        ? parsedConfidence
                        : null;
                var rawWord = value.GetString() ?? string.Empty;
                if (rawWord.Contains("<EOU>", StringComparison.OrdinalIgnoreCase))
                {
                    endOfUtterance = TimeSpan.FromSeconds(endSeconds);
                }

                var cleanWord = CleanSpecialTokens(rawWord);
                if (cleanWord.Length == 0)
                {
                    continue;
                }

                words.Add(new TranscriptWord(
                    cleanWord,
                    TimeSpan.FromSeconds(startSeconds),
                    TimeSpan.FromSeconds(endSeconds),
                    confidence));
            }
        }

        return new ServerTranscriptSegment(
            new TranscriptResult(text, elapsed, null, words),
            endOfUtterance);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopServer();
        _httpClient.Dispose();
    }

    private async Task<Uri> EnsureServerAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (ServerIsRunning())
        {
            return _endpoint!;
        }

        await _startupLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (ServerIsRunning())
            {
                return _endpoint!;
            }

            StopServer();
            var port = _portProvider();
            var process = _serverProcessFactory(port);
            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("Windows could not start the Parakeet local server.");
            }

            _serverProcess = process;
            _standardOutput = process.StandardOutput.ReadToEndAsync();
            _standardError = process.StandardError.ReadToEndAsync();
            _endpoint = new Uri($"http://127.0.0.1:{port}/v1/audio/transcriptions");
            try
            {
                await WaitUntilReadyAsync(process, port, cancellationToken);
                return _endpoint;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                StopServer();
                throw;
            }
            catch
            {
                var detail = await StopServerAndReadErrorAsync();
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(detail)
                        ? "The Parakeet local server did not become ready."
                        : $"The Parakeet local server did not become ready: {detail}");
            }
        }
        finally
        {
            _startupLock.Release();
        }
    }

    private Process CreateServerProcess(int port)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _serverPath,
            WorkingDirectory = Path.GetDirectoryName(_serverPath),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            ErrorDialog = false
        };
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add(_options.ModelPath);
        startInfo.ArgumentList.Add("--host");
        startInfo.ArgumentList.Add("127.0.0.1");
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        ApplyRuntimePath(startInfo);
        return new Process { StartInfo = startInfo };
    }

    private void ApplyRuntimePath(ProcessStartInfo startInfo)
    {
        var directories = RuntimePathBuilder.GetRuntimeSearchPaths(_options.CliPath);
        if (directories.Count == 0)
        {
            return;
        }

        var existingPath = startInfo.Environment.TryGetValue("PATH", out var path)
            ? path
            : Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        startInfo.Environment["PATH"] = string.Join(Path.PathSeparator, directories.Concat([existingPath]));
    }

    private static async Task WaitUntilReadyAsync(
        Process process,
        int port,
        CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(StartupTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        while (true)
        {
            linked.Token.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new InvalidOperationException("The Parakeet local server exited during startup.");
            }

            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port, linked.Token);
                if (TcpProcessInspector.IsOwnedBy(IPAddress.Loopback, port, process.Id))
                {
                    return;
                }

                throw new InvalidOperationException(
                    "A different local process claimed the Parakeet server port.");
            }
            catch (SocketException)
            {
                await Task.Delay(50, linked.Token);
            }
        }
    }

    private bool ServerIsRunning()
    {
        return _serverProcess is { HasExited: false } && _endpoint is not null;
    }

    private async ValueTask<Stream> ConnectToExpectedServerAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var process = _serverProcess;
        if (process is null || process.HasExited)
        {
            throw new InvalidOperationException(
                "The Parakeet endpoint is not owned by the expected local server process.");
        }

        return await ProcessBoundLoopbackConnector.ConnectAsync(
            context.DnsEndPoint,
            process.Id,
            cancellationToken);
    }

    private async Task<string> StopServerAndReadErrorAsync()
    {
        StopServer();
        try
        {
            return _standardError is null ? string.Empty : await _standardError;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private void StopServer()
    {
        var process = _serverProcess;
        _serverProcess = null;
        _endpoint = null;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private static int ReserveAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string CleanSpecialTokens(string text)
    {
        return text.Replace("<EOU>", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    internal sealed record ServerTranscriptSegment(
        TranscriptResult Transcript,
        TimeSpan? EndOfUtterance);

    private sealed record PcmWaveAudio(
        ushort Format,
        ushort Channels,
        uint SampleRate,
        uint ByteRate,
        ushort BlockAlign,
        ushort BitsPerSample,
        byte[] Pcm)
    {
        public TimeSpan Duration => TimeSpan.FromSeconds((double)Pcm.Length / ByteRate);

        public static PcmWaveAudio Parse(byte[] wav)
        {
            if (wav.Length < 44
                || !wav.AsSpan(0, 4).SequenceEqual("RIFF"u8)
                || !wav.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            {
                throw new InvalidOperationException("Parakeet recording was not a valid WAV file.");
            }

            ushort format = 0;
            ushort channels = 0;
            uint sampleRate = 0;
            uint byteRate = 0;
            ushort blockAlign = 0;
            ushort bitsPerSample = 0;
            byte[]? pcm = null;
            var offset = 12;
            while (offset <= wav.Length - 8)
            {
                var chunkSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(offset + 4, sizeof(uint))));
                var chunkDataOffset = offset + 8;
                if (chunkDataOffset > wav.Length - chunkSize)
                {
                    break;
                }

                var id = wav.AsSpan(offset, 4);
                if (id.SequenceEqual("fmt "u8) && chunkSize >= 16)
                {
                    format = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(chunkDataOffset, sizeof(ushort)));
                    channels = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(chunkDataOffset + 2, sizeof(ushort)));
                    sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(chunkDataOffset + 4, sizeof(uint)));
                    byteRate = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(chunkDataOffset + 8, sizeof(uint)));
                    blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(chunkDataOffset + 12, sizeof(ushort)));
                    bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(chunkDataOffset + 14, sizeof(ushort)));
                }
                else if (id.SequenceEqual("data"u8))
                {
                    pcm = wav.AsSpan(chunkDataOffset, chunkSize).ToArray();
                }

                offset = checked(chunkDataOffset + chunkSize + (chunkSize & 1));
            }

            if (format != 1
                || channels == 0
                || sampleRate == 0
                || byteRate == 0
                || blockAlign == 0
                || bitsPerSample == 0
                || pcm is null)
            {
                throw new InvalidOperationException("Parakeet recording must contain PCM WAV audio.");
            }

            return new PcmWaveAudio(format, channels, sampleRate, byteRate, blockAlign, bitsPerSample, pcm);
        }

        public byte[] CreateSegment(TimeSpan start)
        {
            var requestedOffset = checked((long)Math.Floor(start.TotalSeconds * ByteRate));
            var alignedOffset = Math.Min(Pcm.Length, requestedOffset / BlockAlign * BlockAlign);
            var segmentLength = checked(Pcm.Length - (int)alignedOffset);
            var wav = new byte[44 + segmentLength];
            "RIFF"u8.CopyTo(wav);
            BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(4, sizeof(uint)), checked((uint)(wav.Length - 8)));
            "WAVE"u8.CopyTo(wav.AsSpan(8));
            "fmt "u8.CopyTo(wav.AsSpan(12));
            BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(16, sizeof(uint)), 16);
            BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20, sizeof(ushort)), Format);
            BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(22, sizeof(ushort)), Channels);
            BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(24, sizeof(uint)), SampleRate);
            BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(28, sizeof(uint)), ByteRate);
            BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(32, sizeof(ushort)), BlockAlign);
            BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(34, sizeof(ushort)), BitsPerSample);
            "data"u8.CopyTo(wav.AsSpan(36));
            BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(40, sizeof(uint)), checked((uint)segmentLength));
            Pcm.AsSpan((int)alignedOffset).CopyTo(wav.AsSpan(44));
            return wav;
        }
    }
}
