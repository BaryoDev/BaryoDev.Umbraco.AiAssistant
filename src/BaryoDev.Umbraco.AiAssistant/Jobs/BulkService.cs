using Microsoft.Extensions.Logging;
using BaryoDev.Umbraco.AiAssistant.Polish;
using System.Security.Cryptography;
using System.Text;
using BaryoDev.Umbraco.AiAssistant.Settings;
using BaryoDev.Umbraco.AiAssistant.Usage;

namespace BaryoDev.Umbraco.AiAssistant.Jobs;

public sealed record BulkRequest(
    JobKind Kind,
    Guid? NodeId,
    bool IncludeDescendants,
    string? DocumentType,
    string? Culture = null,
    bool Overwrite = false,
    bool Confirm = false);

public sealed record BulkEstimate(
    int Nodes,
    int Items,
    int Skipped,
    int NoAccess,
    bool Truncated,
    long InputTokens,
    long OutputTokens,
    decimal? EstimatedCost,
    bool FitsCap,
    string? CapMessage);

public sealed record AcceptAllResult(int Accepted, int SkippedFactsFailed, int Failed, string? FirstError);

/// <summary>Planning, starting and reviewing bulk jobs. Nothing reaches content except through Accept.</summary>
public sealed class BulkService(
    JobStore jobs,
    IContentGateway gateway,
    IEnumerable<IKindHandler> handlers,
    IEffectiveSettings settings,
    SpendService spend,
    TimeProvider time,
    ILogger<BulkService>? log = null)
{
    public const string NoKeyMessage = "Add an API key in Settings.";
    private readonly Dictionary<JobKind, IKindHandler> _handlers = handlers.ToDictionary(h => h.Kind);

    public async Task<(BulkEstimate Estimate, List<BulkItem> Items)> PlanAsync(
        BulkRequest request, Func<Guid, string?, Task<bool>> allow, CancellationToken cancellationToken)
    {
        if (!_handlers.TryGetValue(request.Kind, out IKindHandler? handler)) throw new BulkException("This kind of job is not available.");
        if (request.Kind == JobKind.Alt && request.NodeId is null && string.IsNullOrWhiteSpace(request.DocumentType))
            request = request with { DocumentType = "Image" };
        if (request.NodeId is null && string.IsNullOrWhiteSpace(request.DocumentType))
            throw new BulkException("Pick a content node or a document type.");

        if (request.Kind == JobKind.Translate && gateway.CheckTargetCulture(request.Culture) is { } cultureProblem)
            throw new BulkException(cultureProblem);

        IReadOnlyList<NodeRef> found = gateway.FindNodes(request.Kind, new NodeScope(request.NodeId, request.IncludeDescendants, request.DocumentType));
        var options = new JobOptions(request.Culture, request.Overwrite);
        int max = Math.Max(1, settings.Options.MaxBulkItems);

        var items = new List<BulkItem>();
        int noAccess = 0;
        int nodes = 0;
        bool truncated = false;
        foreach (NodeRef node in found)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await allow(node.Id, null)) { noAccess++; continue; }
            if (request.Kind == JobKind.Translate && !await allow(node.Id, gateway.SourceCultureCode())) { noAccess++; continue; }
            nodes++;
            bool denied = false;
            foreach (BulkItem item in gateway.Plan(request.Kind, node, options))
            {
                if (item.Culture is not null && !await allow(node.Id, item.Culture)) { denied = true; continue; }
                if (items.Count >= max) { truncated = true; break; }
                items.Add(item);
            }
            if (denied) noAccess++;
            if (truncated) break;
        }

        long input = 0, output = 0;
        foreach (BulkItem i in items.Where(i => i.State == ItemState.Queued))
        {
            (long inTok, long outTok) = handler.Estimate(i);
            input += inTok;
            output += outTok;
        }

        decimal? cost = spend.Cost(input, output);
        bool fits = spend.Fits(cost ?? 0, out string? reason);
        var estimate = new BulkEstimate(
            nodes, items.Count(i => i.State == ItemState.Queued), items.Count(i => i.State == ItemState.Skipped),
            noAccess, truncated, input, output, cost, fits, reason);
        return (estimate, items);
    }

    public BulkJob Start(BulkRequest request, BulkEstimate estimate, List<BulkItem> items, string user, string? userName = null)
    {
        if (!settings.IsConfigured) throw new BulkException(NoKeyMessage);
        if (estimate.Items == 0)
            throw new BulkException(string.IsNullOrWhiteSpace(request.Culture)
                ? "There is nothing to do for this selection."
                : $"There is nothing to do for this selection in the language \"{request.Culture}\". Check the language code.");
        if (!request.Confirm) throw new BulkException("Confirm the estimated cost before starting.");
        if (!estimate.FitsCap) throw new BulkException(estimate.CapMessage ?? "This run would go over the monthly spend cap.");

        var job = new BulkJob
        {
            Kind = request.Kind,
            CreatedBy = user,
            CreatedByName = userName ?? "",
            CreatedAt = time.GetUtcNow(),
            TargetCulture = request.Culture,
            Overwrite = request.Overwrite,
            EstimatedCost = estimate.EstimatedCost,
            Items = items,
        };
        if (jobs.TryAddRunning(job) is { } running) throw new BulkException(Busy(running));
        return job;
    }

    public BulkJob Cancel(Guid id) =>
        jobs.Update(id, j =>
        {
            if (!j.IsActive) return;
            j.State = JobState.Cancelled;
            j.Message = "Cancelled.";
        }) ?? throw new BulkException("That run no longer exists.");

    public BulkJob Resume(Guid id)
    {
        if (!settings.IsConfigured) throw new BulkException(NoKeyMessage);
        BulkJob job = jobs.Get(id) ?? throw new BulkException("That run no longer exists.");
        if (job.State != JobState.Stopped) throw new BulkException("Only a stopped run can be resumed.");
        if (jobs.TryResume(id, out BulkJob? resumed) is { } running) throw new BulkException(Busy(running));
        return resumed!;
    }

    private static string Busy(BulkJob running) =>
        $"Another bulk run is in progress, started by {running.Creator}. It can be cancelled by them or by a user with Settings access, or you can wait until it finishes.";

    public const int MaxEditLength = 20_000;

    public BulkJob Edit(Guid id, Guid itemId, string alias, string value)
    {
        return jobs.Update(id, j =>
        {
            BulkItem item = Ready(j, itemId);
            FieldChange f = item.Fields.FirstOrDefault(x => string.Equals(x.Alias, alias, StringComparison.OrdinalIgnoreCase))
                ?? throw new BulkException("That field is not part of this suggestion.");
            if (value.Length > Math.Max(MaxEditLength, 4 * f.Original.Length)) throw new BulkException("That text is too long.");
            f.Edited = value;
            if (_handlers.TryGetValue(j.Kind, out IKindHandler? h)) h.Recheck(item);
        }) ?? throw new BulkException("That run no longer exists.");
    }

    public BulkJob Reject(Guid id, Guid itemId) =>
        jobs.Update(id, j =>
        {
            BulkItem item = j.Items.FirstOrDefault(i => i.Id == itemId) ?? throw new BulkException("That item no longer exists.");
            if (item.State is ItemState.Accepted) throw new BulkException("That item was already accepted.");
            item.State = ItemState.Rejected;
        }) ?? throw new BulkException("That run no longer exists.");

    /// <summary>Writes one item as a saved draft. The only place content changes.</summary>
    public async Task<BulkJob> AcceptAsync(Guid id, Guid itemId, int userId, Func<Guid, string?, Task<bool>> allow, bool acceptFailedFacts = false)
    {
        BulkJob job = jobs.Get(id) ?? throw new BulkException("That run no longer exists.");
        BulkItem peek = job.Items.FirstOrDefault(i => i.Id == itemId) ?? throw new BulkException("That item no longer exists.");
        if (!await allow(peek.NodeId, peek.Culture)) throw new BulkException("You do not have permission to change this content.");
        if (peek.Facts == FactsStatus.Failed && !acceptFailedFacts)
            throw new BulkException("This suggestion failed the facts check. Confirm that you want to accept it anyway.");
        if (!await SharedAllowed(peek, allow)) throw new BulkException("This item also changes a field shared by all languages, which needs permission for the default language.");
        return jobs.Update(id, j => AcceptItem(j, Ready(j, itemId), userId))!;
    }

    public async Task<(BulkJob Job, AcceptAllResult Result)> AcceptAllPassedAsync(Guid id, int userId, Func<Guid, string?, Task<bool>> allow)
    {
        BulkJob job = jobs.Get(id) ?? throw new BulkException("That run no longer exists.");
        var permitted = new HashSet<(Guid, string?)>();
        foreach (var key in job.Items.Select(i => (i.NodeId, i.Culture)).Distinct())
            if (await allow(key.NodeId, key.Culture)) permitted.Add(key);
        var sharedDenied = new HashSet<Guid>();
        foreach (BulkItem shared in job.Items.Where(i => i.State == ItemState.Ready))
            if (!await SharedAllowed(shared, allow)) sharedDenied.Add(shared.Id);

        int accepted = 0, skipped = 0, failed = 0;
        string? firstError = null;
        BulkJob updated = jobs.Update(id, j =>
        {
            foreach (BulkItem item in j.Items.Where(i => i.State == ItemState.Ready))
            {
                if (item.Facts is FactsStatus.Failed or FactsStatus.Pending) { skipped++; continue; }
                if (sharedDenied.Contains(item.Id)) { failed++; firstError ??= "This item also changes a field shared by all languages, which needs permission for the default language."; continue; }
                if (!permitted.Contains((item.NodeId, item.Culture))) { failed++; firstError ??= "No permission for some items."; continue; }
                try
                {
                    AcceptItem(j, item, userId);
                    accepted++;
                }
                catch (BulkException e)
                {
                    failed++;
                    firstError ??= e.Message;
                }
                catch (Exception e)
                {
                    // Keep going so the items already accepted are saved with the job.
                    log?.LogError("Accepting an item failed with {ExceptionType}", e.GetType().Name);
                    item.State = ItemState.Failed;
                    item.Reason = $"{item.Name}: could not be saved. See the server log.";
                    failed++;
                    firstError ??= item.Reason;
                }
            }
        })!;
        return (updated, new AcceptAllResult(accepted, skipped, failed, firstError));
    }

    /// <summary>Publishing is its own step, and only for an item the editor already accepted.</summary>
    public async Task<BulkJob> PublishAsync(Guid id, Guid itemId, int userId, Func<Guid, string?, Task<bool>> allowPublish)
    {
        BulkJob job = jobs.Get(id) ?? throw new BulkException("That run no longer exists.");
        BulkItem peek = job.Items.FirstOrDefault(i => i.Id == itemId) ?? throw new BulkException("That item no longer exists.");
        if (job.Kind == JobKind.Alt) throw new BulkException("Media is saved when you accept. There is nothing to publish.");
        if (peek.State != ItemState.Accepted) throw new BulkException("Accept the suggestion first. Publishing is a separate step after that.");
        if (!await allowPublish(peek.NodeId, peek.Culture)) throw new BulkException("You do not have permission to publish this content.");
        if (!await SharedAllowed(peek, allowPublish)) throw new BulkException("This item includes a field shared by all languages, so publishing needs permission for the default language.");
        return jobs.Update(id, j =>
        {
            BulkItem item = j.Items.First(i => i.Id == itemId);
            try { gateway.Publish(item.NodeId, item.Culture, userId); }
            catch (GatewayException e) { throw new BulkException(e.Message); }
            item.Published = true;
        })!;
    }

    /// <summary>A field shared by all languages changes every language, so the default language must be allowed too.
    /// Read from the live content type, because the type may have changed since the plan was made.</summary>
    private async Task<bool> SharedAllowed(BulkItem item, Func<Guid, string?, Task<bool>> allow) =>
        !item.Fields.Any(f => gateway.IsShared(item.NodeId, f.Alias)) || await allow(item.NodeId, gateway.DefaultCultureCode());

    private static BulkItem Ready(BulkJob job, Guid itemId)
    {
        BulkItem item = job.Items.FirstOrDefault(i => i.Id == itemId) ?? throw new BulkException("That item no longer exists.");
        if (item.State != ItemState.Ready) throw new BulkException("Only a ready suggestion can be changed.");
        return item;
    }

    private void AcceptItem(BulkJob job, BulkItem item, int userId)
    {
        foreach (FieldChange f in item.Fields)
        {
            string? now = gateway.Current(item.NodeId, f.Alias, item.Culture);
            if (now is null || Hash(now) != f.OriginalHash)
                throw new BulkException($"{item.Name}: the content changed after this suggestion was made. Reject it and run again.");
            if (string.IsNullOrWhiteSpace(f.Final)) throw new BulkException($"{item.Name}: the suggestion is empty.");
            if (f.Unsafe(f.Final)) throw new BulkException($"{item.Name}: the text contains unsafe markup (script, event handler or script link) and cannot be accepted.");
        }

        var writes = item.Fields.Select(f => new FieldWrite(f.Alias, f.Final!, f.IsRich)).ToList();
        try { gateway.SaveDraft(item.NodeId, item.Culture, writes, userId); }
        catch (GatewayException e) { throw new BulkException($"{item.Name}: {e.Message}"); }
        item.State = ItemState.Accepted;
    }

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
