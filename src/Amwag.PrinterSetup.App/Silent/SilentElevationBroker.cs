using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.App.Silent;

/// <summary>
/// Silent runs never show a UAC prompt: when this process is elevated the flow applies machine-wide steps in-process; otherwise
/// they are refused, which ends the run with exit code <see cref="ExitCodes.ElevationRequired"/>.
/// </summary>
internal sealed class SilentElevationBroker : IElevationBroker
{
    public bool IsElevated => Environment.IsPrivilegedProcess;

    public Task<ElevatedRunResult> RunMachineStepsAsync(WorkerRequest request, IProgress<ProgressUpdate> progress, CancellationToken cancellationToken) =>
        Task.FromResult(new ElevatedRunResult(ElevationOutcome.Declined, ExitCodes.ElevationRequired)
        {
            Error = "Silent mode is not running elevated and cannot ask for administrator approval; run it elevated (for example as SYSTEM from the deployment tool).",
        });
}
