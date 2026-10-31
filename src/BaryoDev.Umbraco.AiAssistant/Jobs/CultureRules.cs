namespace BaryoDev.Umbraco.AiAssistant.Jobs;

/// <summary>Which culture a request may touch, kept apart from Umbraco so it can be tested.</summary>
public static class CultureRules
{
    /// <summary>
    /// The culture to read, write and authorize. Invariant content has none. Variant content needs one of its own
    /// cultures; no request means the default language.
    /// </summary>
    public static string? Resolve(bool varies, IReadOnlyCollection<string> available, string defaultCulture, string? requested)
    {
        if (!varies)
        {
            if (!string.IsNullOrWhiteSpace(requested)) throw new ArgumentException("This page does not vary by language.");
            return null;
        }
        if (string.IsNullOrWhiteSpace(requested)) requested = defaultCulture;
        string? match = available.FirstOrDefault(c => string.Equals(c, requested, StringComparison.OrdinalIgnoreCase));
        return match ?? throw new ArgumentException("That language is not available for this page.");
    }

    /// <summary>Cultures to publish. A variant page is never published with a wildcard, only the culture that was approved.</summary>
    public static string[] PublishCultures(bool varies, string? culture)
    {
        if (!varies) return ["*"];
        if (string.IsNullOrWhiteSpace(culture)) throw new GatewayException("This page varies by language, so the language to publish is needed.");
        return [culture];
    }
}
