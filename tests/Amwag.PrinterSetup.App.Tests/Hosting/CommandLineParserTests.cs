using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.App.Hosting;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.App.Tests.Hosting;

public sealed class CommandLineParserTests
{
    private const string Pipe = "AMWAG.PrinterSetup.0123456789abcdef0123456789abcdef";
    private const string Session = "0123456789ab";
    private const string ConfigSha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    [Fact]
    public void No_arguments_start_the_window_in_setup_mode()
    {
        var options = CommandLineParser.Parse([]);

        Assert.Empty(options.Errors);
        Assert.Equal(AppRole.Interactive, options.Role);
        Assert.Equal(SetupMode.Setup, options.Mode);
        Assert.False(options.ShowHelp);
        Assert.Null(options.Address);
    }

    [Theory]
    [InlineData(new[] { "--mode", "repair" }, SetupMode.Repair)]
    [InlineData(new[] { "--mode=diagnostics" }, SetupMode.Diagnostics)]
    [InlineData(new[] { "/mode", "Repair" }, SetupMode.Repair)]
    [InlineData(new[] { "/MODE=SETUP" }, SetupMode.Setup)]
    [InlineData(new[] { "--Mode", "DIAGNOSTICS" }, SetupMode.Diagnostics)]
    public void Mode_accepts_both_value_forms_both_prefixes_and_any_case(string[] args, SetupMode expected)
    {
        var options = CommandLineParser.Parse(args);

        Assert.Empty(options.Errors);
        Assert.Equal(expected, options.Mode);
    }

