using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using BaryoDev.Umbraco.AiAssistant.Claude;
using BaryoDev.Umbraco.AiAssistant.Polish;

namespace BaryoDev.Umbraco.AiAssistant.Translate;

public sealed class TranslateService(IClaudeClient claude)
{
    public const string System = """
        You translate website text from one language into another.
        - Keep the meaning, tone and paragraph structure. Do not add, remove or explain anything.
        - Keep names of people, places, organisations and products as they are written.
        - Keep every number and date value the same. Write dates and numbers in the target language's usual style.
        - Keep quoted terms exactly as quoted.
        - If the text contains HTML, keep every tag and attribute unchanged and translate only the text between them.
        """ + "\n\n" + DataBlock.Warning + "\nThe text to translate is in a <source> block.\n" + """

        Answer with one JSON object and nothing else: {"text": "the translation"}
        """;

    public async Task<string> TranslateAsync(string text, string? sourceCulture, string targetCulture, bool isRich, int maxCharacters, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("There is no text to translate.");
        if (text.Length > maxCharacters) throw new ArgumentException($"The text is longer than the limit of {maxCharacters} characters.");
        string user = $"Translate from {Name(sourceCulture)} to {Name(targetCulture)}.{(isRich ? " The text is HTML." : "")}\n" + DataBlock.Wrap("source", text);
        ClaudeResponse response = await claude.CompleteAsync(new ClaudeRequest(System, user, null, 16000), cancellationToken);
        return Parse(response.Text);
    }

    public static string Name(string? culture)
    {
        if (string.IsNullOrWhiteSpace(culture)) return "the source language";
        try { return $"{CultureInfo.GetCultureInfo(culture).EnglishName} ({culture})"; }
        catch (CultureNotFoundException) { return culture; }
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
                && doc.RootElement.TryGetProperty("text", out JsonElement t) && t.ValueKind == JsonValueKind.String)
                return t.GetString()!;
        }
        catch (JsonException) { }
        throw new ClaudeException("Claude did not return the translation in the expected form. Try again.");
    }
}
