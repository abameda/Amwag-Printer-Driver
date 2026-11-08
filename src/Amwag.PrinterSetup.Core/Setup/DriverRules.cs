using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Inventory;

namespace Amwag.PrinterSetup.Core.Setup;

internal enum DriverState { Installed, Outdated, Missing }

internal sealed record DriverCheck(DriverState State, PrinterDriverInfo? Driver, string Detail)
{
    public bool IsInstalled => State == DriverState.Installed;
}

/// <summary>Which installed drivers count for a model (accepted name, print environment, minimum version).</summary>
internal static class DriverRules
{
    private static readonly string[] MicrosoftClassDrivers =
        ["Microsoft IPP Class Driver", "Microsoft PCL6 Class Driver", "Microsoft PS Class Driver", "Generic / Text Only"];

    private static readonly string[] VendorNamePrefixes = ["TA ", "UTAX", "Kyocera", "Triumph-Adler"];

    /// <summary>CM_PROB_NEED_RESTART: the device's new driver takes effect once Windows is restarted.</summary>
    public const int NeedRestartProblemCode = 14;

    public static bool IsAccepted(ModelDefinition model, string? driverName) =>
        driverName is not null && model.AcceptedDrivers.Any(d => string.Equals(d.Name, driverName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether the accepted driver offers Windows forms as papers (<see cref="AcceptedDriver.ListsWindowsForms"/>); true for other names.</summary>
    public static bool ListsWindowsForms(ModelDefinition model, string? driverName) =>
        model.AcceptedDrivers.FirstOrDefault(d => string.Equals(d.Name, driverName, StringComparison.OrdinalIgnoreCase))?.ListsWindowsForms ?? true;

    /// <summary>
    /// Whether Windows linked the device itself to an accepted driver of the model: no problem code, or only a restart pending
    /// (<see cref="IsRestartPending"/>), which installing the driver again would not change.
    /// </summary>
    public static bool IsBound(PnpPrinterDevice device, ModelDefinition model) =>
        device.ProblemCode is null or NeedRestartProblemCode && IsAccepted(model, device.DriverDescription);

    /// <summary>The device is linked to an accepted driver of the model that takes effect once Windows is restarted.</summary>
    public static bool IsRestartPending(PnpPrinterDevice device, ModelDefinition model) =>
        device.ProblemCode == NeedRestartProblemCode && IsAccepted(model, device.DriverDescription);

    /// <summary>
    /// The driver a new or repaired queue uses: the preferred driver when it is installed, else another accepted driver that is
    /// already installed and lists Windows forms (a working approved driver is reused rather than installing a second one; one
    /// that ignores the ticket form is not, nor <paramref name="excluded"/>: a queue's driver that is being replaced), else the
    /// preferred driver, which the plan installs.
    /// </summary>
    public static string DriverForQueue(ModelDefinition model, IReadOnlyList<PrinterDriverInfo> drivers, string environment, string? excluded = null)
    {
        if (Check(model, model.PreferredDriverName, drivers, environment).IsInstalled)
            return model.PreferredDriverName;
        return model.AcceptedDrivers.FirstOrDefault(d => d.ListsWindowsForms && !string.Equals(d.Name, excluded, StringComparison.OrdinalIgnoreCase)
                && Check(model, d.Name, drivers, environment).IsInstalled)?.Name
            ?? model.PreferredDriverName;
    }

    /// <summary>Whether the driver is installed for <paramref name="environment"/> and meets the model's minimum version for it.</summary>
    public static DriverCheck Check(ModelDefinition model, string driverName, IReadOnlyList<PrinterDriverInfo> drivers, string environment)
    {
        // An empty environment means the driver list did not say; it lists the native environment only.
        var driver = drivers.FirstOrDefault(d =>
            string.Equals(d.Name, driverName, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrEmpty(d.Environment) || string.Equals(d.Environment, environment, StringComparison.OrdinalIgnoreCase)));
        if (driver is null)
            return new DriverCheck(DriverState.Missing, null, $"Driver '{driverName}' is not installed for {environment}.");

        var accepted = model.AcceptedDrivers.FirstOrDefault(d => string.Equals(d.Name, driverName, StringComparison.OrdinalIgnoreCase));
        if (accepted?.MinVersion is { } text && Version.TryParse(text, out var minimum))
        {
            if (driver.Version is null)
                return new DriverCheck(DriverState.Outdated, driver, $"The version of driver '{driver.Name}' could not be read; at least {text} is required.");
            if (Normalize(driver.Version) < Normalize(minimum))
                return new DriverCheck(DriverState.Outdated, driver, $"Driver '{driver.Name}' {driver.Version} is older than the required {text}.");
        }

        return new DriverCheck(DriverState.Installed, driver, $"Driver '{driver.Name}' {driver.Version?.ToString() ?? "(version unknown)"} is installed.");
    }

    /// <summary>A TA, UTAX or Kyocera driver name (by naming convention, accepted or not).</summary>
    public static bool IsVendorName(string? name) =>
        !string.IsNullOrEmpty(name) &&
        (VendorNamePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) || name.Contains(" KX", StringComparison.OrdinalIgnoreCase));

    public static bool IsMicrosoftClassDriver(string? name) => MicrosoftClassDrivers.Contains(name, StringComparer.OrdinalIgnoreCase);

    // Version compares missing components as lower than zero; "8.4" must equal "8.4.0.0".
    private static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));
}
