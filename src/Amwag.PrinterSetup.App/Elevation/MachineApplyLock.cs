using Amwag.PrinterSetup.Core.Logging;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.App.Elevation;

/// <summary>
/// Machine-wide lock (named mutex Global\AMWAG.PrinterSetup.Apply) held while machine-wide changes are applied, so two runs
/// (an employee's window and an IT deployment, for example) never create the same queue, port or form at the same time.
/// The mutex has thread affinity: dispose the lock on the thread that acquired it.
/// </summary>
internal sealed class MachineApplyLock : IDisposable
{
    public const string MutexName = @"Global\AMWAG.PrinterSetup.Apply";

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(5);

    private readonly Mutex _mutex;

    private MachineApplyLock(Mutex mutex) => _mutex = mutex;

    /// <summary>
    /// Waits up to <paramref name="timeout"/> and returns null when another run still holds the lock (or holds it under an
    /// account this process cannot open it as). A lock abandoned by a crashed run is taken over: every action re-checks its state.
    /// </summary>
    public static MachineApplyLock? Acquire(TimeSpan timeout, ILog log, CancellationToken cancellationToken, Action? waiting = null)
    {
        Mutex mutex;
        try
        {
            mutex = new Mutex(initiallyOwned: false, MutexName);
        }
        catch (UnauthorizedAccessException ex)
        {
            log.Warn("apply.lock.denied", "The apply lock exists under another account; another run is applying changes.", exception: ex);
            return null;
        }

        bool acquired = false;
        try
        {
            acquired = mutex.WaitOne(0);
            if (!acquired)
            {
                waiting?.Invoke();
                log.Info("apply.lock.waiting", "Another run is applying changes; waiting for it to finish.");
                acquired = WaitHandle.WaitAny([mutex, cancellationToken.WaitHandle], timeout) == 0;
                if (!acquired)
                    cancellationToken.ThrowIfCancellationRequested();
            }
        }
        catch (AbandonedMutexException)
        {
            log.Warn("apply.lock.abandoned", "A previous run ended while applying changes; continuing because every action re-checks its state.");
            acquired = true;
        }
        finally
        {
            if (!acquired)
                mutex.Dispose();
        }

        if (acquired)
            return new MachineApplyLock(mutex);

        log.Warn("apply.lock.timeout", "Another run kept applying changes for too long.", new Dictionary<string, object?> { ["timeoutSeconds"] = timeout.TotalSeconds });
        return null;
    }

    /// <summary>The finding for a run that could not get the lock (<see cref="Acquire"/> returned null): nothing was changed.</summary>
    public static Finding BusyFinding() => new(FindingCode.ApplyLockBusy, Severity.Error,
        "Another AMWAG Printer Setup run is applying changes on this computer; nothing was changed. Try again in a few minutes.");

    public void Dispose()
    {
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
