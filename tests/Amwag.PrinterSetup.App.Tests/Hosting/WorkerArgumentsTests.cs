using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.App.Hosting;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.App.Tests.Hosting;

public sealed class WorkerArgumentsTests
{
    private const string Pipe = "AMWAG.PrinterSetup.fedcba9876543210fedcba9876543210";
    private const string Session = "a1b2c3d4e5f6";
    private const string Sha256 = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "\"\"")]
    [InlineData("two words", "\"two words\"")]
    [InlineData(@"C:\path\file.json", @"C:\path\file.json")]
    [InlineData(@"C:\with space\", "\"C:\\with space\\\\\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("a\\\"b", "\"a\\\\\\\"b\"")]
    [InlineData("tab\there", "\"tab\there\"")]
    public void Quote_follows_the_Windows_rules(string argument, string expected)
    {
        Assert.Equal(expected, WorkerArguments.Quote(argument));
    }

    public static TheoryData<string> AwkwardArguments() => new()
    {
        "",
        "plain",
        "two words",
        @"ends with backslash\",
        @"ends with two backslashes\\",
        "quote\"inside",
        "\"quoted\"",
        "backslash before quote\\\"x",
        "two backslashes before quote\\\\\"x",
        @"\\server\share\path with spaces\",
        "tab\tand  double  spaces",
        "queue:TA P-4030DN KX (\"front desk\")\\",
    };

    [Theory]
    [MemberData(nameof(AwkwardArguments))]
    public void Quoted_arguments_split_back_unchanged(string argument)
    {
        string commandLine = string.Join(' ', new[] { "first", argument, "last" }.Select(WorkerArguments.Quote));

        Assert.Equal(new[] { "first", argument, "last" }, WindowsCommandLine.Split(commandLine));
    }

    [Theory]
    [Trait("Category", "WindowsIntegration")]
    [MemberData(nameof(AwkwardArguments))]
    public void Quoted_arguments_split_back_unchanged_by_CommandLineToArgvW(string argument)
    {
        string commandLine = string.Join(' ', new[] { "first", argument, "last" }.Select(WorkerArguments.Quote));

        Assert.Equal(new[] { "first", argument, "last" }, WindowsCommandLine.SplitWithWindows(commandLine));
    }

    public static TheoryData<string, string?, bool> Targets() => new()
    {
        { "queue:AMWAG Ticket Printer", "P-4030DN", false },
        { "queue:Front \"desk\" printer\\", "P-4531DN", true },
        { @"usb:USBPRINT\UTAX_TAP-5032DN07F2\7&2B5A1C3&0&VCF5X01234", "P-5032DN", false },
        { "net:10.0.0.42", "P-4030DN", true },
        { "net:fe80::1", null, false },
        { WorkerArguments.SpoolerStartTarget, null, false },
    };

    [Theory]
    [MemberData(nameof(Targets))]
    public void Worker_arguments_round_trip_through_the_parser(string targetKey, string? modelId, bool asserted)
    {
        var request = new WorkerRequest(SetupMode.Setup, new TargetSelector(targetKey, modelId, asserted), AllowUnconfirmedDimensions: asserted, Session);
        var pin = asserted ? new ConfigurationPin(Sha256, ConfigurationPin.NoBranch) : new ConfigurationPin(ConfigurationPin.Embedded, Sha256);

        string arguments = WorkerArguments.Build(request, Pipe, 31337, "printer-07.branch.local", pin);
        var options = CommandLineParser.Parse(WindowsCommandLine.Split(arguments), out ConfigurationPin? parsedPin);

        Assert.Empty(options.Errors);
        Assert.Equal(pin, parsedPin);
        Assert.Equal(AppRole.Worker, options.Role);
        Assert.Equal(Pipe, options.PipeName);
        Assert.Equal(31337, options.WindowProcessId);
        Assert.Equal(Session, options.SessionId);
        Assert.Equal(targetKey, options.TargetKey);
        Assert.Equal(modelId, options.ModelId);
        Assert.Equal(asserted && modelId is not null, options.ModelAssertedByUser);
        Assert.Equal(SetupMode.Setup, options.Mode);
        Assert.Equal(asserted, options.AllowUnconfirmedDimensions);
        Assert.Equal("printer-07.branch.local", options.Address);
        Assert.Null(options.ConfigPath);
    }

    [Fact]
    public void Worker_arguments_without_optional_parts()
    {
        var request = new WorkerRequest(SetupMode.Repair, new TargetSelector("queue:Q"), false, Session);

        string arguments = WorkerArguments.Build(request, Pipe, 4, address: null, new ConfigurationPin(ConfigurationPin.Embedded, ConfigurationPin.NoBranch));
        var options = CommandLineParser.Parse(WindowsCommandLine.Split(arguments));

        Assert.Equal($"--worker --pipe {Pipe} --window-pid 4 --session {Session} --target queue:Q --mode repair --config-sha256 embedded --branch-sha256 none", arguments);
        Assert.Empty(options.Errors);
        Assert.Null(options.Address);
        Assert.Null(options.ModelId);
        Assert.False(options.AllowUnconfirmedDimensions);
    }

    [Fact]
    public void Valid_requests_have_no_problem()
    {
        Assert.Null(WorkerArguments.FindProblem(new WorkerRequest(SetupMode.Setup, new TargetSelector("net:10.0.0.5", "P-4030DN", true), false, Session)));
    }

    [Theory]
    [InlineData(SetupMode.Diagnostics, "queue:Q", Session, null)]
    [InlineData(SetupMode.Setup, "queue:Q", "test-session", null)]
    [InlineData(SetupMode.Setup, "printer", Session, null)]
    [InlineData(SetupMode.Setup, "queue:Q", Session, "P/4030")]
    public void Requests_the_worker_would_reject_are_found_before_starting_it(SetupMode mode, string key, string session, string? modelId)
    {
        Assert.NotNull(WorkerArguments.FindProblem(new WorkerRequest(mode, new TargetSelector(key, modelId), false, session)));
    }
}
