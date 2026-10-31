using Umbraco.Cms.Core.Services;

namespace BaryoDev.Umbraco.AiAssistant.Storage;

/// <summary>Small string store. In a site it is Umbraco's key-value table, so it lives in the site database.</summary>
public interface IKeyValueStore
{
    string? Get(string key);
    void Set(string key, string value);
}

internal sealed class UmbracoKeyValueStore(IKeyValueService keyValues) : IKeyValueStore
{
    public string? Get(string key) => keyValues.GetValue(key);

    public void Set(string key, string value) => keyValues.SetValue(key, value);
}
