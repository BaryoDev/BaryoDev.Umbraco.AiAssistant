using BaryoDev.Umbraco.AiAssistant.Usage;
using System.Security.Claims;
using Asp.Versioning;
using BaryoDev.Umbraco.AiAssistant.Claude;
using BaryoDev.Umbraco.AiAssistant.Configuration;
using BaryoDev.Umbraco.AiAssistant.Settings;
using BaryoDev.Umbraco.AiAssistant.Polish;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;
using Umbraco.Cms.Core.Security.Authorization;
using Umbraco.Cms.Core.Actions;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Web.Common.Authorization;

namespace BaryoDev.Umbraco.AiAssistant.Api;

/// <summary>The backoffice calls this. Claude is called from here, never from the browser.</summary>
[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("ai-assistant")]
[ApiExplorerSettings(GroupName = "AI Assistant")]
[Authorize(Policy = AuthorizationPolicies.SectionAccessContent)]
[PlainServerErrors]
public sealed class AiAssistantController(
    PolishService polish,
    UserRateLimiter limiter,
    IAuthorizationService authorization,
    IContentService contentService,
    IEffectiveSettings settings,
    Media.AltTextService altText,
    Seo.SeoService seo,
    Jobs.IContentGateway gateway) : ManagementApiControllerBase
{
    public const string NoKeyMessage = "Add an API key in Settings.";
    public const string UnsafeMessage = "Claude returned markup that is not allowed, so the suggestion was refused. Try again.";

    [HttpGet("status")]
    [ProducesResponseType<AiAssistantStatusModel>(StatusCodes.Status200OK)]
    public IActionResult Status()
    {
        return Ok(new AiAssistantStatusModel(settings.IsConfigured, settings.Model, settings.Options.MaxCharacters, settings.IsConfigured ? null : NoKeyMessage));
    }

    [HttpPost("seo")]
    [ProducesResponseType<SeoResult>(StatusCodes.Status200OK)]
    public async Task<IActionResult> SeoPack([FromBody] SeoRequest request, CancellationToken cancellationToken)
    {
        AiAssistantOptions opt = settings.Options;
        if (!settings.IsConfigured) return Problem(NoKeyMessage, statusCode: StatusCodes.Status400BadRequest);
        AuthorizationResult allowed = await authorization.AuthorizeAsync(
            User,
            ContentPermissionResource.WithKeys(ActionUpdate.ActionLetter, request.NodeId),
            AuthorizationPolicies.ContentPermissionByResource);
        if (!allowed.Succeeded) return Forbid();

        string user = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.Identity?.Name ?? "unknown";
        if (!limiter.TryAcquire(user, opt.RequestsPerMinute))
            return Problem("Too many requests. Wait a minute.", statusCode: StatusCodes.Status429TooManyRequests);
        try
        {
            string? culture = gateway.EffectiveCulture(request.NodeId, request.Culture);
            if (culture is not null)
            {
                AuthorizationResult cultureAllowed = await authorization.AuthorizeAsync(
                    User,
                    ContentPermissionResource.WithKeys(ActionUpdate.ActionLetter, request.NodeId, [culture]),
                    AuthorizationPolicies.ContentPermissionByResource);
                if (!cultureAllowed.Succeeded) return Forbid();
            }
            string source = gateway.SourceText(request.NodeId, culture) ?? throw new ArgumentException("The page was not found.");
            var s = await seo.SuggestAsync(source, cancellationToken);
            if (Seo.SeoService.HasMarkup(s)) return Problem(UnsafeMessage, statusCode: StatusCodes.Status502BadGateway);
            return Ok(new SeoResult(s.Title, s.Description, s.ShareText, Seo.SeoService.Problems(source, s),
                opt.Seo.TitleProperty, opt.Seo.DescriptionProperty, opt.Seo.ShareTextProperty));
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

    [HttpPost("alt")]
    [ProducesResponseType<AltResult>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Alt([FromBody] AltRequest request, CancellationToken cancellationToken)
    {
        AiAssistantOptions opt = settings.Options;
        if (!settings.IsConfigured) return Problem(NoKeyMessage, statusCode: StatusCodes.Status400BadRequest);
        // The media permission check does not look at sections, so the Media section is required here.
        if (!(await authorization.AuthorizeAsync(User, AuthorizationPolicies.SectionAccessMedia)).Succeeded) return Forbid();
        AuthorizationResult allowed = await authorization.AuthorizeAsync(
            User, MediaPermissionResource.WithKeys(request.MediaId), AuthorizationPolicies.MediaPermissionByResource);
        if (!allowed.Succeeded) return Forbid();

        string user = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.Identity?.Name ?? "unknown";
        if (!limiter.TryAcquire(user, opt.RequestsPerMinute))
            return Problem("Too many requests. Wait a minute.", statusCode: StatusCodes.Status429TooManyRequests);
        try
        {
            var image = gateway.ReadImage(request.MediaId);
            string text = await altText.DescribeAsync(image, gateway.FileName(request.MediaId), cancellationToken);
            if (HtmlGuard.IsUnsafe("", text, allowMarkup: false)) return Problem(UnsafeMessage, statusCode: StatusCodes.Status502BadGateway);
            return Ok(new AltResult(text, opt.AltTextProperty));
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

    [HttpPost("polish")]
    [ProducesResponseType<PolishResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> Polish([FromBody] PolishRequest request, CancellationToken cancellationToken)
    {
        AiAssistantOptions opt = settings.Options;
        if (!settings.IsConfigured) return Problem(NoKeyMessage, statusCode: StatusCodes.Status400BadRequest);
        AuthorizationResult allowed = await authorization.AuthorizeAsync(
            User,
            ContentPermissionResource.WithKeys(ActionUpdate.ActionLetter, request.NodeId),
            AuthorizationPolicies.ContentPermissionByResource);
        if (!allowed.Succeeded) return Forbid();

        // Check the allow-list against the stored node, not the document type the browser sent.
        IContent? node = contentService.GetById(request.NodeId);
        if (node is null || !node.HasProperty(request.Property) || !opt.Allows(node.ContentType.Alias, request.Property))
            return Problem("Polish is not enabled for this property.", statusCode: StatusCodes.Status400BadRequest);
        request = request with { DocumentType = node.ContentType.Alias };

        string user = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.Identity?.Name ?? "unknown";
        if (!limiter.TryAcquire(user, opt.RequestsPerMinute))
            return Problem("Too many polish requests. Wait a minute.", statusCode: StatusCodes.Status429TooManyRequests);

        try
        {
            PolishResult result = await polish.PolishAsync(request, cancellationToken);
            // The text the editor sent is the baseline. Script, event handlers and script links are refused whatever it holds.
            if (HtmlGuard.IsUnsafe(request.Text, result.Suggestion, request.IsRichText)
                || HtmlGuard.IsUnsafe("", result.Title, allowMarkup: false) || HtmlGuard.IsUnsafe("", result.Summary, allowMarkup: false))
                return Problem(UnsafeMessage, statusCode: StatusCodes.Status502BadGateway);
            return Ok(result);
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

public sealed record AiAssistantStatusModel(bool Configured, string Model, int MaxCharacters, string? Message);

public sealed record AltRequest(Guid MediaId);

public sealed record AltResult(string Suggestion, string Property);

public sealed record SeoRequest(Guid NodeId, string? Culture);

public sealed record SeoResult(
    string Title, string Description, string ShareText, IReadOnlyList<string> Problems,
    string TitleProperty, string DescriptionProperty, string ShareTextProperty);
