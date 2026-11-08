using System.Globalization;
using System.Text.RegularExpressions;
using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.App.Hosting;

/// <summary>
/// Parses the command line. Switches start with "--" or "/", names are case-insensitive, and a value follows as the next
/// argument or after "=". Problems are collected in <see cref="CommandLineOptions.Errors"/>, never thrown. The internal
/// worker switches are validated strictly because the worker runs elevated.
/// </summary>
internal static partial class CommandLineParser
{
    public const string Usage = """
        AMWAG Printer Setup.exe [options]

          --mode setup|repair|diagnostics  Setup (default) installs and configures the ticket printer; repair fixes
                                           existing printer queues; diagnostics only reports and changes nothing.
          --address <ip|host>              Network printer to identify (IPv4, IPv6 or host name).
          --model <id>                     Preferred model id from the configuration, for example P-4030DN.
          --confirm-model                  With --address and --model: the printer at that address is that model,
                                           even when it cannot report its identity.
          --lang en|ar                     Window language.
          --report <path>                  Silent mode: text report path (a .json copy is written next to it).
          --config <path>                  Configuration file to use instead of the one next to the program.
          --allow-unconfirmed-dimensions   IT testing only: run although the ticket dimensions are not confirmed.
          --silent                         Run without windows; the exit code tells the result.
          --help                           Show this help.

        Exit codes: 0 ready, 1 ready with warnings, 2 not ready, 3 no supported printer, 4 (setup and repair)
        several printers or a model to confirm: choose with --address/--model (they cannot choose between two
        connected USB printers of the same model), confirm with --confirm-model, 5 administrator rights required
        or declined, 6 configuration invalid or ticket dimensions unconfirmed, 7 print spooler unavailable,
        8 blocked, 9 cancelled, 10 internal error, 11 invalid arguments. Diagnostics never returns 4: several
        printers without a known ticket printer give 1 if one is ready, else 2; a model to confirm gives 2.
        """;

    private const int MaxTargetKeyLength = 512;

    private enum SwitchScope { Public, PublicAndWorker, WorkerOnly }

    private sealed record SwitchSpec(string Name, bool TakesValue, SwitchScope Scope);

    private static readonly Dictionary<string, SwitchSpec> Switches = new SwitchSpec[]
    {
        new("mode", true, SwitchScope.PublicAndWorker),
        new("address", true, SwitchScope.PublicAndWorker),
        new("model", true, SwitchScope.PublicAndWorker),
        new("confirm-model", false, SwitchScope.Public),
        new("lang", true, SwitchScope.Public),
        new("report", true, SwitchScope.Public),
        // Not for the worker: an elevated process must not load a configuration chosen by its non-elevated caller.
        new("config", true, SwitchScope.Public),
        new("allow-unconfirmed-dimensions", false, SwitchScope.PublicAndWorker),
        new("silent", false, SwitchScope.Public),
        new("help", false, SwitchScope.Public),
        new("worker", false, SwitchScope.WorkerOnly),
        new("pipe", true, SwitchScope.WorkerOnly),
        // The window's process id: the worker reports only to a pipe server in that process.
        new("window-pid", true, SwitchScope.WorkerOnly),
        new("session", true, SwitchScope.WorkerOnly),
        new("target", true, SwitchScope.WorkerOnly),
        new("model-asserted", false, SwitchScope.WorkerOnly),
        // The window's ConfigurationPin: the worker refuses to apply anything when it loads other bytes.
        new("config-sha256", true, SwitchScope.WorkerOnly),
        new("branch-sha256", true, SwitchScope.WorkerOnly),
    }.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

    public static CommandLineOptions Parse(IReadOnlyList<string> args) => Parse(args, out _);

