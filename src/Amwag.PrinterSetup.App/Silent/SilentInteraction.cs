using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.App.Silent;

/// <summary>
/// Answers the flow's questions without a user. It never chooses between printers and never asks for an address; it asserts a
/// model only for the printer at --address, and only when IT said so with --model and --confirm-model. A model it cannot
/// confirm therefore stops the run for IT (exit code 4) instead of counting as an operator's "no".
/// </summary>
internal sealed class SilentInteraction : ISetupInteraction
{
    private readonly CommandLineOptions _options;

    public SilentInteraction(CommandLineOptions options) => _options = options;

    public bool IsUnattended => true;

    public Task<PrinterCandidate?> ChooseCandidateAsync(IReadOnlyList<PrinterCandidate> candidates, CancellationToken cancellationToken) =>
        Task.FromResult<PrinterCandidate?>(null);

    public Task<bool> ConfirmProbableMatchAsync(PrinterCandidate candidate, CancellationToken cancellationToken) =>
        Task.FromResult(IsAssertedPrinter(candidate) && string.Equals(candidate.Model?.Id, _options.ModelId, StringComparison.OrdinalIgnoreCase));

    public Task<string?> RequestNetworkAddressAsync(NetworkAddressPrompt prompt, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    public Task<ModelDefinition?> ChooseModelForUnidentifiedPrinterAsync(PrinterCandidate candidate, IReadOnlyList<ModelDefinition> models, CancellationToken cancellationToken) =>
        Task.FromResult(AssertedModel(candidate, models));

    public ModelDefinition? AssertedModel(PrinterCandidate candidate, IReadOnlyList<ModelDefinition> models) =>
        IsAssertedPrinter(candidate) ? models.FirstOrDefault(m => string.Equals(m.Id, _options.ModelId, StringComparison.OrdinalIgnoreCase)) : null;

    private bool IsAssertedPrinter(PrinterCandidate candidate)
    {
        if (!_options.ConfirmModel || _options.ModelId is null || _options.Address is not { } address)
            return false;

        return candidate.IsAtAddress(address);
    }
}
