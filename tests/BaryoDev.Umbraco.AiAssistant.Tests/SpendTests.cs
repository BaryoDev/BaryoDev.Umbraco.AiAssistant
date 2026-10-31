using BaryoDev.Umbraco.AiAssistant.Claude;
using BaryoDev.Umbraco.AiAssistant.Configuration;
using System.Globalization;
using System.Net;
using System.Text;
using BaryoDev.Umbraco.AiAssistant.Settings;
using BaryoDev.Umbraco.AiAssistant.Usage;

namespace BaryoDev.Umbraco.AiAssistant.Tests;

public class SpendTests
{
    private sealed class UsageClaude(int input, int output) : IClaudeClient
    {
        public int Calls { get; private set; }

        public Task<ClaudeResponse> CompleteAsync(ClaudeRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new ClaudeResponse("ok", new ClaudeUsage(input, output)));
        }
    }

    private static (MeteredClaudeClient Client, UsageClaude Inner, UsageStore Usage, SpendService Spend) Make(AiAssistantOptions o, int input = 1_000_000, int output = 0)
    {
        var store = new MemoryKeyValueStore();
        var settings = Opts.Effective(o, store);
        var usage = new UsageStore(store, TimeProvider.System);
        var spend = new SpendService(settings, usage);
        var inner = new UsageClaude(input, output);
        return (new MeteredClaudeClient(inner, spend), inner, usage, spend);
    }

    private static readonly ClaudeRequest Req = new("system", "user");

    [Fact]
    public async Task Tokens_from_the_response_are_recorded_for_the_month()
    {
        var (client, _, usage, _) = Make(new AiAssistantOptions { InputPricePerMillion = 1, OutputPricePerMillion = 1 }, 1200, 300);

        await client.CompleteAsync(Req, TestContext.Current.CancellationToken);
        await client.CompleteAsync(Req, TestContext.Current.CancellationToken);

        var u = usage.Current();
        Assert.Equal(2400, u.InputTokens);
        Assert.Equal(600, u.OutputTokens);
        Assert.Equal(2, u.Calls);
    }

    [Fact]
    public async Task No_cap_means_no_stop()
    {
        var (client, inner, _, _) = Make(new AiAssistantOptions { InputPricePerMillion = 1000, OutputPricePerMillion = 1000 });

        for (int i = 0; i < 5; i++) await client.CompleteAsync(Req, TestContext.Current.CancellationToken);

        Assert.Equal(5, inner.Calls);
    }

    [Fact]
    public async Task A_call_is_refused_before_it_is_made_once_the_cap_is_reached()
    {
        var (client, inner, _, _) = Make(new AiAssistantOptions { MonthlySpendCap = 1.5m, InputPricePerMillion = 1, OutputPricePerMillion = 1 });

        await client.CompleteAsync(Req, TestContext.Current.CancellationToken); // 1.00 spent
        await client.CompleteAsync(Req, TestContext.Current.CancellationToken); // 2.00 spent, cap passed
        var ex = await Assert.ThrowsAsync<SpendCapException>(() => client.CompleteAsync(Req, TestContext.Current.CancellationToken));

        Assert.Equal(2, inner.Calls);
        Assert.Contains("cap", ex.Message);
    }

    [Fact]
    public async Task A_cap_without_any_price_refuses_with_a_plain_message()
    {
        var (client, inner, _, _) = Make(new AiAssistantOptions { Model = "some-unlisted-model", MonthlySpendCap = 5 });

        var ex = await Assert.ThrowsAsync<SpendCapException>(() => client.CompleteAsync(Req, TestContext.Current.CancellationToken));

        Assert.Equal(0, inner.Calls);
        Assert.Contains("price", ex.Message);
    }

    [Fact]
    public void Configured_prices_win_over_saved_and_suggested_ones()
    {
        var store = new MemoryKeyValueStore();
        var s = Opts.Effective(new AiAssistantOptions { InputPricePerMillion = 7, OutputPricePerMillion = 9 }, store);
        new Settings.SettingsStore(store).Update(x => { x.InputPrice = 1; x.OutputPrice = 1; });

        Assert.Equal(7, s.InputPrice);
        Assert.Equal(9, s.OutputPrice);
        Assert.Equal("configuration", s.PriceSource);
    }

    [Fact]
    public void Fits_compares_a_planned_run_with_what_is_left_under_the_cap()
    {
        var (_, _, usage, spend) = Make(new AiAssistantOptions { MonthlySpendCap = 3, InputPricePerMillion = 1, OutputPricePerMillion = 1 });
        usage.Record(2_000_000, 0, 2m);

        Assert.True(spend.Fits(1m, out _));
        Assert.False(spend.Fits(1.5m, out string? reason));
        Assert.Contains("cap", reason);
    }

    [Theory]
    [InlineData("refusal")]
    [InlineData("max_tokens")]
    public async Task A_billed_call_that_is_then_rejected_still_counts(string stopReason)
    {
        var store = new MemoryKeyValueStore();
        var settings = Opts.Effective(new AiAssistantOptions { Key = "test-NOT-A-REAL-KEY-000", InputPricePerMillion = 1, OutputPricePerMillion = 1 }, store);
        var usage = new UsageStore(store, TimeProvider.System);
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$$"""{"content":[{"type":"text","text":"x"}],"stop_reason":"{{{stopReason}}}","usage":{"input_tokens":500,"output_tokens":40}}""", Encoding.UTF8, "application/json"),
        });
        var real = new ClaudeClient(new StubFactory(handler), settings, new ListLogger<ClaudeClient>());
        var client = new MeteredClaudeClient(real, new SpendService(settings, usage));

        await Assert.ThrowsAsync<ClaudeException>(() => client.CompleteAsync(Req, TestContext.Current.CancellationToken));

        var u = usage.Current();
        Assert.Equal(500, u.InputTokens);
        Assert.Equal(40, u.OutputTokens);
        Assert.Equal(1, u.Calls);
    }

    [Fact]
    public async Task Changing_prices_later_does_not_rewrite_spend_already_recorded()
    {
        var store = new MemoryKeyValueStore();
        var settings = Opts.Effective(new AiAssistantOptions { MonthlySpendCap = 1.5m }, store);
        var settingsStore = new SettingsStore(store);
        var usage = new UsageStore(store, TimeProvider.System);
        var spend = new SpendService(settings, usage);
        var client = new MeteredClaudeClient(new UsageClaude(1_000_000, 0), spend);

        // Configuration sets the cap but no prices: the suggested price (2 USD) applies and dashboard prices are ignored.
        settingsStore.Update(s => { s.InputPrice = 0.0001m; s.OutputPrice = 0.0001m; });
        await client.CompleteAsync(Req, TestContext.Current.CancellationToken);
        Assert.Equal(2m, usage.Current().Cost);
        await Assert.ThrowsAsync<SpendCapException>(() => client.CompleteAsync(Req, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Dashboard_prices_of_zero_cannot_defeat_a_cap_set_on_the_dashboard()
    {
        var store = new MemoryKeyValueStore();
        var settings = Opts.Effective(new AiAssistantOptions { Model = "some-unlisted-model" }, store);
        var settingsStore = new SettingsStore(store);
        var usage = new UsageStore(store, TimeProvider.System);
        var spend = new SpendService(settings, usage);
        settingsStore.Update(s => { s.MonthlyCap = 1; s.InputPrice = 1; s.OutputPrice = 1; });
        var client = new MeteredClaudeClient(new UsageClaude(1_500_000, 0), spend);
        await client.CompleteAsync(Req, TestContext.Current.CancellationToken); // 1.50 recorded at price 1

        settingsStore.Update(s => { s.InputPrice = 0; s.OutputPrice = 0; });

        Assert.Equal(1.5m, spend.SpentThisMonth());
        await Assert.ThrowsAsync<SpendCapException>(() => client.CompleteAsync(Req, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_cap_in_configuration_ignores_dashboard_prices()
    {
        var store = new MemoryKeyValueStore();
        var settings = Opts.Effective(new AiAssistantOptions { MonthlySpendCap = 5, Model = "some-unlisted-model" }, store);
        new SettingsStore(store).Update(s => { s.InputPrice = 0; s.OutputPrice = 0; });
        var spend = new SpendService(settings, new UsageStore(store, TimeProvider.System));

        Assert.False(spend.PricesUsable);
        Assert.Throws<SpendCapException>(() => spend.EnsureWithinCap());
    }

    [Fact]
    public void The_month_key_ignores_the_current_culture_and_uses_utc()
    {
        CultureInfo before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");
            var late = new DateTimeOffset(2026, 1, 31, 23, 30, 0, TimeSpan.FromHours(-5)); // 2026-02-01 UTC

            Assert.Equal("2026-02", UsageStore.Month(late));
            Assert.Equal("BaryoDev.AiAssistant.Usage.2026-02", UsageStore.KeyFor(late));
        }
        finally { CultureInfo.CurrentCulture = before; }
    }
}
