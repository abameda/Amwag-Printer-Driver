using Amwag.PrinterSetup.App.Localization;
using Amwag.PrinterSetup.App.UI;
using Amwag.PrinterSetup.App.UI.Views;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.App.Tests.UI;

/// <summary>
/// The model question shows what the printer reports about itself (its Kyocera model name, then its device description,
/// then sysDescr), never the printer name an administrator assigned, which is often the name of the printer it replaced.
/// </summary>
public sealed class ConfirmModelViewTests
{
    private const string AssignedName = "P-4030DN front desk";

    private static PrinterCandidate Reporting(string? vendorModelName, string? deviceDescription, string? sysDescr) => UiSamples.NetworkPrinter with
    {
        NetworkIdentity = new NetworkIdentity
        {
            Address = "10.0.0.5",
            RawPortReachable = true,
            SnmpResponded = true,
            PrinterName = AssignedName,
            VendorModelName = vendorModelName,
            DeviceDescription = deviceDescription,
            SysDescr = sysDescr,
        },
    };

    /// <summary>The question as shown, with the no-break spaces that keep data on one line in Arabic text read as spaces.</summary>
    private static string Question(UiLanguage language, PrinterCandidate candidate)
    {
        using var form = new MainForm(TestRuntime.Create());
        using var view = new ConfirmModelView(new ViewContext(form, Strings.For(language), TestRuntime.Config), candidate, _ => { });
        return view.Question.Replace(' ', ' ');
    }

    [Theory]
    [InlineData(UiLanguage.English)]
    [InlineData(UiLanguage.Arabic)]
    public void The_question_shows_the_kyocera_model_name_first(UiLanguage language) => Sta.Run(() =>
    {
        string question = Question(language, Reporting("TA_UTAX P-4531DN", "TA Triumph-Adler P-4531DN Series", "TA_UTAX Printing System"));

        Assert.Contains("TA_UTAX P-4531DN", question, StringComparison.Ordinal);
        Assert.DoesNotContain("Series", question, StringComparison.Ordinal);
        Assert.DoesNotContain(AssignedName, question, StringComparison.Ordinal);
    });

    [Fact]
    public void Without_a_kyocera_model_name_the_device_description_then_sysdescr_is_shown() => Sta.Run(() =>
    {
        Assert.Contains("TA Triumph-Adler P-4531DN Series", Question(UiLanguage.English, Reporting(null, "TA Triumph-Adler P-4531DN Series", "TA_UTAX Printing System")), StringComparison.Ordinal);
        Assert.Contains("TA_UTAX P-4531DN printer", Question(UiLanguage.English, Reporting(" ", null, "TA_UTAX P-4531DN printer")), StringComparison.Ordinal);
    });

    [Fact]
    public void The_administrator_assigned_printer_name_is_never_shown_as_what_the_printer_reports() => Sta.Run(() =>
    {
        string question = Question(UiLanguage.English, Reporting(null, null, null));

        Assert.DoesNotContain(AssignedName, question, StringComparison.Ordinal);
    });
}
