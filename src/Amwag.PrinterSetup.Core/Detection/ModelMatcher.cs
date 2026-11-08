using System.Text;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.Core.Detection;

/// <summary>The outcome of identifying one device or network identity.</summary>
internal sealed record ModelMatch(ModelDefinition? Model, MatchConfidence Confidence, string Evidence)
{
    public static readonly ModelMatch None = new(null, MatchConfidence.None, "");

    /// <summary>The device's own ID that identified the model (used to bind the driver to the device).</summary>
    public string? MatchedId { get; init; }

    /// <summary>Models that matched at the same, strongest level; then none is chosen.</summary>
    public IReadOnlyList<ModelDefinition> AmbiguousModels { get; init; } = [];

    public bool IsAmbiguous => AmbiguousModels.Count > 1;
}

/// <summary>
/// Identifies supported models from Plug and Play IDs, SNMP identities and driver names. Pure (no I/O). A model is never
/// inferred from a partial name alone, and when several models match at the same level nothing matches.
/// </summary>
internal sealed class ModelMatcher
{
    private static readonly string[] ManufacturerTokens = ["TA", "Triumph-Adler", "UTAX", "Kyocera"];
    private static readonly string[] VendorHardwareIdPrefixes = ["UTAX", "KYOCERA", "TRIUMPH", "TA_"];

    private readonly IReadOnlyList<ModelDefinition> _models;

    public ModelMatcher(IReadOnlyList<ModelDefinition> models) => _models = models;

    /// <summary>Exact identification from hardware IDs (ranked first) or compatible IDs: an approved ID, or the 1284-derived ID with a matching checksum.</summary>
    public ModelMatch MatchDevice(PnpPrinterDevice device)
    {
        foreach (var ids in new[] { device.HardwareIds, device.CompatibleIds })
        {
            var found = new List<(ModelDefinition Model, string Id, string Rule)>();
            foreach (string id in ids)
            {
                foreach (var model in _models)
                {
                    if (found.Any(f => ReferenceEquals(f.Model, model)))
                        continue;
                    if (model.HardwareIds.Any(h => string.Equals(h, id, StringComparison.OrdinalIgnoreCase)))
                        found.Add((model, id, "equals an approved hardware ID"));
                    else if (model.Ieee1284Ids.FirstOrDefault(i => Ieee1284.MatchesHardwareId(id, i)) is { } identity)
                        found.Add((model, id, $"is the IEEE 1284 hardware ID (checksum verified) of MFG '{identity.Manufacturer}' MDL '{identity.Model}'"));
                }
            }

            if (found.Count == 1)
            {
                var (model, id, rule) = found[0];
                return new ModelMatch(model, MatchConfidence.Exact, $"Device {device.InstanceId}: '{id}' {rule} of {model.Id}.") { MatchedId = id };
            }
            if (found.Count > 1)
                return Ambiguous(found.Select(f => f.Model), $"Device {device.InstanceId}");
        }

        return ModelMatch.None;
    }

