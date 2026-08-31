namespace PttDictation.App;

internal sealed class PcmChunkBuffer(int bytesPerSecond, int chunkBytes, int overlapBytes) : IDisposable
{
    private readonly MemoryStream _pcm = new();
    private long _chunkStartByte;
    private bool _hasCreatedChunk;

    public void Append(ReadOnlySpan<byte> pcm)
    {
        _pcm.Write(pcm);
    }

    public PendingAudioChunk? TryCreateChunk(string path)
    {
        if (_pcm.Length - _chunkStartByte < chunkBytes)
        {
            return null;
        }

        if (!_pcm.TryGetBuffer(out var buffer))
        {
            return null;
        }

        var chunkStart = checked((int)_chunkStartByte);
        var chunkEnd = checked((int)(_chunkStartByte + chunkBytes));
        var chunkLength = chunkEnd - chunkStart;
        var chunkPcm = new byte[chunkLength];
        Buffer.BlockCopy(buffer.Array!, buffer.Offset + chunkStart, chunkPcm, 0, chunkLength);
        _chunkStartByte = Math.Max(0, chunkEnd - overlapBytes);
        var overlapDuration = _hasCreatedChunk
            ? TimeSpan.FromSeconds((double)overlapBytes / bytesPerSecond)
            : TimeSpan.Zero;
        _hasCreatedChunk = true;
        return new PendingAudioChunk(
            path,
            chunkPcm,
            TimeSpan.FromSeconds((double)chunkPcm.Length / bytesPerSecond),
            overlapDuration);
    }

    public byte[] ToArray()
    {
        return _pcm.ToArray();
    }

    public void Dispose()
    {
        _pcm.Dispose();
    }
}

internal sealed record PendingAudioChunk(string Path, byte[] Pcm, TimeSpan Duration, TimeSpan OverlapDuration);
