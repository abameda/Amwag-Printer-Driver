using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.App.Localization;
using Amwag.PrinterSetup.App.TestTicket;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.App.Tests.Localization;

public sealed partial class StringsTests
{
    [GeneratedRegex(@"\p{IsArabic}")]
    private static partial Regex ArabicLetter();

    [GeneratedRegex(@"\{(\d+)\}")]
    private static partial Regex Placeholder();

    // Any literal that looks like a catalog key, except log event names (which share the "ui." and "testprint." prefixes).
    [GeneratedRegex(@"(?<!\b(?:Info|Warn|Error|Debug|Write)\(\s*)""((?:ui|header|steps|progress|card|value|connection|confidence|choose|confirm|network|model|close|result|testprint|ticketcheck|step|phase|action|finding|outcome|check|status)\.[A-Za-z0-9_.]+)""")]
    private static partial Regex KeyUse();

    public static TheoryData<string> AllKeys()
    {
        var data = new TheoryData<string>();
        foreach (string key in Strings.Keys.Order(StringComparer.Ordinal))
            data.Add(key);
        return data;
    }

    [Fact]
    public void Catalog_is_not_empty() => Assert.True(Strings.Keys.Count() > 300);

    [Theory]
    [MemberData(nameof(AllKeys))]
    public void Every_key_has_English_and_Arabic_text(string key)
    {
        Assert.False(string.IsNullOrWhiteSpace(Strings.English.Text(key)));
        Assert.False(string.IsNullOrWhiteSpace(Strings.Arabic.Text(key)));
        Assert.NotEqual(key, Strings.English.Text(key));
    }

    [Theory]
    [MemberData(nameof(AllKeys))]
    public void Arabic_text_is_written_in_Arabic(string key) =>
        Assert.Matches(ArabicLetter(), Strings.Arabic.Text(key));

    [Theory]
    [MemberData(nameof(AllKeys))]
    public void English_text_has_no_Arabic_letters(string key) =>
        Assert.DoesNotMatch(ArabicLetter(), Strings.English.Text(key));

    [Theory]
    [MemberData(nameof(AllKeys))]
    public void Placeholders_match_between_languages(string key)
    {
        static string[] Of(string text) => Placeholder().Matches(text).Select(m => m.Value).Distinct().Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(Of(Strings.English.Text(key)), Of(Strings.Arabic.Text(key)));
    }

