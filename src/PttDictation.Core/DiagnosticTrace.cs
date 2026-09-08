using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace PttDictation.Core;

/// <summary>Opt-in local diagnostics. Sink failures must never affect dictation.</summary>
public static class DiagnosticTrace
{
    private static Sink? sink;
    private static readonly AsyncLocal<string?> ambientRecordingId = new();

    public static string? CurrentRecordingId => ambientRecordingId.Value ?? Volatile.Read(ref sink)?.CurrentRecordingId;

    public static IDisposable EnterRecording(string recordingId)
    {
        var previous = ambientRecordingId.Value;
        ambientRecordingId.Value = recordingId;
        return new RecordingScope(previous);
    }

    public static IDisposable Configure(string rootDirectory, string buildLabel)
    {
        try
        {
            var next = new Sink(Path.GetFullPath(rootDirectory), buildLabel);
            Interlocked.Exchange(ref sink, next)?.Dispose();
            next.Write("diagnostics.started", new { audioRetentionCount = 3 });
            return next;
        }
        catch (Exception error)
        {
            Write("diagnostics.configuration_failed", error: error);
            return new DisabledScope();
        }
    }

    public static string BeginRecording(string trigger) =>
        Volatile.Read(ref sink)?.BeginRecording(trigger) ?? string.Empty;

    public static void Write(string stage, object? data = null, Exception? error = null, string? recordingId = null) =>
        Volatile.Read(ref sink)?.Write(stage, data, error, recordingId);

    public static void RetainRecording(string recordingId, string wavPath) =>
        Volatile.Read(ref sink)?.RetainRecording(recordingId, wavPath);

    /// <summary>Transfers ownership of an already-open audio stream, including when diagnostics is disabled.</summary>
    public static void RetainRecording(string recordingId, Stream source)
    {
        var active = Volatile.Read(ref sink);
        if (active != null)
            active.RetainRecording(recordingId, source);
        else
            try { source.Dispose(); } catch { }
    }

    public static Task FlushAsync() => Volatile.Read(ref sink)?.FlushAsync() ?? Task.CompletedTask;

    private sealed class Sink : IDisposable
    {
        private const long MaximumLogBytes = 4 * 1024 * 1024;
        private static readonly Regex OwnedSessionName = new(
            "^recording-[0-9]{17}-[a-f0-9]{32}$", RegexOptions.CultureInvariant);
        private readonly Channel<Entry> queue = Channel.CreateBounded<Entry>(new BoundedChannelOptions(512)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        private readonly ConcurrentDictionary<string, Session> sessions = new();
        private readonly string root;
        private readonly string build;
        private readonly long started = Stopwatch.GetTimestamp();
        private readonly Task worker;
        private string? currentRecordingId;
        private long sequence;
        private long dropped;
        private long failures;
        private int disposed;

        public Sink(string root, string build)
        {
            this.root = root;
            this.build = build;
            worker = Task.Run(RunAsync);
        }

        public string? CurrentRecordingId => ambientRecordingId.Value ?? Volatile.Read(ref currentRecordingId);

        public string BeginRecording(string trigger)
        {
            var id = $"recording-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
            sessions[id] = new Session();
            Volatile.Write(ref currentRecordingId, id);
            foreach (var old in sessions.OrderByDescending(pair => pair.Value.Started).Skip(32))
                sessions.TryRemove(old.Key, out _);
            Write("recording.begin", new { trigger }, recordingId: id);
            return id;
        }

        public void Write(string stage, object? data = null, Exception? error = null, string? recordingId = null)
        {
            var entry = CreateEntry(stage, data, error, recordingId);
            if (!queue.Writer.TryWrite(entry))
                Interlocked.Increment(ref dropped);
        }

        private Entry CreateEntry(string stage, object? data, Exception? error, string? recordingId)
        {
            recordingId ??= CurrentRecordingId;
            Session? session = null;
            if (recordingId != null)
                sessions.TryGetValue(recordingId, out session);
            return new Entry
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                Sequence = Interlocked.Increment(ref sequence),
                RecordingId = recordingId,
                SessionSequence = session == null ? null : Interlocked.Increment(ref session.Sequence),
                ElapsedMs = Stopwatch.GetElapsedTime(session?.Started ?? started).TotalMilliseconds,
                Stage = stage,
                Data = data,
                Error = error
            };
        }

        public void RetainRecording(string recordingId, string wavPath)
        {
            if (!OwnedSessionName.IsMatch(recordingId))
            {
                Write("audio.retention_failed", new { reason = "Invalid recording identifier" }, recordingId: recordingId);
                return;
            }
            try
            {
                // Acquire ownership before the caller releases/deletes its temporary WAV.
                var source = new FileStream(wavPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                    81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                RetainRecording(recordingId, source);
            }
            catch (Exception error)
            {
                Write("audio.retention_failed", error: error, recordingId: recordingId);
            }
        }

        public void RetainRecording(string recordingId, Stream source)
        {
            var transferred = false;
            try
            {
                if (!OwnedSessionName.IsMatch(recordingId))
                {
                    Write("audio.retention_failed", new { reason = "Invalid recording identifier" }, recordingId: recordingId);
                    return;
                }
                var entry = CreateEntry("audio.retained", new { bytes = source.CanSeek ? (long?)source.Length : null }, null, recordingId);
                entry.Audio = source;
                if (queue.Writer.TryWrite(entry))
                    transferred = true;
                else
                    Interlocked.Increment(ref dropped);
            }
            catch (Exception error)
            {
                Write("audio.retention_failed", error: error, recordingId: recordingId);
            }
            finally
            {
                if (!transferred)
                    try { source.Dispose(); } catch { }
            }
        }

        public async Task FlushAsync()
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                await queue.Writer.WriteAsync(new Entry { Completion = completion }).ConfigureAwait(false);
                await completion.Task.ConfigureAwait(false);
            }
            catch (ChannelClosedException) { await worker.ConfigureAwait(false); }
        }

