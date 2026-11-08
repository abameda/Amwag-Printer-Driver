using System.Globalization;

namespace Amwag.PrinterSetup.App.Hosting;

/// <summary>The few message boxes shown before, or instead of, the main window (English or Arabic).</summary>
internal static class HostDialogs
{
    public static void ShowHelp(string? language) =>
        Show(language, CommandLineParser.Usage, MessageBoxIcon.Information);

    public static void ShowInvalidArguments(string? language, IReadOnlyList<string> errors) =>
        Show(language, Pick(language, "The command line is not valid:", "سطر الأوامر غير صالح:") + Bullets(errors) +
            Environment.NewLine + Environment.NewLine + CommandLineParser.Usage, MessageBoxIcon.Warning);

    public static void ShowAlreadyRunning(string? language) =>
        Show(language, Pick(language, "AMWAG Printer Setup is already open.", "برنامج إعداد طابعة AMWAG مفتوح بالفعل."), MessageBoxIcon.Information);

    public static void ShowConfigurationErrors(string? language, string source, IReadOnlyList<string> errors, string? logFile) =>
        Show(language,
            Pick(language,
                "The printer setup configuration is not valid, so nothing was changed. Please contact IT support.",
                "إعدادات برنامج تهيئة الطابعة غير صالحة، لذلك لم يتم تغيير أي شيء. يرجى التواصل مع الدعم الفني.") +
            Environment.NewLine + Environment.NewLine + source + Bullets(errors) + LogLine(language, logFile),
            MessageBoxIcon.Error);

    public static void ShowStartupError(string? language, string detail, string? logFile) =>
        Show(language,
            Pick(language, "AMWAG Printer Setup cannot start with these settings:", "لا يمكن تشغيل برنامج إعداد طابعة AMWAG بهذه الإعدادات:") +
            Bullets([detail]) + LogLine(language, logFile),
            MessageBoxIcon.Error);

    public static void ShowUnexpectedError(string? language, string? logFile) =>
        Show(language,
            Pick(language,
                "Something went wrong and AMWAG Printer Setup has to close. Please contact IT support.",
                "حدث خطأ غير متوقع وسيتم إغلاق برنامج إعداد طابعة AMWAG. يرجى التواصل مع الدعم الفني.") +
            LogLine(language, logFile),
            MessageBoxIcon.Error);

    private static void Show(string? language, string text, MessageBoxIcon icon)
    {
        bool arabic = IsArabic(language);
        MessageBox.Show(
            text,
            arabic ? "إعداد طابعة AMWAG" : "AMWAG Printer Setup",
            MessageBoxButtons.OK,
            icon,
            MessageBoxDefaultButton.Button1,
            arabic ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : 0);
    }

    private static bool IsArabic(string? language) =>
        (language ?? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName) == "ar";

    private static string Pick(string? language, string english, string arabic) => IsArabic(language) ? arabic : english;

    private static string Bullets(IEnumerable<string> lines) =>
        string.Concat(lines.Select(line => Environment.NewLine + "• " + line));

    private static string LogLine(string? language, string? logFile) =>
        logFile is null ? "" : Environment.NewLine + Environment.NewLine + Pick(language, "Log file: ", "ملف السجل: ") + logFile;
}
