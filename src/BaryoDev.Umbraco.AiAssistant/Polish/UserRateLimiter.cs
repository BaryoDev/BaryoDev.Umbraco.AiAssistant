using System.Collections.Concurrent;

namespace BaryoDev.Umbraco.AiAssistant.Polish;

/// <summary>Sliding one-minute window per backoffice user.</summary>
public sealed class UserRateLimiter(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _hits = new();

    public bool TryAcquire(string user, int perMinute)
    {
        DateTimeOffset now = time.GetUtcNow();
        Queue<DateTimeOffset> q = _hits.GetOrAdd(user, _ => new Queue<DateTimeOffset>());
        lock (q)
        {
            while (q.Count > 0 && now - q.Peek() >= TimeSpan.FromMinutes(1)) q.Dequeue();
            if (q.Count >= perMinute) return false;
            q.Enqueue(now);
            return true;
        }
    }
}
