using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Amwag.PrinterSetup.Core.Configuration;

/// <summary>
/// The syntax of a printer address that IT or an employee supplies (--address, branch.json, the address prompt): a dotted-quad
/// IPv4 literal without leading zeros, an IPv6 literal (optionally in brackets, never with a port), or an RFC 1123 host name.
/// Legacy numeric forms ("10.1", "010.0.0.1", "0x0a.0.0.1") are rejected rather than reinterpreted, because Windows would read
/// them as a different host. Hosts read from existing Windows ports do not pass through here; they are compared as stored.
/// </summary>
public static class PrinterAddress
{
    private const int MaxHostNameLength = 253;
    private const int MaxLabelLength = 63;

    public static bool IsValid(string? input) => TryParse(input, out _, out _, out _);

    /// <summary>Validates <paramref name="input"/> and returns its canonical spelling (see <see cref="TryParse"/>).</summary>
    public static bool TryNormalize(string? input, [NotNullWhen(true)] out string? normalized, [NotNullWhen(false)] out string? error) =>
        TryParse(input, out normalized, out _, out error);

    /// <summary>
    /// Validates <paramref name="input"/>. <paramref name="normalized"/> is the spelling used for probing, candidate keys, port
    /// names and comparisons: an IP literal in standard text form (an IPv4-mapped IPv6 address as plain IPv4), a host name
    /// without a trailing dot. <paramref name="ipAddress"/> is null for a host name.
    /// </summary>
    public static bool TryParse(
        string? input,
        [NotNullWhen(true)] out string? normalized,
        out IPAddress? ipAddress,
        [NotNullWhen(false)] out string? error)
    {
        normalized = null;
        ipAddress = null;
        string text = input?.Trim() ?? "";
        if (text.Length == 0)
        {
            error = "No printer address was given.";
            return false;
        }

        if (text.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            error = $"'{text}' contains spaces or control characters.";
            return false;
        }

        if (text.Contains(':') || text.Contains('[') || text.Contains(']'))
        {
            bool bracketed = text.StartsWith('[') && text.EndsWith(']');
            string literal = bracketed ? text[1..^1] : text;
            // IPAddress.TryParse accepts "[2001:db8::5]:9101" and silently drops the port; a port never belongs in the address.
            if (literal.IndexOfAny(['[', ']']) < 0
                && IPAddress.TryParse(literal, out IPAddress? ipv6) && ipv6.AddressFamily == AddressFamily.InterNetworkV6)
            {
                ipAddress = ipv6.IsIPv4MappedToIPv6 ? ipv6.MapToIPv4() : ipv6;
                normalized = ipAddress.ToString();
                error = null;
                return true;
            }

            error = $"'{text}' is not a valid IPv6 address (give the port number separately).";
            return false;
        }

        if (text.All(c => char.IsAsciiDigit(c) || c == '.'))
        {
            if (TryParseDottedQuad(text, out IPAddress? ipv4))
            {
                ipAddress = ipv4;
                normalized = ipv4.ToString();
                error = null;
                return true;
            }

            error = $"'{text}' is not a valid IPv4 address; expected four decimal numbers from 0 to 255 without leading zeros, e.g. 192.168.1.20.";
            return false;
        }

        string name = text.EndsWith('.') ? text[..^1] : text;
        if (IsHostName(name))
        {
            normalized = name;
            error = null;
            return true;
        }

        error = $"'{text}' is not a valid IP address or host name.";
        return false;
    }

    private static bool TryParseDottedQuad(string text, [NotNullWhen(true)] out IPAddress? address)
    {
        address = null;
        string[] parts = text.Split('.');
        if (parts.Length != 4)
            return false;

        var octets = new byte[4];
        for (int i = 0; i < 4; i++)
        {
            string part = parts[i];
            if (part.Length is < 1 or > 3 || (part.Length > 1 && part[0] == '0')
                || !byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out octets[i]))
                return false;
        }

        address = new IPAddress(octets);
        return true;
    }

    /// <summary>RFC 1123 host name syntax; a name whose labels are all numbers (decimal, octal or 0x-hex) is an address in disguise.</summary>
    private static bool IsHostName(string name)
    {
        if (name.Length is 0 or > MaxHostNameLength)
            return false;

        bool allNumeric = true;
        foreach (string label in name.Split('.'))
        {
            if (label.Length is 0 or > MaxLabelLength || label[0] == '-' || label[^1] == '-'
                || !label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
                return false;
            allNumeric &= IsNumericLabel(label);
        }

        return !allNumeric;
    }

    private static bool IsNumericLabel(string label) =>
        label.All(char.IsAsciiDigit)
        || (label.Length > 1 && label[0] == '0' && (label[1] == 'x' || label[1] == 'X') && label[2..].All(char.IsAsciiHexDigit));
}
