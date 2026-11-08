namespace Amwag.PrinterSetup.App.Hosting;

/// <summary>Keeps a second interactive window from starting in the same Windows session (named mutex Local\AMWAG.PrinterSetup.UI).</summary>
internal sealed class SingleInstance : IDisposable
{
    public const string MutexName = @"Local\AMWAG.PrinterSetup.UI";

    private readonly Mutex _mutex;

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>Null when another instance already runs in this session. Dispose on the thread that acquired it.</summary>
    public static SingleInstance? TryAcquire()
    {
        Mutex mutex;
        bool createdNew;
        try
        {
            mutex = new Mutex(initiallyOwned: true, MutexName, out createdNew);
        }
        catch (UnauthorizedAccessException)
        {
            // The mutex exists but belongs to an elevated instance in this session.
            return null;
        }

        if (createdNew)
            return new SingleInstance(mutex);

        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
