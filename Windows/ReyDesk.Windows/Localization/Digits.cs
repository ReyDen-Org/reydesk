using ReyDesk.Windows.Settings;

namespace ReyDesk.Windows.Localization;

/// <summary>Localizes digits in sentence text (counts, times, percentages, sizes):
/// Persian -> Persian digits, anything else stays Latin.
/// Desk ID, Key ID, SAS, remote code, IP, port, version and build must NOT go through
/// this; they stay Latin in a mono LTR run.</summary>
internal static class Digits
{
    private const string Persian = "۰۱۲۳۴۵۶۷۸۹";

    public static string Localize(string text) => Localize(text, Strings.Current);

    public static string Localize(long value) => Localize(value.ToString(System.Globalization.CultureInfo.InvariantCulture), Strings.Current);

    public static string Localize(string text, AppLanguage language)
    {
        var table = language switch
        {
            AppLanguage.Persian => Persian,
            _ => null,
        };
        if (table is null || string.IsNullOrEmpty(text)) return text;
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (chars[i] is >= '0' and <= '9') chars[i] = table[chars[i] - '0'];
        return new string(chars);
    }
}
