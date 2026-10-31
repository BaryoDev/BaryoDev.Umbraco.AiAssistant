using System.Text.RegularExpressions;

namespace BaryoDev.Umbraco.AiAssistant.Polish;

/// <summary>
/// Every number and every capitalised proper noun in the source must appear in the suggestion.
/// A capitalised word that starts a sentence, a line or a heading is not counted as a name unless it is all capitals,
/// and a word that is also written in lower case in the source is an ordinary word, unless a capitalised word
/// follows it (Will Smith is a name, "will rise" is not).
/// </summary>
public static partial class FactsCheck
{
    // titleLines false counts a capitalised word in a heading-like line too. SEO text uses that.
    public static IReadOnlyList<string> Missing(string source, string suggestion, bool titleLines = true) =>
        MissingDetailed(source, suggestion, titleLines).Select(m => m.Name).ToList();

    /// <summary>Exact is true for a name that is also an ordinary word, so it must match with the same capital letter.</summary>
    internal static IReadOnlyList<(string Name, bool Exact)> MissingDetailed(string source, string suggestion, bool titleLines)
    {
        string src = PlainBlocks(source);
        string sug = PlainBlocks(suggestion);
        var missing = new List<(string, bool)>();

        string sugNumbers = Regex.Replace(sug, @"(?<=\d),(?=\d)", "");
        foreach (string n in Numbers(src))
            if (!Regex.IsMatch(sugNumbers, $@"(?<![\d.]){Regex.Escape(n)}(?![\d]|\.\d)"))
                missing.Add((n, false));

        foreach (var (name, exact) in ProperNouns(src, titleLines))
        {
            bool acronym = name.Length > 1 && name.All(c => !char.IsLetter(c) || char.IsUpper(c));
            var options = acronym || exact ? RegexOptions.None : RegexOptions.IgnoreCase;
            if (!Regex.IsMatch(sug, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(name)}(?![\p{{L}}\p{{N}}])", options))
                missing.Add((name, exact));
        }

        return missing.Distinct().ToList();
    }

    public static string Plain(string text) =>
        System.Net.WebUtility.HtmlDecode(TagRegex().Replace(text, " "));

    /// <summary>Like Plain, but block ends become line breaks so the first word of a paragraph or heading counts as a line start.</summary>
    private static string PlainBlocks(string text) =>
        Plain(Regex.Replace(text, @"<br\s*/?>|</(p|div|li|h[1-6]|tr|blockquote)>", "\n", RegexOptions.IgnoreCase));

    private static IEnumerable<string> Numbers(string text) =>
        NumberRegex().Matches(text)
            .Select(m => m.Value.TrimEnd('.', ',').Replace(",", ""))
            .Where(v => v.Length > 0)
            .Distinct();

    private static readonly HashSet<string> Determiners = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "this", "that", "these", "those", "our", "your", "their", "his", "her", "its", "my", "in", "on", "at", "for", "of", "to",
        "and", "but", "or", "if", "when", "as", "we", "they", "it", "he", "she", "you", "i", "there", "here",
    };

    internal static IEnumerable<(string Name, bool Exact)> ProperNouns(string text, bool titleLines = true)
    {
        var found = new List<(string, bool)>();
        var words = WordRegex().Matches(text);
        var lower = new HashSet<string>(
            words.Select(m => m.Value).Where(w => !char.IsUpper(w[0])), StringComparer.OrdinalIgnoreCase);
        for (int k = 0; k < words.Count; k++)
        {
            Match m = words[k];
            string w = Regex.Replace(m.Value, "['’][sS]$", "");
            if (w.Length == 0 || !char.IsUpper(w[0])) continue;
            bool allCaps = w.Length > 1 && w.All(c => !char.IsLetter(c) || char.IsUpper(c));
            bool pair = !Determiners.Contains(w) && FollowedByCapital(text, words, k);
            bool ordinary = lower.Contains(w);
            if (!allCaps)
            {
                if (StartsSentence(text, m.Index) && !(pair && ordinary)) continue;
                if (ordinary && !pair) continue;
                if (titleLines && InTitleLine(text, m.Index)) continue;
            }
            found.Add((w, !allCaps && ordinary));
        }
        return found.Distinct();
    }

    /// <summary>
    /// Capitalised words that appear only in heading-like lines, each with the capitalised words right beside it in that line.
    /// A word that is capitalised anywhere else in the text is left out.
    /// </summary>
    internal static IReadOnlyDictionary<string, List<string>> TitleLineNeighbours(string text)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var outside = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var words = WordRegex().Matches(text);
        string Strip(string w) => Regex.Replace(w, "['’][sS]$", "");
        bool Cap(Match m) => char.IsUpper(m.Value[0]);
        for (int k = 0; k < words.Count; k++)
        {
            if (!Cap(words[k])) continue;
            string w = Strip(words[k].Value);
            if (!InTitleLine(text, words[k].Index)) { outside.Add(w); continue; }
            if (!result.TryGetValue(w, out var list)) result[w] = list = [];
            foreach (int n in new[] { k - 1, k + 1 })
            {
                if (n < 0 || n >= words.Count || !Cap(words[n])) continue;
                int from = Math.Min(k, n), to = Math.Max(k, n);
                if (text[(words[from].Index + words[from].Length)..words[to].Index].Contains('\n')) continue;
                list.Add(Strip(words[n].Value));
            }
        }
        foreach (string w in outside) result.Remove(w);
        return result;
    }

    private static bool FollowedByCapital(string text, MatchCollection words, int k)
    {
        if (k + 1 >= words.Count) return false;
        Match a = words[k], b = words[k + 1];
        string gap = text[(a.Index + a.Length)..b.Index];
        return gap.Length > 0 && gap.All(c => c is ' ' or '\t') && char.IsUpper(b.Value[0]);
    }

    private static bool StartsSentence(string text, int index)
    {
        int i = index - 1;
        while (i >= 0 && text[i] != '\n' && (char.IsWhiteSpace(text[i]) || text[i] is '"' or '\'' or '(' or '“' or '‘')) i--;
        return i < 0 || text[i] is '.' or '!' or '?' or '\n';
    }

    /// <summary>Short words a title writes in lower case between the capitalised ones.</summary>
    private static readonly HashSet<string> JoiningWords = new(StringComparer.Ordinal)
    {
        "from", "with", "into", "over", "that", "this", "than", "then", "your", "ours", "upon", "onto", "near", "past",
    };

    /// <summary>A line of three or more capitalised words with no closing punctuation is a heading, not a list of names.</summary>
    private static bool InTitleLine(string text, int index)
    {
        int start = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        if (index < start) start = 0;
        int end = text.IndexOf('\n', index);
        if (end < 0) end = text.Length;
        string lineText = text[start..end].TrimEnd();
        if (lineText.EndsWith('.') || lineText.EndsWith('!') || lineText.EndsWith('?')) return false;
        var line = WordRegex().Matches(lineText).Select(m => m.Value).Where(w => w.Length > 3 && !JoiningWords.Contains(w)).ToList();
        return line.Count >= 3 && line.All(w => char.IsUpper(w[0]));
    }

    [GeneratedRegex("<[^>]+>")] private static partial Regex TagRegex();
    [GeneratedRegex(@"\d+(?:[.,]\d+)*")] private static partial Regex NumberRegex();
    [GeneratedRegex(@"[\p{L}][\p{L}\p{N}'’-]*")] private static partial Regex WordRegex();
}
