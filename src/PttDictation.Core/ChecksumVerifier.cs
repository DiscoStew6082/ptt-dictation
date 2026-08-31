using System.Security.Cryptography;

namespace PttDictation.Core;

public static class ChecksumVerifier
{
    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static async Task<bool> VerifySha256Async(string path, string expectedSha256, CancellationToken cancellationToken)
    {
        var actual = await ComputeSha256Async(path, cancellationToken);
        return string.Equals(actual, expectedSha256.Trim().ToLowerInvariant(), StringComparison.Ordinal);
    }
}
