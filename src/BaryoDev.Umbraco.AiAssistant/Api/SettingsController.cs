using System.Security.Claims;
using System.Text.RegularExpressions;
using Asp.Versioning;
using BaryoDev.Umbraco.AiAssistant.Claude;
using BaryoDev.Umbraco.AiAssistant.Polish;
using BaryoDev.Umbraco.AiAssistant.Configuration;
using BaryoDev.Umbraco.AiAssistant.Settings;
using BaryoDev.Umbraco.AiAssistant.Usage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;
using Umbraco.Cms.Web.Common.Authorization;

namespace BaryoDev.Umbraco.AiAssistant.Api;

/// <summary>
/// Key, model and limits. Settings section only, checked on the server. No response here carries the key,
/// not even a part of it.
/// </summary>
[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("ai-assistant/settings")]
[ApiExplorerSettings(GroupName = "AI Assistant")]
[Authorize(Policy = AuthorizationPolicies.SectionAccessSettings)]
[PlainServerErrors]
public sealed partial class SettingsController(
    IEffectiveSettings settings,
    ApiKeyStore keys,
    SettingsStore store,
    IClaudeKeyTester tester,
    UserRateLimiter limiter,
    UsageStore usage) : ManagementApiControllerBase
{
    [HttpGet]
    [ProducesResponseType<SettingsModel>(StatusCodes.Status200OK)]
    public IActionResult Get() => Ok(Describe());

    [HttpPut]
    [ProducesResponseType<SettingsModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult Save([FromBody] SaveSettingsRequest request)
    {
        string? model = request.Model?.Trim();
        if (!string.IsNullOrEmpty(model) && !ModelPattern().IsMatch(model))
            return Problem("The model name has characters it should not.", statusCode: StatusCodes.Status400BadRequest);
        foreach (decimal? v in new[] { request.MonthlyCap, request.InputPrice, request.OutputPrice })
            if (v is < 0 or > 1_000_000)
                return Problem("Cap and prices must be between 0 and 1,000,000.", statusCode: StatusCodes.Status400BadRequest);
        decimal? capAfter = settings.Options.MonthlySpendCap ?? request.MonthlyCap;
        if (capAfter is not null)
        {
            bool locked = settings.Options.MonthlySpendCap is not null;
            string modelAfter = settings.Options.Model is { Length: > 0 } m ? m : string.IsNullOrEmpty(model) ? AiAssistantOptions.DefaultModel : model;
            decimal? inPrice = settings.Options.InputPricePerMillion ?? (locked ? null : request.InputPrice) ?? KnownPrices.For(modelAfter)?.In;
            decimal? outPrice = settings.Options.OutputPricePerMillion ?? (locked ? null : request.OutputPrice) ?? KnownPrices.For(modelAfter)?.Out;
            if (inPrice is not > 0 || outPrice is not > 0)
                return Problem(
                    locked
                        ? "The spend cap is set in configuration, so the prices must be set there too (InputPricePerMillion and OutputPricePerMillion)."
                        : "A spend cap needs a price per million input and output tokens above zero.",
                    statusCode: StatusCodes.Status400BadRequest);
        }
        store.Update(s =>
        {
            s.Model = string.IsNullOrEmpty(model) ? null : model;
            s.MonthlyCap = request.MonthlyCap;
            s.InputPrice = request.InputPrice;
            s.OutputPrice = request.OutputPrice;
        });
        return Ok(Describe());
    }

    [HttpPut("key")]
    [ProducesResponseType<SettingsModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult SaveKey([FromBody] SaveKeyRequest request)
    {
        if (settings.KeySource == KeySource.Configuration)
            return Problem("The key is set in configuration, so a key entered here would be ignored.", statusCode: StatusCodes.Status400BadRequest);
        string key = request.Key?.Trim() ?? "";
        if (!LooksLikeKey(key))
            return Problem(NotAKey, statusCode: StatusCodes.Status400BadRequest);
        keys.Save(key, UserName());
        return Ok(Describe());
    }

    [HttpDelete("key")]
    [ProducesResponseType<SettingsModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult RemoveKey()
    {
        if (settings.KeySource == KeySource.Configuration)
            return Problem("The key is set in configuration, so there is nothing to remove here.", statusCode: StatusCodes.Status400BadRequest);
        keys.Clear();
        return Ok(Describe());
    }

    /// <summary>Tests the key typed in the box, or the key in use when the box is empty. The answer is a plain reason.</summary>
    [HttpPost("test")]
    [ProducesResponseType<KeyTestResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Test([FromBody] TestKeyRequest request, CancellationToken cancellationToken)
    {
        string? typed = request.Key?.Trim();
        if (settings.KeySource != KeySource.Configuration && !string.IsNullOrEmpty(typed) && !LooksLikeKey(typed))
            return Problem(NotAKey, statusCode: StatusCodes.Status400BadRequest);
        if (!limiter.TryAcquire("test:" + UserName(), settings.Options.RequestsPerMinute))
            return Problem("Too many tests. Wait a minute.", statusCode: StatusCodes.Status429TooManyRequests);
        return Ok(await tester.TestAsync(settings.KeySource == KeySource.Configuration ? null : typed, cancellationToken));
    }

    private const string NotAKey = "That does not look like an API key. Paste it again.";

    private static bool LooksLikeKey(string key) =>
        key.Length is >= 8 and <= 512 && !key.Any(c => char.IsWhiteSpace(c) || char.IsControl(c));

    private string UserName() => User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name ?? "unknown";

    internal SettingsModel Describe()
    {
        string? message = settings.KeyLost
            ? "The saved key could not be read because its encryption keys are gone. Enter it again."
            : null;
        var info = keys.Info();
        MonthUsage u = usage.Current();
        var use = new UsageModel(usage.CurrentMonth, u.InputTokens, u.OutputTokens, u.Calls, u.Cost, u.UnpricedCalls);
        return new SettingsModel(
            settings.IsConfigured,
            settings.KeySource.ToString().ToLowerInvariant(),
            settings.KeyLost,
            message,
            info?.SavedBy,
            info?.SavedAt,
            settings.Model,
            settings.ModelSource,
            use,
            settings.MonthlyCap,
            settings.CapSource,
            settings.InputPrice,
            settings.OutputPrice,
            settings.PriceSource,
            settings.Rules,
            settings.RulesSource,
            PromptBuilder.DefaultRules);
    }

    [GeneratedRegex(@"^[A-Za-z0-9._:\-]{1,80}$")]
    private static partial Regex ModelPattern();
}

/// <summary>There is deliberately no field that can hold the key or part of it.</summary>
public sealed record SettingsModel(
    bool Configured,
    string KeySource,
    bool KeyLost,
    string? KeyMessage,
    string? KeySavedBy,
    DateTimeOffset? KeySavedAt,
    string Model,
    string ModelSource,
    UsageModel Usage,
    decimal? MonthlyCap,
    string CapSource,
    decimal? InputPrice,
    decimal? OutputPrice,
    string PriceSource,
    string Rules,
    string RulesSource,
    string DefaultRules);

/// <summary>This month's tokens and the estimated cost in USD, from the prices in settings. An estimate, not a bill.</summary>
public sealed record UsageModel(string Month, long InputTokens, long OutputTokens, int Calls, decimal EstimatedCost, int UnpricedCalls);

public sealed record SaveSettingsRequest(string? Model, decimal? MonthlyCap = null, decimal? InputPrice = null, decimal? OutputPrice = null);

public sealed record SaveKeyRequest(string? Key)
{
    /// <summary>MVC logs action arguments with ToString, so the key must not be in it.</summary>
    public override string ToString() => nameof(SaveKeyRequest);
}

public sealed record TestKeyRequest(string? Key)
{
    public override string ToString() => nameof(TestKeyRequest);
}