    /// <summary>
    /// SNMP identity: the 1284 MFG/MDL pair (Exact), else an approved network model name reported in a model-name field
    /// (Exact; see <see cref="ProvesModel"/>; the same name in any other text is only Probable), else a description naming
    /// the model Id as a whole token together with a manufacturer token (Probable). The administrator-assigned printer name
    /// (prtGeneralPrinterName) is never model evidence.
    /// </summary>
    public ModelMatch MatchNetwork(NetworkIdentity identity)
    {
        string address = identity.Address;
        if (Ieee1284.TryParse(identity.Ieee1284DeviceId, out string manufacturer, out string model))
        {
            var byDeviceId = _models
                .Where(m => m.Ieee1284Ids.Any(i => Ieee1284.SameValue(i.Manufacturer, manufacturer) && Ieee1284.SameValue(i.Model, model)))
                .ToList();
            if (byDeviceId.Count == 1)
                return new ModelMatch(byDeviceId[0], MatchConfidence.Exact, $"{address}: IEEE 1284 device ID MFG '{manufacturer}' MDL '{model}' matches {byDeviceId[0].Id}.");
            if (byDeviceId.Count > 1)
                return Ambiguous(byDeviceId, address);
        }

        var texts = DescriptiveTexts(identity);
        var byName = _models.Where(m => texts.Any(t => m.NetworkModelNames.Any(n => SameName(t.Text, n)))).ToList();
        if (byName.Count == 1)
        {
            var named = texts.Where(t => byName[0].NetworkModelNames.Any(n => SameName(t.Text, n))).ToList();
            int proof = named.FindIndex(t => ProvesModel(identity, t.Source));
            return proof >= 0
                ? new ModelMatch(byName[0], MatchConfidence.Exact, $"{address}: {named[proof].Label} '{named[proof].Text}' equals an approved network model name of {byName[0].Id}.")
                : new ModelMatch(byName[0], MatchConfidence.Probable, $"{address}: {named[0].Label} '{named[0].Text}' equals an approved network model name of {byName[0].Id}, but it is not the model name of a device under the Kyocera enterprise OID (probable; the model must be confirmed).");
        }
        if (byName.Count > 1)
            return Ambiguous(byName, address);

        // The 1284 MFG/MDL pair also counts as a description here: an unexpected MFG with a known MDL is only probable.
        var candidates = ProbableTexts(identity, texts);
        var probable = _models.Where(m => candidates.Any(t => NamesModelWithManufacturer(t.Text, m.Id))).ToList();
        if (probable.Count == 1)
        {
            var text = candidates.First(t => NamesModelWithManufacturer(t.Text, probable[0].Id));
            return new ModelMatch(probable[0], MatchConfidence.Probable, $"{address}: {text.Label} '{text.Text}' names {probable[0].Id} with a manufacturer (probable; the model must be confirmed).");
        }
        if (probable.Count > 1)
            return Ambiguous(probable, address);

        return ModelMatch.None;
    }

    /// <summary>
    /// True when the device names a model that is not supported; it is then another printer whatever its descriptions or
    /// queues say. The IEEE 1284 MDL is authoritative. Without a 1284 ID, a TA/UTAX/Kyocera device's model name counts
    /// (kcprtGeneralModelName, else hrDeviceDescr, which these devices fill with the bare model); a text without a model
    /// designation (no token with a digit, such as a brand string) names no model. The administrator-assigned printer name
    /// and sysDescr never count. <paramref name="reported"/> receives "MFG MDL" or the model name.
    /// </summary>
    public bool ReportsUnsupportedModel(NetworkIdentity identity, out string reported)
    {
        reported = "";
        if (Ieee1284.TryParse(identity.Ieee1284DeviceId, out string manufacturer, out string model))
        {
            reported = $"{manufacturer} {model}";
            return !_models.Any(m => string.Equals(m.Id, model, StringComparison.OrdinalIgnoreCase) || m.Ieee1284Ids.Any(i => Ieee1284.SameValue(i.Model, model)));
        }

        if (!IsKyoceraFamily(identity) || FirstText(identity.VendorModelName, identity.DeviceDescription) is not { } text)
            return false;
        string name = NormalizeName(text);
        var tokens = Tokenize(name);
        if (!tokens.Any(t => t.Any(char.IsAsciiDigit)))
            return false;
        if (_models.Any(m => tokens.Contains(m.Id, StringComparer.OrdinalIgnoreCase) || m.NetworkModelNames.Any(n => SameName(name, n))))
            return false;
        reported = name;
        return true;
    }

    /// <summary>Every model whose accepted drivers include <paramref name="driverName"/>.</summary>
    public IReadOnlyList<ModelDefinition> ModelsAcceptingDriver(string? driverName) =>
        string.IsNullOrEmpty(driverName)
            ? []
            : _models.Where(m => m.AcceptedDrivers.Any(d => string.Equals(d.Name, driverName, StringComparison.OrdinalIgnoreCase))).ToList();