        private async Task RunAsync()
        {
            await foreach (var entry in queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    EnsureSafeDirectory(root);
                    var droppedCount = Interlocked.Exchange(ref dropped, 0);
                    var failureCount = Interlocked.Exchange(ref failures, 0);
                    if (droppedCount != 0 || failureCount != 0)
                        Append(CreateEntry("diagnostics.loss", new { droppedEvents = droppedCount, sinkFailures = failureCount }, null, null));
                    if (entry.Audio != null)
                    {
                        try
                        {
                            await SaveAudioAsync(entry).ConfigureAwait(false);
                        }
                        catch (Exception error)
                        {
                            entry.Stage = "audio.retention_failed";
                            entry.Error = error;
                        }
                    }
                    if (entry.Completion == null)
                        Append(entry);
                }
                catch { Interlocked.Increment(ref failures); }
                finally
                {
                    try { entry.Audio?.Dispose(); } catch { Interlocked.Increment(ref failures); }
                    entry.Completion?.TrySetResult();
                }
            }
        }

        private async Task SaveAudioAsync(Entry entry)
        {
            var directory = Path.Combine(root, entry.RecordingId!);
            EnsureSafeDirectory(directory);
            var audioPath = Path.Combine(directory, "audio.wav");
            var pendingPath = Path.Combine(directory, "audio.pending.wav");
            RejectLink(audioPath);
            RejectLink(pendingPath);
            try
            {
                using (var destination = new FileStream(pendingPath, FileMode.Create, FileAccess.Write, FileShare.Read,
                           81920, FileOptions.Asynchronous))
                    await entry.Audio!.CopyToAsync(destination).ConfigureAwait(false);
                File.Move(pendingPath, audioPath, overwrite: true);
            }
            finally
            {
                File.Delete(pendingPath);
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory, recursive: false);
            }
            PruneAudio();
        }

        private void PruneAudio()
        {
            // Never recursively remove arbitrary contents, follow junctions, or touch unrelated directories.
            var owned = Directory.EnumerateDirectories(root)
                .Where(path => OwnedSessionName.IsMatch(Path.GetFileName(path)))
                .Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                .Where(path => File.Exists(Path.Combine(path, "audio.wav")))
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal).ToArray();
            foreach (var old in owned.Skip(3))
            {
                var audioPath = Path.Combine(old, "audio.wav");
                RejectLink(audioPath);
                File.Delete(audioPath);
                if (!Directory.EnumerateFileSystemEntries(old).Any())
                    Directory.Delete(old, recursive: false);
            }
        }

        private void Append(Entry entry)
        {
            var path = Path.Combine(root, "events.jsonl");
            var previous = Path.Combine(root, "events.previous.jsonl");
            RejectLink(path);
            RejectLink(previous);
            var line = JsonSerializer.Serialize(new
            {
                timestampUtc = entry.TimestampUtc,
                sequence = entry.Sequence,
                recordingId = entry.RecordingId,
                sessionSequence = entry.SessionSequence,
                elapsedMs = entry.ElapsedMs,
                build,
                stage = entry.Stage,
                data = entry.Data,
                exception = entry.Error?.ToString()
            });
            if (System.Text.Encoding.UTF8.GetByteCount(line) > MaximumLogBytes)
                line = JsonSerializer.Serialize(new { timestampUtc = entry.TimestampUtc, build, stage = "diagnostics.event_too_large", originalStage = entry.Stage });
            if (File.Exists(path) && new FileInfo(path).Length + System.Text.Encoding.UTF8.GetByteCount(line) + 2 > MaximumLogBytes)
                File.Move(path, previous, overwrite: true);
            File.AppendAllText(path, line + Environment.NewLine);
        }

        private static void EnsureSafeDirectory(string directory)
        {
            // Inspect every existing ancestor so a configured root cannot traverse a junction either.
            for (var current = new DirectoryInfo(directory); current != null; current = current.Parent)
                RejectLink(current.FullName);
            Directory.CreateDirectory(directory);
            RejectLink(directory);
        }

        private static void RejectLink(string path)
        {
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Diagnostics will not follow a reparse point: " + path);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
                return;
            Interlocked.CompareExchange(ref sink, null, this);
            queue.Writer.TryComplete();
            try { worker.Wait(TimeSpan.FromSeconds(2)); } catch { /* Never disrupt application shutdown. */ }
        }

        private sealed class Session
        {
            public readonly long Started = Stopwatch.GetTimestamp();
            public long Sequence;
        }

        private sealed class Entry
        {
            public DateTimeOffset TimestampUtc;
            public long Sequence;
            public string? RecordingId;
            public long? SessionSequence;
            public double ElapsedMs;
            public string Stage = string.Empty;
            public object? Data;
            public Exception? Error;
            public Stream? Audio;
            public TaskCompletionSource? Completion;
        }
    }

    private sealed class RecordingScope(string? previous) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            ambientRecordingId.Value = previous;
        }
    }

    private sealed class DisabledScope : IDisposable
    {
        public void Dispose() { }
    }
}
