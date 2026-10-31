using System.Text.Json;
using BaryoDev.Umbraco.AiAssistant.Storage;

namespace BaryoDev.Umbraco.AiAssistant.Settings;

/// <summary>What the dashboard saves. The API key is not here; it has its own encrypted record.</summary>
public sealed class StoredSettings
{
    public string? Model { get; set; }
    public string? Rules { get; set; }
    public decimal? MonthlyCap { get; set; }
    public decimal? InputPrice { get; set; }
    public decimal? OutputPrice { get; set; }
}

public sealed class SettingsStore(IKeyValueStore store)
{
    internal const string Key = "BaryoDev.AiAssistant.Settings";
    private readonly object _gate = new();

    public StoredSettings Get()
    {
        string? json = store.Get(Key);
        if (string.IsNullOrEmpty(json)) return new StoredSettings();
        try { return JsonSerializer.Deserialize<StoredSettings>(json) ?? new StoredSettings(); }
        catch (JsonException) { return new StoredSettings(); }
    }

    public void Update(Action<StoredSettings> change)
    {
        lock (_gate)
        {
            StoredSettings s = Get();
            change(s);
            store.Set(Key, JsonSerializer.Serialize(s));
        }
    }
}
