using System.Runtime.InteropServices;
using Amwag.PrinterSetup.Windows.Interop;
using Microsoft.Win32.SafeHandles;

namespace Amwag.PrinterSetup.Windows.Drivers;

internal enum CatalogMembershipStatus
{
    Member,
    /// <summary>The file's hash was computed with every algorithm and no catalog member carries it.</summary>
    NotMember,
    /// <summary>The check could not be completed (API failure); Windows still verifies the package when it is staged.</summary>
    Unknown,
}

internal sealed record CatalogMembershipResult(CatalogMembershipStatus Status, string Detail);

/// <summary>Whether a file is listed in a catalog: catalog member tags are the hex hash (SHA-256 or SHA-1) of the file.</summary>
internal static class CatalogMembership
{
    private static readonly string[] Algorithms = [WinTrust.BCRYPT_SHA256_ALGORITHM, WinTrust.BCRYPT_SHA1_ALGORITHM];

    public static CatalogMembershipResult Check(string catalogPath, string filePath)
    {
        using SafeCatalogHandle catalog = WinTrust.CryptCATOpen(catalogPath, WinTrust.CRYPTCAT_OPEN_EXISTING, IntPtr.Zero, 0, 0);
        if (catalog.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            return new(CatalogMembershipStatus.Unknown, $"CryptCATOpen failed with {NativeErrorText.Format(error)}.");
        }

        var problems = new List<string>();
        var triedTags = new List<string>();
        foreach (string algorithm in Algorithms)
        {
            string? tag = TryComputeTag(algorithm, filePath, problems);
            if (tag is null)
                continue;

            if (WinTrust.CryptCATGetMemberInfo(catalog, tag) != IntPtr.Zero)
                return new(CatalogMembershipStatus.Member, $"Listed in the catalog with {algorithm} tag {tag}.");

            triedTags.Add($"{algorithm} {tag}");
        }

        return problems.Count == 0
            ? new(CatalogMembershipStatus.NotMember, $"The catalog has no member with tag {string.Join(" or ", triedTags)}.")
            : new(CatalogMembershipStatus.Unknown, string.Join(" ", problems));
    }

    private static unsafe string? TryComputeTag(string algorithm, string filePath, List<string> problems)
    {
        if (WinTrust.CryptCATAdminAcquireContext2(out SafeCatAdminHandle admin, WinTrust.DriverActionVerify, algorithm, IntPtr.Zero, 0) == 0)
        {
            int error = Marshal.GetLastPInvokeError();
            admin.Dispose();
            problems.Add($"CryptCATAdminAcquireContext2({algorithm}) failed with {NativeErrorText.Format(error)}.");
            return null;
        }

        using (admin)
        {
            SafeFileHandle file;
            try
            {
                file = File.OpenHandle(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"'{filePath}' could not be opened: {ex.Message}");
                return null;
            }

            using (file)
            {
                uint size = 0;
                WinTrust.CryptCATAdminCalcHashFromFileHandle2(admin, file, ref size, null, 0);
                if (size is 0 or > 64)
                {
                    int error = Marshal.GetLastPInvokeError();
                    problems.Add($"CryptCATAdminCalcHashFromFileHandle2({algorithm}) reported a hash size of {size} ({NativeErrorText.Format(error)}).");
                    return null;
                }

                byte[] hash = new byte[size];
                fixed (byte* buffer = hash)
                {
                    if (WinTrust.CryptCATAdminCalcHashFromFileHandle2(admin, file, ref size, buffer, 0) == 0)
                    {
                        int error = Marshal.GetLastPInvokeError();
                        problems.Add($"CryptCATAdminCalcHashFromFileHandle2({algorithm}) failed with {NativeErrorText.Format(error)}.");
                        return null;
                    }
                }

                return Convert.ToHexString(hash, 0, (int)size);
            }
        }
    }
}
