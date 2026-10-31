using System.Net;
using System.Text;
using BaryoDev.Umbraco.AiAssistant.Claude;
using BaryoDev.Umbraco.AiAssistant.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BaryoDev.Umbraco.AiAssistant.Tests;

public class KeyHandlingTests
{
    private const string FakeKey = "test-key-NOT-A-REAL-SECRET-123";

    private static (ClaudeClient Client, ListLogger<ClaudeClient> Log, StubHandler Handler) Make(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new StubHandler(respond);
        var log = new ListLogger<ClaudeClient>();
        var client = new ClaudeClient(new StubFactory(handler), Opts.Effective(new AiAssistantOptions { Key = FakeKey }), log);
        return (client, log, handler);
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task The_key_goes_only_in_the_header_and_the_request_has_the_current_shape()
    {
        var (client, _, handler) = Make(_ => Json(HttpStatusCode.OK, """{"content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn"}"""));

        ClaudeResponse r = await client.CompleteAsync(new ClaudeRequest("sys", "hello"), CancellationToken.None);

        Assert.Equal("ok", r.Text);
        HttpRequestMessage sent = handler.Seen.Single();
        Assert.Equal("https://api.anthropic.com/v1/messages", sent.RequestUri!.ToString());
        Assert.Equal(FakeKey, sent.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", sent.Headers.GetValues("anthropic-version").Single());
        Assert.DoesNotContain(FakeKey, sent.RequestUri.ToString());
    }

    [Theory]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(400)]
    public async Task A_failed_call_never_puts_the_key_or_the_body_in_the_exception_or_the_log(int status)
    {
        var (client, log, _) = Make(_ => Json((HttpStatusCode)status, "{\"error\":{\"message\":\"invalid x-api-key " + FakeKey + "\"}}"));

        ClaudeException e = await Assert.ThrowsAsync<ClaudeException>(() => client.CompleteAsync(new ClaudeRequest("s", "u"), CancellationToken.None));

        Assert.Equal(status, e.StatusCode);
        Assert.DoesNotContain(FakeKey, e.ToString());
        Assert.DoesNotContain(FakeKey, string.Join('\n', log.Lines));
        Assert.NotEmpty(log.Lines);
    }

    [Fact]
    public async Task A_network_failure_never_puts_the_key_in_the_exception_or_the_log()
    {
        var (client, log, _) = Make(_ => throw new HttpRequestException($"connection failed for header x-api-key: {FakeKey}"));

        ClaudeException e = await Assert.ThrowsAsync<ClaudeException>(() => client.CompleteAsync(new ClaudeRequest("s", "u"), CancellationToken.None));

        Assert.DoesNotContain(FakeKey, e.ToString());
        Assert.DoesNotContain(FakeKey, string.Join('\n', log.Lines));
    }

    [Fact]
    public async Task A_missing_key_fails_before_any_request_is_sent()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, "{}"));
        var client = new ClaudeClient(new StubFactory(handler), Opts.Effective(new AiAssistantOptions()), new ListLogger<ClaudeClient>());

        await Assert.ThrowsAsync<ClaudeException>(() => client.CompleteAsync(new ClaudeRequest("s", "u"), CancellationToken.None));
        Assert.Empty(handler.Seen);
    }

    [Fact]
    public void The_default_options_hold_no_key_and_the_default_model()
    {
        var o = new AiAssistantOptions();
        Assert.Equal("", o.Key);
        Assert.Equal("", o.Model);
        Assert.Equal("claude-sonnet-5-5", Opts.Effective(o).Model);
        Assert.False(o.IsConfigured);
    }

    [Fact]
    public void The_status_model_has_no_key_field()
    {
        Assert.DoesNotContain(typeof(BaryoDev.Umbraco.AiAssistant.Api.AiAssistantStatusModel).GetProperties(), p => p.Name.Contains("Key", StringComparison.OrdinalIgnoreCase));
    }
}

public class KeyNeverLoggedTests
{
    private const string FakeKey = "test-key-NOT-A-REAL-SECRET-456";

    private sealed class Capture : ILoggerProvider
    {
        public List<string> Lines { get; } = [];
        public ILogger CreateLogger(string categoryName) => new L(this);
        public void Dispose() { }
        private sealed class L(Capture c) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var sb = new StringBuilder(formatter(state, exception));
                if (state is IEnumerable<KeyValuePair<string, object?>> kv)
                    foreach (var p in kv) sb.Append(' ').Append(p.Key).Append('=').Append(p.Value);
                lock (c.Lines) c.Lines.Add(sb.ToString());
            }
        }
    }

    [Fact]
    public async Task A_real_client_factory_does_not_log_the_key_at_trace_level()
    {
        var capture = new Capture();
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(capture));
        AiAssistantComposer.AddClaudeHttpClient(services)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"content":[{"type":"text","text":"ok"}],"usage":{"input_tokens":1,"output_tokens":1}}""", Encoding.UTF8, "application/json")
            }));
        using var sp = services.BuildServiceProvider();
        var client = new ClaudeClient(
            sp.GetRequiredService<IHttpClientFactory>(),
            Opts.Effective(new AiAssistantOptions { Key = FakeKey }),
            sp.GetRequiredService<ILogger<ClaudeClient>>());

        await client.CompleteAsync(new ClaudeRequest("s", "u"), CancellationToken.None);
        await client.TestAsync(FakeKey, CancellationToken.None);

        Assert.DoesNotContain(capture.Lines, l => l.Contains(FakeKey));
    }

    [Fact]
    public void The_key_request_models_do_not_print_the_key()
    {
        Assert.DoesNotContain(FakeKey, new BaryoDev.Umbraco.AiAssistant.Api.SaveKeyRequest(FakeKey).ToString());
        Assert.DoesNotContain(FakeKey, new BaryoDev.Umbraco.AiAssistant.Api.TestKeyRequest(FakeKey).ToString());
    }
}
