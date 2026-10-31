using BaryoDev.Umbraco.AiAssistant.Usage;
using System.Security.Claims;
using Asp.Versioning;
using BaryoDev.Umbraco.AiAssistant.Claude;
using BaryoDev.Umbraco.AiAssistant.Jobs;
using BaryoDev.Umbraco.AiAssistant.Polish;
using BaryoDev.Umbraco.AiAssistant.Settings;
using BaryoDev.Umbraco.AiAssistant.Voice;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;
using Umbraco.Cms.Core.Actions;
using Umbraco.Cms.Core.Security.Authorization;
using Umbraco.Cms.Web.Common.Authorization;

namespace BaryoDev.Umbraco.AiAssistant.Api;

/// <summary>Brand voice: save the rules, or draft them from chosen articles. Settings section only.</summary>
[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("ai-assistant/voice")]
[ApiExplorerSettings(GroupName = "AI Assistant")]
[Authorize(Policy = AuthorizationPolicies.SectionAccessSettings)]
[PlainServerErrors]
public sealed class VoiceController(
    IEffectiveSettings settings,
    SettingsStore store,
    VoiceService voice,
    IContentGateway gateway,
    IAuthorizationService authorization,
    UserRateLimiter limiter) : ManagementApiControllerBase
{
    [HttpPut("rules")]
    [ProducesResponseType<VoiceModel>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult Save([FromBody] SaveRulesRequest request)
    {
        string rules = (request.Rules ?? "").Trim();
        if (rules.Length > VoiceService.MaxRulesLength)
            return Problem($"The rules are longer than {VoiceService.MaxRulesLength} characters.", statusCode: StatusCodes.Status400BadRequest);
        store.Update(s => s.Rules = rules.Length == 0 ? null : rules);
        return Ok(new VoiceModel(settings.Rules, settings.RulesSource));
    }

    [HttpPost("draft")]
    [ProducesResponseType<DraftRulesResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Draft([FromBody] DraftRulesRequest request, CancellationToken cancellationToken)
    {
        if (!settings.IsConfigured) return Problem(AiAssistantController.NoKeyMessage, statusCode: StatusCodes.Status400BadRequest);
        var ids = (request.NodeIds ?? []).Distinct().ToList();
        if (ids.Count == 0) return Problem("Choose at least one article.", statusCode: StatusCodes.Status400BadRequest);
        if (ids.Count > VoiceService.MaxArticles)
            return Problem($"Choose at most {VoiceService.MaxArticles} articles.", statusCode: StatusCodes.Status400BadRequest);

        foreach (Guid id in ids)
        {
            var allowed = await authorization.AuthorizeAsync(User, ContentPermissionResource.WithKeys(ActionBrowse.ActionLetter, id), AuthorizationPolicies.ContentPermissionByResource);
            if (!allowed.Succeeded) return Forbid();
        }

        // A page that has no text in the source language is left out, not a reason to stop the whole draft.
        var usable = new List<(Guid Id, string? Culture)>();
        int skipped = 0;
        foreach (Guid id in ids)
        {
            string? culture;
            try { culture = gateway.EffectiveCulture(id, null); }
            catch (ArgumentException) { skipped++; continue; }
            if (culture is not null)
            {
                var allowed = await authorization.AuthorizeAsync(User, ContentPermissionResource.WithKeys(ActionBrowse.ActionLetter, id, [culture]), AuthorizationPolicies.ContentPermissionByResource);
                if (!allowed.Succeeded) return Forbid();
            }
            usable.Add((id, culture));
        }
        if (usable.Count == 0)
            return Problem("None of the chosen pages has text in the source language.", statusCode: StatusCodes.Status400BadRequest);

        string user = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.Identity?.Name ?? "unknown";
        if (!limiter.TryAcquire("voice:" + user, settings.Options.RequestsPerMinute))
            return Problem("Too many requests. Wait a minute.", statusCode: StatusCodes.Status429TooManyRequests);

        try
        {
            var texts = usable.Select(u => gateway.SourceText(u.Id, u.Culture) ?? "").ToList();
            skipped += texts.Count(t => string.IsNullOrWhiteSpace(t));
            return Ok(new DraftRulesResult(await voice.DraftAsync(texts, cancellationToken), skipped));
        }
        catch (ArgumentException e)
        {
            return Problem(e.Message, statusCode: StatusCodes.Status400BadRequest);
        }
        catch (SpendCapException e)
        {
            return Problem(e.Message, statusCode: StatusCodes.Status402PaymentRequired);
        }
        catch (ClaudeException e)
        {
            return Problem(e.Message, statusCode: StatusCodes.Status502BadGateway);
        }
    }
}

public sealed record SaveRulesRequest(string? Rules);

public sealed record VoiceModel(string Rules, string RulesSource);

public sealed record DraftRulesRequest(List<Guid>? NodeIds);

public sealed record DraftRulesResult(string Rules, int Skipped = 0);
