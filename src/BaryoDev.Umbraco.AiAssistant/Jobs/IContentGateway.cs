namespace BaryoDev.Umbraco.AiAssistant.Jobs;

/// <summary>Everything the bulk jobs need from Umbraco, so the job logic can be tested without a site.</summary>
public interface IContentGateway
{
    IReadOnlyList<NodeRef> FindNodes(JobKind kind, NodeScope scope);

    /// <summary>Items to work on for one node: queued items, or skipped ones with a reason.</summary>
    IReadOnlyList<BulkItem> Plan(JobKind kind, NodeRef node, JobOptions options);

    /// <summary>The value as the item shows it now, or null when the node or property is gone.</summary>
    string? Current(Guid nodeId, string alias, string? culture);

    /// <summary>Saves as a draft. Throws GatewayException with a plain message when it cannot.</summary>
    void SaveDraft(Guid nodeId, string? culture, IReadOnlyList<FieldWrite> writes, int userId);

    /// <summary>The image of a media item, scaled for the API. Throws ArgumentException with the reason when it cannot be used.</summary>
    BaryoDev.Umbraco.AiAssistant.Claude.ClaudeImage ReadImage(Guid mediaId);

    string? FileName(Guid mediaId);

    /// <summary>Plain text of the page used as the base for SEO text, or null when the node is gone.</summary>
    string? SourceText(Guid nodeId, string? culture);

    /// <summary>A plain message when the language cannot be a translation target, otherwise null.</summary>
    string? CheckTargetCulture(string? culture);

    /// <summary>The language of the source text for translation and for reading pages. Throws ArgumentException when the language is not valid for the node.</summary>
    string? EffectiveCulture(Guid nodeId, string? requested);

    /// <summary>Default language used as the translation source.</summary>
    string? SourceCultureCode();

    /// <summary>The default language of the site. Fields shared by all languages are planned and authorized against it.</summary>
    string? DefaultCultureCode();

    /// <summary>True when the page varies by language now and this property does not, read from the live content type.</summary>
    bool IsShared(Guid nodeId, string alias);

    void Publish(Guid nodeId, string? culture, int userId);
}

public sealed class GatewayException(string message) : Exception(message);
