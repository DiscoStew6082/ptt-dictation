using PttDictation.Core;

namespace PttDictation.App;

// One capture producer owns Append/TryCreateSnapshot. Published blocks are
// immutable: a selected worker pins a tail in O(1), then copies off the capture
// lock. Stop can release the recorder's reference without invalidating that tail.
internal sealed class CumulativePcmBuffer(Guid recordingId, int firstSamples, int intervalSamples)
{
    private sealed record Block(byte[] Pcm, long EndByte, Block? Previous);
    private Block? _tail;
    private long _lastSnapshot;

    public void Append(byte[] pcm)
    {
        if (pcm.Length % 2 != 0) throw new ArgumentException("PCM16 requires complete samples.", nameof(pcm));
        if (pcm.Length == 0) return;
        var tail = _tail;
        Volatile.Write(ref _tail, new(pcm, checked((tail?.EndByte ?? 0) + pcm.Length), tail));
    }

    public CumulativeAudioSnapshot? TryCreateSnapshot()
    {
        var samples = (_tail?.EndByte ?? 0) / 2;
        if (samples < firstSamples) return null;
        // A large callback publishes the newest complete cadence boundary only.
        var end = firstSamples + (samples - firstSamples) / intervalSamples * intervalSamples;
        if (end <= _lastSnapshot) return null;
        _lastSnapshot = end;
        return new(recordingId, end);
    }

    public byte[] CopyPrefix(CumulativeAudioSnapshot snapshot, CancellationToken cancellationToken)
    {
        if (snapshot.RecordingId != recordingId || snapshot.EndSample <= 0)
            throw new InvalidOperationException("Snapshot belongs to another recording or is empty.");
        return Copy(Volatile.Read(ref _tail), checked(snapshot.EndSample * 2), cancellationToken);
    }

    public byte[] ToArray()
    {
        var tail = Volatile.Read(ref _tail);
        return Copy(tail, tail?.EndByte ?? 0, CancellationToken.None);
    }

    private static byte[] Copy(Block? tail, long endByte, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (endByte > (tail?.EndByte ?? 0)) throw new InvalidOperationException("Snapshot exceeds captured audio.");
        var pcm = new byte[checked((int)endByte)];
        for (var block = tail; block is not null; block = block.Previous)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = block.EndByte - block.Pcm.Length;
            var length = Math.Min(block.EndByte, endByte) - start;
            if (length > 0) Buffer.BlockCopy(block.Pcm, 0, pcm, checked((int)start), checked((int)length));
        }
        return pcm;
    }
}
