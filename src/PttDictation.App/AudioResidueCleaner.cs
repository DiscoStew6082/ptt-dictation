namespace PttDictation.App;

internal static class AudioResidueCleaner
{
    private static readonly string[] AppOwnedPatterns = ["utterance-*.wav", "chunk-*.wav"];

    public static IReadOnlyList<string> DeleteStaleFiles(string directory)
    {
        var failures = new List<string>();
        if (!Directory.Exists(directory))
        {
            return failures;
        }

        foreach (var pattern in AppOwnedPatterns)
        {
            IEnumerable<string> paths;
            try
            {
                paths = Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).ToArray();
            }
            catch (IOException)
            {
                failures.Add(directory);
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                failures.Add(directory);
                continue;
            }

            foreach (var path in paths)
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    failures.Add(path);
                }
                catch (UnauthorizedAccessException)
                {
                    failures.Add(path);
                }
            }
        }

        return failures;
    }
}
