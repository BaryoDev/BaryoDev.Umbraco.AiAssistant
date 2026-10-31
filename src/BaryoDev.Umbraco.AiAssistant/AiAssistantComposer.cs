using BaryoDev.Umbraco.AiAssistant.Claude;
using BaryoDev.Umbraco.AiAssistant.Configuration;
using BaryoDev.Umbraco.AiAssistant.Jobs;
using BaryoDev.Umbraco.AiAssistant.Polish;
using BaryoDev.Umbraco.AiAssistant.Settings;
using BaryoDev.Umbraco.AiAssistant.Storage;
using BaryoDev.Umbraco.AiAssistant.Usage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace BaryoDev.Umbraco.AiAssistant;

public sealed class AiAssistantComposer : IComposer
{
    public void Compose(IUmbracoBuilder builder)
    {
        IServiceCollection services = builder.Services;
        services.AddOptions<AiAssistantOptions>().BindConfiguration(AiAssistantOptions.Section);
        AddClaudeHttpClient(services);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IKeyValueStore, UmbracoKeyValueStore>();
        services.AddSingleton<SettingsStore>();
        services.AddSingleton(sp => new ApiKeyStore(
            sp.GetRequiredService<IKeyValueStore>(),
            KeyRing(sp.GetRequiredService<IHostEnvironment>(), sp.GetRequiredService<IOptions<AiAssistantOptions>>().Value),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IEffectiveSettings, EffectiveSettings>();
        services.AddSingleton<ClaudeClient>();
        services.AddSingleton<UsageStore>();
        services.AddSingleton<SpendService>();
        services.AddSingleton<IClaudeClient>(sp => new MeteredClaudeClient(sp.GetRequiredService<ClaudeClient>(), sp.GetRequiredService<SpendService>()));
        services.AddSingleton<IClaudeKeyTester>(sp => sp.GetRequiredService<ClaudeClient>());
        services.AddSingleton<UserRateLimiter>();
        services.AddSingleton<PolishService>();
        services.AddSingleton<JobStore>();
        services.AddSingleton<IContentGateway, UmbracoContentGateway>();
        services.AddSingleton<Media.AltTextService>();
        services.AddSingleton<IKindHandler, PolishHandler>();
        services.AddSingleton<IKindHandler, AltTextHandler>();
        services.AddSingleton<Seo.SeoService>();
        services.AddSingleton<Voice.VoiceService>();
        services.AddSingleton<IKindHandler, SeoHandler>();
        services.AddSingleton<Translate.TranslateService>();
        services.AddSingleton<IKindHandler, TranslateHandler>();
        services.AddSingleton(new RetryPolicy());
        services.AddSingleton<BulkService>();
        services.AddSingleton<JobProcessor>();
        services.AddHostedService<JobRunner>();
    }

    /// <summary>A key ring of its own, so the site's other Data Protection use is untouched. A redeploy must keep the folder.</summary>
    internal static IDataProtectionProvider KeyRing(IHostEnvironment hosting, AiAssistantOptions options)
    {
        string path = string.IsNullOrWhiteSpace(options.KeysPath)
            ? Path.Combine(hosting.ContentRootPath, "umbraco", "Data", "DataProtection-Keys", "AiAssistant")
            : options.KeysPath;
        return DataProtectionProvider.Create(new DirectoryInfo(path), c => c.SetApplicationName("BaryoDev.AiAssistant"));
    }

    /// <summary>The client factory logs request headers at Trace, which would write the key. Its loggers are removed.</summary>
    internal static IHttpClientBuilder AddClaudeHttpClient(IServiceCollection services) =>
        services.AddHttpClient(ClaudeClient.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(120))
            .RemoveAllLoggers();
}
