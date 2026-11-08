using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Logging;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Windows.Devices;
using Amwag.PrinterSetup.Windows.Drivers;
using Amwag.PrinterSetup.Windows.Network;
using Amwag.PrinterSetup.Windows.Platform;
using Amwag.PrinterSetup.Windows.Security;
using Amwag.PrinterSetup.Windows.Spooler;

namespace Amwag.PrinterSetup.App.Hosting;

/// <summary>A command-line value that only the loaded configuration can reject, such as an unknown --model.</summary>
internal sealed class StartupArgumentException(string message) : Exception(message);

/// <summary>Builds what every role (window, silent, elevated worker) needs from the command line.</summary>
internal static class AppBootstrap
{
    /// <summary>Folder of the executable; AppContext.BaseDirectory stays correct for single-file publishing.</summary>
    public static string ExecutableDirectory { get; } = AppContext.BaseDirectory;

    public static string AppVersion { get; } =
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(Program).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    /// <summary>Correlates one run's log entries and reports; the elevated worker reuses the caller's id.</summary>
    public static string NewSessionId() => RandomNumberGenerator.GetHexString(12, lowercase: true);

    public static string RoleName(AppRole role) => role switch
    {
        AppRole.Interactive => "interactive",
        AppRole.Silent => "silent",
        AppRole.Worker => "worker",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };

    public static JsonLinesLog OpenLog(AppRole role, string sessionId) => JsonLinesLog.Open(RoleName(role), sessionId);

    public static void LogStartup(ILog log, CommandLineOptions options, IReadOnlyList<string> args) =>
        log.Info("app.start", "AMWAG Printer Setup started.", new Dictionary<string, object?>
        {
            ["version"] = AppVersion,
            ["role"] = RoleName(options.Role),
            ["mode"] = CommandLineParser.ModeName(options.Mode),
            ["arguments"] = args,
            ["elevated"] = Environment.IsPrivilegedProcess,
            ["os"] = RuntimeInformation.OSDescription,
            ["osVersion"] = Environment.OSVersion.Version.ToString(),
            ["osArchitecture"] = RuntimeInformation.OSArchitecture.ToString(),
            ["processArchitecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["machine"] = Environment.MachineName,
            ["user"] = Environment.UserName,
            ["executable"] = Environment.ProcessPath,
        });

    /// <summary>
    /// Loads and validates the configuration (throws <see cref="ConfigurationException"/>), checks configuration-dependent
    /// arguments (throws <see cref="StartupArgumentException"/>) and wires the Windows services.
    /// </summary>
    public static FlowEnvironment Build(CommandLineOptions options, ILog log) => Build(options, log, out _);

    /// <summary>
    /// <see cref="Build(CommandLineOptions, ILog)"/>, also returning the <paramref name="pin"/> of the exact configuration and
    /// branch bytes loaded, which the elevated worker must find again.
    /// </summary>
    public static FlowEnvironment Build(CommandLineOptions options, ILog log, out ConfigurationPin pin)
    {
        LoadedConfiguration loaded = Load(options, log);
        pin = ConfigurationPin.Of(loaded);
        return Build(options, loaded, log);
    }

    /// <summary>Loads and validates the configuration for this command line (throws <see cref="ConfigurationException"/>).</summary>
    public static LoadedConfiguration Load(CommandLineOptions options, ILog log) =>
        LoadConfiguration(options.ConfigPath, ExecutableDirectory, AppPaths.DataRoot, log);

    /// <summary>
    /// The environment for an already loaded configuration: checks configuration-dependent arguments (throws
    /// <see cref="StartupArgumentException"/>) and wires the Windows services.
    /// </summary>
    public static FlowEnvironment Build(CommandLineOptions options, LoadedConfiguration loaded, ILog log)
    {
        if (options.ModelId is { } modelId && loaded.Config.FindModel(modelId) is null)
        {
            string known = loaded.Config.Models.Count == 0 ? "none" : string.Join(", ", loaded.Config.Models.Select(m => m.Id));
            throw new StartupArgumentException($"Unknown --model '{modelId}'. Models in the configuration: {known}.");
        }

        var setupOptions = new SetupOptions
        {
            Mode = options.Mode,
            AllowUnconfirmedDimensions = options.AllowUnconfirmedDimensions,
            PreferredModelId = options.ModelId,
            PreferredAddress = options.Address,
            AppVersion = AppVersion,
            ConfigSource = loaded.Source,
        };
        var services = new SetupServices
        {
            Spooler = new WindowsPrintSpooler(log),
            Devices = new WindowsDeviceInventory(log),
            Platform = new WindowsPlatformInspector(),
            Network = new NetworkPrinterProbe(log),
            Packages = new DriverPackageProvider(loaded.Config.DriverSource, ExecutableDirectory, log),
            Installer = new DriverInstaller(log),
            Log = log,
            Clock = SystemClock.Instance,
        };

        return new FlowEnvironment(loaded.Config, loaded.Branch, setupOptions, services);
    }

    /// <summary>
    /// Loads the configuration the same way in every role (window, silent run, elevated worker), so the worker finds what the
    /// window assessed: amwag-printer-config.json and branch.json next to the program, and branch.json in
    /// <paramref name="programDataDirectory"/>, are only used when only administrators can change them
    /// (<see cref="AdministratorControlledFile"/>). Otherwise the configuration built into the program applies and the ignored
    /// file is named in the log. An explicit --config is the operator's own choice (the elevated worker never accepts one).
    /// </summary>
    internal static LoadedConfiguration LoadConfiguration(string? configPath, string executableDirectory, string programDataDirectory, ILog log)
    {
        LoadedConfiguration loaded = ConfigLoader.Load(
            configPath,
            executableDirectory,
            () => typeof(Program).Assembly.GetManifestResourceStream(ConfigLoader.ConfigFileName),
            programDataDirectory,
            AdministratorControlledFile.FindProblem);

        log.Info("config.loaded", "Configuration loaded.", new Dictionary<string, object?>
        {
            ["source"] = loaded.Source,
            ["configVersion"] = loaded.Config.ConfigVersion,
            ["schemaVersion"] = loaded.Config.SchemaVersion,
            ["dimensionsConfirmed"] = loaded.Config.Ticket.DimensionsConfirmed,
            ["configSha256"] = loaded.ConfigSha256,
            ["branchSource"] = loaded.BranchSource,
            ["branchSha256"] = loaded.BranchSha256,
            ["branchId"] = loaded.Branch?.BranchId,
            ["ignoredFiles"] = loaded.IgnoredFiles,
            ["warnings"] = loaded.Warnings,
        });
        foreach (string warning in loaded.Warnings)
            log.Warn("config.warning", warning);

        return loaded;
    }
}