    /// <summary>
    /// <see cref="Parse(IReadOnlyList{string})"/>, also returning the window's <paramref name="workerPin"/> from a worker command
    /// line (null when it has no valid --config-sha256 and --branch-sha256).
    /// </summary>
    public static CommandLineOptions Parse(IReadOnlyList<string> args, out ConfigurationPin? workerPin)
    {
        workerPin = null;
        var errors = new List<string>();
        var given = ReadSwitches(args, errors);

        bool worker = given.ContainsKey("worker");
        foreach (string name in given.Keys)
        {
            SwitchScope scope = Switches[name].Scope;
            if (worker && scope == SwitchScope.Public)
                errors.Add($"--{name} cannot be used with --worker.");
            else if (!worker && scope == SwitchScope.WorkerOnly)
                errors.Add($"--{name} is internal and only valid with --worker.");
        }

        SetupMode mode = SetupMode.Setup;
        if (given.TryGetValue("mode", out string? modeText) && !TryParseMode(modeText!, out mode))
            errors.Add($"Invalid --mode '{modeText}': expected setup, repair or diagnostics.");

        string? address = null;
        if (given.TryGetValue("address", out string? addressText))
        {
            if (PrinterAddress.TryNormalize(addressText, out string? normalizedAddress, out string? addressProblem))
                address = normalizedAddress;
            else
                errors.Add($"Invalid --address '{addressText}': expected an IPv4 or IPv6 address or a host name ({addressProblem}).");
        }
        string? model = Check(given, "model", errors, IsValidModelId, "a model id such as P-4030DN");
        string? language = Check(given, "lang", errors, v => v.ToLowerInvariant() is "en" or "ar", "en or ar")?.ToLowerInvariant();
        string? reportPath = FullPath(given, "report", errors);
        string? configPath = FullPath(given, "config", errors);

        bool confirmModel = given.ContainsKey("confirm-model");
        if (confirmModel && (!given.ContainsKey("model") || !given.ContainsKey("address")))
            errors.Add("--confirm-model needs both --model and --address.");

        bool modelAsserted = given.ContainsKey("model-asserted");
        string? pipeName = null, sessionId = null, targetKey = null;
        int? windowProcessId = null;
        if (worker)
        {
            pipeName = Required(given, "pipe", errors, v => PipeNamePattern().IsMatch(v));
            windowProcessId = Required(given, "window-pid", errors, IsValidProcessId) is { } windowPid
                ? int.Parse(windowPid, NumberStyles.None, CultureInfo.InvariantCulture)
                : null;
            sessionId = Required(given, "session", errors, IsValidSessionId);
            targetKey = Required(given, "target", errors, IsValidTargetKey);
            if (!given.ContainsKey("mode"))
                errors.Add("--mode is required with --worker.");
            else if (mode == SetupMode.Diagnostics)
                errors.Add("--mode diagnostics cannot be used with --worker (diagnostics never changes the system).");
            if (modelAsserted && !given.ContainsKey("model"))
                errors.Add("--model-asserted needs --model.");

            string? configPin = Required(given, "config-sha256", errors, v => ConfigPinPattern().IsMatch(v));
            string? branchPin = Required(given, "branch-sha256", errors, v => BranchPinPattern().IsMatch(v));
            if (configPin is not null && branchPin is not null)
                workerPin = new ConfigurationPin(configPin, branchPin);
        }

        return new CommandLineOptions
        {
            Role = worker ? AppRole.Worker : given.ContainsKey("silent") ? AppRole.Silent : AppRole.Interactive,
            Mode = mode,
            Address = address,
            ModelId = model,
            ConfirmModel = confirmModel,
            Language = language,
            ReportPath = reportPath,
            ConfigPath = configPath,
            AllowUnconfirmedDimensions = given.ContainsKey("allow-unconfirmed-dimensions"),
            ShowHelp = given.ContainsKey("help"),
            PipeName = pipeName,
            SessionId = sessionId,
            TargetKey = targetKey,
            ModelAssertedByUser = modelAsserted,
            WindowProcessId = windowProcessId,
            Errors = errors,
        };
    }