    /// <summary>The single model a driver indicates; null when no model or several models accept it.</summary>
    public ModelDefinition? ModelForDriver(string? driverName) => ModelsAcceptingDriver(driverName) is [var only] ? only : null;

    /// <summary>
    /// A local queue that may be a TA printer's whatever its driver names: a supported model's driver, a TA/UTAX/Kyocera driver
    /// or queue name, or a Microsoft class driver (which Windows gives network and WSD printers itself).
    /// </summary>
    public bool MayBeTaQueue(PrintQueueInfo queue) =>
        ModelsAcceptingDriver(queue.DriverName).Count > 0
        || DriverRules.IsVendorName(queue.DriverName)
        || DriverRules.IsVendorName(queue.Name)
        || DriverRules.IsMicrosoftClassDriver(queue.DriverName);

    /// <summary>
    /// Models the identity points to by any rule, loosely (the 1284 MDL alone, an approved name, or the model Id as a whole token).
    /// Used to refuse an operator's model choice that the device itself contradicts.
    /// </summary>
    public IReadOnlyList<ModelDefinition> ModelsSuggestedBy(NetworkIdentity identity)
    {
        var suggested = new List<ModelDefinition>();
        if (Ieee1284.TryParse(identity.Ieee1284DeviceId, out _, out string model))
        {
            suggested.AddRange(_models.Where(m =>
                string.Equals(m.Id, model, StringComparison.OrdinalIgnoreCase) || m.Ieee1284Ids.Any(i => Ieee1284.SameValue(i.Model, model))));
        }

        foreach (var (_, _, text) in DescriptiveTexts(identity))
        {
            var tokens = Tokenize(text);
            suggested.AddRange(_models.Where(m =>
                m.NetworkModelNames.Any(n => SameName(text, n)) || tokens.Contains(m.Id, StringComparer.OrdinalIgnoreCase)));
        }

        return suggested.Distinct().ToList();
    }

    /// <summary>True when the text contains "TA", "Triumph-Adler", "UTAX" or "Kyocera" as a whole token.</summary>
    public static bool HasManufacturerToken(string? text) =>
        !string.IsNullOrWhiteSpace(text) && Tokenize(text).Any(t => ManufacturerTokens.Contains(t, StringComparer.OrdinalIgnoreCase));

    /// <summary>A TA, UTAX or Kyocera device (whether or not it is a supported model).</summary>
    public static bool IsVendorDevice(PnpPrinterDevice device) =>
        HasManufacturerToken(device.Manufacturer) || HasManufacturerToken(device.FriendlyName) || HasManufacturerToken(device.Description) ||
        device.HardwareIds.Any(id =>
        {
            int separator = id.IndexOf('\\');
            string name = separator >= 0 ? id[(separator + 1)..] : id;
            return VendorHardwareIdPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        });

    /// <summary>A network identity that reports a TA, UTAX or Kyocera manufacturer.</summary>
    public static bool IsVendorIdentity(NetworkIdentity identity) =>
        (Ieee1284.TryParse(identity.Ieee1284DeviceId, out string manufacturer, out _) && HasManufacturerToken(manufacturer)) ||
        DescriptiveTexts(identity).Any(t => HasManufacturerToken(t.Text));

    /// <summary>Kyocera enterprise sysObjectID (1.3.6.1.4.1.1347), an answer from the Kyocera private MIB, or a TA/UTAX/Kyocera manufacturer.</summary>
    public static bool IsKyoceraFamily(NetworkIdentity identity) =>
        IsKyoceraEnterprise(identity) ||
        !string.IsNullOrWhiteSpace(identity.VendorModelName) ||
        IsVendorIdentity(identity);

    private const string KyoceraEnterpriseOidPrefix = "1.3.6.1.4.1.1347.";

    private static bool IsKyoceraEnterprise(NetworkIdentity identity) =>
        identity.SysObjectId?.StartsWith(KyoceraEnterpriseOidPrefix, StringComparison.Ordinal) == true;

