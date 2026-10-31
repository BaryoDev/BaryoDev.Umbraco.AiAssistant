using BaryoDev.Umbraco.AiAssistant.Configuration;
using Microsoft.Extensions.Options;

namespace BaryoDev.Umbraco.AiAssistant.Settings;

public enum KeySource
{
    None,
    Configuration,
    Dashboard,
}

/// <summary>Read at every call, so a change in the dashboard takes effect with no restart.</summary>
public interface IEffectiveSettings
{
    AiAssistantOptions Options { get; }

    /// <summary>For the Claude client only. Never put this in a response or a log.</summary>
    string? Key { get; }

    KeySource KeySource { get; }
    bool KeyLost { get; }
    bool IsConfigured { get; }

    string Model { get; }
    string ModelSource { get; }
    string Rules { get; }
    string RulesSource { get; }

    decimal? MonthlyCap { get; }
    string CapSource { get; }

    /// <summary>USD per million tokens. An estimate only.</summary>
    decimal? InputPrice { get; }
    decimal? OutputPrice { get; }
    string PriceSource { get; }
}

/// <summary>
/// Suggested prices for known models, in USD per million tokens, from Anthropic's published rates
/// (checked 2026-10-06). Shown as a starting point the owner can overwrite. Prices change; they are not authoritative.
/// </summary>
internal static class KnownPrices
{
    private static readonly Dictionary<string, (decimal In, decimal Out)> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-sonnet-5-5"] = (2m, 10m),
        ["claude-sonnet-5"] = (2m, 10m),
        ["claude-sonnet-4-6"] = (3m, 15m),
        ["claude-opus-5-5"] = (4m, 20m),
        ["claude-opus-5"] = (5m, 25m),
        ["claude-opus-4-8"] = (5m, 25m),
        ["claude-haiku-5-5"] = (0.10m, 0.50m),
        ["claude-haiku-4-5"] = (1m, 5m),
    };

    public static (decimal In, decimal Out)? For(string model) => Table.TryGetValue(model, out var p) ? p : null;
}

/// <summary>Configuration wins over the dashboard for every value.</summary>
internal sealed class EffectiveSettings(
    IOptionsMonitor<AiAssistantOptions> options,
    SettingsStore settings,
    ApiKeyStore keys) : IEffectiveSettings
{
    public AiAssistantOptions Options => options.CurrentValue;

    public string? Key =>
        !string.IsNullOrWhiteSpace(Options.Key) ? Options.Key : keys.Read();

    public KeySource KeySource =>
        !string.IsNullOrWhiteSpace(Options.Key) ? KeySource.Configuration
        : keys.Read() is not null ? KeySource.Dashboard
        : KeySource.None;

    public bool KeyLost => string.IsNullOrWhiteSpace(Options.Key) && keys.State == StoredKeyState.Lost;

    public bool IsConfigured => KeySource != KeySource.None;

    public string Model =>
        !string.IsNullOrWhiteSpace(Options.Model) ? Options.Model
        : !string.IsNullOrWhiteSpace(settings.Get().Model) ? settings.Get().Model!
        : AiAssistantOptions.DefaultModel;

    public string ModelSource =>
        !string.IsNullOrWhiteSpace(Options.Model) ? "configuration"
        : !string.IsNullOrWhiteSpace(settings.Get().Model) ? "dashboard" : "default";

    public string Rules =>
        !string.IsNullOrWhiteSpace(Options.Rules) ? Options.Rules
        : settings.Get().Rules ?? "";

    public string RulesSource =>
        !string.IsNullOrWhiteSpace(Options.Rules) ? "configuration"
        : !string.IsNullOrWhiteSpace(settings.Get().Rules) ? "dashboard" : "default";

    public decimal? MonthlyCap => Options.MonthlySpendCap ?? settings.Get().MonthlyCap;

    public string CapSource => Options.MonthlySpendCap is not null ? "configuration" : settings.Get().MonthlyCap is not null ? "dashboard" : "default";

    public decimal? InputPrice => Options.InputPricePerMillion ?? (CapFromConfiguration ? null : settings.Get().InputPrice) ?? KnownPrices.For(Model)?.In;

    public decimal? OutputPrice => Options.OutputPricePerMillion ?? (CapFromConfiguration ? null : settings.Get().OutputPrice) ?? KnownPrices.For(Model)?.Out;

    /// <summary>A cap set in configuration cannot be weakened from the dashboard, so dashboard prices are ignored then.</summary>
    private bool CapFromConfiguration => Options.MonthlySpendCap is not null;

    public string PriceSource =>
        Options.InputPricePerMillion is not null || Options.OutputPricePerMillion is not null ? "configuration"
        : !CapFromConfiguration && (settings.Get().InputPrice is not null || settings.Get().OutputPrice is not null) ? "dashboard"
        : KnownPrices.For(Model) is not null ? "suggested" : "none";
}
