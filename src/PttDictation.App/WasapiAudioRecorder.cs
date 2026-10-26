using NAudio.CoreAudioApi;
using NAudio.Wave;
using PttDictation.Core;

namespace PttDictation.App;

internal sealed class WasapiAudioRecorder : ICumulativeAudioRecorder, IDisposable
{
    private const int BytesPerSecond = 32000;
    private const int ChunkDurationMilliseconds = 2000;
    private const int ChunkOverlapMilliseconds = 1200;
    private const int ChunkBytes = BytesPerSecond * ChunkDurationMilliseconds / 1000;
    private const int ChunkOverlapBytes = BytesPerSecond * ChunkOverlapMilliseconds / 1000;
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly string _appData;
    private WasapiRecorder? _recorder;
    private TaskCompletionSource<Exception?>? _recordingStopped;
    private CumulativePcmBuffer? _pcm;
    private DateTimeOffset _startedAt;
    private bool _recording;
    private bool _disposed;
    private Guid _recordingId;

    public event Action<double>? AudioLevelChanged;

    public event Action<CumulativeAudioSnapshot>? SnapshotReady;

    internal static TimeSpan ChunkDurationForTest => TimeSpan.FromMilliseconds(ChunkDurationMilliseconds);

    internal static TimeSpan ChunkOverlapForTest => TimeSpan.FromMilliseconds(ChunkOverlapMilliseconds);

    public WasapiAudioRecorder()
        : this(Path.GetTempPath())
    {
    }

    public WasapiAudioRecorder(string appData)
    {
        _appData = appData;
    }

    public Task StartAsync(CancellationToken cancellationToken) => StartAsync(Guid.NewGuid(), cancellationToken);

    public async Task StartAsync(Guid recordingId, CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            await Task.Run(() => StartCore(recordingId), cancellationToken);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private void StartCore(Guid recordingId)
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
        CumulativePcmBuffer? pcm = null;
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

            pcm = new CumulativePcmBuffer(recordingId, ChunkBytes / 2, (ChunkBytes - ChunkOverlapBytes) / 2);
            lock (_gate)
            {
                ThrowIfDisposed();
                _recorder = recorder;
                _recordingStopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pcm = pcm;
                _startedAt = DateTimeOffset.UtcNow;
                _recordingId = recordingId;
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
        CumulativePcmBuffer pcm;
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
            stopError = StopCapture(() => recorder.CaptureState, recorder.StopRecording, stopped.Task, StopTimeout);
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
            throw new InvalidOperationException(DescribeCaptureFailure(deviceName), stopError);
        }

        var wavPath = Path.Combine(_appData, $"utterance-{_recordingId:N}.wav");
        try
        {
            WriteWav(wavPath, pcm.ToArray());
            return new RecordedAudio(wavPath, duration, DeleteAfterUse: true);
        }
        catch { TryDelete(wavPath); throw; }
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
        CumulativeAudioSnapshot? snapshot = null;

        lock (_gate)
        {
            if (_pcm is null)
            {
                return;
            }

            _pcm.Append(data);
            level = AudioLevelCalculator.CalculatePeakLevel(data);
            if (_recording && SnapshotReady is not null) snapshot = _pcm.TryCreateSnapshot();
        }

        AudioLevelChanged?.Invoke(level.Value);
        if (snapshot is not null) SnapshotReady?.Invoke(snapshot);
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

    public RecordedAudio MaterializeSnapshot(CumulativeAudioSnapshot snapshot, CancellationToken cancellationToken)
    {
        CumulativePcmBuffer pcm;
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_pcm is null || snapshot.RecordingId != _recordingId)
                throw new InvalidOperationException("Snapshot recording is no longer available.");
            pcm = _pcm;
        }
        // Neither the growing prefix copy nor WAV I/O runs on the capture callback.
        // The local buffer reference survives Stop/Dispose and pins its blocks.
        var path = Path.Combine(_appData, $"chunk-{snapshot.RecordingId:N}-{snapshot.EndSample}.wav");
        try
        {
            var data = pcm.CopyPrefix(snapshot, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            WriteWav(path, data);
            cancellationToken.ThrowIfCancellationRequested();
            return new RecordedAudio(path, TimeSpan.FromSeconds(snapshot.EndSample / 16000d),
                DeleteAfterUse: true, IsCumulative: true);
        }
        catch { TryDelete(path); throw; }
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
        TaskCompletionSource<Exception?>? stopped;
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
            stopped = _recordingStopped;
            _pcm = null;
        }

        if (recorder is not null)
        {
            try
            {
                if (stopped is not null)
                {
                    StopCapture(() => recorder.CaptureState, recorder.StopRecording, stopped.Task, StopTimeout);
                }
                else
                {
                    recorder.StopRecording();
                }
            }
            catch
            {
            }

            DetachAndDispose(recorder);
        }

        lock (_gate)
        {
            _recordingStopped = null;
        }

    }

    internal static WaveFormat CreateCaptureFormat()
    {
        return new WaveFormat(16000, 16, 1);
    }

    internal static Exception? StopCapture(
        Func<CaptureState> getCaptureState,
        Action stopRecording,
        Task<Exception?> recordingStopped,
        TimeSpan timeout)
    {
        if (!recordingStopped.IsCompleted)
        {
            stopRecording();
        }

        // NAudio 3.1.0 can overwrite an early Stopping state with Capturing
        // when its capture thread finishes starting. Replay only on that state
        // transition; a fixed startup delay cannot establish this handshake.
        var completed = SpinWait.SpinUntil(() =>
        {
            if (recordingStopped.IsCompleted)
            {
                return true;
            }

            if (getCaptureState() == CaptureState.Capturing)
            {
                stopRecording();
            }

            return recordingStopped.IsCompleted;
        }, timeout);

        return completed
            ? recordingStopped.GetAwaiter().GetResult()
            : new TimeoutException("Windows did not stop microphone capture in time.");
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
