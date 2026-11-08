using System.Reflection;
using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.App.UI;
using Amwag.PrinterSetup.App.UI.Views;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.App.Tests.UI;

/// <summary>The IT shortcut: --address, --model and --confirm-model answer the model questions for that one printer.</summary>
public sealed class UiInteractionTests
{
    private static readonly CommandLineOptions Asserted = new() { Address = "10.0.0.23", ModelId = "p-4531dn", ConfirmModel = true };

    private static ISetupInteraction InteractionOf(MainForm form) =>
        (ISetupInteraction)typeof(MainForm).GetField("_interaction", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(form)!;

    private static PrinterCandidate NetworkPrinterAt(string address, MatchConfidence confidence, int? model) => UiSamples.NetworkPrinter with
    {
        Key = PrinterCandidate.NetworkKey(address),
        NetworkAddress = address,
        NetworkIdentity = new NetworkIdentity { Address = address, RawPortReachable = true },
        Model = model is { } index ? TestRuntime.Config.Models[index] : null,
        Confidence = confidence,
    };

    [Fact]
    public void Unidentified_printer_at_the_given_address_gets_the_given_model_without_asking() => Sta.Run(() =>
    {
        var log = new MemoryLog();
        using var form = new MainForm(TestRuntime.Create("en", log: log, commandLine: Asserted));
        _ = form.Handle;

        var answer = InteractionOf(form).ChooseModelForUnidentifiedPrinterAsync(UiSamples.UnidentifiedNetworkPrinter, TestRuntime.Config.Models, CancellationToken.None);

        Assert.True(answer.IsCompletedSuccessfully);
        Assert.Equal("P-4531DN", answer.Result?.Id);
        Application.DoEvents();
        Assert.IsNotType<ChooseModelView>(form.CurrentView);
        Assert.Contains(log.Entries, e => e.Event == "ui.choose_model.from_command_line");
    });

    [Fact]
    public void Probable_match_of_the_given_model_at_the_given_address_is_confirmed_without_asking() => Sta.Run(() =>
    {
        using var form = new MainForm(TestRuntime.Create("en", commandLine: Asserted));
        _ = form.Handle;

        var answer = InteractionOf(form).ConfirmProbableMatchAsync(NetworkPrinterAt("10.0.0.23", MatchConfidence.Probable, model: 1), CancellationToken.None);

        Assert.True(answer.IsCompletedSuccessfully);
        Assert.True(answer.Result);
    });

    [Fact]
    public void Probable_match_of_another_model_is_still_asked() => Sta.Run(() =>
    {
        using var form = new MainForm(TestRuntime.Create("en", commandLine: Asserted));
        _ = form.Handle;

        var answer = InteractionOf(form).ConfirmProbableMatchAsync(NetworkPrinterAt("10.0.0.23", MatchConfidence.Probable, model: 0), CancellationToken.None);

        PumpUntil(() => form.CurrentView is ConfirmModelView);
        Assert.False(answer.IsCompleted);
        Click((Button)form.CancelButton!);
        PumpUntil(() => answer.IsCompleted);
        Assert.False(answer.Result);
    });

    [Theory]
    [InlineData("10.0.0.99", true)]  // another printer
    [InlineData("10.0.0.23", false)] // without --confirm-model
    public void Other_printers_and_unconfirmed_switches_are_still_asked(string address, bool confirmModel) => Sta.Run(() =>
    {
        var options = Asserted with { ConfirmModel = confirmModel };
        using var form = new MainForm(TestRuntime.Create("en", commandLine: options));
        _ = form.Handle;

        var answer = InteractionOf(form).ChooseModelForUnidentifiedPrinterAsync(NetworkPrinterAt(address, MatchConfidence.None, model: null), TestRuntime.Config.Models, CancellationToken.None);

        PumpUntil(() => form.CurrentView is ChooseModelView);
        Assert.False(answer.IsCompleted);
        Click((Button)form.CancelButton!);
        PumpUntil(() => answer.IsCompleted);
        Assert.Null(answer.Result);
    });

    [Fact]
    public void Model_that_is_not_configured_is_still_asked() => Sta.Run(() =>
    {
        using var form = new MainForm(TestRuntime.Create("en", commandLine: Asserted with { ModelId = "P-9999DN" }));
        _ = form.Handle;

        var answer = InteractionOf(form).ChooseModelForUnidentifiedPrinterAsync(UiSamples.UnidentifiedNetworkPrinter, TestRuntime.Config.Models, CancellationToken.None);

        PumpUntil(() => form.CurrentView is ChooseModelView);
        Assert.False(answer.IsCompleted);
    });

    private static void Click(Button button) =>
        typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, [EventArgs.Empty]);

    private static void PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the UI thread.");
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }
}
