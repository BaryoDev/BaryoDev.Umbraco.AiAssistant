using Microsoft.Extensions.Logging;
using BaryoDev.Umbraco.AiAssistant.Claude;
using BaryoDev.Umbraco.AiAssistant.Settings;
using BaryoDev.Umbraco.AiAssistant.Usage;

namespace BaryoDev.Umbraco.AiAssistant.Jobs;

/// <summary>Works one item of the one running job per call. Safe to call again after a restart: the next queued item is the resume point.</summary>
public sealed class JobProcessor(
    JobStore jobs,
    IEnumerable<IKindHandler> handlers,
    IEffectiveSettings settings,
    RetryPolicy retry,
    Microsoft.Extensions.Logging.ILogger<JobProcessor>? logger = null)
{
    private readonly Dictionary<JobKind, IKindHandler> _handlers = handlers.ToDictionary(h => h.Kind);

    /// <summary>True when it did something, so the caller can loop without waiting.</summary>
    public async Task<bool> RunNextAsync(CancellationToken cancellationToken)
    {
        BulkJob? job = jobs.Running();
        if (job is null) return false;

        BulkItem? next = job.Items.FirstOrDefault(i => i.State == ItemState.Queued);
        if (next is null)
        {
            jobs.Update(job.Id, j => { if (j.State == JobState.Running) j.State = JobState.Done; });
            return true;
        }

        if (!settings.IsConfigured)
        {
            Stop(job.Id, "Add an API key in Settings, then resume.");
            return true;
        }

        if (!_handlers.TryGetValue(job.Kind, out IKindHandler? handler))
        {
            Stop(job.Id, "This kind of job is not available.");
            return true;
        }

        BulkItem work = JobJson.Clone(next);
        var options = new JobOptions(job.TargetCulture, job.Overwrite);
        try
        {
            await retry.RunAsync(() => handler.RunAsync(work, options, cancellationToken), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SpendCapException e)
        {
            Stop(job.Id, e.Message);
            return true;
        }
        catch (ClaudeException e) when (e.StatusCode is 401 or 403)
        {
            Stop(job.Id, "Claude rejected the key. Check it in Settings, then resume.");
            return true;
        }
        catch (ClaudeException e) when (e.IsRetryable)
        {
            Stop(job.Id, "Claude is busy or not reachable. Resume in a few minutes.");
            return true;
        }
        catch (ClaudeException e)
        {
            work.State = ItemState.Failed;
            work.Reason = e.Message;
        }
        catch (ArgumentException e)
        {
            work.State = ItemState.Skipped;
            work.Reason = e.Message;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Only the type is logged: the message could carry text from the page or the reply.
            logger?.LogWarning("A bulk item failed with {ExceptionType}.", e.GetType().Name);
            work.State = ItemState.Failed;
            work.Reason = "This item could not be processed. Open it in the editor instead.";
        }

        jobs.Update(job.Id, j =>
        {
            // A cancel that arrived while Claude was working wins: the result is dropped.
            if (j.State != JobState.Running) return;
            int at = j.Items.FindIndex(i => i.Id == work.Id);
            if (at >= 0 && j.Items[at].State == ItemState.Queued) j.Items[at] = work;
        });
        return true;
    }

    private void Stop(Guid id, string message) =>
        jobs.Update(id, j => { if (j.State == JobState.Running) { j.State = JobState.Stopped; j.Message = message; } });
}
