using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.App.Hosting;
using Amwag.PrinterSetup.App.Silent;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Testing;

namespace Amwag.PrinterSetup.App.Tests.Silent;

public sealed class SilentInteractionTests
{
    private static readonly IReadOnlyList<ModelDefinition> Models = SampleConfig.Create().Models;

    [Fact]
    public async Task Never_chooses_between_printers_and_never_asks_for_an_address()
    {
        var interaction = new SilentInteraction(Parse("--silent", "--address", "10.0.0.5", "--model", SampleConfig.P4030, "--confirm-model"));

        Assert.Null(await interaction.ChooseCandidateAsync([Network("10.0.0.5", SampleConfig.P4030), Network("10.0.0.6", SampleConfig.P4030)], CancellationToken.None));
        Assert.Null(await interaction.RequestNetworkAddressAsync(new NetworkAddressPrompt(true, null, null, null), CancellationToken.None));
    }

    [Fact]
    public async Task Confirms_a_probable_match_only_for_the_asserted_address_and_model()
    {
        var interaction = new SilentInteraction(Parse("--silent", "--address", "10.0.0.5", "--model", "p-4030dn", "--confirm-model"));

        Assert.True(await interaction.ConfirmProbableMatchAsync(Network("10.0.0.5", SampleConfig.P4030), CancellationToken.None));
        Assert.False(await interaction.ConfirmProbableMatchAsync(Network("10.0.0.6", SampleConfig.P4030), CancellationToken.None));
        Assert.False(await interaction.ConfirmProbableMatchAsync(Network("10.0.0.5", SampleConfig.P5032), CancellationToken.None));
    }

    [Fact]
    public async Task Without_confirm_model_nothing_is_confirmed()
    {
        var interaction = new SilentInteraction(Parse("--silent", "--address", "10.0.0.5", "--model", SampleConfig.P4030));

        Assert.False(await interaction.ConfirmProbableMatchAsync(Network("10.0.0.5", SampleConfig.P4030), CancellationToken.None));
        Assert.Null(await interaction.ChooseModelForUnidentifiedPrinterAsync(Network("10.0.0.5", null), Models, CancellationToken.None));
    }

    [Fact]
    public async Task Names_the_model_of_an_unidentified_printer_at_the_asserted_address()
    {
        var interaction = new SilentInteraction(Parse("--silent", "--address", "printer-07", "--model", SampleConfig.P4531, "--confirm-model"));

        ModelDefinition? model = await interaction.ChooseModelForUnidentifiedPrinterAsync(Network("PRINTER-07", null), Models, CancellationToken.None);
        ModelDefinition? elsewhere = await interaction.ChooseModelForUnidentifiedPrinterAsync(Network("10.0.0.6", null), Models, CancellationToken.None);

        Assert.Equal(SampleConfig.P4531, model?.Id);
        Assert.Null(elsewhere);
    }

    private static CommandLineOptions Parse(params string[] args)
    {
        var options = CommandLineParser.Parse(args);
        Assert.Empty(options.Errors);
        return options;
    }

    private static PrinterCandidate Network(string address, string? modelId) => new()
    {
        Key = PrinterCandidate.NetworkKey(address),
        Kind = TargetKind.NetworkAddress,
        Model = modelId is null ? null : SampleConfig.Model(modelId),
        Confidence = modelId is null ? MatchConfidence.None : MatchConfidence.Probable,
        Connection = ConnectionType.TcpIp,
        NetworkAddress = address,
        NetworkIdentity = new NetworkIdentity { Address = address, RawPortReachable = true },
        PhysicallyAvailable = true,
    };
}
