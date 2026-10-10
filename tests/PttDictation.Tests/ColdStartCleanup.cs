using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace PttDictation.Tests;

internal sealed class ColdStartCleanup(Action<string, Exception> report)
{
    private readonly List<Exception> _errors = [];
    public void Run(string stage, Action cleanup)
    {
        try { cleanup(); }
        catch (Exception error) { Record(stage, error); }
    }
    public async Task RunAsync(string stage, Func<Task> cleanup)
    {
        try { await cleanup(); }
        catch (Exception error) { Record(stage, error); }
    }
    private void Record(string stage, Exception error)
    {
        _errors.Add(error);
        try { report(stage, error); }
        catch (Exception reportError) { _errors.Add(reportError); }
    }
    public void ThrowIfNeeded(Exception? primaryFailure)
    {
        if (primaryFailure is null && _errors.Count > 0) ExceptionDispatchInfo.Capture(_errors[0]).Throw();
    }

    public static void DeleteProfile(string profile, TimeSpan? retryWindow = null)
    {
        var fullPath = Path.GetFullPath(profile);
        var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(fullPath).StartsWith("ptt-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing cleanup outside the owned temporary profile directory.");
        var elapsed = Stopwatch.StartNew();
        var limit = retryWindow ?? TimeSpan.FromSeconds(5);
        while (Directory.Exists(fullPath))
        {
            try { Directory.Delete(fullPath, recursive: true); }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 or 145 && elapsed.Elapsed < limit)
            {
                // Job exit can precede final file-handle release. Retry only
                // sharing/lock/directory-not-empty races, never unknown failures.
                Thread.Sleep(50);
            }
        }
    }
}
