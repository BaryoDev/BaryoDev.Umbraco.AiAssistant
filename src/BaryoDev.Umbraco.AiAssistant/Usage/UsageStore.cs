using System.Globalization;
using System.Text.Json;
using BaryoDev.Umbraco.AiAssistant.Storage;

namespace BaryoDev.Umbraco.AiAssistant.Usage;

public sealed class MonthUsage
{
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public int Calls { get; set; }

    /// <summary>USD, added up at record time with the prices in force then. Later price changes do not rewrite it.</summary>
    public decimal Cost { get; set; }

    /// <summary>Calls recorded while no price was known. Their cost is not in Cost.</summary>
    public int UnpricedCalls { get; set; }
}

/// <summary>Input and output tokens per calendar month (UTC), read from each API response and kept in the database.</summary>
public sealed class UsageStore(IKeyValueStore store, TimeProvider time)
{
    private readonly object _gate = new();

    internal static string Month(DateTimeOffset when) => when.ToUniversalTime().ToString("yyyy-MM", CultureInfo.InvariantCulture);

    internal static string KeyFor(DateTimeOffset when) => "BaryoDev.AiAssistant.Usage." + Month(when);

    public string CurrentMonth => Month(time.GetUtcNow());

    public MonthUsage Current()
    {
        string? json = store.Get(KeyFor(time.GetUtcNow()));
        if (string.IsNullOrEmpty(json)) return new MonthUsage();
        try { return JsonSerializer.Deserialize<MonthUsage>(json) ?? new MonthUsage(); }
        catch (JsonException) { return new MonthUsage(); }
    }

    public void Record(int inputTokens, int outputTokens, decimal? cost)
    {
        lock (_gate)
        {
            MonthUsage u = Current();
            u.InputTokens += Math.Max(0, inputTokens);
            u.OutputTokens += Math.Max(0, outputTokens);
            u.Calls++;
            if (cost is { } c) u.Cost += c; else u.UnpricedCalls++;
            store.Set(KeyFor(time.GetUtcNow()), JsonSerializer.Serialize(u));
        }
    }
}