    [Fact]
    public void All_public_switches_are_read()
    {
        var options = CommandLineParser.Parse(
        [
            "--mode", "repair", "--address", "10.20.30.40", "--model", "P-4030DN", "--confirm-model", "--lang", "AR",
            "--report", "out\\report.txt", "--config=cfg.json", "--allow-unconfirmed-dimensions", "--silent",
        ]);

        Assert.Empty(options.Errors);
        Assert.Equal(AppRole.Silent, options.Role);
        Assert.Equal(SetupMode.Repair, options.Mode);
        Assert.Equal("10.20.30.40", options.Address);
        Assert.Equal("P-4030DN", options.ModelId);
        Assert.True(options.ConfirmModel);
        Assert.Equal("ar", options.Language);
        Assert.Equal(Path.GetFullPath("out\\report.txt"), options.ReportPath);
        Assert.Equal(Path.GetFullPath("cfg.json"), options.ConfigPath);
        Assert.True(options.AllowUnconfirmedDimensions);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("/help")]
    [InlineData("/?")]
    [InlineData("--HELP")]
    public void Help_switches(string argument)
    {
        var options = CommandLineParser.Parse([argument]);

        Assert.Empty(options.Errors);
        Assert.True(options.ShowHelp);
    }

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("192.168.100.254")]
    [InlineData("0.0.0.0")]
    [InlineData("printer-01.branch.amwag.local")]
    [InlineData("PRN-CAIRO01")]
    [InlineData("fe80::1")]
    [InlineData("2001:db8::42")]
    public void Valid_addresses(string address)
    {
        var options = CommandLineParser.Parse(["--address", address]);

        Assert.Empty(options.Errors);
        Assert.Equal(address, options.Address);
    }

    [Theory]
    [InlineData("[2001:db8::5]", "2001:db8::5")]
    [InlineData("::ffff:192.168.1.20", "192.168.1.20")]
    [InlineData("printer.branch.local.", "printer.branch.local")]
    public void Addresses_are_stored_in_their_canonical_spelling(string address, string expected)
    {
        var options = CommandLineParser.Parse(["--address", address]);

        Assert.Empty(options.Errors);
        Assert.Equal(expected, options.Address);
    }

    [Theory]
    [InlineData("10.0.0.256")]
    [InlineData("010.0.0.1")]
    [InlineData("10.0.0")]
    [InlineData("10.0.0.5.6")]
    [InlineData("printer name")]
    [InlineData("-printer")]
    [InlineData("printer..local")]
    [InlineData("10.0.0.5:9100")]
    [InlineData("[2001:db8::5]:9101")] // a bracketed IPv6 address with a port would otherwise lose the port silently
    [InlineData("PRN_CAIRO01")] // the probe cannot use NetBIOS-style names with underscores
    [InlineData("http://10.0.0.5")]
    public void Invalid_addresses_are_errors(string address)
    {
        var options = CommandLineParser.Parse(["--address", address]);

        Assert.Contains(options.Errors, e => e.Contains("--address", StringComparison.Ordinal));
        Assert.Null(options.Address);
    }

    [Theory]
    [InlineData(new[] { "--frobnicate" }, "Unknown switch")]
    [InlineData(new[] { "stray" }, "Unexpected argument")]
    [InlineData(new[] { "-mode", "setup" }, "Unexpected argument")]
    [InlineData(new[] { "--mode" }, "Missing value for --mode")]
    [InlineData(new[] { "--mode", "--silent" }, "Missing value for --mode")]
    [InlineData(new[] { "--mode", "install" }, "Invalid --mode")]
    [InlineData(new[] { "--lang", "fr" }, "Invalid --lang")]
    [InlineData(new[] { "--silent=yes" }, "does not take a value")]
    [InlineData(new[] { "--mode", "setup", "--mode", "repair" }, "more than once")]
    [InlineData(new[] { "--model", "P 4030/DN" }, "Invalid --model")]
    [InlineData(new[] { "--confirm-model" }, "--confirm-model needs both --model and --address")]
    [InlineData(new[] { "--confirm-model", "--model", "P-4030DN" }, "--confirm-model needs both --model and --address")]
    [InlineData(new[] { "--confirm-model", "--address", "10.0.0.5" }, "--confirm-model needs both --model and --address")]
    [InlineData(new[] { "--report", "" }, "Invalid --report path")]
    [InlineData(new[] { "--pipe", Pipe }, "only valid with --worker")]
    [InlineData(new[] { "--model-asserted" }, "only valid with --worker")]
    [InlineData(new[] { "--config-sha256", "embedded" }, "only valid with --worker")]
    [InlineData(new[] { "--branch-sha256", "none", "--silent" }, "only valid with --worker")]
    [InlineData(new[] { "--window-pid", "4242" }, "only valid with --worker")]
    public void Problems_are_collected_as_errors(string[] args, string expected)
    {
        var options = CommandLineParser.Parse(args);

        Assert.Contains(options.Errors, e => e.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Several_problems_are_all_reported()
    {
        var options = CommandLineParser.Parse(["--frobnicate", "--mode", "x", "--lang"]);

        Assert.Equal(3, options.Errors.Count);
    }

    [Fact]
    public void A_slash_value_that_is_not_a_switch_name_is_a_value()
    {
        var options = CommandLineParser.Parse(["--report", "/reports/run.txt"]);

        Assert.Empty(options.Errors);
        Assert.Equal(Path.GetFullPath("/reports/run.txt"), options.ReportPath);
    }

    [Fact]
    public void Confirm_model_with_model_and_address_is_valid()
    {
        var options = CommandLineParser.Parse(["--address", "10.0.0.5", "--model", "P-5032DN", "--confirm-model", "--silent"]);

        Assert.Empty(options.Errors);
        Assert.True(options.ConfirmModel);
    }

    [Fact]
    public void A_complete_worker_command_line_is_valid()
    {
        var options = CommandLineParser.Parse(WorkerArgs(), out ConfigurationPin? pin);

        Assert.Empty(options.Errors);
        Assert.Equal(new ConfigurationPin(ConfigSha256, ConfigurationPin.NoBranch), pin);
        Assert.Equal(AppRole.Worker, options.Role);
        Assert.Equal(Pipe, options.PipeName);
        Assert.Equal(4242, options.WindowProcessId);
        Assert.Equal(Session, options.SessionId);
        Assert.Equal("queue:AMWAG Ticket Printer", options.TargetKey);
        Assert.Equal(SetupMode.Repair, options.Mode);
        Assert.Equal("P-4030DN", options.ModelId);
        Assert.True(options.ModelAssertedByUser);
        Assert.True(options.AllowUnconfirmedDimensions);
    }

    [Theory]
    [InlineData("spooler:start")]
    [InlineData(@"usb:USBPRINT\UTAX_TAP-4030DN1652\7&2B5A1C3&0&VCF5X01234")]
    [InlineData("net:10.0.0.5")]
    [InlineData("queue:TA P-4030DN KX (Copy 1)")]
    public void Worker_target_keys(string key)
    {
        var options = CommandLineParser.Parse(WorkerArgs(target: key));

        Assert.Empty(options.Errors);
        Assert.Equal(key, options.TargetKey);
    }

    public static TheoryData<string[]> InvalidWorkerCommandLines() => new()
    {
        WorkerArgs(pipe: "AMWAG.PrinterSetup.0123456789ABCDEF0123456789ABCDEF"),
        WorkerArgs(pipe: "AMWAG.PrinterSetup.0123456789abcdef"),
        WorkerArgs(pipe: "Other.0123456789abcdef0123456789abcdef"),
        WorkerArgs(pipe: Pipe + "\n"),
        WorkerArgs(session: "0123456789a"),
        WorkerArgs(session: new string('a', 33)),
        WorkerArgs(session: "0123456789xy"),
        WorkerArgs(target: "printer"),
        WorkerArgs(target: "usb:"),
        WorkerArgs(target: "queue: "),
        WorkerArgs(target: "queue:bad\u0007name"),
        WorkerArgs(target: "spooler:stop"),
        WorkerArgs(target: "queue:" + new string('q', 507)),
        WorkerArgs(mode: "diagnostics"),
        WorkerArgs(mode: null),
        WorkerArgs(pipe: null),
        WorkerArgs(session: null),
        WorkerArgs(target: null),
        WorkerArgs(model: null),
        WorkerArgs().Append("--silent").ToArray(),
        WorkerArgs().Concat(["--report", "x.txt"]).ToArray(),
        WorkerArgs().Append("--confirm-model").ToArray(),
        WorkerArgs().Concat(["--config", @"C:\Users\Public\other-config.json"]).ToArray(),
        WorkerArgs(configPin: null),
        WorkerArgs(branchPin: null),
        WorkerArgs(configPin: ConfigSha256.ToUpperInvariant()),
        WorkerArgs(configPin: ConfigSha256[..63]),
        WorkerArgs(configPin: ConfigSha256 + "0"),
        WorkerArgs(configPin: ConfigSha256 + "\n"),
        WorkerArgs(configPin: "none"),
        WorkerArgs(configPin: "Embedded"),
        WorkerArgs(branchPin: "embedded"),
        WorkerArgs(branchPin: ""),
        WorkerArgs(windowPid: null),
        WorkerArgs(windowPid: ""),
        WorkerArgs(windowPid: "0"),
        WorkerArgs(windowPid: "-4242"),
        WorkerArgs(windowPid: "+4242"),
        WorkerArgs(windowPid: "04242"),
        WorkerArgs(windowPid: " 4242"),
        WorkerArgs(windowPid: "4242\n"),
        WorkerArgs(windowPid: "0x1092"),
        WorkerArgs(windowPid: "4,242"),
        WorkerArgs(windowPid: "٤٢٤٢"), // Arabic-Indic digits
        WorkerArgs(windowPid: "2147483648"),
        WorkerArgs(windowPid: "99999999999"),
    };

    [Theory]
    [MemberData(nameof(InvalidWorkerCommandLines))]
    public void Worker_arguments_are_validated_strictly(string[] args)
    {
        var options = CommandLineParser.Parse(args);

        Assert.Equal(AppRole.Worker, options.Role);
        Assert.NotEmpty(options.Errors);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("4", 4)]
    [InlineData("2147483647", int.MaxValue)]
    public void The_window_process_id_is_read_as_a_positive_int(string value, int expected)
    {
        var options = CommandLineParser.Parse(WorkerArgs(windowPid: value));

        Assert.Empty(options.Errors);
        Assert.Equal(expected, options.WindowProcessId);
    }

    [Fact]
    public void A_worker_without_the_window_process_id_is_refused()
    {
        var options = CommandLineParser.Parse(WorkerArgs(windowPid: null));

        Assert.Contains("--window-pid is required with --worker.", options.Errors);
        Assert.Null(options.WindowProcessId);
    }

    [Fact]
    public void The_usage_tells_which_modes_return_4_and_what_diagnostics_returns_instead()
    {
        string usage = string.Join(' ', CommandLineParser.Usage.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries));

        Assert.Contains("4 (setup and repair) several printers or a model to confirm", usage, StringComparison.Ordinal);
        Assert.Contains("cannot choose between two connected USB printers of the same model", usage, StringComparison.Ordinal);
        Assert.Contains("Diagnostics never returns 4: several printers without a known ticket printer give 1 if one is ready, else 2; a model to confirm gives 2.", usage, StringComparison.Ordinal);
    }

    [Fact]
    public void Worker_target_key_of_exactly_512_characters_is_accepted()
    {
        string key = "queue:" + new string('q', 506);

        var options = CommandLineParser.Parse(WorkerArgs(target: key));

        Assert.Empty(options.Errors);
    }

    /// <summary>A valid worker command line; null leaves a switch out (--model also drops --model-asserted).</summary>
    private static string[] WorkerArgs(
        string? pipe = Pipe,
        string? session = Session,
        string? target = "queue:AMWAG Ticket Printer",
        string? mode = "repair",
        string? model = "P-4030DN",
        string? configPin = ConfigSha256,
        string? branchPin = "none",
        string? windowPid = "4242")
    {
        var args = new List<string> { "--worker" };
        if (pipe is not null)
            args.AddRange(["--pipe", pipe]);
        if (windowPid is not null)
            args.AddRange(["--window-pid", windowPid]);
        if (session is not null)
            args.AddRange(["--session", session]);
        if (target is not null)
            args.AddRange(["--target", target]);
        if (mode is not null)
            args.AddRange(["--mode", mode]);
        args.Add("--model-asserted");
        if (model is not null)
            args.AddRange(["--model", model]);
        args.Add("--allow-unconfirmed-dimensions");
        if (configPin is not null)
            args.AddRange(["--config-sha256", configPin]);
        if (branchPin is not null)
            args.AddRange(["--branch-sha256", branchPin]);
        return [.. args];
    }
}
