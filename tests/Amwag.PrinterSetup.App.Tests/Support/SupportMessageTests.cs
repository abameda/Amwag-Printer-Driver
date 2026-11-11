using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.App.Support;
using Amwag.PrinterSetup.App.Tests.UI;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.App.Tests.Support;

public sealed class SupportMessageTests
{
    private static readonly DateTimeOffset Time = new(2026, 11, 10, 14, 32, 0, TimeSpan.FromHours(2));

    [Fact]
    public void Message_says_where_it_happened_what_the_window_did_and_which_session_to_look_for()
    {
        var runtime = TestRuntime.Create("en", log: new Amwag.PrinterSetup.Testing.MemoryLog("abc123def456"));
        runtime = runtime with { Environment = runtime.Environment with { Branch = new BranchOverride { BranchId = "HEL-01", BranchName = "Heliopolis" } } };
        var result = new FlowResult(FlowOutcome.NotReady)
        {
            CurrentFindings =
            [
                new Finding(FindingCode.SpoolerNotRunning, Severity.Warning, ""),
                new Finding(FindingCode.DriverPackageUnavailable, Severity.Error, ""),
                new Finding(FindingCode.ProtectedPrintModeEnabled, Severity.Info, ""),
            ],
        };

        string[] lines = SupportMessage.Compose(runtime, SetupMode.Repair, result, Time).Split(Environment.NewLine);

        Assert.Equal(
            [
                "AMWAG Printer Setup: request for IT help",
                "Branch: Heliopolis (HEL-01)",
                $"Computer: {Environment.MachineName}, user {Environment.UserName}",
                "Version: 1.0.0, configuration test",
                "Mode: Repair",
                "Result: NotReady (DriverPackageUnavailable, SpoolerNotRunning)",
                "Session: abc123def456",
                "Time: 2026-11-10 14:32 +02:00",
                "The picture of the window and the log files follow.",
            ],
            lines);
    }

    [Fact]
    public void Without_a_result_or_branch_the_message_says_so()
    {
        string text = SupportMessage.Compose(TestRuntime.Create("en"), SetupMode.Setup, result: null, Time);

        Assert.Contains("Branch: not set" + Environment.NewLine, text);
        Assert.Contains("Result: not finished yet" + Environment.NewLine, text);
        Assert.Contains("Result: Ready" + Environment.NewLine, SupportMessage.Compose(TestRuntime.Create("en"), SetupMode.Setup, new FlowResult(FlowOutcome.Ready), Time));
    }

    [Fact]
    public void Chat_link_is_the_click_to_chat_address_of_the_number_with_the_whole_message()
    {
        var contact = new ItContact { Name = "Eng. A", NameArabic = "م. أ", WhatsApp = "201061922226" };
        const string message = "Line 1\r\nA & B? #5 = 100% م";

        var link = new Uri(SupportMessage.WhatsAppLink(contact, message));

        Assert.Equal("https://wa.me/201061922226", link.GetLeftPart(UriPartial.Path));
        Assert.StartsWith("?text=", link.Query, StringComparison.Ordinal);
        Assert.Equal(message, Uri.UnescapeDataString(link.Query["?text=".Length..]));
        Assert.Empty(link.Fragment);
    }
}
