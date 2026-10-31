using System.Text.Json;
using BaryoDev.Umbraco.AiAssistant.Configuration;
using BaryoDev.Umbraco.AiAssistant.Polish;
using Microsoft.Extensions.Logging.Abstractions;

namespace BaryoDev.Umbraco.AiAssistant.Tests;

public class PromptTests
{
    private static PolishRequest Req(string text = Sample.Post, bool rich = false) =>
        new(Guid.NewGuid(), "post", "body", text, rich, "news");

    [Fact]
    public async Task Site_rules_text_reaches_the_prompt()
    {
        var options = new AiAssistantOptions { Key = "k", Rules = "- Always write in the second person." };
        var fake = new FakeClaude(_ => JsonSerializer.Serialize(new { text = Sample.Post }));
        var service = new PolishService(fake, Opts.Effective(options), NullLogger<PolishService>.Instance);

        await service.PolishAsync(Req(), CancellationToken.None);

        string system = fake.Requests.Single().System;
        Assert.Contains("Always write in the second person.", system);
        Assert.DoesNotContain("Pick the form", system);
        Assert.Contains("Never invent names, numbers, dates, places or partners", system);
    }

    [Fact]
    public async Task Default_rules_are_used_when_no_rules_text_is_set()
    {
        var fake = new FakeClaude(_ => JsonSerializer.Serialize(new { text = Sample.Post }));
        var service = new PolishService(fake, Opts.Effective(new AiAssistantOptions { Key = "k" }), NullLogger<PolishService>.Instance);

        await service.PolishAsync(Req(), CancellationToken.None);

        string system = fake.Requests.Single().System;
        Assert.Contains("Pick the form", system);
        Assert.Contains("No em dashes", system);
        Assert.Contains("Filipino and Taglish", system);
    }

    [Fact]
    public async Task Source_text_form_and_html_hint_reach_the_user_message()
    {
        var fake = new FakeClaude(_ => JsonSerializer.Serialize(new { text = Sample.Post }));
        var service = new PolishService(fake, Opts.Effective(new AiAssistantOptions { Key = "k" }), NullLogger<PolishService>.Instance);

        await service.PolishAsync(Req("<p>" + Sample.Post + "</p>", rich: true), CancellationToken.None);

        string user = fake.Requests.Single().User;
        Assert.Contains("<source>", user);
        Assert.Contains("Maligaya Neighbors Club", user);
        Assert.Contains("Form: news.", user);
        Assert.Contains("HTML", user);
    }

    [Fact]
    public async Task Text_over_the_limit_is_refused_before_claude_is_called()
    {
        var fake = new FakeClaude(_ => "{}");
        var service = new PolishService(fake, Opts.Effective(new AiAssistantOptions { Key = "k", MaxCharacters = 20 }), NullLogger<PolishService>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() => service.PolishAsync(Req(), CancellationToken.None));
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task A_form_other_than_news_or_article_is_refused_before_claude_is_called()
    {
        var fake = new FakeClaude(_ => "{}");
        var service = new PolishService(fake, Opts.Effective(new AiAssistantOptions { Key = "k" }), NullLogger<PolishService>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.PolishAsync(Req() with { Form = new string('x', 50_000) }, CancellationToken.None));
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task Title_and_summary_come_back_as_their_own_suggestions()
    {
        var fake = new FakeClaude(_ => "```json\n" + JsonSerializer.Serialize(new { text = Sample.Post, title = "Creek cleanup", summary = "42 volunteers filled 120 sacks." }) + "\n```");
        var service = new PolishService(fake, Opts.Effective(new AiAssistantOptions { Key = "k" }), NullLogger<PolishService>.Instance);

        PolishResult result = await service.PolishAsync(Req() with { WantTitle = true, WantSummary = true }, CancellationToken.None);

        Assert.Equal("Creek cleanup", result.Title);
        Assert.Equal("42 volunteers filled 120 sacks.", result.Summary);
        Assert.False(result.Flagged);
    }

    [Fact]
    public void Allow_list_limits_document_types_and_properties()
    {
        var o = new AiAssistantOptions { DocumentTypes = [new DocumentTypeRule { Alias = "post", Properties = ["body"] }] };
        Assert.True(o.Allows("post", "body"));
        Assert.False(o.Allows("post", "title"));
        Assert.False(o.Allows("event", "body"));
        Assert.True(new AiAssistantOptions().Allows("anything", "anything"));
    }

    [Fact]
    public void Rate_limiter_blocks_the_seventh_call_in_a_minute_and_frees_after_it()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var limiter = new UserRateLimiter(time);
        for (int i = 0; i < 6; i++) Assert.True(limiter.TryAcquire("u1", 6));
        Assert.False(limiter.TryAcquire("u1", 6));
        Assert.True(limiter.TryAcquire("u2", 6));
        time.Advance(TimeSpan.FromSeconds(61));
        Assert.True(limiter.TryAcquire("u1", 6));
    }
}