    public static bool TryParseMode(string text, out SetupMode mode)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "setup":
                mode = SetupMode.Setup;
                return true;
            case "repair":
                mode = SetupMode.Repair;
                return true;
            case "diagnostics":
                mode = SetupMode.Diagnostics;
                return true;
            default:
                mode = SetupMode.Setup;
                return false;
        }
    }

    public static string ModeName(SetupMode mode) => mode switch
    {
        SetupMode.Setup => "setup",
        SetupMode.Repair => "repair",
        SetupMode.Diagnostics => "diagnostics",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    /// <summary>A printer address as <see cref="PrinterAddress"/> accepts it: dotted-quad IPv4, IPv6 (no port) or an RFC 1123 host name.</summary>
    public static bool IsValidAddress(string value) => PrinterAddress.IsValid(value);

    public static bool IsValidModelId(string value) => ModelIdPattern().IsMatch(value);

    public static bool IsValidSessionId(string value) => SessionIdPattern().IsMatch(value);

    /// <summary>"spooler:start", or a "usb:", "net:" or "queue:" key: at most 512 characters, no control characters.</summary>
    public static bool IsValidTargetKey(string key) =>
        key.Length <= MaxTargetKeyLength &&
        !key.Any(char.IsControl) &&
        (key == WorkerArguments.SpoolerStartTarget || HasValueAfter(key, "usb:") || HasValueAfter(key, "net:") || HasValueAfter(key, "queue:"));

    private static Dictionary<string, string?> ReadSwitches(IReadOnlyList<string> args, List<string> errors)
    {
        var given = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Count; i++)
        {
            string argument = args[i];
            if (!TrySplitSwitch(argument, out string name, out string? inlineValue))
            {
                errors.Add($"Unexpected argument '{argument}'.");
                continue;
            }
            if (!Switches.TryGetValue(name, out var spec))
            {
                errors.Add($"Unknown switch '{argument}'.");
                continue;
            }

            string? value = null;
            if (spec.TakesValue)
            {
                if (inlineValue is not null)
                    value = inlineValue;
                else if (i + 1 < args.Count && !IsSwitch(args[i + 1]))
                    value = args[++i];
                else
                {
                    errors.Add($"Missing value for --{spec.Name}.");
                    continue;
                }
            }
            else if (inlineValue is not null)
            {
                errors.Add($"--{spec.Name} does not take a value.");
                continue;
            }

            if (!given.TryAdd(spec.Name, value))
                errors.Add($"--{spec.Name} is given more than once.");
        }

        return given;
    }

    private static bool TrySplitSwitch(string argument, out string name, out string? value)
    {
        name = "";
        value = null;
        ReadOnlySpan<char> body;
        if (argument.StartsWith("--", StringComparison.Ordinal))
            body = argument.AsSpan(2);
        else if (argument.StartsWith('/'))
            body = argument.AsSpan(1);
        else
            return false;

        int separator = body.IndexOf('=');
        ReadOnlySpan<char> namePart = separator < 0 ? body : body[..separator];
        if (namePart.IsEmpty)
            return false;

        name = namePart is "?" ? "help" : namePart.ToString();
        value = separator < 0 ? null : body[(separator + 1)..].ToString();
        return true;
    }

    // "/x" is only a switch when x is one; otherwise it is a value (for example a root-relative path).
    private static bool IsSwitch(string argument) =>
        argument.StartsWith("--", StringComparison.Ordinal) ||
        (argument.StartsWith('/') && TrySplitSwitch(argument, out string name, out _) && Switches.ContainsKey(name));

    private static string? Check(Dictionary<string, string?> given, string name, List<string> errors, Func<string, bool> isValid, string expected)
    {
        if (!given.TryGetValue(name, out string? raw))
            return null;

        string value = raw!.Trim();
        if (isValid(value))
            return value;

        errors.Add($"Invalid --{name} '{raw}': expected {expected}.");
        return null;
    }

    private static string? Required(Dictionary<string, string?> given, string name, List<string> errors, Func<string, bool> isValid)
    {
        if (!given.TryGetValue(name, out string? value))
        {
            errors.Add($"--{name} is required with --worker.");
            return null;
        }
        if (isValid(value!))
            return value;

        errors.Add($"Invalid --{name} value.");
        return null;
    }

    private static string? FullPath(Dictionary<string, string?> given, string name, List<string> errors)
    {
        if (!given.TryGetValue(name, out string? raw))
            return null;

        string value = raw!.Trim();
        try
        {
            if (value.Length > 0 && !value.Any(char.IsControl) && value.IndexOfAny(Path.GetInvalidPathChars()) < 0)
                return Path.GetFullPath(value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Reported below.
        }

        errors.Add($"Invalid --{name} path '{raw}'.");
        return null;
    }

    private static bool HasValueAfter(string key, string prefix) =>
        key.StartsWith(prefix, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(key[prefix.Length..]);

    /// <summary>A process id as the broker writes it: a positive decimal number that fits an int, without sign, spaces or leading zeros.</summary>
    private static bool IsValidProcessId(string value) =>
        ProcessIdPattern().IsMatch(value) && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _);

    [GeneratedRegex(@"^AMWAG\.PrinterSetup\.[0-9a-f]{32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PipeNamePattern();

    [GeneratedRegex(@"^[1-9][0-9]{0,9}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ProcessIdPattern();

    [GeneratedRegex(@"^[0-9a-f]{12,32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SessionIdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._+ -]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ModelIdPattern();

    [GeneratedRegex(@"^(?:[0-9a-f]{64}|embedded)\z", RegexOptions.CultureInvariant)]
    private static partial Regex ConfigPinPattern();

    [GeneratedRegex(@"^(?:[0-9a-f]{64}|none)\z", RegexOptions.CultureInvariant)]
    private static partial Regex BranchPinPattern();
}
