using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amwag.PrinterSetup.Core.Printing;
using Amwag.PrinterSetup.Core.Units;

namespace Amwag.PrinterSetup.Core.Configuration;

/// <param name="ConfigSha256">
/// SHA-256 (lowercase hexadecimal) of the exact configuration bytes used, or <see cref="ConfigLoader.EmbeddedSource"/> for the
/// copy embedded in the executable.
/// </param>
/// <param name="BranchSha256">SHA-256 (lowercase hexadecimal) of the exact branch file bytes used; null without a branch file.</param>
/// <param name="IgnoredFiles">Files that were present but not used because the trust check rejected them (named in the warnings).</param>
public sealed record LoadedConfiguration(
    DeploymentConfig Config,
    string Source,
    BranchOverride? Branch,
    string? BranchSource,
    IReadOnlyList<string> Warnings,
    string ConfigSha256,
    string? BranchSha256,
    IReadOnlyList<string> IgnoredFiles);

/// <summary>
/// Why a configuration or branch file may not be used (for example because standard users can change it), or null when it may.
/// Gets the file opened for reading and sharing only reads; the loader then reads the bytes through that same handle, so the
/// file cannot be swapped or changed between the check and the read.
/// </summary>
public delegate string? FileTrustCheck(FileStream file);

/// <summary>The configuration or branch file is missing, malformed or violates the schema rules.</summary>
public sealed class ConfigurationException : Exception
{
    public ConfigurationException(string source, IReadOnlyList<string> errors)
        : base($"Invalid configuration ({source}): {string.Join("; ", errors)}")
    {
        Source = source;
        Errors = errors;
    }

    public new string Source { get; }
    public IReadOnlyList<string> Errors { get; }
}

public static class ConfigLoader
{
    public const string ConfigFileName = "amwag-printer-config.json";
    public const string BranchFileName = "branch.json";

    /// <summary>Source name reported when the configuration embedded in the executable is used.</summary>
    public const string EmbeddedSource = "embedded";

    private const string RequiredEnvironment = "Windows x64";
    private const int MaxQueueNameLength = 220;

    /// <summary>Official TA Triumph-Adler / UTAX / Kyocera download domains (subdomains included).</summary>
    private static readonly string[] OfficialDownloadDomains =
    [
        "triumph-adler.com", "triumph-adler.de", "triumph-adler.net",
        "utax.com", "utax.de", "utax.it",
        "kyoceradocumentsolutions.com",
    ];

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Loads the deployment configuration: <paramref name="explicitConfigPath"/> (must exist) &gt; file next to the executable &gt;
    /// embedded copy; then validates it. Loads an optional branch.json from the executable folder, else <paramref name="programDataDirectory"/>.
    /// With <paramref name="untrustedReason"/>, a file next to the executable or in <paramref name="programDataDirectory"/> is
    /// only used when the check accepts it; a rejected one is skipped (the next source applies) and named in the warnings and
    /// <see cref="LoadedConfiguration.IgnoredFiles"/>. An explicit path is the caller's own choice and is not checked.
    /// Throws <see cref="ConfigurationException"/> when invalid.
    /// </summary>
    public static LoadedConfiguration Load(
        string? explicitConfigPath,
        string executableDirectory,
        Func<Stream?> openEmbeddedConfig,
        string? programDataDirectory,
        FileTrustCheck? untrustedReason = null)
    {
        var rejected = new Rejections();
        var (config, source, configSha256) = ReadConfig(explicitConfigPath, executableDirectory, openEmbeddedConfig, untrustedReason, rejected);
        var (errors, warnings) = Check(config);
        if (errors.Count > 0)
            throw new ConfigurationException(source, errors);

        var (branch, branchSource, branchSha256) = ReadBranch(config, executableDirectory, programDataDirectory, untrustedReason, rejected);
        return new LoadedConfiguration(config, source, branch, branchSource, [.. rejected.Warnings, .. warnings], configSha256, branchSha256, rejected.Paths);
    }

    /// <summary>Schema and safety rules; returns an empty list when valid.</summary>
    public static IReadOnlyList<string> Validate(DeploymentConfig config) => Check(config).Errors;

