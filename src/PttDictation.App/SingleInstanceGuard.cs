namespace PttDictation.App;

internal sealed class SingleInstanceGuard : IDisposable
{
    internal const string DefaultMutexName = "Local\\PttDictation.App";

    private static readonly object Gate = new();
    private static readonly HashSet<string> OwnedMutexNames = new(StringComparer.Ordinal);

    private readonly string mutexName;
    private readonly Mutex mutex;
    private bool disposed;

    private SingleInstanceGuard(string mutexName, Mutex mutex)
    {
        this.mutexName = mutexName;
        this.mutex = mutex;
    }

    public static SingleInstanceGuard? TryAcquire(string mutexName = DefaultMutexName)
    {
        lock (Gate)
        {
            if (OwnedMutexNames.Contains(mutexName))
            {
                return null;
            }

            var mutex = new Mutex(initiallyOwned: false, name: mutexName);
            var acquired = false;
            try
            {
                acquired = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (acquired)
            {
                OwnedMutexNames.Add(mutexName);
                return new SingleInstanceGuard(mutexName, mutex);
            }

            mutex.Dispose();
            return null;
        }
    }

    public void Dispose()
    {
        lock (Gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            OwnedMutexNames.Remove(mutexName);
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }
}
