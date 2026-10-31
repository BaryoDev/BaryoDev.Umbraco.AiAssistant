using System.Text.Json;
using System.Text.RegularExpressions;
using BaryoDev.Umbraco.AiAssistant.Claude;
using BaryoDev.Umbraco.AiAssistant.Polish;

namespace BaryoDev.Umbraco.AiAssistant.Seo;

public sealed record SeoSuggestion(string Title, string Description, string ShareText);

/// <summary>Asks Claude for a page title, meta description and share text, then checks limits and invented facts.</summary>
public sealed partial class SeoService(IClaudeClient claude)
{
    public const int MaxTitle = 59;
    public const int MaxDescription = 154;
    public const int MaxShareText = 200;

    public const string System = """
        You write search and sharing text for one web page from its content.
        - title: under 60 characters. Say what the page is about. No clickbait, no all capitals.
        - description: at most 150 characters. This is a hard limit, so count the characters before you answer. Aim for 110 to 150. A longer description is rejected.
        - shareText: one or two sentences for a social post, under 200 characters.
        - Use only facts that are in the page: names, numbers, dates and places. Do not add any.
        - Write in the language of the page.
        """ + "\n\n" + DataBlock.Warning + "\nThe page is in a <source> block.\n" + """

        Answer with one JSON object and nothing else: {"title": "...", "description": "...", "shareText": "..."}
        """;

    public async Task<SeoSuggestion> SuggestAsync(string source, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("The page has no text to work from.");
        string user = "Write the title, description and share text for this page.\n" + DataBlock.Wrap("source", source);
        ClaudeResponse response = await claude.CompleteAsync(new ClaudeRequest(System, user, null, 3000, "low"), cancellationToken);
        return Parse(response.Text);
    }

    internal static SeoSuggestion Parse(string raw)
    {
        string json = raw.Trim();
        Match fence = Regex.Match(json, @"^```(?:json)?\s*(.*?)\s*```$", RegexOptions.Singleline);
        if (fence.Success) json = fence.Groups[1].Value;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                string Get(string n) => root.TryGetProperty(n, out JsonElement e) && e.ValueKind == JsonValueKind.String ? OneLine(e.GetString()!) : "";
                return new SeoSuggestion(Get("title"), Get("description"), Get("shareText"));
            }
        }
        catch (JsonException) { }
        throw new ClaudeException("Claude did not return the SEO text in the expected form. Try again.");
    }

    private static string OneLine(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    /// <summary>These fields are plain text, so any markup in them is refused.</summary>
    public static bool HasMarkup(SeoSuggestion s) =>
        new[] { s.Title, s.Description, s.ShareText }.Any(t => Polish.HtmlGuard.IsUnsafe("", t, allowMarkup: false));

    /// <summary>Length limit breaches and numbers or names in the suggestion that the page does not contain.</summary>
    public static IReadOnlyList<string> Problems(string source, SeoSuggestion s)
    {
        return [.. LengthProblems(s), .. FactProblems(source, s)];
    }

    /// <summary>Limit breaches only. These are advice about length, not a failed facts check.</summary>
    public static IReadOnlyList<string> LengthProblems(SeoSuggestion s)
    {
        var problems = new List<string>();
        if (s.Title.Length > MaxTitle) problems.Add($"title is {s.Title.Length} characters (limit {MaxTitle})");
        if (s.Description.Length > MaxDescription) problems.Add($"description is {s.Description.Length} characters (limit {MaxDescription})");
        if (s.ShareText.Length > MaxShareText) problems.Add($"share text is {s.ShareText.Length} characters (limit {MaxShareText})");
        return problems;
    }

    public static IReadOnlyList<string> FactProblems(string source, SeoSuggestion s)
    {
        return Invented(source, $"{s.Title}\n{s.Description}\n{s.ShareText}").Select(x => $"not in the page: {x}").ToList();
    }

    private static readonly HashSet<string> FunctionWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "the", "and", "or", "but", "nor", "of", "at", "in", "on", "to", "for", "from", "by", "with", "as", "is", "are", "be", "our", "your", "this", "that", "into", "over", "per", "vs",
    };

    /// <summary>
    /// Numbers and capitalised words in the text that the source does not contain. Case is ignored for names, because titles capitalise freely.
    /// In a heading-like line a Title Case word that is not in the source counts only when a capitalised word beside it is also not in
    /// the source, so a verb such as "Extends" passes but an invented name next to another unknown word is caught. A word with no
    /// capitalised neighbour, a word in all capitals, and a word that is capitalised outside a title line are never exempt. A word that
    /// starts the same four letters as a source word counts as present (Closes and closing).
    /// </summary>
    public static IReadOnlyList<string> Invented(string source, string text)
    {
        string plainSource = FactsCheck.Plain(source);
        var neighbours = FactsCheck.TitleLineNeighbours(FactsCheck.Plain(text));
        var sourceWords = Regex.Matches(plainSource, @"[\p{L}]+").Select(m => m.Value).ToList();

        bool Present(string name, bool exact) =>
            Regex.IsMatch(plainSource, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(name)}(?![\p{{L}}\p{{N}}])", exact ? RegexOptions.None : RegexOptions.IgnoreCase);

        bool Absent(string name) =>
            !FunctionWords.Contains(name) && !Present(name, false)
            && !(name.Length >= 5 && sourceWords.Any(w => w.Length >= 4 && w.StartsWith(name[..4], StringComparison.OrdinalIgnoreCase)));

        bool Exempt(string name) =>
            !(name.Length > 1 && name.All(c => !char.IsLetter(c) || char.IsUpper(c)))
            && neighbours.TryGetValue(name, out var near)
            && (!Absent(name) || (near.Count > 0 && !near.Any(Absent)));

        return FactsCheck.MissingDetailed(text, source, titleLines: false)
            .Where(x => char.IsDigit(x.Name[0])
                ? true
                : !FunctionWords.Contains(x.Name) && !Present(x.Name, x.Exact)
                  && !Exempt(x.Name))
            .Select(x => x.Name)
            .ToList();
    }
}
