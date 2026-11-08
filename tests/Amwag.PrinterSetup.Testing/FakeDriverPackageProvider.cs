using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.Testing;

/// <summary>Prepares packages instantly, or throws a configured failure (offline, hash mismatch, bad signature ...).</summary>
public sealed class FakeDriverPackageProvider : IDriverPackageProvider
{
    private readonly object _gate = new();
    private readonly List<string> _prepared = [];

    public static SignatureInfo TrustedSignature { get; } = new(
        IsTrusted: true,
        SignerSubject: "CN=KYOCERA Document Solutions Inc., O=KYOCERA Document Solutions Inc., L=Osaka, C=JP",
        IssuerSubject: "CN=Microsoft Windows Third Party Component CA 2014",
        Thumbprint: "3F1C7A2D9B4E8F60A1C2D3E4F5061728394A5B6C",
        StatusCode: 0,
        StatusText: "The catalog signature is valid.");

    /// <summary>When set, <see cref="Prepare"/> throws it (e.g. DriverPackageUnavailable when offline, or an unexpected error).</summary>
    public Exception? Failure { get; set; }

    public PackageOrigin Origin { get; set; } = PackageOrigin.Downloaded;

    public SignatureInfo Signature { get; set; } = TrustedSignature;

    /// <summary>Ids of every package <see cref="Prepare"/> was called for (including failed attempts).</summary>
    public IReadOnlyList<string> PreparedPackageIds
    {
        get { lock (_gate) return [.. _prepared]; }
    }

    public PreparedDriverPackage Prepare(DriverPackageDefinition package, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
            _prepared.Add(package.Id);
        if (Failure is not null)
            throw Failure;

        Report(progress, ProgressPhase.Downloading, $"Downloading {package.FileName}", 1.0);
        Report(progress, ProgressPhase.Verifying, $"Verifying SHA-256 and signature of {package.FileName}", null);
        Report(progress, ProgressPhase.Extracting, $"Extracting {package.FileName}", null);

        string root = $@"C:\ProgramData\AMWAG\PrinterSetup\drivers\{package.Id}\extracted";
        return new PreparedDriverPackage(
            package,
            ArchivePath: $@"C:\ProgramData\AMWAG\PrinterSetup\drivers\{package.Id}\{package.FileName}",
            Origin,
            ExtractedRoot: root,
            InfPath: Path.Combine(root, package.InfPath.Replace('/', '\\')),
            Signature);
    }

    private static void Report(IProgress<ProgressUpdate>? progress, ProgressPhase phase, string detail, double? fraction) =>
        progress?.Report(new ProgressUpdate(StepId.InstallDriver, StepState.Running, ActionKind.PrepareDriverPackage, phase, detail) { Fraction = fraction });
}
