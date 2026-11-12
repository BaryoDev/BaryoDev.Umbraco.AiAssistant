using System.Text.Json;
using System.Text.Json.Nodes;
using BaryoDev.Umbraco.AiAssistant.Claude;
using BaryoDev.Umbraco.AiAssistant.Configuration;
using BaryoDev.Umbraco.AiAssistant.Media;
using BaryoDev.Umbraco.AiAssistant.Polish;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Extensions;
using Umbraco.Cms.Core.IO;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;

namespace BaryoDev.Umbraco.AiAssistant.Jobs;

internal sealed class UmbracoContentGateway(
    IContentService contentService,
    IContentTypeService contentTypeService,
    ILanguageService languageService,
    IMediaService mediaService,
    IMediaTypeService mediaTypeService,
    MediaFileManager mediaFiles,
    Settings.IEffectiveSettings settings) : IContentGateway
{
    private const int Page = 100;
    private const int ScanLimit = 5000;

    private static readonly HashSet<string> TextEditors = new(StringComparer.OrdinalIgnoreCase)
    {
        "Umbraco.TextBox", "Umbraco.TextArea", "Umbraco.RichText",
    };

    public IReadOnlyList<NodeRef> FindNodes(JobKind kind, NodeScope scope) =>
        kind == JobKind.Alt ? FindMedia(scope) : FindContent(scope);

    private IReadOnlyList<NodeRef> FindMedia(NodeScope scope)
    {
        var found = new Dictionary<Guid, IMedia>();
        if (scope.NodeId is { } id)
        {
            IMedia root = mediaService.GetById(id) ?? throw new BulkException("That media item was not found.");
            if (!root.Trashed) found[root.Key] = root;
            if (scope.IncludeDescendants)
            {
                long index = 0;
                while (found.Count < ScanLimit)
                {
                    var batch = mediaService.GetPagedDescendants(root.Id, index, Page, out long total).ToList();
                    foreach (IMedia m in batch) if (!m.Trashed) found[m.Key] = m;
                    index++;
                    if (index * Page >= total || batch.Count == 0) break;
                }
            }
        }
        if (!string.IsNullOrWhiteSpace(scope.DocumentType))
        {
            IMediaType type = (Guid.TryParse(scope.DocumentType, out Guid key) ? mediaTypeService.Get(key) : mediaTypeService.Get(scope.DocumentType))
                ?? throw new BulkException("That media type was not found.");
            long index = 0;
            while (found.Count < ScanLimit)
            {
                var batch = mediaService.GetPagedOfType(type.Id, index, Page, out long total, null!).ToList();
                foreach (IMedia m in batch) if (!m.Trashed) found[m.Key] = m;
                index++;
                if (index * Page >= total || batch.Count == 0) break;
            }
        }
        return found.Values.Select(m => new NodeRef(m.Key, m.Name ?? "", m.ContentType.Alias)).ToList();
    }

    private IReadOnlyList<NodeRef> FindContent(NodeScope scope)
    {
        var found = new Dictionary<Guid, IContent>();

        if (scope.NodeId is { } id)
        {
            IContent? root = contentService.GetById(id);
            if (root is null) throw new BulkException("That content node was not found.");
            Add(found, root);
            if (scope.IncludeDescendants)
            {
                long index = 0;
                while (found.Count < ScanLimit)
                {
                    var batch = contentService.GetPagedDescendants(root.Id, index, Page, out long total).ToList();
                    foreach (IContent c in batch) Add(found, c);
                    index++;
                    if (index * Page >= total || batch.Count == 0) break;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(scope.DocumentType))
        {
            IContentType? type = Guid.TryParse(scope.DocumentType, out Guid typeKey)
                ? contentTypeService.Get(typeKey)
                : contentTypeService.Get(scope.DocumentType);
            if (type is null) throw new BulkException("That document type was not found.");
            long index = 0;
            while (found.Count < ScanLimit)
            {
                var batch = contentService.GetPagedOfType(type.Id, index, Page, out long total, null!).ToList();
                foreach (IContent c in batch) Add(found, c);
                index++;
                if (index * Page >= total || batch.Count == 0) break;
            }
        }

        return found.Values.Select(c => new NodeRef(c.Key, c.Name ?? "", c.ContentType.Alias)).ToList();
    }

    private static void Add(Dictionary<Guid, IContent> found, IContent c)
    {
        if (!c.Trashed) found[c.Key] = c;
    }

    public IReadOnlyList<BulkItem> Plan(JobKind kind, NodeRef node, JobOptions options)
    {
        if (kind == JobKind.Alt) return PlanAlt(node, options);
        if (kind == JobKind.Seo) return PlanSeo(node, options);
        if (kind == JobKind.Translate) return PlanTranslate(node, options);
        if (kind != JobKind.Polish) return [];
        IContent? content = contentService.GetById(node.Id);
        if (content is null) return [];

        var items = new List<BulkItem>();
        string?[] cultures = content.ContentType.Variations.HasFlag(ContentVariation.Culture) ? content.AvailableCultures.Cast<string?>().ToArray() : new string?[] { null };
        foreach (IProperty p in content.Properties)
        {
            if (!TextEditors.Contains(p.PropertyType.PropertyEditorAlias)) continue;
            if (!settings.Options.Allows(node.DocumentType, p.Alias)) continue;
            foreach (string? culture in cultures)
            {
                if (SkipForLanguage(culture, options.TargetCulture, DefaultCulture)) continue;
                if (culture is not null && !string.IsNullOrWhiteSpace(options.TargetCulture) && !string.Equals(culture, options.TargetCulture, StringComparison.OrdinalIgnoreCase)) continue;
                if (culture is not null && !p.PropertyType.Variations.HasFlag(ContentVariation.Culture)) continue;
                if (culture is null && p.PropertyType.Variations.HasFlag(ContentVariation.Culture)) continue;
                string? text = Display(content.GetValue(p.Alias, culture), out bool rich);
                if (string.IsNullOrWhiteSpace(text)) continue;

                var item = new BulkItem { NodeId = node.Id, Name = culture is null ? node.Name : $"{node.Name} ({culture})", Culture = culture };
                if (text.Length > settings.Options.MaxCharacters)
                {
                    item.State = ItemState.Skipped;
                    item.Reason = $"{p.Alias} is longer than the limit of {settings.Options.MaxCharacters} characters.";
                }
                item.Fields.Add(new FieldChange { Alias = p.Alias, IsRich = rich, Original = text, OriginalHash = BulkService.Hash(text) });
                items.Add(item);
            }
        }
        return items;
    }

    private IReadOnlyList<BulkItem> PlanSeo(NodeRef node, JobOptions options)
    {
        IContent? content = contentService.GetById(node.Id);
        if (content is null) return [];
        SeoOptions seo = settings.Options.Seo;
        var targets = new (string Role, string Alias)[] { ("title", seo.TitleProperty), ("description", seo.DescriptionProperty), ("share", seo.ShareTextProperty) }
            .Where(t => content.HasProperty(t.Alias)).ToArray();
        if (targets.Length == 0) return [];

        var items = new List<BulkItem>();
        foreach (string? culture in DocumentCultures(content))
        {
            if (SkipForLanguage(culture, options.TargetCulture, DefaultCulture)) continue;
            if (culture is not null && !string.IsNullOrWhiteSpace(options.TargetCulture) && !string.Equals(culture, options.TargetCulture, StringComparison.OrdinalIgnoreCase)) continue;
            var fields = new List<FieldChange>();
            foreach ((string role, string alias) in targets)
            {
                bool shared = culture is not null && !VariesOn(content, alias);
                if (shared && !string.Equals(culture, DefaultCulture(), StringComparison.OrdinalIgnoreCase)) continue;
                string existing = Display(content.GetValue(alias, PropertyCulture(content, alias, culture)), out _) ?? "";
                if (!AltTextService.Needs(existing, options.Overwrite)) continue;
                fields.Add(new FieldChange { Alias = alias, Role = role, Original = existing, OriginalHash = BulkService.Hash(existing), Shared = shared });
            }
            if (fields.Count == 0) continue;
            var item = new BulkItem { NodeId = node.Id, Name = culture is null ? node.Name : $"{node.Name} ({culture})", Culture = culture };
            item.Fields.AddRange(fields);
            if (string.IsNullOrWhiteSpace(SourceText(node.Id, culture)))
            {
                item.State = ItemState.Skipped;
                item.Reason = "The page has no text to work from.";
            }
            items.Add(item);
        }
        return items;
    }

    private string SourceCulture() =>
        !string.IsNullOrWhiteSpace(settings.Options.TranslateSourceCulture)
            ? settings.Options.TranslateSourceCulture
            : languageService.GetDefaultIsoCodeAsync().GetAwaiter().GetResult();

    public string? CheckTargetCulture(string? culture)
    {
        if (string.IsNullOrWhiteSpace(culture)) return "Pick the language to translate into.";
        var codes = languageService.GetAllIsoCodesAsync().GetAwaiter().GetResult();
        if (!codes.Contains(culture, StringComparer.OrdinalIgnoreCase)) return "That language is not set up in Umbraco.";
        if (string.Equals(culture, SourceCulture(), StringComparison.OrdinalIgnoreCase)) return "That is the source language. Pick another one.";
        return null;
    }

    private IReadOnlyList<BulkItem> PlanTranslate(NodeRef node, JobOptions options)
    {
        IContent? content = contentService.GetById(node.Id);
        if (content is null || !content.ContentType.Variations.HasFlag(ContentVariation.Culture)) return [];
        string target = languageService.GetAllIsoCodesAsync().GetAwaiter().GetResult()
            .FirstOrDefault(c => string.Equals(c, options.TargetCulture, StringComparison.OrdinalIgnoreCase)) ?? "";
        if (target.Length == 0) return [];
        string source = SourceCulture();

        var items = new List<BulkItem>();
        foreach (IProperty p in content.Properties)
        {
            if (!TextEditors.Contains(p.PropertyType.PropertyEditorAlias)) continue;
            if (!p.PropertyType.Variations.HasFlag(ContentVariation.Culture)) continue;
            string? text = Display(content.GetValue(p.Alias, source), out bool rich);
            if (string.IsNullOrWhiteSpace(text)) continue;
            string existing = Display(content.GetValue(p.Alias, target), out _) ?? "";
            if (!string.IsNullOrWhiteSpace(existing) && !options.Overwrite) continue;

            var item = new BulkItem { NodeId = node.Id, Name = $"{node.Name} ({target})", Culture = target };
            if (text.Length > settings.Options.MaxCharacters)
            {
                item.State = ItemState.Skipped;
                item.Reason = $"{p.Alias} is longer than the limit of {settings.Options.MaxCharacters} characters.";
            }
            item.Fields.Add(new FieldChange { Alias = p.Alias, IsRich = rich, SourceText = text, Original = existing, OriginalHash = BulkService.Hash(existing) });
            items.Add(item);
        }
        AddNameItem(items, content, node, source, target, options.Overwrite);
        return items;
    }

    /// <summary>The page name of the target language is translated like a field, under this reserved alias.</summary>
    internal const string NameAlias = "$name";

    private static void AddNameItem(List<BulkItem> items, IContent content, NodeRef node, string source, string target, bool overwrite)
    {
        string? sourceName = content.GetCultureName(source);
        if (string.IsNullOrWhiteSpace(sourceName) || items.Count == 0) return;
        string existing = TranslatedName(content, source, target);
        if (existing.Length > 0 && !overwrite) return;
        var item = new BulkItem { NodeId = node.Id, Name = $"{node.Name} ({target}) name", Culture = target };
        item.Fields.Add(new FieldChange { Alias = NameAlias, IsRich = false, SourceText = sourceName, Original = existing, OriginalHash = BulkService.Hash(existing) });
        items.Add(item);
    }

    /// <summary>A name that is blank or still the source language name counts as not translated yet.</summary>
    private static string TranslatedName(IContent content, string source, string target)
    {
        string name = content.GetCultureName(target) ?? "";
        return string.Equals(name, content.GetCultureName(source) ?? "", StringComparison.OrdinalIgnoreCase) ? "" : name;
    }

    private static bool VariesOn(IContent content, string alias) =>
        alias == NameAlias || PropertyVariation(content, alias)?.HasFlag(ContentVariation.Culture) == true;

    private static string?[] DocumentCultures(IContent content) =>
        content.ContentType.Variations.HasFlag(ContentVariation.Culture) ? content.AvailableCultures.Cast<string?>().ToArray() : new string?[] { null };

    private static string? PropertyCulture(IContent content, string alias, string? culture) =>
        VariesOn(content, alias) ? culture : null;

    public string? EffectiveCulture(Guid nodeId, string? requested)
    {
        IContent content = contentService.GetById(nodeId) ?? throw new ArgumentException("The page was not found.");
        bool varies = content.ContentType.Variations.HasFlag(ContentVariation.Culture);
        return CultureRules.Resolve(varies, content.AvailableCultures.ToArray(), SourceCulture(), requested);
    }

    public string? SourceCultureCode() => SourceCulture();

    /// <summary>
    /// An invariant page has no language of its own, so it counts as the default language: a language limit
    /// leaves it out unless the limit is the default language. A site with one language still works.
    /// </summary>
    internal static bool SkipForLanguage(string? culture, string? target, Func<string> defaultCulture) =>
        culture is null && !string.IsNullOrWhiteSpace(target) && !string.Equals(target, defaultCulture(), StringComparison.OrdinalIgnoreCase);

    private string DefaultCulture() => languageService.GetDefaultIsoCodeAsync().GetAwaiter().GetResult();

    public string? DefaultCultureCode() => DefaultCulture();

    public bool IsShared(Guid nodeId, string alias)
    {
        IContent? content = contentService.GetById(nodeId);
        return content is not null
            && SharedField(alias, content.ContentType.Variations.HasFlag(ContentVariation.Culture), PropertyVariation(content, alias));
    }

    /// <summary>A property that exists on a culture variant document but does not vary. The page name (NameAlias) always varies.</summary>
    internal static bool SharedField(string alias, bool documentVaries, ContentVariation? propertyVariation) =>
        alias != NameAlias && documentVaries && propertyVariation is { } v && !v.HasFlag(ContentVariation.Culture);

    private static ContentVariation? PropertyVariation(IContent content, string alias) =>
        content.HasProperty(alias) ? content.Properties[alias]?.PropertyType.Variations : null;

    public string? SourceText(Guid nodeId, string? culture)
    {
        IContent? content = contentService.GetById(nodeId);
        if (content is null) return null;
        SeoOptions seo = settings.Options.Seo;
        var skip = new[] { seo.TitleProperty, seo.DescriptionProperty, seo.ShareTextProperty };
        var parts = new List<string>();
        foreach (IProperty p in content.Properties)
        {
            if (skip.Contains(p.Alias, StringComparer.OrdinalIgnoreCase)) continue;
            if (seo.SourceProperties.Count > 0 && !seo.SourceProperties.Contains(p.Alias, StringComparer.OrdinalIgnoreCase)) continue;
            // Block list, grid and other structured editors hold JSON, which must not be sent as page text.
            if (!TextEditors.Contains(p.PropertyType.PropertyEditorAlias)) continue;
            string? text = Display(content.GetValue(p.Alias, p.PropertyType.Variations.HasFlag(ContentVariation.Culture) ? culture ?? SourceCulture() : null), out bool rich);
            if (string.IsNullOrWhiteSpace(text)) continue;
            parts.Add(rich ? FactsCheck.Plain(text) : text);
        }
        string joined = string.Join("\n\n", parts);
        int max = settings.Options.MaxCharacters;
        return joined.Length > max ? joined[..max] : joined;
    }

    private IReadOnlyList<BulkItem> PlanAlt(NodeRef node, JobOptions options)
    {
        IMedia? media = mediaService.GetById(node.Id);
        string alias = settings.Options.AltTextProperty;
        if (media is null || !media.HasProperty(alias) || !media.HasProperty(Constants.Conventions.Media.File)) return [];

        string existing = media.GetValue<string>(alias) ?? "";
        if (!AltTextService.Needs(existing, options.Overwrite)) return [];

        var item = new BulkItem { NodeId = node.Id, Name = node.Name, Source = FileSrc(media) };
        item.Fields.Add(new FieldChange { Alias = alias, Original = existing, OriginalHash = BulkService.Hash(existing) });
        if (string.IsNullOrEmpty(item.Source))
        {
            item.State = ItemState.Skipped;
            item.Reason = "There is no file on this media item.";
        }
        else if (string.Equals(Path.GetExtension(item.Source), ".svg", StringComparison.OrdinalIgnoreCase))
        {
            item.State = ItemState.Skipped;
            item.Reason = "SVG files are skipped. Claude cannot read them as images.";
        }
        return [item];
    }

    private static string? FileSrc(IMedia media)
    {
        string? raw = media.GetValue<string>(Constants.Conventions.Media.File);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (raw.TrimStart().StartsWith('{'))
        {
            try { return JsonNode.Parse(raw)?["src"]?.GetValue<string>(); }
            catch (JsonException) { return null; }
        }
        return raw;
    }

    public string? FileName(Guid mediaId) =>
        mediaService.GetById(mediaId) is { } m && FileSrc(m) is { } src ? Path.GetFileName(src) : null;

    public ClaudeImage ReadImage(Guid mediaId)
    {
        IMedia media = mediaService.GetById(mediaId) ?? throw new ArgumentException("The media item no longer exists.");
        string src = FileSrc(media) ?? throw new ArgumentException("There is no file on this media item.");
        if (string.Equals(Path.GetExtension(src), ".svg", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("SVG files are skipped. Claude cannot read them as images.");

        byte[] bytes;
        try
        {
            string path = mediaFiles.FileSystem.GetRelativePath(src);
            if (!mediaFiles.FileSystem.FileExists(path)) throw new ArgumentException("The file is missing from media storage.");
            if (mediaFiles.FileSystem.GetSize(path) > ImagePreparer.MaxBytes) throw new ArgumentException("The image is larger than 4.5 MB. Resize it and run again.");
            using Stream stream = mediaFiles.FileSystem.OpenFile(path);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            bytes = buffer.ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ArgumentException("The file could not be read from media storage.");
        }
        return ImagePreparer.Prepare(bytes, src);
    }

    public string? Current(Guid nodeId, string alias, string? culture)
    {
        if (mediaService.GetById(nodeId) is { } m) return m.HasProperty(alias) ? m.GetValue<string>(alias) ?? "" : null;
        IContent? content = contentService.GetById(nodeId);
        if (alias == NameAlias && content is not null && culture is not null) return TranslatedName(content, SourceCulture(), culture);
        return content is null || !content.HasProperty(alias) ? null : Display(content.GetValue(alias, PropertyCulture(content, alias, culture)), out _) ?? "";
    }

    public void SaveDraft(Guid nodeId, string? culture, IReadOnlyList<FieldWrite> writes, int userId)
    {
        if (mediaService.GetById(nodeId) is { } media)
        {
            foreach (FieldWrite w in writes)
            {
                if (!media.HasProperty(w.Alias)) throw new GatewayException($"The property {w.Alias} no longer exists.");
                media.SetValue(w.Alias, w.Value);
            }
            if (!mediaService.Save(media, userId).Success) throw new GatewayException("Umbraco could not save the media item.");
            return;
        }
        IContent content = contentService.GetById(nodeId) ?? throw new GatewayException("The content no longer exists.");
        foreach (FieldWrite w in writes)
        {
            if (w.Alias == NameAlias && culture is not null) { content.SetCultureName(w.Value.Trim(), culture); continue; }
            if (!content.HasProperty(w.Alias)) throw new GatewayException($"The property {w.Alias} no longer exists.");
            string? pc = PropertyCulture(content, w.Alias, culture);
            content.SetValue(w.Alias, Merge(content.GetValue(w.Alias, pc), w.Value, w.Rich), pc);
        }
        if (culture is not null && string.IsNullOrWhiteSpace(content.GetCultureName(culture)))
            content.SetCultureName(content.GetCultureName(SourceCulture()) ?? content.Name ?? "Untitled", culture);
        var result = contentService.Save(content, userId);
        if (!result.Success) throw new GatewayException("Umbraco could not save the draft.");
    }

    public void Publish(Guid nodeId, string? culture, int userId)
    {
        IContent content = contentService.GetById(nodeId) ?? throw new GatewayException("The content no longer exists.");
        string[] cultures = CultureRules.PublishCultures(content.ContentType.Variations.HasFlag(ContentVariation.Culture), culture);
        var result = contentService.Publish(content, cultures, userId);
        if (!result.Success) throw new GatewayException("Umbraco could not publish this content. Check it in the editor.");
    }

    /// <summary>Rich text is stored as JSON with the markup inside. The markup is what the editor sees and what Claude gets.</summary>
    internal static string? Display(object? raw, out bool rich)
    {
        rich = false;
        string? s = raw?.ToString();
        if (s is not null && s.TrimStart().StartsWith('{'))
        {
            try
            {
                if (JsonNode.Parse(s) is JsonObject o && o["markup"] is JsonNode m)
                {
                    rich = true;
                    return m.GetValue<string>();
                }
            }
            catch (JsonException) { }
        }
        return s;
    }

    internal static object Merge(object? existing, string value, bool rich = false)
    {
        string? s = existing?.ToString();
        if (s is not null && s.TrimStart().StartsWith('{'))
        {
            try
            {
                if (JsonNode.Parse(s) is JsonObject o && o["markup"] is not null)
                {
                    o["markup"] = value;
                    return o.ToJsonString();
                }
            }
            catch (JsonException) { }
        }
        return rich ? new JsonObject { ["markup"] = value, ["blocks"] = null }.ToJsonString() : value;
    }
}
