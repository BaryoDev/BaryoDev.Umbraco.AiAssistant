using BaryoDev.Umbraco.AiAssistant.Claude;
using BaryoDev.Umbraco.AiAssistant.Configuration;
using BaryoDev.Umbraco.AiAssistant.Polish;
using Microsoft.Extensions.Logging.Abstractions;

namespace BaryoDev.Umbraco.AiAssistant.Tests;

/// <summary>Skipped unless BaryoDev__AiAssistant__Key is set. Spends a few cents.</summary>
public class LiveClaudeTests
{
    [Fact]
    public async Task Polishes_a_short_sample_post_and_keeps_every_fact()
    {
        string? key = Environment.GetEnvironmentVariable("BaryoDev__AiAssistant__Key");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(key), "BaryoDev__AiAssistant__Key is not set.");

        var options = Opts.Effective(new AiAssistantOptions { Key = key! });
        var claude = new ClaudeClient(new LiveFactory(), options, NullLogger<ClaudeClient>.Instance);
        var service = new PolishService(claude, options, NullLogger<PolishService>.Instance);

        PolishResult result = await service.PolishAsync(
            new(Guid.NewGuid(), "post", "body", Sample.Post, Form: "news", WantTitle: true), CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(result.Suggestion));
        Assert.False(result.Flagged, "Dropped facts: " + string.Join(", ", result.Missing));
        Assert.DoesNotContain('\u2014', result.Suggestion);
    }

    private sealed class LiveFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { Timeout = TimeSpan.FromSeconds(120) };
    }
}
