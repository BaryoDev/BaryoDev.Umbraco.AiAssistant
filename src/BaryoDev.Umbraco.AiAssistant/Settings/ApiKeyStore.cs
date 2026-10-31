using System.Security.Cryptography;
using System.Text.Json;
using BaryoDev.Umbraco.AiAssistant.Storage;
using Microsoft.AspNetCore.DataProtection;

namespace BaryoDev.Umbraco.AiAssistant.Settings;

public enum StoredKeyState
{
    None,
    Stored,
    /// <summary>A key is stored but the encryption keys that protected it are gone.</summary>
    Lost,
}

internal sealed class StoredKeyRecord
{
    public string Protected { get; set; } = "";
    public string SavedBy { get; set; } = "";
    public DateTimeOffset SavedAt { get; set; }
}

/// <summary>
/// The API key entered in the dashboard, encrypted with Data Protection under a key ring of its own.
/// Nothing here returns the key to a caller outside the Claude client.
/// </summary>
public sealed class ApiKeyStore(IKeyValueStore store, IDataProtectionProvider protection, TimeProvider time)
{
    internal const string Key = "BaryoDev.AiAssistant.Key";
    internal const string Purpose = "BaryoDev.AiAssistant.ApiKey";

    private readonly IDataProtector _protector = protection.CreateProtector(Purpose);

    private StoredKeyRecord? Record()
    {
        string? json = store.Get(Key);
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            StoredKeyRecord? r = JsonSerializer.Deserialize<StoredKeyRecord>(json);
            return r is null || string.IsNullOrEmpty(r.Protected) ? null : r;
        }
        catch (JsonException) { return null; }
    }

    public void Save(string apiKey, string savedBy) =>
        store.Set(Key, JsonSerializer.Serialize(new StoredKeyRecord
        {
            Protected = _protector.Protect(apiKey.Trim()),
            SavedBy = savedBy,
            SavedAt = time.GetUtcNow(),
        }));

    public void Clear() => store.Set(Key, "");

    /// <summary>The decrypted key, or null when none is stored or it cannot be decrypted.</summary>
    internal string? Read()
    {
        StoredKeyRecord? r = Record();
        if (r is null) return null;
        try { return _protector.Unprotect(r.Protected); }
        catch (CryptographicException) { return null; }
    }

    public StoredKeyState State => Record() is null ? StoredKeyState.None : Read() is null ? StoredKeyState.Lost : StoredKeyState.Stored;

    public (string SavedBy, DateTimeOffset SavedAt)? Info()
    {
        StoredKeyRecord? r = Record();
        return r is null ? null : (r.SavedBy, r.SavedAt);
    }
}
