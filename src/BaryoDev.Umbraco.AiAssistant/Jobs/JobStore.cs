using System.Text.Json;
using BaryoDev.Umbraco.AiAssistant.Storage;

namespace BaryoDev.Umbraco.AiAssistant.Jobs;

/// <summary>
/// Jobs live in the database, so a restart loses nothing. One lock guards every change.
/// The lock is in this process only, so run one web server per site (or one with the job runner).
/// </summary>
public sealed class JobStore(IKeyValueStore store)
{
    private const string IndexKey = "BaryoDev.AiAssistant.Jobs";
    private const int Keep = 10;
    private readonly object _gate = new();

    private static string KeyFor(Guid id) => $"BaryoDev.AiAssistant.Job.{id}";

    public BulkJob? Get(Guid id)
    {
        lock (_gate) return Load(id);
    }

    public IReadOnlyList<BulkJob> List()
    {
        lock (_gate) return Ids().Select(Load).OfType<BulkJob>().OrderByDescending(j => j.CreatedAt).ToList();
    }

    public BulkJob? Active()
    {
        lock (_gate) return Ids().Select(Load).OfType<BulkJob>().FirstOrDefault(j => j.IsActive);
    }

    public BulkJob? Running()
    {
        lock (_gate) return Ids().Select(Load).OfType<BulkJob>().FirstOrDefault(j => j.State == JobState.Running);
    }

    /// <summary>Adds the job only when no other job is running, as one step under the lock. Returns the running job when it refuses.</summary>
    public BulkJob? TryAddRunning(BulkJob job)
    {
        lock (_gate)
        {
            BulkJob? running = Running();
            if (running is not null) return running;
            Add(job);
            return null;
        }
    }

    /// <summary>Sets a stopped job running again unless another job is running. Returns the running job when it refuses.</summary>
    public BulkJob? TryResume(Guid id, out BulkJob? resumed)
    {
        lock (_gate)
        {
            resumed = null;
            BulkJob? running = Running();
            if (running is not null) return running;
            resumed = Update(id, j => { if (j.State == JobState.Stopped) { j.State = JobState.Running; j.Message = null; } });
            return null;
        }
    }

    public void Add(BulkJob job)
    {
        lock (_gate)
        {
            List<Guid> ids = Ids();
            ids.Add(job.Id);
            while (ids.Count > Keep)
            {
                Guid oldest = ids.Select(Load).OfType<BulkJob>().Where(j => !j.IsActive).OrderBy(j => j.CreatedAt).Select(j => j.Id).FirstOrDefault();
                if (oldest == Guid.Empty) break;
                ids.Remove(oldest);
                store.Set(KeyFor(oldest), "");
            }
            store.Set(KeyFor(job.Id), JsonSerializer.Serialize(job, JobJson.Options));
            store.Set(IndexKey, JsonSerializer.Serialize(ids));
        }
    }

    /// <summary>Reads the job fresh, applies the change and saves it, all under the lock. Returns null when the job is gone.</summary>
    public BulkJob? Update(Guid id, Action<BulkJob> change)
    {
        lock (_gate)
        {
            BulkJob? job = Load(id);
            if (job is null) return null;
            change(job);
            store.Set(KeyFor(id), JsonSerializer.Serialize(job, JobJson.Options));
            return job;
        }
    }

    private BulkJob? Load(Guid id)
    {
        string? json = store.Get(KeyFor(id));
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<BulkJob>(json, JobJson.Options); }
        catch (JsonException) { return null; }
    }

    private List<Guid> Ids()
    {
        string? json = store.Get(IndexKey);
        if (string.IsNullOrEmpty(json)) return [];
        try { return JsonSerializer.Deserialize<List<Guid>>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}
