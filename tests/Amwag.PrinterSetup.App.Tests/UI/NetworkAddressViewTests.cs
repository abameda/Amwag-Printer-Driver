using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.App.Localization;
using Amwag.PrinterSetup.App.UI;
using Amwag.PrinterSetup.App.UI.Views;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.App.Tests.UI;

/// <summary>Why the address entry is shown: nothing was found, the address did not work, or the employee chose none of the printers found.</summary>
public sealed class NetworkAddressViewTests
{
    [Theory]
    [InlineData(false, false, true, "network.text.declined")]
    [InlineData(true, false, true, "network.text.declined")]
    [InlineData(true, false, false, "network.text.nothing")]
    [InlineData(true, true, false, "network.text.retry")]
    [InlineData(false, false, false, "network.text.retry")]
    public void The_address_entry_says_why_it_is_shown(bool nothingDetected, bool previousProblem, bool declined, string key)
    {
        var prompt = new NetworkAddressPrompt(
            nothingDetected,
            previousProblem ? "10.0.0.99" : null,
            previousProblem ? FindingCode.NetworkPrinterUnreachable : null,
            previousProblem ? "timeout" : null)
        {
            OfferedPrintersDeclined = declined,
        };

        foreach (UiLanguage language in new[] { UiLanguage.English, UiLanguage.Arabic })
        {
            List<string> texts = [];
            Sta.Run(() =>
            {
                using var form = new MainForm(TestRuntime.Create(language == UiLanguage.Arabic ? "ar" : "en"));
                form.AskNetworkAddress(prompt, _ => true);
                Assert.IsType<NetworkAddressView>(form.CurrentView);
                texts = All(form.CurrentView!).OfType<Label>().Select(l => l.Text).ToList();
            });

            Assert.Contains((language == UiLanguage.Arabic ? Strings.Arabic : Strings.English).Text(key), texts);
        }
    }

    private static IEnumerable<Control> All(Control root) => root.Controls.Cast<Control>().SelectMany(c => All(c).Prepend(c));
}