    /// <summary>Errors (the configuration is unusable) and warnings (usable, but IT should know).</summary>
    internal static (List<string> Errors, List<string> Warnings) Check(DeploymentConfig config)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (config.SchemaVersion != 1)
            errors.Add(Inv($"schemaVersion must be 1 (found {config.SchemaVersion})."));

        if (config.Ticket is null)
            errors.Add("ticket is required.");
        else
            CheckTicket(config.Ticket, errors, warnings);

        if (config.PrintPreferences is null)
            errors.Add("printPreferences must not be null.");
        else
            CheckPreferences(config.PrintPreferences, errors, warnings);

        if (config.Connection is null)
            errors.Add("connection must not be null.");
        else
            CheckConnection(config.Connection, errors);

        if (config.DriverSource is null)
            errors.Add("driverSource must not be null.");
        else if (config.DriverSource.OfflineFolder is null)
            errors.Add("driverSource.offlineFolder must not be null.");
        else if (config.DriverSource.DownloadTimeoutSeconds <= 0)
            errors.Add("driverSource.downloadTimeoutSeconds must be greater than zero.");

        if (config.DriverPackages is null || config.DriverPackages.Count == 0)
            errors.Add("driverPackages must contain at least one approved driver package.");
        else
            CheckPackages(config.DriverPackages, errors);

        if (config.Models is null || config.Models.Count == 0)
            errors.Add("models must contain at least one supported printer model.");
        else
            CheckModels(config, errors, warnings);

