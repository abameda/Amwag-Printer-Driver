using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Inventory;

namespace Amwag.PrinterSetup.Testing;

public sealed class FakePlatformInspector : IPlatformInspector
{
    /// <summary>A typical branch PC: Windows 11 24H2 x64, interactive, not elevated, protected print mode off.</summary>
    public static PlatformInfo Windows11 { get; } = new()
    {
        ProductName = "Windows 11 Pro",
        DisplayVersion = "24H2",
        Build = 26100,
        Ubr = 2033,
        Architecture = "x64",
        ProcessArchitecture = "x64",
        MachineName = "AMWAG-BR01-PC01",
        UserName = @"AMWAG\employee",
        IsElevated = false,
        IsSystemAccount = false,
        IsInteractive = true,
        ProtectedPrintModeEnabled = false,
        RestrictDriverInstallationToAdministrators = true,
    };

    public PlatformInfo Info { get; set; } = Windows11;

    /// <summary>When set, <see cref="Inspect"/> throws it.</summary>
    public Exception? Failure { get; set; }

    public PlatformInfo Inspect() => Failure is null ? Info : throw Failure;
}
