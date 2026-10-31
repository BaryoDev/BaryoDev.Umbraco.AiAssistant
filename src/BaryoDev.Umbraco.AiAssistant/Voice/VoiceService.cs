using System.Text.Json;
using System.Text.RegularExpressions;
using BaryoDev.Umbraco.AiAssistant.Claude;
using BaryoDev.Umbraco.AiAssistant.Polish;

namespace BaryoDev.Umbraco.AiAssistant.Voice;

/// <summary>Proposes writing rules from sample articles. The editor reads and edits the result before anything is saved.</summary>
public sealed class VoiceService(IClaudeClient claude)
{
    public const int MaxArticles = 10;
    public const int MaxCharactersPerArticle = 6000;
    public const int MaxRulesLength = 4000;

    public const string System = """
        You describe the writing voice of a website from sample articles, as short rules for an editor.
        - Write 4 to 8 rules. Each is one line that starts with "- ".
        - Cover tone, sentence length, vocabulary, how headings and openings are written, and words or habits to avoid.
        - Describe style only. Do not quote the articles and do not repeat their facts, names or numbers.
        - Do not write rules about facts, translation or answer format. Those are fixed elsewhere.
        """ + "\n\n" + DataBlock.Warning + "\nEach sample is in its own <article> block.\n" + """

        Answer with one JSON object and nothing else: {"rules": "- first rule\n- second rule"}
        """;

    public async Task<string> DraftAsync(IReadOnlyList<string> articles, CancellationToken cancellationToken)
    {
        var usable = articles.Where(a => !string.IsNullOrWhiteSpace(a)).Take(MaxArticles).ToList();
        if (usable.Count == 0) throw new ArgumentException("None of the chosen pages has text to learn from.");
        string user = "Describe the voice of these articles.\n" + string.Join('\n',
            usable.Select(a => DataBlock.Wrap("article", a.Length > MaxCharactersPerArticle ? a[..MaxCharactersPerArticle] : a)));
        ClaudeResponse response = await claude.CompleteAsync(new ClaudeRequest(System, user, null, 4000, "low"), cancellationToken);
        string rules = CleanRules(Parse(response.Text));
        if (rules.Length == 0) throw new ClaudeException("Claude did not return any rules. Try again.");
        return rules;
    }

    internal static string Parse(string raw)
    {
        string json = raw.Trim();
        Match fence = Regex.Match(json, @"^```(?:json)?\s*(.*?)\s*```$", RegexOptions.Singleline);
        if (fence.Success) json = fence.Groups[1].Value;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("rules", out JsonElement r) && r.ValueKind == JsonValueKind.String)
                return r.GetString()!;
        }
        catch (JsonException) { }
        return raw;
    }

    /// <summary>Keeps only "- " lines, at most 12, within the length limit.</summary>
    internal static string CleanRules(string text)
    {
        var lines = text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("- ") && l.Length > 2)
            .Take(12);
        string joined = string.Join('\n', lines);
        return joined.Length > MaxRulesLength ? joined[..MaxRulesLength] : joined;
    }
}
