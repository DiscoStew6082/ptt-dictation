using NAudio.CoreAudioApi;
using NAudio.Wave;
using PttDictation.Core;

namespace PttDictation.App;

internal sealed class WasapiAudioRecorder : IChunkedAudioRecorder, IDisposable
{
    private const int BytesPerSecond = 32000;
    private const int ChunkDurationMilliseconds = 4000;
    private const int ChunkOverlapMilliseconds = 800;
    private const int ChunkBytes = BytesPerSecond * ChunkDurationMilliseconds / 1000;
    private const int ChunkOverlapBytes = BytesPerSecond * ChunkOverlapMilliseconds / 1000;
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly string _appData;
    private WasapiRecorder? _recorder;
    private TaskCompletionSource<Exception?>? _recordingStopped;
    private PcmChunkBuffer? _pcm;
    private DateTimeOffset _startedAt;
    private bool _recording;
    private bool _disposed;
    private int _chunkSequence;

    public event Action<double>? AudioLevelChanged;

    public event Action<RecordedAudio>? AudioChunkReady;

    public WasapiAudioRecorder()
        : this(Path.GetTempPath())
    {
    }

    public WasapiAudioRecorder(string appData)
    {
        _appData = appData;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            await Task.Run(StartCore, cancellationToken);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private void StartCore()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_recording || _recorder is not null)
            {
                return;
            }
        }

        Directory.CreateDirectory(_appData);
        WasapiRecorder? recorder = null;
        PcmChunkBuffer? pcm = null;
        try
        {
            recorder = new WasapiRecorderBuilder()
                .WithSharedMode()
                .WithEventSync()
                .WithBufferLength(100)
                .WithFormat(CreateCaptureFormat())
                .Build();
            recorder.DataAvailable += OnDataAvailable;
            recorder.RecordingStopped += OnRecordingStopped;

            pcm = new PcmChunkBuffer(BytesPerSecond, ChunkBytes, ChunkOverlapBytes);
            lock (_gate)
            {
                ThrowIfDisposed();
                _recorder = recorder;
                _recordingStopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pcm = pcm;
                _startedAt = DateTimeOffset.UtcNow;
                _chunkSequence = 0;
                _recording = true;
            }

            recorder.StartRecording();
        }
        catch (Exception ex)
        {
            var deviceName = recorder?.DeviceFriendlyName;
            lock (_gate)
            {
                _recording = false;
                _recorder = null;
                _recordingStopped = null;
                _pcm = null;
            }

            pcm?.Dispose();
            DetachAndDispose(recorder);
            throw new InvalidOperationException(DescribeCaptureFailure(deviceName), ex);
        }
    }

    public async Task<RecordedAudio> StopAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(CancellationToken.None);
        try
        {
            ThrowIfDisposed();
            return await Task.Run(StopCore, CancellationToken.None);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private RecordedAudio StopCore()
    {
        WasapiRecorder recorder;
        TaskCompletionSource<Exception?> stopped;
        PcmChunkBuffer pcm;
        TimeSpan duration;

        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_recording || _recorder is null || _recordingStopped is null || _pcm is null)
            {
                throw new InvalidOperationException("Recorder is not running.");
            }

            _recording = false;
            recorder = _recorder;
            stopped = _recordingStopped;
            pcm = _pcm;
            duration = DateTimeOffset.UtcNow - _startedAt;
        }

        var deviceName = recorder.DeviceFriendlyName;
        Exception? stopError = null;
        try
        {
            if (!stopped.Task.IsCompleted)
            {
                recorder.StopRecording();
            }

            if (!stopped.Task.Wait(StopTimeout))
            {
                stopError = new TimeoutException("Windows did not stop microphone capture in time.");
            }
            else
            {
                stopError = stopped.Task.Result;
            }
        }
        catch (Exception ex)
        {
            stopError = ex;
        }
        finally
        {
            lock (_gate)
            {
                _recorder = null;
                _recordingStopped = null;
                _pcm = null;
            }

            DetachAndDispose(recorder);
        }

        if (stopError is not null)
        {
            pcm.Dispose();
            throw new InvalidOperationException(DescribeCaptureFailure(deviceName), stopError);
        }

        var wavPath = Path.Combine(_appData, $"utterance-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}.wav");
        WriteWav(wavPath, pcm.ToArray());
        pcm.Dispose();
        return new RecordedAudio(wavPath, duration, DeleteAfterUse: true);
    }

    private void OnDataAvailable(
        ReadOnlySpan<byte> buffer,
        AudioClientBufferFlags flags,
        long devicePosition,
        long qpcPosition)
    {
        if (buffer.IsEmpty)
        {
            return;
        }

        var data = flags.HasFlag(AudioClientBufferFlags.Silent)
            ? new byte[buffer.Length]
            : buffer.ToArray();
        double? level = null;
        PendingAudioChunk? pendingChunk = null;

        lock (_gate)
        {
            if (_pcm is null)
            {
                return;
            }

            _pcm.Append(data);
            level = AudioLevelCalculator.CalculatePeakLevel(data);
            pendingChunk = TryCreatePendingChunk();
        }

        AudioLevelChanged?.Invoke(level.Value);
        if (pendingChunk is not null)
        {
            _ = Task.Run(() => WriteChunkAndPublish(pendingChunk));
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs eventArgs)
    {
        TaskCompletionSource<Exception?>? stopped;
        lock (_gate)
        {
            stopped = _recordingStopped;
        }

        stopped?.TrySetResult(eventArgs.Exception);
    }

    private PendingAudioChunk? TryCreatePendingChunk()
    {
        if (AudioChunkReady is null || _pcm is null)
        {
            return null;
        }

        var path = Path.Combine(_appData, $"chunk-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}-{_chunkSequence:000}.wav");
        var chunk = _pcm.TryCreateChunk(path);
        if (chunk is not null)
        {
            _chunkSequence++;
        }

        return chunk;
    }

    private void WriteChunkAndPublish(PendingAudioChunk chunk)
    {
        AudioChunkPublisher.Publish(chunk, AudioChunkReady, WriteWav, TryDelete);
    }

    public void Dispose()
    {
        _lifecycle.Wait();
        try
        {
            DisposeCore();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private void DisposeCore()
    {
        WasapiRecorder? recorder;
        PcmChunkBuffer? pcm;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _recording = false;
            recorder = _recorder;
            _recorder = null;
            _recordingStopped = null;
            pcm = _pcm;
            _pcm = null;
        }

        if (recorder is not null)
        {
            try
            {
                recorder.StopRecording();
            }
            catch
            {
            }

            DetachAndDispose(recorder);
        }

        pcm?.Dispose();
    }

    internal static WaveFormat CreateCaptureFormat()
    {
        return new WaveFormat(16000, 16, 1);
    }

    internal static string DescribeCaptureFailure(string? deviceName)
    {
        var target = string.IsNullOrWhiteSpace(deviceName)
            ? "the default Windows microphone"
            : $"\"{deviceName}\"";
        return $"Windows could not record from {target}. Check Settings > System > Sound > Input, then try again.";
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WasapiAudioRecorder));
        }
    }

    private void DetachAndDispose(WasapiRecorder? recorder)
    {
        if (recorder is null)
        {
            return;
        }

        recorder.DataAvailable -= OnDataAvailable;
        recorder.RecordingStopped -= OnRecordingStopped;
        recorder.Dispose();
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

    private static void WriteWav(string path, byte[] pcm)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + pcm.Length);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(16000);
        writer.Write(BytesPerSecond);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(pcm.Length);
        writer.Write(pcm);
    }
}
