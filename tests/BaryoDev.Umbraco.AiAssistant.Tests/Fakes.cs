using BaryoDev.Umbraco.AiAssistant.Claude;
using BaryoDev.Umbraco.AiAssistant.Configuration;
using BaryoDev.Umbraco.AiAssistant.Settings;
using BaryoDev.Umbraco.AiAssistant.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BaryoDev.Umbraco.AiAssistant.Tests;

internal sealed class FakeClaude(Func<ClaudeRequest, string> answer) : IClaudeClient
{
    public List<ClaudeRequest> Requests { get; } = [];

    public Task<ClaudeResponse> CompleteAsync(ClaudeRequest request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(new ClaudeResponse(answer(request)));
    }
}

internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Seen { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Seen.Add(request);
        return Task.FromResult(respond(request));
    }
}

internal sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

internal sealed class ListLogger<T> : ILogger<T>
{
    public List<string> Lines { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Lines.Add(formatter(state, exception));
        if (exception is not null) Lines.Add(exception.ToString());
    }
}

internal sealed class MemoryKeyValueStore : IKeyValueStore
{
    public Dictionary<string, string> Values { get; } = [];
    public string? Get(string key) => Values.GetValueOrDefault(key);
    public void Set(string key, string value) => Values[key] = value;
}

internal static class Opts
{
    public static IEffectiveSettings Effective(AiAssistantOptions o, MemoryKeyValueStore? store = null, IDataProtectionProvider? protection = null)
    {
        store ??= new MemoryKeyValueStore();
        var keys = new ApiKeyStore(store, protection ?? new EphemeralDataProtectionProvider(), TimeProvider.System);
        return new EffectiveSettings(Monitor(o), new SettingsStore(store), keys);
    }

    public static IOptionsMonitor<AiAssistantOptions> Monitor(AiAssistantOptions o) => new FixedMonitor(o);

    private sealed class FixedMonitor(AiAssistantOptions o) : IOptionsMonitor<AiAssistantOptions>
    {
        public AiAssistantOptions CurrentValue => o;
        public AiAssistantOptions Get(string? name) => o;
        public IDisposable? OnChange(Action<AiAssistantOptions, string?> listener) => null;
    }
}

internal static class Sample
{
    /// <summary>An invented post. No real group or place.</summary>
    public const string Post =
        "Last Saturday, 42 volunteers from the Maligaya Neighbors Club cleaned the creek near Bonifacio Street. " +
        "They filled 120 sacks of trash by 10:30 AM. The club thanks Kapitan Reyes and the Barangay Council for the trucks.";
}