        return (errors, warnings);
    }

    private static void CheckTicket(TicketPaperConfig ticket, List<string> errors, List<string> warnings)
    {
        string name = ticket.FormName ?? "";
        if (string.IsNullOrWhiteSpace(name))
        {
            errors.Add("ticket.formName must not be empty.");
        }
        else
        {
            if (name.Length > DevMode.MaxNameLength)
                errors.Add(Inv($"ticket.formName '{name}' has {name.Length} characters; at most {DevMode.MaxNameLength} fit DEVMODE.dmFormName."));
            if (name.Any(char.IsControl))
                errors.Add("ticket.formName must not contain control characters.");
        }

        if (!Enum.IsDefined(ticket.Unit))
        {
            errors.Add("ticket.unit must be \"in\" or \"mm\".");
        }
        else
        {
            CheckTicketLength("ticket.width", ticket.Width, ticket.Unit, errors, warnings);
            CheckTicketLength("ticket.height", ticket.Height, ticket.Unit, errors, warnings);
        }

        if (!ticket.DimensionsConfirmed)
        {
            warnings.Add("Ticket dimensions are not confirmed (ticket.dimensionsConfirmed is false); Setup and Repair are blocked until IT confirms them.");
            return;
        }

        if (string.IsNullOrWhiteSpace(ticket.ConfirmedBy))
            errors.Add("ticket.confirmedBy is required when ticket.dimensionsConfirmed is true.");
        if (!DateOnly.TryParseExact(ticket.ConfirmedOn, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            errors.Add("ticket.confirmedOn must be a date in yyyy-MM-dd format when ticket.dimensionsConfirmed is true.");
    }

    private static void CheckTicketLength(string name, decimal value, LengthUnit unit, List<string> errors, List<string> warnings)
    {
        string unitText = unit == LengthUnit.Inch ? "in" : "mm";
        if (value <= 0)
        {
            errors.Add($"{name} must be greater than zero.");
            return;
        }

        decimal millimeters = unit == LengthUnit.Inch ? value * 25.4m : value;
        if (millimeters is < 10 or > 1000)
        {
            errors.Add(Inv($"{name} {value} {unitText} is {millimeters} mm; it must be between 10 and 1000 mm."));
            return;
        }

        int micrometers = PaperDimensions.ToMicrometers(value, unit, out decimal residual);
        if (residual != 0)
            warnings.Add(Inv($"{name} {value} {unitText} is not a whole number of micrometers; it is rounded to {micrometers} µm (residual {residual} µm)."));
    }

    private static void CheckPreferences(PrintPreferencesConfig preferences, List<string> errors, List<string> warnings)
    {
        if (!Enum.IsDefined(preferences.Orientation))
            errors.Add("printPreferences.orientation must be \"portrait\" or \"landscape\".");
        if (!Enum.IsDefined(preferences.Duplex))
            errors.Add("printPreferences.duplex must be \"simplex\", \"longEdge\" or \"shortEdge\".");
        if (!Enum.IsDefined(preferences.PaperSource))
            errors.Add("printPreferences.paperSource must be \"driverDefault\", \"preferMultipurposeTray\" or \"requireMultipurposeTray\".");

        if (preferences.ScalePercent is < 1 or > 400)
            errors.Add(Inv($"printPreferences.scalePercent must be between 1 and 400 (found {preferences.ScalePercent})."));
        else if (preferences.ScalePercent != 100)
            warnings.Add(Inv($"printPreferences.scalePercent is {preferences.ScalePercent}; tickets will not print at 100 %."));
    }

    private static void CheckConnection(ConnectionConfig connection, List<string> errors)
    {
        CheckQueueName("connection.newQueueName", connection.NewQueueName, errors);
        if (connection.RawPortNumber is < 1 or > 65535)
            errors.Add(Inv($"connection.rawPortNumber must be between 1 and 65535 (found {connection.RawPortNumber})."));
        if (connection.SnmpCommunity is null)
            errors.Add("connection.snmpCommunity must not be null.");
        if (connection.MaxNetworkProbes < 1)
            errors.Add("connection.maxNetworkProbes must be at least 1.");
        if (connection.NetworkTimeoutMilliseconds <= 0)
            errors.Add("connection.networkTimeoutMilliseconds must be greater than zero.");
        if (connection.UsbQueueWaitSeconds <= 0)
            errors.Add("connection.usbQueueWaitSeconds must be greater than zero.");
    }

    private static void CheckQueueName(string name, string? value, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
            errors.Add($"{name} must not be empty.");
        else if (value.Contains('\\') || value.Contains(','))
            errors.Add($"{name} '{value}' must not contain '\\' or ','.");
        else if (value.Length > MaxQueueNameLength)
            errors.Add(Inv($"{name} must be at most {MaxQueueNameLength} characters (found {value.Length})."));
    }

    private static void CheckPackages(IReadOnlyList<DriverPackageDefinition> packages, List<string> errors)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < packages.Count; i++)
        {
            var package = packages[i];
            if (package is null)
            {
                errors.Add(Inv($"driverPackages[{i}] must not be null."));
                continue;
            }

            string label = Inv($"driverPackages[{i}] ('{package.Id}')");
            if (string.IsNullOrWhiteSpace(package.Id))
                errors.Add(Inv($"driverPackages[{i}].id must not be empty."));
            else if (!ids.Add(package.Id))
                errors.Add($"{label}: id is not unique.");

            if (package.Sha256 is not { Length: 64 } sha || !sha.All(char.IsAsciiHexDigit))
                errors.Add($"{label}: sha256 must be 64 hexadecimal characters.");
            if (package.SizeBytes <= 0)
                errors.Add($"{label}: sizeBytes must be greater than zero.");
            if (CheckDownloadUrl(package.DownloadUrl) is { } urlProblem)
                errors.Add($"{label}: downloadUrl {urlProblem}");
            if (!IsPlainFileName(package.FileName))
                errors.Add($"{label}: fileName must be a file name without path separators.");
            if (!IsSafeRelativePath(package.InfPath))
                errors.Add($"{label}: infPath must be a relative path inside the archive without '..'.");
            if (package.CatalogPath is not null && !IsSafeRelativePath(package.CatalogPath))
                errors.Add($"{label}: catalogPath must be a relative path inside the archive without '..'.");
            if (package.TrustedSigners is null || package.TrustedSigners.Count == 0 || package.TrustedSigners.Any(string.IsNullOrWhiteSpace))
                errors.Add($"{label}: trustedSigners must list at least one signer.");
            if (package.SupportedOs is null)
                errors.Add($"{label}: supportedOs must not be null.");
            if (!string.Equals(package.Environment, RequiredEnvironment, StringComparison.OrdinalIgnoreCase))
                errors.Add($"{label}: environment must be \"{RequiredEnvironment}\" (found \"{package.Environment}\").");
        }
    }

    private static void CheckModels(DeploymentConfig config, List<string> errors, List<string> warnings)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hardwareIdOwners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < config.Models.Count; i++)
        {
            var model = config.Models[i];
            if (model is null)
            {
                errors.Add(Inv($"models[{i}] must not be null."));
                continue;
            }

            string label = Inv($"models[{i}] ('{model.Id}')");
            if (string.IsNullOrWhiteSpace(model.Id))
                errors.Add(Inv($"models[{i}].id must not be empty."));
            else if (!ids.Add(model.Id))
                errors.Add($"{label}: id is not unique.");
            if (string.IsNullOrWhiteSpace(model.DisplayName))
                errors.Add($"{label}: displayName must not be empty.");

            if (model.HardwareIds is null || model.Ieee1284Ids is null || model.NetworkModelNames is null || model.AcceptedDrivers is null || model.TicketPaperSource is null)
            {
                errors.Add($"{label}: hardwareIds, ieee1284Ids, networkModelNames, acceptedDrivers and ticketPaperSource must not be null.");
                continue;
            }

            CheckModelIdentity(model, label, hardwareIdOwners, errors);
            CheckModelDrivers(config, model, label, errors, warnings);

            var source = model.TicketPaperSource;
            if (source.BinIds is null || source.BinNames is null)
                errors.Add($"{label}: ticketPaperSource.binIds and binNames must not be null.");
            else if (source.BinIds.Count == 0 && source.BinNames.Count == 0)
                warnings.Add($"{label}: ticketPaperSource lists no bin ids or names; the multipurpose tray cannot be selected.");

            CheckRange($"{label}: multipurposeTrayRange", model.MultipurposeTrayRange, errors);
            CheckRange($"{label}: cassetteRange", model.CassetteRange, errors);
        }
    }

    private static void CheckModelIdentity(ModelDefinition model, string label, Dictionary<string, string> hardwareIdOwners, List<string> errors)
    {
        if (model.HardwareIds.Count == 0 && model.Ieee1284Ids.Count == 0)
            errors.Add($"{label}: at least one hardwareId or ieee1284Id is required.");

        foreach (string? hardwareId in model.HardwareIds)
        {
            if (string.IsNullOrWhiteSpace(hardwareId))
            {
                errors.Add($"{label}: hardwareIds must not contain empty entries.");
                continue;
            }

            if (!hardwareIdOwners.TryAdd(hardwareId, model.Id) && !string.Equals(hardwareIdOwners[hardwareId], model.Id, StringComparison.OrdinalIgnoreCase))
                errors.Add($"Hardware ID '{hardwareId}' appears in models '{hardwareIdOwners[hardwareId]}' and '{model.Id}'.");
        }

        if (model.Ieee1284Ids.Any(id => id is null || string.IsNullOrWhiteSpace(id.Manufacturer) || string.IsNullOrWhiteSpace(id.Model)))
            errors.Add($"{label}: every ieee1284Id needs a manufacturer and a model.");
        if (model.NetworkModelNames.Any(string.IsNullOrWhiteSpace))
            errors.Add($"{label}: networkModelNames must not contain empty entries.");
    }

    private static void CheckModelDrivers(DeploymentConfig config, ModelDefinition model, string label, List<string> errors, List<string> warnings)
    {
        if (model.AcceptedDrivers.Count == 0)
            errors.Add($"{label}: acceptedDrivers must not be empty.");

        foreach (var accepted in model.AcceptedDrivers)
        {
            if (accepted is null || string.IsNullOrWhiteSpace(accepted.Name))
                errors.Add($"{label}: every accepted driver needs a name.");
            else if (accepted.MinVersion is not null && !Version.TryParse(accepted.MinVersion, out _))
                errors.Add($"{label}: minVersion '{accepted.MinVersion}' of '{accepted.Name}' is not a version (a.b.c.d).");
        }

        var preferred = model.AcceptedDrivers.FirstOrDefault(d => d is not null && string.Equals(d.Name, model.PreferredDriverName, StringComparison.OrdinalIgnoreCase));
        if (preferred is null)
            errors.Add($"{label}: preferredDriverName '{model.PreferredDriverName}' must be one of acceptedDrivers.");
        else if (!preferred.ListsWindowsForms)
            errors.Add($"{label}: the preferred driver '{preferred.Name}' must list Windows forms (listsWindowsForms), so that the ticket form becomes one of its papers.");

        var package = config.DriverPackages?.FirstOrDefault(p => p is not null && string.Equals(p.Id, model.DriverPackageId, StringComparison.OrdinalIgnoreCase));
        if (package is null)
            errors.Add($"{label}: driverPackageId '{model.DriverPackageId}' does not name a driver package.");
        else if (!package.Approved)
            warnings.Add($"{label}: driver package '{package.Id}' is not approved; the driver will not be installed until IT approves it.");
    }

    private static void CheckRange(string label, PaperRangeMm? range, List<string> errors)
    {
        if (range is null)
            return;
        if (range.MinWidth <= 0 || range.MinHeight <= 0 || range.MinWidth > range.MaxWidth || range.MinHeight > range.MaxHeight)
            errors.Add($"{label} must have positive minimums not larger than its maximums.");
    }

    /// <summary>Null when the URL is an https URL on an official domain; otherwise the problem.</summary>
    private static string? CheckDownloadUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "must be an absolute URL.";
        if (uri.Scheme != Uri.UriSchemeHttps)
            return "must use https.";
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return "must not contain user information.";

        string host = uri.IdnHost.TrimEnd('.');
        bool official = OfficialDownloadDomains.Any(domain =>
            string.Equals(host, domain, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
        return official ? null : $"host '{uri.Host}' is not an official TA Triumph-Adler, UTAX or Kyocera download domain.";
    }

    private static bool IsPlainFileName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name is not ("." or "..") &&
        name.IndexOfAny(['/', '\\', ':']) < 0 &&
        !name.Any(char.IsControl);

    private static bool IsSafeRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl))
            return false;
        if (path.StartsWith('/') || path.StartsWith('\\') || path.Contains(':'))
            return false;
        return path.Split('/', '\\').All(segment => segment.Length > 0 && segment != "..");
    }

    private static (DeploymentConfig Config, string Source, string Sha256) ReadConfig(
        string? explicitConfigPath, string executableDirectory, Func<Stream?> openEmbeddedConfig, FileTrustCheck? untrustedReason, Rejections rejected)
    {
        if (!string.IsNullOrWhiteSpace(explicitConfigPath))
        {
            if (!File.Exists(explicitConfigPath))
                throw new ConfigurationException(explicitConfigPath, [$"The configuration file '{explicitConfigPath}' does not exist."]);
            byte[] chosen = ReadAccepted(explicitConfigPath, untrustedReason: null, out _)!;
            return (Deserialize<DeploymentConfig>(Decode(chosen), explicitConfigPath), explicitConfigPath, Sha256(chosen));
        }

        string besideExecutable = Path.Combine(executableDirectory, ConfigFileName);
        if (File.Exists(besideExecutable))
        {
            if (ReadAccepted(besideExecutable, untrustedReason, out string? reason) is { } bytes)
                return (Deserialize<DeploymentConfig>(Decode(bytes), besideExecutable), besideExecutable, Sha256(bytes));
            rejected.Add(besideExecutable, reason!, "the configuration built into the program is used instead");
        }

        using var stream = openEmbeddedConfig()
            ?? throw new ConfigurationException(EmbeddedSource, ["There is no usable configuration file next to the executable and no embedded configuration."]);
        return (Deserialize<DeploymentConfig>(ReadAll(stream, EmbeddedSource), EmbeddedSource), EmbeddedSource, EmbeddedSource);
    }

    private static (BranchOverride? Branch, string? Source, string? Sha256) ReadBranch(
        DeploymentConfig config, string executableDirectory, string? programDataDirectory, FileTrustCheck? untrustedReason, Rejections rejected)
    {
        string?[] candidates =
        [
            Path.Combine(executableDirectory, BranchFileName),
            programDataDirectory is null ? null : Path.Combine(programDataDirectory, BranchFileName),
        ];
        foreach (string path in candidates.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path))
                continue;
            if (ReadAccepted(path, untrustedReason, out string? reason) is { } bytes)
                return (ParseBranch(config, Decode(bytes), path), path, Sha256(bytes));
            rejected.Add(path, reason!, "its branch settings are not applied");
        }

        return (null, null, null);
    }

    private static BranchOverride ParseBranch(DeploymentConfig config, string json, string path)
    {
        using (var document = ParseDocument(json, path))
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ConfigurationException(path, ["The branch file must contain a JSON object."]);
            if (document.RootElement.EnumerateObject().Any(p => string.Equals(p.Name, "ticket", StringComparison.OrdinalIgnoreCase)))
                throw new ConfigurationException(path, ["Ticket dimensions cannot be overridden per branch."]);
        }

        var parsed = Deserialize<BranchOverride>(json, path);
        var errors = new List<string>();
        string? printerAddress = Clean(parsed.PrinterAddress);
        if (printerAddress is not null)
        {
            if (PrinterAddress.TryNormalize(printerAddress, out string? normalized, out string? problem))
                printerAddress = normalized;
            else
                errors.Add($"printerAddress '{printerAddress}' is neither an IP address nor a DNS host name: {problem}");
        }

        var branch = new BranchOverride
        {
            BranchId = Clean(parsed.BranchId),
            BranchName = Clean(parsed.BranchName),
            PrinterAddress = printerAddress,
            ModelId = Clean(parsed.ModelId),
            QueueName = Clean(parsed.QueueName),
        };

        if (branch.ModelId is { } modelId && config.FindModel(modelId) is null)
            errors.Add($"modelId '{modelId}' is not a model of the deployment configuration.");
        if (branch.QueueName is not null)
            CheckQueueName("queueName", branch.QueueName, errors);
        if (errors.Count > 0)
            throw new ConfigurationException(path, errors);

        return branch;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// The file's bytes, read through the same handle <paramref name="untrustedReason"/> checked (opened sharing only reads, so
    /// nobody can write, replace or delete the file in between), or null with the <paramref name="rejection"/> when the check
    /// rejects the file.
    /// </summary>
    private static byte[]? ReadAccepted(string path, FileTrustCheck? untrustedReason, out string? rejection)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            rejection = untrustedReason?.Invoke(stream);
            if (rejection is not null)
                return null;

            stream.Position = 0;
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            return bytes.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ConfigurationException(path, [$"The file could not be read: {ex.Message}"]);
        }
    }

    /// <summary>Text as <see cref="File.ReadAllText(string)"/> reads it: UTF-8 unless a byte order mark says otherwise.</summary>
    private static string Decode(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string ReadAll(Stream stream, string source)
    {
        try
        {
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException ex)
        {
            throw new ConfigurationException(source, [$"The configuration could not be read: {ex.Message}"]);
        }
    }

    private static JsonDocument ParseDocument(string json, string source)
    {
        try
        {
            return JsonDocument.Parse(json, DocumentOptions);
        }
        catch (JsonException ex)
        {
            throw new ConfigurationException(source, [$"Malformed JSON: {ex.Message}"]);
        }
    }

    private static T Deserialize<T>(string json, string source) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, ConfigJson.Options)
                ?? throw new ConfigurationException(source, ["The file contains JSON null."]);
        }
        catch (JsonException ex)
        {
            throw new ConfigurationException(source, [$"Malformed JSON: {ex.Message}"]);
        }
    }

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);

    /// <summary>Files the trust check rejected, with the warning that names each one and what applies instead.</summary>
    private sealed class Rejections
    {
        public List<string> Paths { get; } = [];

        public List<string> Warnings { get; } = [];

        public void Add(string path, string reason, string consequence)
        {
            Paths.Add(path);
            Warnings.Add($"'{path}' is not used because {reason}; {consequence}.");
        }
    }
}
