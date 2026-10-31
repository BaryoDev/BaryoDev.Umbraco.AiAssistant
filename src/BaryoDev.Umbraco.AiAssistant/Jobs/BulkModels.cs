using System.Text.Json;
using System.Text.Json.Serialization;

namespace BaryoDev.Umbraco.AiAssistant.Jobs;

public enum JobKind { Polish, Seo, Alt, Translate }

public enum ItemState { Queued, Ready, Failed, Skipped, Accepted, Rejected }

public enum FactsStatus { Pending, Passed, Failed, NotApplicable }

/// <summary>Stopped means the run paused itself (spend cap, key problem, Claude unreachable) and can be resumed.</summary>
public enum JobState { Running, Stopped, Cancelled, Done }

public sealed class FieldChange
{
    public string Alias { get; set; } = "";
    public bool IsRich { get; set; }

    /// <summary>The property is shared by all languages of a variant page, so writing it needs the default language.</summary>
    public bool Shared { get; set; }

    /// <summary>For SEO items: title, description or share. Empty for other jobs.</summary>
    public string Role { get; set; } = "";

    /// <summary>For translation: the text in the source language. Original holds what the target language has now.</summary>
    public string? SourceText { get; set; }
    public string Original { get; set; } = "";
    public string OriginalHash { get; set; } = "";
    public string? Suggestion { get; set; }
    public string? Edited { get; set; }

    [JsonIgnore]
    public string? Final => Edited ?? Suggestion;

    /// <summary>True when the text breaks the markup rules. Markup already in the source or current text is exempt, except script.</summary>
    public bool Unsafe(string? text) => Polish.HtmlGuard.IsUnsafe((SourceText ?? "") + "\n" + Original, text, IsRich);
}

public sealed class BulkItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid NodeId { get; set; }
    public string Name { get; set; } = "";
    public string? Culture { get; set; }
    public ItemState State { get; set; } = ItemState.Queued;
    public FactsStatus Facts { get; set; } = FactsStatus.Pending;
    public List<string> Missing { get; set; } = [];
    /// <summary>Advice that does not fail the facts check, such as a text over its length limit.</summary>
    public List<string> Notes { get; set; } = [];
    public string? Reason { get; set; }
    public List<FieldChange> Fields { get; set; } = [];

    /// <summary>For image items: the media key is NodeId and this holds the file reference.</summary>
    public string? Source { get; set; }
    public bool Published { get; set; }
}

public sealed class BulkJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public JobKind Kind { get; set; }
    public string CreatedBy { get; set; } = "";
    /// <summary>What people see. CreatedBy is the user id and is what access is checked against.</summary>
    public string CreatedByName { get; set; } = "";
    public string Creator => string.IsNullOrWhiteSpace(CreatedByName) ? CreatedBy : CreatedByName;
    public DateTimeOffset CreatedAt { get; set; }
    public JobState State { get; set; } = JobState.Running;
    public string? Message { get; set; }
    public string? TargetCulture { get; set; }
    public bool Overwrite { get; set; }
    public decimal? EstimatedCost { get; set; }
    public List<BulkItem> Items { get; set; } = [];

    public bool IsActive => State is JobState.Running or JobState.Stopped;
}

public sealed record JobOptions(string? TargetCulture, bool Overwrite);

public sealed record NodeScope(Guid? NodeId, bool IncludeDescendants, string? DocumentType);

public sealed record NodeRef(Guid Id, string Name, string DocumentType);

public sealed record FieldWrite(string Alias, string Value, bool Rich = false);

/// <summary>A plain, user-facing failure from the bulk service.</summary>
public sealed class BulkException(string message) : Exception(message);

internal static class JobJson
{
    public static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };

    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;
}
