using System.Security.Claims;
using Asp.Versioning;
using BaryoDev.Umbraco.AiAssistant.Jobs;
using BaryoDev.Umbraco.AiAssistant.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Api.Management.Controllers;
using Umbraco.Cms.Api.Management.Routing;
using Umbraco.Cms.Core.Actions;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Security.Authorization;
using Umbraco.Cms.Web.Common.Authorization;

namespace BaryoDev.Umbraco.AiAssistant.Api;

/// <summary>
/// Bulk review queue. Needs the Content section. Every node is checked against the signed-in user's
/// permissions (which include start nodes) when a run is planned and again when something is accepted.
/// </summary>
[ApiVersion("1.0")]
[VersionedApiBackOfficeRoute("ai-assistant/bulk")]
[ApiExplorerSettings(GroupName = "AI Assistant")]
[Authorize(Policy = AuthorizationPolicies.SectionAccessContent)]
[PlainServerErrors]
public sealed class BulkController(
    BulkService bulk,
    JobStore jobs,
    IAuthorizationService authorization,
    IBackOfficeSecurityAccessor security,
    IEffectiveSettings settings) : ManagementApiControllerBase
{
    private string UserName => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.Identity?.Name ?? "unknown";

    private string? DisplayName => security.BackOfficeSecurity?.CurrentUser is { } u
        ? (string.IsNullOrWhiteSpace(u.Name) ? u.Username : u.Name)
        : null;

    private int? UserId => security.BackOfficeSecurity?.CurrentUser?.Id;

    private Func<Guid, string?, Task<bool>> Can(string actionLetter, JobKind kind = JobKind.Polish) => kind == JobKind.Alt
        ? async (id, _) => (await authorization.AuthorizeAsync(User, MediaPermissionResource.WithKeys(id), AuthorizationPolicies.MediaPermissionByResource)).Succeeded
        : async (id, culture) => (await authorization.AuthorizeAsync(
            User,
            culture is null ? ContentPermissionResource.WithKeys(actionLetter, id) : ContentPermissionResource.WithKeys(actionLetter, id, [culture]),
            AuthorizationPolicies.ContentPermissionByResource)).Succeeded;

    /// <summary>Alt text jobs touch media, and the media permission check does not look at sections.</summary>
    private async Task<bool> HasSectionFor(JobKind kind) =>
        kind != JobKind.Alt || (await authorization.AuthorizeAsync(User, AuthorizationPolicies.SectionAccessMedia)).Succeeded;

    private async Task<bool> CanSee(BulkJob job) =>
        job.CreatedBy == UserName || (await authorization.AuthorizeAsync(User, AuthorizationPolicies.SectionAccessSettings)).Succeeded;

    [HttpPost("estimate")]
    public async Task<IActionResult> Estimate([FromBody] BulkRequest request, CancellationToken cancellationToken)
    {
        if (!settings.IsConfigured) return Fail(AiAssistantController.NoKeyMessage);
        if (!await HasSectionFor(request.Kind)) return Forbid();
        try
        {
            var (estimate, _) = await bulk.PlanAsync(request, Can(ActionUpdate.ActionLetter, request.Kind), cancellationToken);
            return Ok(estimate);
        }
        catch (BulkException e) { return Fail(e.Message); }
    }

    [HttpPost("start")]
    public async Task<IActionResult> Start([FromBody] BulkRequest request, CancellationToken cancellationToken)
    {
        if (!settings.IsConfigured) return Fail(AiAssistantController.NoKeyMessage);
        if (!await HasSectionFor(request.Kind)) return Forbid();
        try
        {
            var (estimate, items) = await bulk.PlanAsync(request, Can(ActionUpdate.ActionLetter, request.Kind), cancellationToken);
            return Ok(Summary(bulk.Start(request, estimate, items, UserName, DisplayName)));
        }
        catch (BulkException e) { return Fail(e.Message); }
    }

    [HttpGet("jobs")]
    public async Task<IActionResult> List()
    {
        var visible = new List<JobSummary>();
        foreach (BulkJob j in jobs.List())
            if (await CanSee(j) && await CanBrowseAny(j)) visible.Add(Summary(j));
        return Ok(visible);
    }

    /// <summary>The job's creator sees their own job. Anyone else sees it only when they can browse at least one of its nodes.</summary>
    private async Task<bool> CanBrowseAny(BulkJob job)
    {
        if (job.CreatedBy == UserName || job.Items.Count == 0) return true;
        var can = Can(ActionBrowse.ActionLetter, job.Kind);
        foreach (BulkItem item in job.Items)
            if (await can(item.NodeId, item.Culture)) return true;
        return false;
    }

    /// <summary>Items come back only for nodes the caller can browse, so text on other nodes is never sent.</summary>
    [HttpGet("jobs/{id:guid}")]
    public Task<IActionResult> Get(Guid id) =>
        With(id, async j =>
        {
            var can = Can(ActionBrowse.ActionLetter, j.Kind);
            var allowed = new List<BulkItem>();
            foreach (BulkItem item in j.Items)
                if (await can(item.NodeId, item.Culture)) allowed.Add(item);
            j.Items = allowed;
            return j;
        });

    [HttpPost("jobs/{id:guid}/cancel")]
    public Task<IActionResult> Cancel(Guid id) => With(id, j => Task.FromResult<object>(bulk.Cancel(id)));

    [HttpPost("jobs/{id:guid}/resume")]
    public Task<IActionResult> Resume(Guid id) =>
        With(id, async j =>
        {
            await RequireUpdate(j, j.Items.Where(i => i.State == ItemState.Queued));
            return bulk.Resume(id);
        });

    [HttpPut("jobs/{id:guid}/items/{itemId:guid}")]
    public Task<IActionResult> Edit(Guid id, Guid itemId, [FromBody] EditRequest request) =>
        With(id, async j =>
        {
            await RequireUpdate(j, j.Items.Where(i => i.Id == itemId));
            return bulk.Edit(id, itemId, request.Alias, request.Value);
        });

    [HttpPost("jobs/{id:guid}/items/{itemId:guid}/accept")]
    public Task<IActionResult> Accept(Guid id, Guid itemId, [FromBody] AcceptRequest? request = null) =>
        With(id, async j => await bulk.AcceptAsync(id, itemId, RequireUser(), Can(ActionUpdate.ActionLetter, j.Kind), request?.AcceptFailedFacts == true));

    [HttpPost("jobs/{id:guid}/items/{itemId:guid}/reject")]
    public Task<IActionResult> Reject(Guid id, Guid itemId) =>
        With(id, async j =>
        {
            await RequireUpdate(j, j.Items.Where(i => i.Id == itemId));
            return bulk.Reject(id, itemId);
        });

    [HttpPost("jobs/{id:guid}/items/{itemId:guid}/publish")]
    public Task<IActionResult> Publish(Guid id, Guid itemId) =>
        With(id, async j => await bulk.PublishAsync(id, itemId, RequireUser(), Can(ActionPublish.ActionLetter)));

    [HttpPost("jobs/{id:guid}/accept-all")]
    public Task<IActionResult> AcceptAll(Guid id) =>
        With(id, async j =>
        {
            var (job, result) = await bulk.AcceptAllPassedAsync(id, RequireUser(), Can(ActionUpdate.ActionLetter, j.Kind));
            return new { job, result };
        });

    /// <summary>Changing a suggestion needs the same node permission as accepting it.</summary>
    private async Task RequireUpdate(BulkJob job, IEnumerable<BulkItem> items)
    {
        var can = Can(ActionUpdate.ActionLetter, job.Kind);
        foreach (BulkItem item in items.ToList())
            if (!await can(item.NodeId, item.Culture))
                throw new BulkException("You do not have permission to change this content.");
    }

    private int RequireUser() => UserId ?? throw new BulkException("Could not tell which user you are. Sign in again.");

    private async Task<IActionResult> With(Guid id, Func<BulkJob, Task<object>> action)
    {
        BulkJob? job = jobs.Get(id);
        if (job is null) return NotFound();
        if (!await CanSee(job) || !await HasSectionFor(job.Kind)) return Forbid();
        try { return Ok(await action(job)); }
        catch (BulkException e) { return Fail(e.Message); }
    }

    private IActionResult Fail(string message) => Problem(message, statusCode: StatusCodes.Status400BadRequest);

    private static JobSummary Summary(BulkJob j) => new(
        j.Id, j.Kind, j.State, j.Message, j.CreatedAt, j.Creator, j.EstimatedCost,
        j.Items.Count,
        j.Items.Count(i => i.State == ItemState.Queued),
        j.Items.Count(i => i.State == ItemState.Ready),
        j.Items.Count(i => i.State == ItemState.Accepted));
}

public sealed record AcceptRequest(bool AcceptFailedFacts);

public sealed record EditRequest(string Alias, string Value);

public sealed record JobSummary(
    Guid Id, JobKind Kind, JobState State, string? Message, DateTimeOffset CreatedAt, string CreatedBy,
    decimal? EstimatedCost, int Items, int Queued, int Ready, int Accepted);
