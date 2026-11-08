using System.Text;
using System.Text.Json;
using Amwag.PrinterSetup.App.Elevation;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.App.Tests.Elevation;

public sealed class PipeProtocolTests
{
    internal static ProgressUpdate SampleProgress() =>
        new(StepId.InstallDriver, StepState.Running, ActionKind.InstallDriver, ProgressPhase.Downloading, "Downloading KX_Driver_8.4.1013_x64.zip\r\n(42 %)")
        {
            Fraction = 0.42,
            Code = FindingCode.DriverMissing,
        };

    internal static ApplyResult SampleResult() => new()
    {
        Results =
        [
            new ActionResult(
                new PlannedAction(ActionKind.CreateForm, RequiresElevation: true, "Create form \"AMWAG Ticket\"")
                {
                    Parameters = new Dictionary<string, string> { ["form"] = "AMWAG Ticket", ["widthMicrometers"] = "82296" },
                },
                ActionOutcome.Succeeded,
                "Form created."),
            new ActionResult(new PlannedAction(ActionKind.InstallDriver, true, "Install TA P-4030DN KX"), ActionOutcome.Failed, "InstallPrinterDriverFromPackage failed.")
            {
                FailureCode = FindingCode.DriverInstallFailed,
                Win32Error = 1797,
            },
        ],
        Findings =
        [
            new Finding(FindingCode.RebootRequired, Severity.Warning, "Windows asks for a restart.")
            {
                Data = new Dictionary<string, string> { ["device"] = @"USBPRINT\UTAX_TAP-4030DN1652\7&2B5A1C3&0&VCF5X01234" },
            },
        ],
        RebootRequired = true,
    };

    [Fact]
    public void Progress_round_trips()
    {
        ProgressUpdate progress = SampleProgress();

        PipeMessage? decoded = PipeProtocol.Decode(Line(PipeMessage.ForProgress(progress)));

        Assert.NotNull(decoded);
        Assert.Equal(PipeMessageType.Progress, decoded.Type);
        Assert.Equal(progress, decoded.Progress);
    }

    [Fact]
    public void Result_round_trips()
    {
        ApplyResult result = SampleResult();

        PipeMessage? decoded = PipeProtocol.Decode(Line(PipeMessage.ForResult(result, 2)));

        Assert.NotNull(decoded);
        Assert.Equal(PipeMessageType.Result, decoded.Type);
        Assert.Equal(2, decoded.ExitCode);
        ApplyResult roundTripped = decoded.Result!;
        Assert.Equal(JsonSerializer.Serialize(result, ConfigJson.Options), JsonSerializer.Serialize(roundTripped, ConfigJson.Options));
        Assert.False(roundTripped.Succeeded);
        Assert.True(roundTripped.RebootRequired);
        Assert.Equal(2, roundTripped.Results.Count);
        Assert.Equal(ActionKind.CreateForm, roundTripped.Results[0].Action.Kind);
        Assert.Equal("82296", roundTripped.Results[0].Action.Parameters["widthMicrometers"]);
        Assert.Equal(1797, roundTripped.Results[1].Win32Error);
        Assert.Equal(FindingCode.DriverInstallFailed, roundTripped.Results[1].FailureCode);
        Assert.Equal(result.Findings[0].Data["device"], roundTripped.Findings[0].Data["device"]);
    }

    [Fact]
    public void Error_round_trips()
    {
        PipeMessage? decoded = PipeProtocol.Decode(Line(PipeMessage.ForError("Another run holds the lock.")));

        Assert.Equal(PipeMessageType.Error, decoded?.Type);
        Assert.Equal("Another run holds the lock.", decoded?.Message);
    }

    [Fact]
    public void Each_message_is_exactly_one_line_in_the_documented_shape()
    {
        byte[] bytes = PipeProtocol.Encode(PipeMessage.ForResult(SampleResult(), 0));
        string text = Encoding.UTF8.GetString(bytes);

        Assert.EndsWith("\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', text[..^1]);
        Assert.DoesNotContain('\r', text);
        using var document = JsonDocument.Parse(text);
        Assert.Equal("result", document.RootElement.GetProperty("type").GetString());
        Assert.Equal(0, document.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Equal("createForm", document.RootElement.GetProperty("result").GetProperty("results")[0].GetProperty("action").GetProperty("kind").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[1,2]")]
    [InlineData("{\"type\":\"progress\"}")]
    [InlineData("{\"type\":\"result\",\"exitCode\":0}")]
    [InlineData("{\"type\":\"error\"}")]
    [InlineData("{\"type\":\"command\",\"message\":\"install\"}")]
    [InlineData("{\"type\":7,\"message\":\"x\"}")]
    public void Malformed_or_incomplete_lines_are_rejected(string line)
    {
        Assert.Null(PipeProtocol.Decode(line));
    }

    private static string Line(PipeMessage message) => Encoding.UTF8.GetString(PipeProtocol.Encode(message)).TrimEnd('\n');
}
