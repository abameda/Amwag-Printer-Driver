using System.Globalization;
using System.Text;
using Amwag.PrinterSetup.Core.Configuration;

namespace Amwag.PrinterSetup.Core.Detection;

/// <summary>
/// IEEE 1284 device IDs ("MFG:...;MDL:...;") and the Plug and Play hardware IDs Windows derives from them:
/// enumerator + "\" + MFG+MDL (spaces as '_', truncated to 20 characters) + a 4-hex-digit checksum of the raw MFG+MDL.
/// The checksum algorithm and its test vectors are documented in docs/research/network-identity.md, section 4.5.
/// </summary>
internal static class Ieee1284
{
    /// <summary>Windows truncates MFG + MDL to this many characters in a 1284-derived hardware ID.</summary>
    public const int MaxHardwareIdNameLength = 20;

    private const int ChecksumLength = 4;

    // A CRC-16 (reflected polynomial 0xA001, initial value 0) driven by two nibble tables. The last high-nibble entry is
    // 0x4600 rather than the mathematically correct 0x4400, so this is not CRC-16/ARC; it is what Windows computes.
    private static readonly ushort[] LowNibble =
        [0x0000, 0xC0C1, 0xC181, 0x0140, 0xC301, 0x03C0, 0x0280, 0xC241, 0xC601, 0x06C0, 0x0780, 0xC741, 0x0500, 0xC5C1, 0xC481, 0x0440];

    private static readonly ushort[] HighNibble =
        [0x0000, 0xCC01, 0xD801, 0x1400, 0xF001, 0x3C00, 0x2800, 0xE401, 0xA001, 0x6C00, 0x7800, 0xB401, 0x5000, 0x9C01, 0x8801, 0x4600];

    /// <summary>Reads MFG/MANUFACTURER and MDL/MODEL (keys case-insensitive, values trimmed); false unless both are present.</summary>
    public static bool TryParse(string? deviceId, out string manufacturer, out string model)
    {
        manufacturer = "";
        model = "";
        if (string.IsNullOrWhiteSpace(deviceId))
            return false;

        foreach (string pair in deviceId.Split(';'))
        {
            int colon = pair.IndexOf(':');
            if (colon <= 0)
                continue;

            // Raw device IDs start with a binary length prefix; ignore control characters around keys.
            string key = new string(pair[..colon].Where(c => !char.IsControl(c)).ToArray()).Trim();
            string value = pair[(colon + 1)..].Trim();
            if (value.Length == 0)
                continue;

            if (key.Equals("MFG", StringComparison.OrdinalIgnoreCase) || key.Equals("MANUFACTURER", StringComparison.OrdinalIgnoreCase))
                manufacturer = manufacturer.Length == 0 ? value : manufacturer;
            else if (key.Equals("MDL", StringComparison.OrdinalIgnoreCase) || key.Equals("MODEL", StringComparison.OrdinalIgnoreCase))
                model = model.Length == 0 ? value : model;
        }

        return manufacturer.Length > 0 && model.Length > 0;
    }

    /// <summary>MFG + MDL with spaces replaced by '_', before truncation (e.g. "UTAX_TA" + "P-4030DN" = "UTAX_TAP-4030DN").</summary>
    public static string HardwareIdName(string manufacturer, string model) => (manufacturer + model).Replace(' ', '_');

    /// <summary>The checksum Windows appends, computed over the raw MFG + MDL (spaces not yet replaced, not truncated).</summary>
    public static ushort Checksum(string rawManufacturerAndModel)
    {
        ushort crc = 0;
        foreach (byte b in Encoding.ASCII.GetBytes(rawManufacturerAndModel))
        {
            int index = (b ^ crc) & 0xFF;
            crc = (ushort)((crc >> 8) ^ LowNibble[index & 0xF] ^ HighNibble[index >> 4]);
        }
        return crc;
    }

    /// <summary>
    /// True when <paramref name="hardwareId"/> is "&lt;ENUMERATOR&gt;\" + MFG + MDL + its checksum for this identity. Only untruncated
    /// names (at most 20 characters) can match: a truncated name is ambiguous.
    /// </summary>
    public static bool MatchesHardwareId(string hardwareId, Ieee1284Identity identity)
    {
        int separator = hardwareId.IndexOf('\\');
        if (separator <= 0)
            return false;

        string raw = identity.Manufacturer + identity.Model;
        string name = raw.Replace(' ', '_');
        if (name.Length > MaxHardwareIdNameLength)
            return false;

        string rest = hardwareId[(separator + 1)..];
        return rest.Length == name.Length + ChecksumLength
            && rest.StartsWith(name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(rest[name.Length..], Checksum(raw).ToString("X4", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Equal ignoring case, also when one side writes spaces as '_' (as hardware IDs do).</summary>
    public static bool SameValue(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(a.Replace(' ', '_'), b.Replace(' ', '_'), StringComparison.OrdinalIgnoreCase);
}
