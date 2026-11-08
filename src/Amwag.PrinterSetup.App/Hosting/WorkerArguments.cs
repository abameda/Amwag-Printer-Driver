using System.Globalization;
using System.Text;
using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.App.Hosting;

/// <summary>The elevated worker's command line, written here and read back by <see cref="CommandLineParser.Parse"/>.</summary>
internal static class WorkerArguments
{
    /// <summary>Target key that asks the worker only to start the Print Spooler service.</summary>
    public const string SpoolerStartTarget = "spooler:start";

    public const string PipeNamePrefix = "AMWAG.PrinterSetup.";

    /// <summary>Why the worker would reject this request, or null when it is valid (checked before any UAC prompt).</summary>
    public static string? FindProblem(WorkerRequest request)
    {
        if (request.Mode == SetupMode.Diagnostics)
            return "Diagnostics never runs elevated steps.";
        if (!CommandLineParser.IsValidSessionId(request.SessionId))
            return $"Session id '{request.SessionId}' is not 12-32 lowercase hexadecimal characters.";
        if (!CommandLineParser.IsValidTargetKey(request.Target.Key))
            return "The target key is not a valid worker target.";
        if (request.Target.ModelId is { } modelId && !CommandLineParser.IsValidModelId(modelId))
            return $"Model id '{modelId}' is not valid.";
        return null;
    }

    /// <summary>
    /// Builds the argument string (without the program name). <paramref name="windowProcessId"/> is this process, which owns
    /// <paramref name="pipeName"/>: the worker reports to no other pipe server. <paramref name="address"/> repeats the --address
    /// this process was started with, so the worker identifies the same printers; <paramref name="pin"/> is the configuration
    /// this process loaded, so the worker refuses to apply a different one.
    /// </summary>
    public static string Build(WorkerRequest request, string pipeName, int windowProcessId, string? address, ConfigurationPin pin)
    {
        var args = new List<string>
        {
            "--worker",
            "--pipe", pipeName,
            "--window-pid", windowProcessId.ToString(CultureInfo.InvariantCulture),
            "--session", request.SessionId,
            "--target", request.Target.Key,
            "--mode", CommandLineParser.ModeName(request.Mode),
            "--config-sha256", pin.Config,
            "--branch-sha256", pin.Branch,
        };
        if (request.Target.ModelId is { } modelId)
        {
            args.Add("--model");
            args.Add(modelId);
            if (request.Target.ModelAssertedByUser)
                args.Add("--model-asserted");
        }
        if (request.AllowUnconfirmedDimensions)
            args.Add("--allow-unconfirmed-dimensions");
        if (address is not null)
        {
            args.Add("--address");
            args.Add(address);
        }

        return string.Join(' ', args.Select(Quote));
    }

    /// <summary>
    /// Quotes one argument so that CommandLineToArgvW and the C runtime both read it back unchanged: backslashes are literal
    /// unless they precede a quote, so runs of them are doubled before an embedded or the closing quote.
    /// </summary>
    public static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
            return argument;

        var quoted = new StringBuilder(argument.Length + 2).Append('"');
        int backslashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            quoted.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes).Append(c);
            backslashes = 0;
        }

        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
