using Amwag.PrinterSetup.App.Localization;
using Amwag.PrinterSetup.App.UI;
using Amwag.PrinterSetup.App.UI.Views;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.App.Tests.UI;

/// <summary>
/// The result's details are one flat grid (glyph | text), and still read as a list of printers with their checks indented under
/// them: a printer's name starts at the reading edge after its glyph, its checks' texts sit 8 px further in, in English and
/// mirrored in Arabic. Screen readers hear each check with its expected and found values.
/// </summary>
public sealed class DetailsGridTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("ar")]
    public void A_printers_checks_are_indented_under_its_name(string language) => Sta.Run(() =>
    {
        using var form = new MainForm(TestRuntime.Create(language, SetupMode.Diagnostics));
        form.ClientSize = new Size(form.LogicalToDeviceUnits(900), form.LogicalToDeviceUnits(700));
        form.ShowResult(UiSamples.Diagnostics());
        form.PerformLayout();
        Application.DoEvents();
        var view = (ResultView)form.CurrentView!;
        Strings t = Strings.For(form.Language);
        var scale = UiScale.For(form);

        Label printer = WindowRun.All(view).OfType<Label>().First(l => l.Text.StartsWith(t.Data("TA P-4030DN  ·  AMWAG Ticket Printer"), StringComparison.Ordinal));
        Label check = WindowRun.All(view).OfType<Label>().First(l => l.Text.StartsWith(t.ItemName(VerificationItem.SpoolerRunning) + "  ·  ", StringComparison.Ordinal));
        var grid = (TableLayoutPanel)check.Parent!;
        Control row = printer.Parent!;
        Rectangle name = InGrid(printer, grid), checkText = InGrid(check, grid);

        // The printer is one row across both columns (as the "Printers checked" heading is); the grid stays flat for the checks.
        Assert.Same(grid, row.Parent);
        Assert.Equal(2, grid.GetColumnSpan(row));
        Assert.IsType<GlyphBox>(grid.GetControlFromPosition(0, grid.GetRow(check)));
        // Before: the checks' indented glyphs widened the glyph column, and the printer's name moved onto the checks' text edge.
        if (language == "en")
        {
            Assert.Equal(scale.Px(26), name.Left);
            Assert.Equal(scale.Px(34), checkText.Left);
        }
        else
        {
            Assert.Equal(grid.Width - scale.Px(26), name.Right);
            Assert.Equal(grid.Width - scale.Px(34), checkText.Right);
        }
    });

    [Fact]
    public void A_long_printer_name_does_not_widen_the_glyph_column() => Sta.Run(() =>
    {
        using var form = new MainForm(TestRuntime.Create("en", SetupMode.Diagnostics));
        form.ClientSize = new Size(form.LogicalToDeviceUnits(840), form.LogicalToDeviceUnits(600));
        var sample = UiSamples.Diagnostics();
        string queue = string.Join(" ", Enumerable.Repeat("Ticket printer at the front desk of the Cairo branch", 4));
        form.ShowResult(sample with { Verifications = [sample.Verifications[0] with { Summary = sample.Verifications[0].Summary with { QueueName = queue } }] });
        form.PerformLayout();
        Application.DoEvents();
        var view = (ResultView)form.CurrentView!;
        var scale = UiScale.For(form);

        Label check = WindowRun.All(view).OfType<Label>().First(l => l.Text.StartsWith(Strings.English.ItemName(VerificationItem.SpoolerRunning) + "  ·  ", StringComparison.Ordinal));
        Label printer = WindowRun.All(view).OfType<Label>().First(l => l.Text.Contains(queue, StringComparison.Ordinal));
        var grid = (TableLayoutPanel)check.Parent!;

        Assert.Equal(scale.Px(34), grid.GetColumnWidths()[0]);
        Assert.Equal(scale.Px(34), InGrid(check, grid).Left);
        // The name wraps within the grid instead of running past it.
        Assert.True(InGrid(printer, grid).Right <= grid.Width, $"name ends at {InGrid(printer, grid).Right}, grid is {grid.Width} wide");
        Assert.True(printer.Height > printer.Font.Height, "the long name wraps to more lines");
    });

    [Theory]
    [InlineData("en")]
    [InlineData("ar")]
    public void Screen_readers_hear_a_check_with_its_expected_and_found_values(string language) => Sta.Run(() =>
    {
        using var form = new MainForm(TestRuntime.Create(language, SetupMode.Setup));
        form.ShowResult(UiSamples.Ready() with { Outcome = Amwag.PrinterSetup.App.Flow.FlowOutcome.NotReady });
        var view = (ResultView)form.CurrentView!;
        Strings t = Strings.For(form.Language);

        Label check = WindowRun.All(view).OfType<Label>().First(l => l.Text.StartsWith(t.ItemName(VerificationItem.SpoolerRunning) + "  ·  ", StringComparison.Ordinal));

        // Before: "Print Spooler service: Passed" only; the values were a separate label read on its own.
        Assert.Equal(t.ItemName(VerificationItem.SpoolerRunning) + ": " + t.StatusName(CheckStatus.Pass) + ". " + t.Format("result.expectedActual", "Running", "Running"), check.AccessibleName);
        if (language == "en")
            Assert.Equal("Print Spooler service: Passed. Expected: Running   Found: Running", check.AccessibleName);
    });

    /// <summary>The bounds of <paramref name="control"/> in the client coordinates of <paramref name="grid"/>, which it sits in or under.</summary>
    private static Rectangle InGrid(Control control, Control grid)
    {
        var bounds = control.Bounds;
        for (Control? parent = control.Parent; parent is not null && parent != grid; parent = parent.Parent)
            bounds.Offset(parent.Left, parent.Top);
        return bounds;
    }
}
