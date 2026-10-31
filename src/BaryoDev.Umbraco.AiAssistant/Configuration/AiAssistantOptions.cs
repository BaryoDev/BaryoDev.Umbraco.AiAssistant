namespace BaryoDev.Umbraco.AiAssistant.Configuration;

/// <summary>
/// Bound from BaryoDev:AiAssistant. A value set here wins over the same value saved in the dashboard.
/// The Key comes from the environment or user secrets, or is entered in the Settings dashboard.
/// </summary>
public sealed class AiAssistantOptions
{
    public const string Section = "BaryoDev:AiAssistant";
    public const string DefaultModel = "claude-sonnet-5-5";

    public string Provider { get; set; } = "Claude";

    /// <summary>Empty means the dashboard value, then claude-sonnet-5-5.</summary>
    public string Model { get; set; } = "";

    /// <summary>Set BaryoDev__AiAssistant__Key in the environment or user secrets. Never in appsettings.</summary>
    public string Key { get; set; } = "";

    public int MaxCharacters { get; set; } = 20000;
    public int MaxOutputTokens { get; set; } = 16000;
    public int RequestsPerMinute { get; set; } = 6;

    /// <summary>Most items one bulk run may hold.</summary>
    public int MaxBulkItems { get; set; } = 500;

    /// <summary>Thinking effort sent with calls (low, medium, high). Short calls (alt text, SEO, brand voice, key test) always use low. Models without an effort setting never receive it. Empty leaves it out.</summary>
    public string Effort { get; set; } = "medium";

    /// <summary>Monthly cap in USD, estimated from the prices below. Unset means no cap. Wins over the dashboard value.</summary>
    public decimal? MonthlySpendCap { get; set; }

    /// <summary>Price per million input and output tokens in USD, used only for estimates. Unset means the dashboard value, then a suggestion for known models.</summary>
    public decimal? InputPricePerMillion { get; set; }
    public decimal? OutputPricePerMillion { get; set; }

    /// <summary>Alias of the alt text property on image media types.</summary>
    public string AltTextProperty { get; set; } = "altText";

    public SeoOptions Seo { get; set; } = new();

    /// <summary>Language the translation starts from. Empty means the default language in Umbraco.</summary>
    public string TranslateSourceCulture { get; set; } = "";

    /// <summary>Replaces the default writing rules when set. The facts rule and the output format always stay.</summary>
    public string Rules { get; set; } = "";

    /// <summary>Folder for the encryption keys of the stored API key. Empty means umbraco/Data/DataProtection-Keys/AiAssistant.</summary>
    public string KeysPath { get; set; } = "";

    /// <summary>Document types and properties the Polish action applies to. Empty means every text and rich text property.</summary>
    public List<DocumentTypeRule> DocumentTypes { get; set; } = [];

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Key);

    public bool Allows(string documentType, string property) =>
        DocumentTypes.Count == 0
        || DocumentTypes.Any(d =>
            string.Equals(d.Alias, documentType, StringComparison.OrdinalIgnoreCase)
            && (d.Properties.Count == 0 || d.Properties.Contains(property, StringComparer.OrdinalIgnoreCase)));
}

public sealed class DocumentTypeRule
{
    public string Alias { get; set; } = "";
    public List<string> Properties { get; set; } = [];
}

/// <summary>Where the SEO pack is read from and written to. Aliases are the same on every page type that has them.</summary>
public sealed class SeoOptions
{
    public string TitleProperty { get; set; } = "seoTitle";
    public string DescriptionProperty { get; set; } = "seoDescription";
    public string ShareTextProperty { get; set; } = "shareText";

    /// <summary>Properties the suggestion is based on. Empty means every text and rich text property except the three above. Other editors, such as block list or grid, are never sent.</summary>
    public List<string> SourceProperties { get; set; } = [];
}
