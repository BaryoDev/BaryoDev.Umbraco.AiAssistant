using System.Text.Json;
using System.Text.RegularExpressions;
using BaryoDev.Umbraco.AiAssistant.Claude;
using BaryoDev.Umbraco.AiAssistant.Settings;
using Microsoft.Extensions.Logging;

namespace BaryoDev.Umbraco.AiAssistant.Polish;

public sealed record PolishRequest(
    Guid NodeId,
    string DocumentType,
    string Property,
    string Text,
    bool IsRichText = false,
    string? Form = null,
    bool WantTitle = false,
    bool WantSummary = false);

public sealed record PolishResult(string Suggestion, string? Title, string? Summary, bool Flagged, IReadOnlyList<string> Missing, bool Unparsed = false);

public sealed class PolishService(
    IClaudeClient claude,
    IEffectiveSettings settings,
    ILogger<PolishService> logger)
{
    public async Task<PolishResult> PolishAsync(PolishRequest request, CancellationToken cancellationToken)
    {
        int maxCharacters = settings.Options.MaxCharacters;
        if (string.IsNullOrWhiteSpace(request.Text)) throw new ArgumentException("There is no text to polish.");
        if (request.Text.Length > maxCharacters)
            throw new ArgumentException($"The text is longer than the limit of {maxCharacters} characters.");
        if (request.Form is not null && !Forms.Contains(request.Form))
            throw new ArgumentException("Form must be news or article.");

        ClaudeResponse response = await claude.CompleteAsync(
            new ClaudeRequest(PromptBuilder.BuildSystem(settings.Rules), PromptBuilder.BuildUser(request)), cancellationToken);

        (string text, string? title, string? summary) = Parse(response.Text);
        bool unparsed = !IsStructured(response.Text);
        var missing = FactsCheck.Missing(request.Text, text).ToList();
        missing.AddRange(HtmlGuard.Problems(request.Text, text));
        if (unparsed) missing.Add("Claude did not answer in the expected form");
        if (missing.Count > 0)
            logger.LogInformation("Suggestion for {Property} flagged: {Count} source facts missing.", request.Property, missing.Count);

        return new PolishResult(text, Blank(title), Blank(summary), missing.Count > 0, missing, unparsed);
    }

    private static readonly HashSet<string> Forms = new(StringComparer.OrdinalIgnoreCase) { "news", "article" };

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    internal static bool IsStructured(string raw)
    {
        string json = raw.Trim();
        Match fence = Regex.Match(json, @"^```(?:json)?\s*(.*?)\s*```$", RegexOptions.Singleline);
        if (fence.Success) json = fence.Groups[1].Value;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("text", out JsonElement t) && t.ValueKind == JsonValueKind.String;
        }
        catch (JsonException) { return false; }
    }

    internal static (string Text, string? Title, string? Summary) Parse(string raw)
    {
        string json = raw.Trim();
        Match fence = Regex.Match(json, @"^```(?:json)?\s*(.*?)\s*```$", RegexOptions.Singleline);
        if (fence.Success) json = fence.Groups[1].Value;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("text", out JsonElement t) && t.ValueKind == JsonValueKind.String)
            {
                string? Get(string n) => root.TryGetProperty(n, out JsonElement e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                return (t.GetString()!, Get("title"), Get("summary"));
            }
        }
        catch (JsonException) { }
        return (raw.Trim(), null, null);
    }
}
