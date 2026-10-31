using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BaryoDev.Umbraco.AiAssistant.Polish;

namespace BaryoDev.Umbraco.AiAssistant.Translate;

/// <summary>
/// Facts check across two languages. Numbers must survive in any digit style (6 PM and 18:00 are the same time),
/// web addresses and quoted terms must be kept as written. Other words, names included, are never compared,
/// because they are meant to change.
/// </summary>
public static partial class TranslateFacts
{
    public static IReadOnlyList<string> Missing(string source, string translation)
    {
        string src = FactsCheck.Plain(source);
        string tr = FactsCheck.Plain(translation);
        var missing = new List<string>();

        missing.AddRange(HtmlGuard.Problems(source, translation));

        var pool = Numbers(tr).Select(Readings).ToList();
        foreach (string n in Numbers(src))
        {
            HashSet<string> readings = Readings(n);
            int hit = pool.FindIndex(r => r.Overlaps(readings));
            if (hit < 0) missing.Add(n);
            else pool.RemoveAt(hit);
        }

        string foldedTranslation = Fold(tr);
        foreach (string quoted in Quoted(src))
            if (!foldedTranslation.Contains(Fold(quoted), StringComparison.Ordinal))
                missing.Add($"\"{quoted}\"");

        foreach (string url in Urls(src))
            if (!tr.Contains(url, StringComparison.OrdinalIgnoreCase))
                missing.Add(url);

        return missing.Distinct(StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<string> Urls(string text) =>
        UrlRegex().Matches(text).Select(m => m.Value.TrimEnd('.', ',', ';', ':', ')', '!', '?')).Where(u => u.Length > 8).Distinct();

    private static IEnumerable<string> Quoted(string text) =>
        QuoteRegex().Matches(text).Select(m => m.Groups[1].Value.Trim()).Where(q => q.Length > 1);

    private static IEnumerable<string> Numbers(string text) =>
        NumberRegex().Matches(TwentyFourHour(AsciiDigits(text))).Select(m => m.Value);

    /// <summary>Writes 6 PM and 6:30 pm as 18 and 18:30, and drops a ":00", so both styles give the same numbers.</summary>
    private static string TwentyFourHour(string text)
    {
        text = Regex.Replace(text, @"(?<![\d:.,])(\d{1,2})(:\d{2})?\s*([AaPp])\.?[Mm]\.?(?![\p{L}])", m =>
        {
            int h = int.Parse(m.Groups[1].Value) % 12;
            if (char.ToLowerInvariant(m.Groups[3].Value[0]) == 'p') h += 12;
            return h + m.Groups[2].Value;
        });
        return Regex.Replace(text, @"(?<![\d:.,])(\d{1,2}):00(?!\d)", "$1");
    }

    /// <summary>
    /// The values a token can mean. 2.5 and 2,5 are both 2.5. 1,500 and 1.500 are 1500 or 1.5. Spaces and apostrophes only
    /// group digits. A decimal separator is never dropped unless it can be read as a thousands separator.
    /// </summary>
    internal static HashSet<string> Readings(string token)
    {
        string t = token.Replace('\u00A0', ' ').Replace('\u202F', ' ').Replace('\'', ' ');
        var values = new HashSet<string>();
        MatchCollection seps = Regex.Matches(t, @"[.,]");

        if (seps.Count == 0)
        {
            values.Add(Canon(t.Replace(" ", ""), ""));
            return values;
        }

        Match last = seps[^1];
        string tail = t[(last.Index + 1)..];
        string head = t[..last.Index].Replace(" ", "");
        bool mixed = seps.Select(m => m.Value).Distinct().Count() > 1;
        string beforeLast = head.Replace(".", "").Replace(",", "");

        if (mixed || (seps.Count == 1 && tail.Length != 3))
            values.Add(Canon(beforeLast, tail));
        else if (seps.Count > 1)
            values.Add(Canon(beforeLast + tail, ""));
        else
        {
            values.Add(Canon(beforeLast + tail, ""));
            values.Add(Canon(beforeLast, tail));
        }
        return values;
    }

    private static string Canon(string whole, string fraction)
    {
        whole = whole.TrimStart('0');
        if (whole.Length == 0) whole = "0";
        fraction = fraction.TrimEnd('0');
        return fraction.Length == 0 ? whole : whole + "." + fraction;
    }

    private static string AsciiDigits(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            int d = char.IsDigit(c) ? CharUnicodeInfo.GetDecimalDigitValue(c) : -1;
            sb.Append(d >= 0 ? (char)('0' + d) : c);
        }
        return sb.ToString();
    }

    /// <summary>Lower case without accents, so a name matches inside an inflected word.</summary>
    private static string Fold(string s)
    {
        string d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (char c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    [GeneratedRegex(@"\d+(?:(?:[,.\u00A0\u202F']| (?=\d{3}(?!\d)))\d{3})*(?:[.,]\d+)?")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"https?://[^\s<>""]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex("[\"“„«]([^\"”“«»]{2,80})[\"”“»]")]
    private static partial Regex QuoteRegex();
}
