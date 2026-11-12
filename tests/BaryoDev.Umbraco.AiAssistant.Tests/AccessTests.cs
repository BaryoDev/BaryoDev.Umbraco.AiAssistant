using System.Security.Claims;
using BaryoDev.Umbraco.AiAssistant.Api;
using BaryoDev.Umbraco.AiAssistant.Configuration;
using BaryoDev.Umbraco.AiAssistant.Jobs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Web.Common.Authorization;

namespace BaryoDev.Umbraco.AiAssistant.Tests;

public class AccessTests
{
    /// <summary>Succeeds for everything except the named section policies. Resource checks succeed, as the media check does without a section test.</summary>
    private sealed class Authz(params string[] denied) : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements) =>
            Task.FromResult(AuthorizationResult.Success());

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName) =>
            Task.FromResult(resource is null && denied.Contains(policyName) ? AuthorizationResult.Failed() : AuthorizationResult.Success());
    }

    /// <summary>Allows the section policies and refuses every node or media resource check.</summary>
    private sealed class NoNodeAccess : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements) =>
            Task.FromResult(AuthorizationResult.Failed());

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName) =>
            Task.FromResult(resource is null ? AuthorizationResult.Success() : AuthorizationResult.Failed());
    }

    private static T WithUser<T>(T controller) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity("test")) } };
        return controller;
    }

    private static BulkController Bulk(IAuthorizationService authz, JobStore? jobs = null)
    {
        jobs ??= new JobStore(new MemoryKeyValueStore());
        var settings = Opts.Effective(new AiAssistantOptions { Key = "test-NOT-A-REAL-KEY-000" });
        var bulk = new BulkService(jobs, new FakeGateway(), [], settings, null!, TimeProvider.System);
        return WithUser(new BulkController(bulk, jobs, authz, null!, settings));
    }

    [Fact]
    public async Task A_content_only_user_cannot_run_alt_text_on_one_image()
    {
        var c = WithUser(new AiAssistantController(null!, null!, new Authz(AuthorizationPolicies.SectionAccessMedia), null!,
            Opts.Effective(new AiAssistantOptions { Key = "test-NOT-A-REAL-KEY-000" }), null!, null!, null!));

        IActionResult result = await c.Alt(new AltRequest(Guid.NewGuid()), CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task Edit_reject_and_resume_need_permission_on_the_node_like_accept()
    {
        var jobs = new JobStore(new MemoryKeyValueStore());
        var job = new BulkJob { Kind = JobKind.Polish, CreatedBy = "x", State = JobState.Stopped };
        var item = new BulkItem { NodeId = Guid.NewGuid(), Name = "Page", State = ItemState.Ready };
        var queued = new BulkItem { NodeId = Guid.NewGuid(), Name = "Other", State = ItemState.Queued };
        job.Items.AddRange([item, queued]);
        jobs.Add(job);
        var c = Bulk(new NoNodeAccess(), jobs);
        c.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "x")], "test"));

        foreach (IActionResult result in new[]
        {
            await c.Edit(job.Id, item.Id, new EditRequest("body", "text")),
            await c.Reject(job.Id, item.Id),
            await c.Resume(job.Id),
        })
            Assert.Equal(400, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal(ItemState.Ready, jobs.Get(job.Id)!.Items.First().State);
    }

    private static (JobStore Jobs, BulkJob Job) OthersJob()
    {
        var jobs = new JobStore(new MemoryKeyValueStore());
        var job = new BulkJob { Kind = JobKind.Polish, CreatedBy = "someone-else", State = JobState.Stopped };
        var item = new BulkItem { NodeId = Guid.NewGuid(), Name = "Private page", State = ItemState.Ready };
        item.Fields.Add(new FieldChange { Alias = "body", Original = "secret original", Suggestion = "secret suggestion" });
        job.Items.Add(item);
        jobs.Add(job);
        return (jobs, job);
    }

    private static BulkController AsUser(BulkController c, string id)
    {
        c.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, id)], "test"));
        return c;
    }

    [Fact]
    public async Task A_settings_user_reading_someone_elses_job_gets_no_items_for_nodes_they_cannot_browse()
    {
        (JobStore jobs, BulkJob job) = OthersJob();
        var c = AsUser(Bulk(new NoNodeAccess(), jobs), "x");

        var read = Assert.IsType<BulkJob>(Assert.IsType<OkObjectResult>(await c.Get(job.Id)).Value);

        Assert.Empty(read.Items);
        Assert.Empty(Assert.IsType<List<JobSummary>>(Assert.IsType<OkObjectResult>(await c.List()).Value));
    }

    [Fact]
    public async Task Cancel_and_reject_return_no_item_text_for_nodes_the_caller_cannot_browse()
    {
        (JobStore jobs, BulkJob job) = OthersJob();
        var c = AsUser(Bulk(new NoNodeAccess(), jobs), "x");

        var cancelled = Assert.IsType<BulkJob>(Assert.IsType<OkObjectResult>(await c.Cancel(job.Id)).Value);

        Assert.Empty(cancelled.Items);
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(cancelled));
    }

    /// <summary>Allows the section policies and Update on nodes, but refuses Browse.</summary>
    private sealed class UpdateNotBrowse : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements) =>
            Task.FromResult(AuthorizationResult.Failed());

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName) =>
            Task.FromResult(resource is global::Umbraco.Cms.Core.Security.Authorization.ContentPermissionResource r
                && r.PermissionsToCheck.Contains(global::Umbraco.Cms.Core.Actions.ActionBrowse.ActionLetter)
                    ? AuthorizationResult.Failed()
                    : AuthorizationResult.Success());
    }

    [Fact]
    public async Task Reject_returns_no_item_text_for_nodes_the_caller_cannot_browse()
    {
        (JobStore jobs, BulkJob job) = OthersJob();
        var c = AsUser(Bulk(new UpdateNotBrowse(), jobs), "x");

        var rejected = Assert.IsType<BulkJob>(Assert.IsType<OkObjectResult>(await c.Reject(job.Id, job.Items[0].Id)).Value);

        Assert.Empty(rejected.Items);
    }

    [Fact]
    public async Task A_user_who_can_browse_the_nodes_reads_the_items_and_the_creator_always_sees_the_job_listed()
    {
        (JobStore jobs, BulkJob job) = OthersJob();

        var read = Assert.IsType<BulkJob>(Assert.IsType<OkObjectResult>(await AsUser(Bulk(new Authz(), jobs), "x").Get(job.Id)).Value);
        Assert.Single(read.Items);

        var listed = Assert.IsType<List<JobSummary>>(Assert.IsType<OkObjectResult>(await AsUser(Bulk(new NoNodeAccess(), jobs), "someone-else").List()).Value);
        Assert.Single(listed);
    }

    [Fact]
    public void Every_api_controller_carries_an_authorize_policy()
    {
        var controllers = typeof(BulkController).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(global::Umbraco.Cms.Api.Management.Controllers.ManagementApiControllerBase).IsAssignableFrom(t))
            .ToList();

        Assert.NotEmpty(controllers);
        foreach (Type t in controllers)
            Assert.True(
                t.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false).Cast<AuthorizeAttribute>().Any(a => !string.IsNullOrWhiteSpace(a.Policy)),
                $"{t.Name} has no [Authorize(Policy = ...)]");
    }

    [Fact]
    public async Task A_content_only_user_cannot_estimate_or_start_an_alt_text_run()
    {
        var c = Bulk(new Authz(AuthorizationPolicies.SectionAccessMedia));
        var req = new BulkRequest(JobKind.Alt, Guid.NewGuid(), true, null);

        Assert.IsType<ForbidResult>(await c.Estimate(req, CancellationToken.None));
        Assert.IsType<ForbidResult>(await c.Start(req, CancellationToken.None));
    }

    [Fact]
    public async Task A_content_only_user_cannot_accept_or_accept_all_in_an_alt_text_job()
    {
        var jobs = new JobStore(new MemoryKeyValueStore());
        var job = new BulkJob { Kind = JobKind.Alt, CreatedBy = "x" };
        jobs.Add(job);
        var c = Bulk(new Authz(AuthorizationPolicies.SectionAccessMedia), jobs);
        c.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "x")], "test"));

        Assert.IsType<ForbidResult>(await c.Accept(job.Id, Guid.NewGuid()));
        Assert.IsType<ForbidResult>(await c.AcceptAll(job.Id));
    }

    private sealed class Says(string text) : BaryoDev.Umbraco.AiAssistant.Claude.IClaudeClient
    {
        public Task<BaryoDev.Umbraco.AiAssistant.Claude.ClaudeResponse> CompleteAsync(BaryoDev.Umbraco.AiAssistant.Claude.ClaudeRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new BaryoDev.Umbraco.AiAssistant.Claude.ClaudeResponse(text, new BaryoDev.Umbraco.AiAssistant.Claude.ClaudeUsage(1, 1)));
    }

    [Fact]
    public void An_unexpected_error_becomes_a_plain_message_without_a_stack()
    {
        var http = new DefaultHttpContext();
        var context = new Microsoft.AspNetCore.Mvc.Filters.ExceptionContext(
            new Microsoft.AspNetCore.Mvc.ActionContext(http, new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor()),
            []) { Exception = new InvalidOperationException("secret detail at Foo.Bar() in C:\\src\\x.cs:line 9") };

        new PlainServerErrorsAttribute().OnException(context);

        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(500, result.StatusCode);
        var detail = Assert.IsType<ProblemDetails>(result.Value).Detail;
        Assert.Equal(PlainServerErrorsAttribute.Message, detail);
        Assert.DoesNotContain("secret", detail);
        Assert.True(context.ExceptionHandled);
    }

    [Fact]
    public void A_cancelled_request_is_left_alone_and_not_turned_into_a_500()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var http = new DefaultHttpContext { RequestAborted = cts.Token };
        var context = new Microsoft.AspNetCore.Mvc.Filters.ExceptionContext(
            new Microsoft.AspNetCore.Mvc.ActionContext(http, new Microsoft.AspNetCore.Routing.RouteData(), new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor()),
            []) { Exception = new OperationCanceledException() };

        new PlainServerErrorsAttribute().OnException(context);

        Assert.Null(context.Result);
        Assert.False(context.ExceptionHandled);
    }

    private sealed class CapReached : BaryoDev.Umbraco.AiAssistant.Claude.IClaudeClient
    {
        public Task<BaryoDev.Umbraco.AiAssistant.Claude.ClaudeResponse> CompleteAsync(BaryoDev.Umbraco.AiAssistant.Claude.ClaudeRequest request, CancellationToken cancellationToken) =>
            throw new BaryoDev.Umbraco.AiAssistant.Usage.SpendCapException("The monthly spend cap is reached.");
    }

    [Fact]
    public async Task A_refusal_at_the_spend_cap_is_a_client_error_that_carries_the_message()
    {
        var gateway = new FakeGateway();
        var node = gateway.Add("Page", "body", "Words.");
        gateway.Sources[node.Ref.Id] = "The Harbour Festival is on 12 June.";
        var c = WithUser(new AiAssistantController(null!, new BaryoDev.Umbraco.AiAssistant.Polish.UserRateLimiter(TimeProvider.System), new Authz(), null!,
            Opts.Effective(new AiAssistantOptions { Key = "test-NOT-A-REAL-KEY-000" }),
            new BaryoDev.Umbraco.AiAssistant.Media.AltTextService(new CapReached()),
            new BaryoDev.Umbraco.AiAssistant.Seo.SeoService(new CapReached()), gateway));

        var result = Assert.IsType<ObjectResult>(await c.SeoPack(new SeoRequest(node.Ref.Id, null), CancellationToken.None));

        Assert.Equal(402, result.StatusCode);
        Assert.Contains("spend cap", Assert.IsType<Microsoft.AspNetCore.Mvc.ProblemDetails>(result.Value).Detail);
    }

    private static AiAssistantController Single(string answer, FakeGateway gateway) =>
        WithUser(new AiAssistantController(null!, new BaryoDev.Umbraco.AiAssistant.Polish.UserRateLimiter(TimeProvider.System), new Authz(), null!,
            Opts.Effective(new AiAssistantOptions { Key = "test-NOT-A-REAL-KEY-000" }),
            new BaryoDev.Umbraco.AiAssistant.Media.AltTextService(new Says(answer)),
            new BaryoDev.Umbraco.AiAssistant.Seo.SeoService(new Says(answer)), gateway));

    [Fact]
    public async Task A_single_seo_suggestion_with_markup_is_refused()
    {
        var gateway = new FakeGateway();
        var node = gateway.Add("Page", "body", "Words.");
        gateway.Sources[node.Ref.Id] = "The Harbour Festival is on 12 June.";
        var c = Single("""{"title":"Harbour <b>Festival</b>","description":"d","shareText":"s"}""", gateway);

        IActionResult result = await c.SeoPack(new SeoRequest(node.Ref.Id, null), CancellationToken.None);

        Assert.Equal(502, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public async Task A_single_alt_suggestion_with_markup_is_refused_and_a_clean_one_passes()
    {
        var gateway = new FakeGateway();
        Assert.Equal(502, Assert.IsType<ObjectResult>(await Single("""{"text":"A dog <script>x()</script>"}""", gateway)
            .Alt(new AltRequest(Guid.NewGuid()), CancellationToken.None)).StatusCode);
        Assert.IsType<OkObjectResult>(await Single("""{"text":"A dog on a beach"}""", gateway)
            .Alt(new AltRequest(Guid.NewGuid()), CancellationToken.None));
    }

    [Fact]
    public async Task A_voice_draft_skips_a_page_without_the_source_culture_and_reports_the_count()
    {
        var gateway = new FakeGateway();
        var good = gateway.Add("Good", "body", "Words.");
        var bad = gateway.Add("Bad", "body", "Words.");
        gateway.Sources[good.Ref.Id] = "A calm, short article.";
        gateway.Sources[bad.Ref.Id] = "Another.";
        gateway.NoSourceCulture.Add(bad.Ref.Id);
        var store = new MemoryKeyValueStore();
        var settings = Opts.Effective(new AiAssistantOptions { Key = "test-NOT-A-REAL-KEY-000" }, store);
        var c = WithUser(new VoiceController(settings, new BaryoDev.Umbraco.AiAssistant.Settings.SettingsStore(store),
            new BaryoDev.Umbraco.AiAssistant.Voice.VoiceService(new Says("""{"rules":"- Short sentences."}""")), gateway, new Authz(),
            new BaryoDev.Umbraco.AiAssistant.Polish.UserRateLimiter(TimeProvider.System)));

        IActionResult result = await c.Draft(new DraftRulesRequest([good.Ref.Id, bad.Ref.Id]), CancellationToken.None);

        var body = Assert.IsType<DraftRulesResult>(Assert.IsType<OkObjectResult>(result).Value);
        Assert.Equal(1, body.Skipped);
        Assert.Contains("Short sentences", body.Rules);
    }
}