    /// <summary>
    /// Whether an approved network model name in this field proves the model: kcprtGeneralModelName (the Kyocera private MIB
    /// that TA/UTAX devices implement), or hrDeviceDescr on a device under the Kyocera enterprise OID, which fills it with the
    /// bare model name. sysDescr is free text (a brand string on these devices) and proves nothing.
    /// </summary>
    private static bool ProvesModel(NetworkIdentity identity, TextSource source) =>
        source == TextSource.VendorModelName || (source == TextSource.DeviceDescription && IsKyoceraEnterprise(identity));

    /// <summary>True when the identity says anything about what the device is.</summary>
    public static bool IsInformative(NetworkIdentity identity) =>
        Ieee1284.TryParse(identity.Ieee1284DeviceId, out _, out _) || DescriptiveTexts(identity).Count > 0;

    /// <summary>Splits at whitespace, control characters, symbols and punctuation other than '-'.</summary>
    public static IReadOnlyList<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        foreach (char c in text)
        {
            if (c != '-' && (char.IsWhiteSpace(c) || char.IsControl(c) || char.IsPunctuation(c) || char.IsSymbol(c)))
            {
                Flush();
                continue;
            }
            current.Append(c);
        }
        Flush();
        return tokens;

        void Flush()
        {
            if (current.Length == 0)
                return;
            tokens.Add(current.ToString());
            current.Clear();
        }
    }

    /// <summary>Trimmed, with internal whitespace runs collapsed to one space.</summary>
    public static string NormalizeName(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static bool SameName(string a, string b) =>
        string.Equals(NormalizeName(a), NormalizeName(b), StringComparison.OrdinalIgnoreCase);

    private static bool NamesModelWithManufacturer(string text, string modelId)
    {
        var tokens = Tokenize(text);
        return tokens.Contains(modelId, StringComparer.OrdinalIgnoreCase)
            && tokens.Any(t => ManufacturerTokens.Contains(t, StringComparer.OrdinalIgnoreCase));
    }

    private enum TextSource { VendorModelName, DeviceDescription, SysDescr, Ieee1284 }

    /// <summary>
    /// The texts a device reports about itself. prtGeneralPrinterName is left out: RFC 3805 makes it a name an administrator
    /// assigns (often the name of the printer a device replaced), so it is shown in reports but never read as evidence.
    /// </summary>
    private static List<(TextSource Source, string Label, string Text)> DescriptiveTexts(NetworkIdentity identity)
    {
        var texts = new List<(TextSource Source, string Label, string Text)>();
        Add(TextSource.VendorModelName, "Kyocera model name", identity.VendorModelName);
        Add(TextSource.DeviceDescription, "device description", identity.DeviceDescription);
        Add(TextSource.SysDescr, "sysDescr", identity.SysDescr);
        return texts;

        void Add(TextSource source, string label, string? text)
        {
            if (!string.IsNullOrWhiteSpace(text))
                texts.Add((source, label, NormalizeName(text)));
        }
    }

    private static List<(TextSource Source, string Label, string Text)> ProbableTexts(NetworkIdentity identity, List<(TextSource Source, string Label, string Text)> texts) =>
        Ieee1284.TryParse(identity.Ieee1284DeviceId, out string manufacturer, out string model)
            ? [.. texts, (TextSource.Ieee1284, "IEEE 1284 MFG/MDL", NormalizeName($"{manufacturer} {model}"))]
            : texts;

    private static string? FirstText(params string?[] texts) => texts.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));

    private static ModelMatch Ambiguous(IEnumerable<ModelDefinition> models, string subject)
    {
        var list = models.Distinct().ToList();
        return ModelMatch.None with
        {
            Evidence = $"{subject} matches several models ({string.Join(", ", list.Select(m => m.Id))}); none is chosen.",
            AmbiguousModels = list,
        };
    }
}
