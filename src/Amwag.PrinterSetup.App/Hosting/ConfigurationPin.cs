using Amwag.PrinterSetup.Core.Configuration;

namespace Amwag.PrinterSetup.App.Hosting;

/// <summary>
/// The exact configuration and branch bytes a run loaded: their SHA-256 (64 lowercase hexadecimal characters), or
/// <see cref="Embedded"/> for the configuration built into the executable and <see cref="NoBranch"/> without a branch file.
/// The window hands its pin to the elevated worker (--config-sha256, --branch-sha256), which refuses to apply anything when
/// it loads different bytes, so the elevated step never applies another configuration than the window assessed.
/// </summary>
internal sealed record ConfigurationPin(string Config, string Branch)
{
    public const string Embedded = ConfigLoader.EmbeddedSource;

    public const string NoBranch = "none";

    public static ConfigurationPin Of(LoadedConfiguration loaded) => new(loaded.ConfigSha256, loaded.BranchSha256 ?? NoBranch);
}
