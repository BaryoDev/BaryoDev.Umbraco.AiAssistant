using System.Net;
using System.Text;
using BaryoDev.Umbraco.AiAssistant.Claude;
using BaryoDev.Umbraco.AiAssistant.Configuration;
using BaryoDev.Umbraco.AiAssistant.Usage;
using Microsoft.Extensions.Logging.Abstractions;

namespace BaryoDev.Umbraco.AiAssistant.Tests;

public class ClientRequestTests
{
    private const string Ok = """{"content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn","usage":{"input_tokens":7,"output_tokens":3}}""";

    private static (ClaudeClient Client, List<string> Bodies, UsageStore Usage) Make(string model, string reply = Ok)
    {
        var bodies = new List<string>();
        var store = new MemoryKeyValueStore();
        var settings = Opts.Effective(new AiAssistantOptions { Key = "test-NOT-A-REAL-KEY-000", Model = model }, store);
        var usage = new UsageStore(store, TimeProvider.System);
        var handler = new StubHandler(r =>
        {
            bodies.Add(r.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        });
        return (new ClaudeClient(new StubFactory(handler), settings, NullLogger<ClaudeClient>.Instance, new SpendService(settings, usage)), bodies, usage);
    }

    [Theory]
    [InlineData("claude-sonnet-5-5", true)]
    [InlineData("claude-haiku-5-5", true)]
    [InlineData("claude-opus-4-8", true)]
    [InlineData("claude-haiku-4-5", false)]
    [InlineData("claude-sonnet-4-5", false)]
    public async Task Effort_is_sent_only_to_models_that_take_it(string model, bool expected)
    {
        var (client, bodies, _) = Make(model);

        await client.CompleteAsync(new ClaudeRequest("s", "u"), TestContext.Current.CancellationToken);
        await client.TestAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(2, bodies.Count);
        Assert.All(bodies, b => Assert.Equal(expected, b.Contains("output_config")));
    }

    [Fact]
    public async Task A_short_call_asks_for_low_effort_and_leaves_room_for_thinking()
    {
        var (client, bodies, _) = Make("claude-sonnet-5-5");

        await client.CompleteAsync(new ClaudeRequest("s", "u", null, 2000, "low"), TestContext.Current.CancellationToken);

        Assert.Contains("\"effort\":\"low\"", bodies.Single());
        Assert.Contains("\"max_tokens\":2000", bodies.Single());
    }

    [Fact]
    public async Task A_key_test_is_counted_in_usage()
    {
        var (client, _, usage) = Make("claude-sonnet-5-5");

        await client.TestAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(7, usage.Current().InputTokens);
        Assert.Equal(1, usage.Current().Calls);
    }

    [Fact]
    public async Task A_200_that_is_not_json_becomes_a_plain_error_without_the_body()
    {
        var (client, _, _) = Make("claude-sonnet-5-5", "<html>secret-body-text</html>");

        var ex = await Assert.ThrowsAsync<ClaudeException>(() => client.CompleteAsync(new ClaudeRequest("s", "u"), TestContext.Current.CancellationToken));

        Assert.DoesNotContain("secret-body-text", ex.Message);
    }
}
