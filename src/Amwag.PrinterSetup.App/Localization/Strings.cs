using System.Collections.Frozen;
using System.Globalization;
using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.App.Localization;

/// <summary>
/// Every text the window shows, English and Arabic side by side under stable dotted keys. Templates use {0}-style
/// placeholders for data (model, queue, address, size), which <see cref="Format"/> isolates (<see cref="Bidi.Isolate"/>)
/// so it keeps its own order and leaves the order of the sentence around it alone.
/// A missing key renders as the key itself rather than crashing the window; tests keep the catalog complete.
/// </summary>
public sealed partial class Strings
{
    private static readonly FrozenDictionary<string, Entry> Catalog = BuildCatalog();

    public static Strings English { get; } = new(UiLanguage.English);
    public static Strings Arabic { get; } = new(UiLanguage.Arabic);

    private Strings(UiLanguage language) => Language = language;

    public UiLanguage Language { get; }
    public bool IsRightToLeft => Language == UiLanguage.Arabic;

    public static Strings For(UiLanguage language) => language == UiLanguage.Arabic ? Arabic : English;

    public static IEnumerable<string> Keys => Catalog.Keys;

    public static bool Contains(string key) => Catalog.ContainsKey(key);

    public string Text(string key) => Catalog.TryGetValue(key, out var entry) ? entry.For(Language) : key;

    /// <summary>
    /// The text of <paramref name="key"/> as it reads in <paramref name="mode"/>: the mode's own text ("{key}.Repair") where the
    /// mode needs other words (a Repair is not "Setup was stopped"; Diagnostics, which changes nothing, points to Setup),
    /// otherwise the common text.
    /// </summary>
    public string Text(string key, SetupMode mode) => Catalog.ContainsKey($"{key}.{mode}") ? Text($"{key}.{mode}") : Text(key);

    public string Format(string key, params object?[] args)
    {
        var values = args.Select(a => (object)Data(Convert.ToString(a, CultureInfo.InvariantCulture))).ToArray();
        return string.Format(CultureInfo.InvariantCulture, Text(key), values);
    }

    /// <summary>Data shown in or as localized text (a value cell, a placeholder): isolated for the reading direction of this language.</summary>
    public string Data(string? value) => Bidi.Isolate(value, IsRightToLeft);

    /// <summary>
    /// Several data values for one placeholder (<see cref="Text(string)"/> filled with string.Format): each isolated as
    /// <see cref="Data"/>, joined with this language's list separator.
    /// </summary>
    public string DataList(IEnumerable<string> values) => string.Join(IsRightToLeft ? "، " : ", ", values.Select(Data));

    public string StepTitle(StepId step) => Text($"step.{step}.title");

    public string StepStatus(StepId step, StepState state) =>
        state == StepState.Pending ? Text("step.pending") : Text($"step.{step}.{state}");

    public string FindingTitle(FindingCode code) => Text($"finding.{code}.title");

    public string FindingText(FindingCode code) => Text($"finding.{code}.text");

    public string FindingText(FindingCode code, SetupMode mode) => Text($"finding.{code}.text", mode);

    public string OutcomeTitle(FlowOutcome outcome) => Text($"outcome.{outcome}.title");

    public string OutcomeTitle(FlowOutcome outcome, SetupMode mode) => Text($"outcome.{outcome}.title", mode);

    public string OutcomeText(FlowOutcome outcome) => Text($"outcome.{outcome}.text");

    public string OutcomeText(FlowOutcome outcome, SetupMode mode) => Text($"outcome.{outcome}.text", mode);

    public string ItemName(VerificationItem item) => Text($"check.{item}");

    public string StatusName(CheckStatus status) => Text($"status.{status}");

    public string ConnectionName(ConnectionType connection) => Text($"connection.{connection}");

    public string ConfidenceName(MatchConfidence confidence) => Text($"confidence.{confidence}");

    public string ModeSubtitle(SetupMode mode) => Text($"header.subtitle.{mode}");

    public string ProgressTitle(SetupMode mode) => Text($"progress.title.{mode}");

    /// <summary>One sentence for an action in a phase: the action's own wording when it starts, finishes, is skipped or fails; a phase text while a driver package is downloaded, checked or unpacked.</summary>
    public string ProgressText(ActionKind? action, ProgressPhase phase)
    {
        if (action is null)
            return Text($"phase.{phase}");

        return phase switch
        {
            ProgressPhase.Started or ProgressPhase.Installing or ProgressPhase.Configuring => Text($"action.{action}.running"),
            ProgressPhase.Completed => Text($"action.{action}.done"),
            ProgressPhase.Skipped => Text($"action.{action}.skipped"),
            ProgressPhase.Failed => Text($"action.{action}.failed"),
            _ => Text($"phase.{phase}"),
        };
    }

    private static FrozenDictionary<string, Entry> BuildCatalog()
    {
        var catalog = new CatalogBuilder();
        AddInterface(catalog);
        AddSteps(catalog);
        AddActions(catalog);
        AddEnvironmentAndDetectionFindings(catalog);
        AddDriverAndQueueFindings(catalog);
        AddSettingsFindings(catalog);
        AddResults(catalog);
        AddTestPrint(catalog);
        AddTicketCheck(catalog);
        return catalog.Entries.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private sealed record Entry(string English, string Arabic)
    {
        public string For(UiLanguage language) => language == UiLanguage.Arabic ? Arabic : English;
    }

    /// <summary>A duplicate key fails type initialization, so every test catches it.</summary>
    private sealed class CatalogBuilder
    {
        public Dictionary<string, Entry> Entries { get; } = new(StringComparer.Ordinal);

        public void Add(string key, string english, string arabic) => Entries.Add(key, new Entry(english, arabic));
    }
}