    [Theory]
    [MemberData(nameof(AllKeys))]
    public void Never_translates_AMWAG(string key)
    {
        // The copyright line names the company by its own Arabic trade name, as the company asked.
        if (key == "footer.copyright")
        {
            Assert.Contains("أمواج ترافيل", Strings.Arabic.Text(key), StringComparison.Ordinal);
            return;
        }
        if (Strings.English.Text(key).Contains("AMWAG", StringComparison.Ordinal))
            Assert.Contains("AMWAG", Strings.Arabic.Text(key), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_enum_value_is_localized()
    {
        var missing = new List<string>();
        void Need(string key)
        {
            if (!Strings.Contains(key))
                missing.Add(key);
        }

        foreach (var code in Enum.GetValues<FindingCode>())
        {
            Need($"finding.{code}.title");
            Need($"finding.{code}.text");
        }
        foreach (var step in Enum.GetValues<StepId>())
        {
            Need($"step.{step}.title");
            foreach (var state in Enum.GetValues<StepState>().Where(s => s != StepState.Pending))
                Need($"step.{step}.{state}");
        }
        Need("step.pending");
        foreach (var outcome in Enum.GetValues<FlowOutcome>())
        {
            Need($"outcome.{outcome}.title");
            Need($"outcome.{outcome}.text");
        }
        foreach (var item in Enum.GetValues<VerificationItem>())
            Need($"check.{item}");
        foreach (var status in Enum.GetValues<CheckStatus>())
            Need($"status.{status}");
        foreach (var connection in Enum.GetValues<ConnectionType>())
            Need($"connection.{connection}");
        foreach (var confidence in Enum.GetValues<MatchConfidence>())
            Need($"confidence.{confidence}");
        foreach (var mode in Enum.GetValues<SetupMode>())
        {
            Need($"header.subtitle.{mode}");
            Need($"progress.title.{mode}");
        }
        foreach (var phase in Enum.GetValues<ProgressPhase>())
            Need($"phase.{phase}");
        foreach (var action in Enum.GetValues<ActionKind>())
        {
            foreach (string form in new[] { "running", "done", "skipped", "failed" })
                Need($"action.{action}.{form}");
        }
        foreach (var stage in Enum.GetValues<JobStage>())
            Need($"testprint.stage.{stage}");
        foreach (var problem in Enum.GetValues<JobProblems>().Where(p => p != JobProblems.None))
            Need($"testprint.problem.{problem}");
        foreach (var setting in Enum.GetValues<TicketSetting>())
            Need($"ticketcheck.setting.{setting}");
        foreach (var status in Enum.GetValues<TicketSettingStatus>())
            Need($"ticketcheck.status.{status}");

        Assert.Empty(missing);
    }

    [Fact]
    public void Every_action_and_phase_has_a_progress_sentence()
    {
        foreach (var strings in new[] { Strings.English, Strings.Arabic })
        {
            foreach (var phase in Enum.GetValues<ProgressPhase>())
            {
                Assert.True(Strings.Contains($"phase.{phase}"));
                foreach (var action in Enum.GetValues<ActionKind>())
                {
                    string text = strings.ProgressText(action, phase);
                    Assert.False(string.IsNullOrWhiteSpace(text));
                    Assert.DoesNotContain("action.", text, StringComparison.Ordinal);
                    Assert.DoesNotContain("phase.", text, StringComparison.Ordinal);
                }
            }
        }
    }

    [Fact]
    public void Keys_used_by_the_window_and_test_ticket_exist()
    {
        string appDirectory = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(SourcePath())!, "..", "..", "..", "src", "Amwag.PrinterSetup.App"));
        Assert.True(Directory.Exists(appDirectory), "App sources not found at " + appDirectory);

        var used = new[] { "UI", "TestTicket" }
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(appDirectory, folder), "*.cs", SearchOption.AllDirectories))
            .SelectMany(file => KeyUse().Matches(File.ReadAllText(file)).Select(m => m.Groups[1].Value))
            .Distinct()
            .ToList();

        Assert.NotEmpty(used);
        var missing = used.Where(key => !Strings.Contains(key)).ToList();
        Assert.True(missing.Count == 0, "Keys used in code but missing from the catalog: " + string.Join(", ", missing));
    }

    [Fact]
    public void Format_keeps_Latin_data_left_to_right_in_Arabic_only()
    {
        string arabic = Strings.Arabic.Format("connection.network", "10.0.0.5");
        string english = Strings.English.Format("connection.network", "10.0.0.5");

        Assert.Contains(Bidi.LeftToRightEmbedding + "10.0.0.5" + Bidi.PopDirectionalFormatting, arabic, StringComparison.Ordinal);
        Assert.Equal("Network, address 10.0.0.5", english);
    }

    [Theory]
    [InlineData("TA P-4030DN KX")]
    [InlineData("10.0.0.5")]
    [InlineData("AMWAG Ticket Printer (USB001)")]
    [InlineData("82.30 x 170.18 mm (3.24 x 6.70 in)")]
    public void Arabic_isolates_data_like_FSI_with_marks_on_both_sides(string value)
    {
        string isolated = Strings.Arabic.Data(value);

        // RLM before and after keep neighbouring punctuation and digits with the Arabic sentence, as an isolate would.
        Assert.Equal(Bidi.RightToLeftMark + "" + Bidi.LeftToRightEmbedding + value.Replace(' ', Bidi.NoBreakSpace) + Bidi.PopDirectionalFormatting + Bidi.RightToLeftMark, isolated);
        Assert.Equal(value, Strings.English.Data(value));
    }

    [Fact]
    public void Data_in_Arabic_script_gets_a_right_to_left_embedding()
    {
        Assert.Equal(Bidi.RightToLeftMark + "" + Bidi.RightToLeftEmbedding + "طابعة التذاكر 2" + Bidi.PopDirectionalFormatting + Bidi.RightToLeftMark, Strings.Arabic.Data("طابعة التذاكر 2"));
        Assert.Equal(Bidi.LeftToRightMark + "" + Bidi.RightToLeftEmbedding + "طابعة (2)" + Bidi.PopDirectionalFormatting + Bidi.LeftToRightMark, Strings.English.Data("طابعة (2)"));
    }

    [Fact]
    public void Long_data_keeps_ordinary_spaces_so_it_can_wrap()
    {
        string message = new string('x', Bidi.MaxUnbrokenLength) + " and more words";

        Assert.Contains(" and more words", Strings.Arabic.Data(message), StringComparison.Ordinal);
        Assert.DoesNotContain(Bidi.NoBreakSpace, Strings.Arabic.Data(message));
    }

    [Fact]
    public void The_neighbours_of_data_stay_in_the_Arabic_sentence()
    {
        // "Windows: " before the size and ": " between the error number and message were drawn inside the data runs.
        string paper = Strings.Arabic.Format("testprint.paperUsed", "AMWAG Ticket (82.3 x 170.18 mm)");
        string error = Strings.Arabic.Format("result.win32", "2", "The system cannot find the file specified.");

        Assert.Contains("Windows: " + Bidi.RightToLeftMark + Bidi.LeftToRightEmbedding, paper, StringComparison.Ordinal);
        Assert.Contains(Bidi.PopDirectionalFormatting + "" + Bidi.RightToLeftMark + ": " + Bidi.RightToLeftMark + Bidi.LeftToRightEmbedding, error, StringComparison.Ordinal);
        Assert.Equal("Windows error 2: The system cannot find the file specified.", Strings.English.Format("result.win32", "2", "The system cannot find the file specified."));
    }

    [Fact]
    public void Technical_lines_are_isolated_one_by_one()
    {
        string arabicLine = Strings.Arabic.Format("result.win32", "2", "The system cannot find the file specified.");
        string text = Bidi.IsolateLines("DriverPackageUnavailable: HTTP 404." + Environment.NewLine + arabicLine, rightToLeftContext: false);

        var lines = text.Split(Environment.NewLine);
        Assert.Equal("DriverPackageUnavailable: HTTP 404.", lines[0]);
        Assert.Equal(Bidi.LeftToRightMark + "" + Bidi.RightToLeftEmbedding + arabicLine + Bidi.PopDirectionalFormatting + Bidi.LeftToRightMark, lines[1]);
    }

    [Theory]
    [InlineData("TA P-4030DN", false)]
    [InlineData("10.0.0.5 طابعة", true)]
    [InlineData("2026-11-03 14:05", null)]
    [InlineData("", null)]
    public void First_strong_character_decides_the_direction(string text, bool? rightToLeft) =>
        Assert.Equal(rightToLeft, Bidi.FirstStrongIsRightToLeft(text));

    [Fact]
    public void Directional_marks_count_as_strong_but_embeddings_do_not()
    {
        Assert.True(Bidi.FirstStrongIsRightToLeft(Bidi.RightToLeftMark + "AMWAG"));
        Assert.False(Bidi.FirstStrongIsRightToLeft(Bidi.RightToLeftEmbedding + "AMWAG"));
        Assert.Equal(-1, Strings.Arabic.Data("AMWAG").IndexOf(Bidi.RightToLeftEmbedding));
    }

    [Fact]
    public void The_several_printers_warning_of_diagnostics_does_not_ask_for_a_choice_nobody_can_make_there()
    {
        string english = Strings.English.FindingText(FindingCode.MultipleSupportedPrinters);
        string arabic = Strings.Arabic.FindingText(FindingCode.MultipleSupportedPrinters);

        Assert.DoesNotContain("choose", english, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("اختر", arabic, StringComparison.Ordinal);
        Assert.Contains("each one was checked", english, StringComparison.Ordinal);
        Assert.Contains("فحص", arabic, StringComparison.Ordinal);
        // The other printers may be office printers, not ticket printers.
        Assert.DoesNotContain("ticket printers", Strings.English.FindingTitle(FindingCode.MultipleSupportedPrinters), StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_key_renders_as_the_key() => Assert.Equal("no.such.key", Strings.Arabic.Text("no.such.key"));

    [Theory]
    [InlineData("ar", "en-US", UiLanguage.Arabic)]
    [InlineData("AR", "en-US", UiLanguage.Arabic)]
    [InlineData("en", "ar-EG", UiLanguage.English)]
    [InlineData(null, "ar-EG", UiLanguage.Arabic)]
    [InlineData(null, "ar-SA", UiLanguage.Arabic)]
    [InlineData(null, "en-GB", UiLanguage.English)]
    [InlineData(null, "fr-FR", UiLanguage.English)]
    [InlineData("de", "ar-EG", UiLanguage.Arabic)]
    public void Language_comes_from_the_switch_then_the_Windows_display_language(string? commandLine, string culture, UiLanguage expected) =>
        Assert.Equal(expected, UiLanguages.Resolve(commandLine, CultureInfo.GetCultureInfo(culture)));

    [Fact]
    public void Right_to_left_only_for_Arabic()
    {
        Assert.True(Strings.Arabic.IsRightToLeft);
        Assert.False(Strings.English.IsRightToLeft);
    }

    private static string SourcePath([CallerFilePath] string path = "") => path;
}
