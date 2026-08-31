namespace PttDictation.App;

internal static class AudioLevelCalculator
{
    public static double CalculatePeakLevel(ReadOnlySpan<byte> pcm16LittleEndian)
    {
        var max = 0;
        for (var i = 0; i + 1 < pcm16LittleEndian.Length; i += 2)
        {
            var sample = BitConverter.ToInt16(pcm16LittleEndian[i..(i + 2)]);
            max = Math.Max(max, sample == short.MinValue ? short.MaxValue : Math.Abs(sample));
        }

        return Math.Clamp(max / (double)short.MaxValue, 0, 1);
    }
}
