namespace PttDictation.App;

internal sealed class AudioChunkPublicationQueue
{
    private readonly object _gate = new();
    private readonly HashSet<Task> _publications = [];
    private bool _accepting;

    public void Open()
    {
        lock (_gate)
        {
            _publications.RemoveWhere(publication => publication.IsCompleted);
            if (_publications.Count > 0)
            {
                throw new InvalidOperationException("Chunk publications from the previous recording are still running.");
            }

            _accepting = true;
        }
    }

    public bool TryQueue(Action publish)
    {
        Task publication;
        lock (_gate)
        {
            if (!_accepting)
            {
                return false;
            }

            publication = Task.Run(publish);
            _publications.Add(publication);
        }

        _ = publication.ContinueWith(
            completed => Complete(completed),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return true;
    }

    public void StopAccepting()
    {
        lock (_gate)
        {
            _accepting = false;
        }
    }

    public void Drain()
    {
        Task[] publications;
        lock (_gate)
        {
            _accepting = false;
            publications = [.. _publications];
        }

        try
        {
            Task.WhenAll(publications).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
        }
    }

    private void Complete(Task publication)
    {
        _ = publication.Exception;
        lock (_gate)
        {
            _publications.Remove(publication);
        }
    }
}
