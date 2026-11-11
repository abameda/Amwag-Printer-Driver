namespace Amwag.PrinterSetup.Windows.Drivers;

/// <summary>The only hosts driver archives (and every redirect on the way to them) may come from.</summary>
internal static class OfficialDownloadHosts
{
    private static readonly string[] Domains =
    [
        "triumph-adler.com",
        "triumph-adler.de",
        "triumph-adler.net",
        "utax.com",
        "utax.de",
        "utax.it",
        "kyoceradocumentsolutions.com",
        "kyoceradocumentsolutions.eu",
    ];

    /// <summary>HTTPS, no user information, and a DNS host that is an official domain or one of its subdomains.</summary>
    public static bool IsAllowed(Uri uri, out string reason)
    {
        if (!uri.IsAbsoluteUri)
        {
            reason = $"'{uri}' is not an absolute URL";
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"'{uri.GetLeftPart(UriPartial.Authority)}' does not use HTTPS";
            return false;
        }

        if (uri.UserInfo.Length > 0)
        {
            reason = "the URL contains user information";
            return false;
        }

        if (uri.HostNameType != UriHostNameType.Dns)
        {
            reason = $"'{uri.Host}' is not a DNS host name";
            return false;
        }

        string host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        foreach (string domain in Domains)
        {
            if (host == domain || host.EndsWith("." + domain, StringComparison.Ordinal))
            {
                reason = "";
                return true;
            }
        }

        reason = $"'{uri.Host}' is not an official TA Triumph-Adler, UTAX or Kyocera host";
        return false;
    }
}
