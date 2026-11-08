using System.Globalization;

namespace Amwag.PrinterSetup.App.Localization;

public enum UiLanguage { English, Arabic }

public static class UiLanguages
{
    /// <summary>--lang wins ("ar" or "en"); otherwise Arabic when the Windows display language is Arabic, else English.</summary>
    public static UiLanguage Resolve(string? commandLineLanguage, CultureInfo uiCulture)
    {
        if (commandLineLanguage?.StartsWith("ar", StringComparison.OrdinalIgnoreCase) == true)
            return UiLanguage.Arabic;
        if (commandLineLanguage?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true)
            return UiLanguage.English;
        return uiCulture.TwoLetterISOLanguageName == "ar" ? UiLanguage.Arabic : UiLanguage.English;
    }
}

/// <summary>
/// Keeps data (model, queue, address, size, message) in its own reading order inside localized text, and keeps it from
/// reordering the text around it: what the Unicode isolates (FSI ... PDI) do. WinForms labels draw with GDI, which shows
/// isolate characters as visible boxes, so the same effect is built from characters GDI has always handled: an embedding
/// in the datum's own direction (LRE or RLE ... PDF, chosen from its first strong character as FSI does), with a mark of
/// the surrounding direction on both sides. Without the marks a bare embedding pulls neighbouring punctuation into the
/// datum: "استخدمه Windows: {0}" was drawn as "Windows: AMWAG Ticket ..." and "رقم {0}: {1}" merged both values into one run.
/// </summary>
public static class Bidi
{
    public const char LeftToRightEmbedding = (char)0x202A;
    public const char RightToLeftEmbedding = (char)0x202B;
    public const char PopDirectionalFormatting = (char)0x202C;
    public const char LeftToRightMark = (char)0x200E;
    public const char RightToLeftMark = (char)0x200F;
    public const char ArabicLetterMark = (char)0x061C;
    public const char NoBreakSpace = (char)0x00A0;

    /// <summary>
    /// Left-to-right data up to this length keeps its words together inside right-to-left text (non-breaking spaces): a model
    /// name split over two Arabic lines reads out of order, and GDI draws ordinary spaces between Latin words in mixed
    /// Arabic text about twice as wide. Longer data (messages) keeps its spaces so it can wrap.
    /// </summary>
    public const int MaxUnbrokenLength = 40;

    /// <summary>
    /// <paramref name="value"/> ready to be placed in text that reads right to left (<paramref name="rightToLeftContext"/>)
    /// or left to right. Left-to-right text gets data without right-to-left letters back unchanged.
    /// </summary>
    public static string Isolate(string? value, bool rightToLeftContext)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        bool rightToLeftValue = FirstStrongIsRightToLeft(value) ?? false; // no strong character (an address): left to right
        if (!rightToLeftContext && !ContainsRightToLeft(value))
            return value;

        string body = rightToLeftContext && !rightToLeftValue && value.Length <= MaxUnbrokenLength && !value.Contains('\n')
            ? value.Replace(' ', NoBreakSpace)
            : value;
        char mark = rightToLeftContext ? RightToLeftMark : LeftToRightMark;
        char embedding = rightToLeftValue ? RightToLeftEmbedding : LeftToRightEmbedding;
        return string.Concat(mark.ToString(), embedding.ToString(), body, PopDirectionalFormatting.ToString(), mark.ToString());
    }

    /// <summary>Isolates every line on its own: an embedding ends at the end of its line (paragraph).</summary>
    public static string IsolateLines(string? text, bool rightToLeftContext)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return string.Join(Environment.NewLine, lines.Select(line => line.Length == 0 ? line : Isolate(line, rightToLeftContext)));
    }

    /// <summary>The direction of the first letter or directional mark, as the bidi algorithm's first-strong rule finds it; null when there is none.</summary>
    public static bool? FirstStrongIsRightToLeft(string text)
    {
        foreach (char c in text)
        {
            if (Strong(c) is { } rightToLeft)
                return rightToLeft;
        }

        return null;
    }

    public static bool ContainsRightToLeft(string text) => text.Any(c => Strong(c) == true);

    /// <summary>
    /// True for right-to-left letters (Hebrew, Arabic, Syriac, Thaana, N'Ko, their supplements and presentation forms) and the
    /// right-to-left marks, false for other letters and the left-to-right mark, null for everything else (digits, punctuation,
    /// spaces, combining marks and the embedding characters, which the first-strong rule skips).
    /// </summary>
    private static bool? Strong(char c)
    {
        if (c is RightToLeftMark or ArabicLetterMark)
            return true;
        if (c == LeftToRightMark)
            return false;
        if (!char.IsLetter(c))
            return null;
        return c is >= (char)0x0590 and <= (char)0x08FF or >= (char)0xFB1D and <= (char)0xFDFF or >= (char)0xFE70 and <= (char)0xFEFF;
    }
}
