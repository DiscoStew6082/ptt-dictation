using System.Buffers.Binary;

namespace PttDictation.Core;

/// <summary>Observes completed full-recording WAV files without changing their lifecycle.</summary>
public sealed class RecordingCapture : IAsyncDisposable
{
    private readonly FileSystemWatcher watcher;
    private readonly Action<string, Stream> retain;
    private readonly Action<string, Exception>? failed;
    private readonly CancellationTokenSource stop = new();
    private readonly object gate = new();
    private readonly HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> recent = new();
    private readonly HashSet<Task> pending = [];

    public RecordingCapture(string directory, Action<string, Stream> retain, Action<string, Exception>? failed = null)
    {
        ArgumentNullException.ThrowIfNull(retain);
        this.retain = retain;
        this.failed = failed;
        watcher = new FileSystemWatcher(directory, "utterance-*.wav")
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 16384
        };
        watcher.Created += OnChanged;
        watcher.Changed += OnChanged;
        watcher.Renamed += OnChanged;
        watcher.Error += (_, args) => failed?.Invoke(directory, args.GetException());
        watcher.EnableRaisingEvents = true;
    }

    private void OnChanged(object sender, FileSystemEventArgs args) => Observe(args.FullPath);

    private void Observe(string path)
    {
        lock (gate)
        {
            if (stop.IsCancellationRequested || !seen.Add(path))
                return;
            recent.Enqueue(path);
            while (recent.Count > 512)
                seen.Remove(recent.Dequeue());
            var task = Task.Run(() => CaptureAsync(path, stop.Token));
            pending.Add(task);
            _ = task.ContinueWith(done => { lock (gate) pending.Remove(done); }, TaskScheduler.Default);
        }
    }

    private async Task CaptureAsync(string path, CancellationToken cancellationToken)
    {
        FileStream? source = null;
        try
        {
            // A full WAV is emitted at stop and normally stays present during final recognition.
            // Retry writer sharing conflicts, but never block or open the source for writing.
            for (var attempt = 0; attempt < 100 && source == null; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("Recording capture will not follow reparse points.");
                    // Disable read-ahead: the writer rewrites this header in place on close.
                    source = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous);
                }
                catch (IOException) when (attempt < 99)
                {
                    await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                }
            }

            if (source == null)
                return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            while (!IsFinalizedPcmWave(source))
                await Task.Delay(20, timeout.Token).ConfigureAwait(false);
            source.Position = 0;
            retain(path, source); // Ownership transfers, including responsibility for disposal.
            source = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            failed?.Invoke(path, error);
        }
        finally
        {
            source?.Dispose();
        }
    }

    private static bool IsFinalizedPcmWave(FileStream stream)
    {
        stream.Position = 0;
        Span<byte> header = stackalloc byte[12];
        if (stream.Length < 44 || stream.Read(header) != header.Length)
            return false;
        if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..].SequenceEqual("WAVE"u8))
            throw new InvalidDataException("Full recording does not contain a RIFF WAVE header.");
        var expectedLength = BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]) + 8L;
        if (expectedLength != stream.Length)
            return false;
        bool pcm = false;
        Span<byte> chunk = stackalloc byte[8];
        Span<byte> format = stackalloc byte[16];
        while (stream.Position + 8 <= expectedLength)
        {
            if (stream.Read(chunk) != 8)
                return false;
            var length = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            var next = stream.Position + length + (length & 1);
            if (next > expectedLength)
                return false;
            if (chunk[..4].SequenceEqual("fmt "u8))
            {
                if (length < 16 || stream.Read(format) != 16)
                    return false;
                pcm = BinaryPrimitives.ReadUInt16LittleEndian(format) == 1
                    && BinaryPrimitives.ReadUInt16LittleEndian(format[2..]) > 0
                    && BinaryPrimitives.ReadUInt32LittleEndian(format[4..]) > 0
                    && BinaryPrimitives.ReadUInt16LittleEndian(format[14..]) > 0;
            }
            if (chunk[..4].SequenceEqual("data"u8))
                return pcm && length > 0;
            stream.Position = next;
        }
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        watcher.Dispose();
        await stop.CancelAsync().ConfigureAwait(false);
        Task[] tasks;
        lock (gate) tasks = pending.ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        stop.Dispose();
    }
}
