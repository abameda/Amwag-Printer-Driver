using System.Globalization;
using System.Text;
using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.App.Support;

/// <summary>The Contact IT request: the text typed into the WhatsApp chat (also summary.txt in the log archive) and the chat link.</summary>
internal static class SupportMessage
{
    private const int MaxProblems = 4;

    /// <summary>English, for IT: where it happened, what the window was doing and the session id that finds its log lines.</summary>
    /// <param name="result">The result on screen, or null while a run is in progress.</param>
    public static string Compose(AppRuntime runtime, SetupMode mode, FlowResult? result, DateTimeOffset time)
    {
        var config = runtime.Environment.Config;
        var branch = runtime.Environment.Branch;
        string branchText = (branch?.BranchName, branch?.BranchId) switch
        {
            ({ } name, { } id) => $"{name} ({id})",
            ({ } name, null) => name,
            (null, { } id) => id,
            _ => "not set",
        };

        var text = new StringBuilder();
        text.AppendLine("AMWAG Printer Setup: request for IT help");
        text.AppendLine("Branch: " + branchText);
        text.AppendLine($"Computer: {Environment.MachineName}, user {Environment.UserName}");
        text.AppendLine($"Version: {runtime.AppVersion}, configuration {config.ConfigVersion}");
        text.AppendLine("Mode: " + mode);
        text.AppendLine("Result: " + ResultText(result));
        text.AppendLine("Session: " + runtime.Log.SessionId);
        text.AppendLine("Time: " + time.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture));
        text.Append("The picture of the window and the log files follow.");
        return text.ToString();
    }

    /// <summary>WhatsApp's documented click-to-chat link: opens a chat with <paramref name="contact"/> with the text already typed.</summary>
    public static string WhatsAppLink(ItContact contact, string message) =>
        "https://wa.me/" + contact.WhatsApp + "?text=" + Uri.EscapeDataString(message);

    private static string ResultText(FlowResult? result)
    {
        if (result is null)
            return "not finished yet";

        string[] problems = result.CurrentFindings
            .Where(f => f.Severity != Severity.Info)
            .OrderByDescending(f => f.Severity)
            .Select(f => f.Code.ToString())
            .Distinct()
            .Take(MaxProblems)
            .ToArray();
        return problems.Length == 0 ? result.Outcome.ToString() : $"{result.Outcome} ({string.Join(", ", problems)})";
    }
}
