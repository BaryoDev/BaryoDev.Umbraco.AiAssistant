using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using BaryoDev.Umbraco.AiAssistant.Settings;
using BaryoDev.Umbraco.AiAssistant.Usage;
using Microsoft.Extensions.Logging;

namespace BaryoDev.Umbraco.AiAssistant.Claude;

/// <summary>Calls the Messages API server side. The key is read per request and only goes into the x-api-key header.</summary>
public sealed class ClaudeClient(
    IHttpClientFactory factory,
    IEffectiveSettings settings,
    ILogger<ClaudeClient> logger,
    SpendService? spend = null) : IClaudeClient, IClaudeKeyTester
{
    private static readonly Regex EffortModels = new(
        @"(opus-4-[5-9]|opus-[5-9]|sonnet-4-[6-9]|sonnet-[5-9]|haiku-[5-9]|fable|mythos)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Older models (Haiku 4.5, Sonnet 4.5) reject the effort parameter, so it is sent only to models known to take it.</summary>
    internal static bool SupportsEffort(string model) => EffortModels.IsMatch(model);

    public const string HttpClientName = "BaryoDev.AiAssistant.Claude";
    public const string Endpoint = "https://api.anthropic.com/v1/messages";
    public const string ApiVersion = "2023-06-01";

    public async Task<ClaudeResponse> CompleteAsync(ClaudeRequest request, CancellationToken cancellationToken)
    {
        if (!string.Equals(settings.Options.Provider, "Claude", StringComparison.OrdinalIgnoreCase))
            throw new ClaudeException($"Provider '{settings.Options.Provider}' is not supported. Use Claude.");
        string? key = settings.Key;
        if (string.IsNullOrWhiteSpace(key)) throw new ClaudeException("The Claude key is not configured.");

        object content = request.Image is null
            ? request.User
            : new object[]
            {
                new { type = "image", source = new { type = "base64", media_type = request.Image.MediaType, data = request.Image.Base64 } },
                new { type = "text", text = request.User },
            };

        var body = new Dictionary<string, object>
        {
            ["model"] = settings.Model,
            ["max_tokens"] = request.MaxTokens ?? settings.Options.MaxOutputTokens,
            ["system"] = request.System,
            ["messages"] = new[] { new { role = "user", content } },
        };
        string? effort = request.Effort ?? settings.Options.Effort;
        if (!string.IsNullOrWhiteSpace(effort) && SupportsEffort(settings.Model))
            body["output_config"] = new { effort };

        MessagesResponse parsed = await SendAsync(key, body, cancellationToken);
        ClaudeUsage? usage = parsed.Usage is null ? null : new ClaudeUsage(parsed.Usage.InputTokens, parsed.Usage.OutputTokens);
        if (parsed.StopReason == "refusal") throw new ClaudeException("Claude declined to process this content.") { Usage = usage };
        if (parsed.StopReason == "max_tokens") throw new ClaudeException("Claude's answer was cut off. Try a shorter text.") { Usage = usage };

        string text = string.Concat(parsed.Content.Where(c => c.Type == "text").Select(c => c.Text));
        if (string.IsNullOrWhiteSpace(text)) throw new ClaudeException("Claude returned no text.") { Usage = usage };
        return new ClaudeResponse(text, usage);
    }

    public async Task<KeyTestResult> TestAsync(string? key, CancellationToken cancellationToken)
    {
        key = string.IsNullOrWhiteSpace(key) ? settings.Key : key.Trim();
        if (string.IsNullOrWhiteSpace(key)) return new KeyTestResult(false, "There is no key to test.");
        var body = new Dictionary<string, object>
        {
            ["model"] = settings.Model,
            ["max_tokens"] = 16,
            ["messages"] = new[] { new { role = "user", content = "Reply with the word ok." } },
        };
        if (SupportsEffort(settings.Model))
            body["output_config"] = new { effort = "low" };
        try
        {
            MessagesResponse reply = await SendAsync(key, body, cancellationToken);
            if (reply.Usage is { } u) spend?.Record(u.InputTokens, u.OutputTokens);
            return new KeyTestResult(true, "The key works.");
        }
        catch (ClaudeException e)
        {
            return new KeyTestResult(false, e.Message);
        }
    }

    private async Task<MessagesResponse> SendAsync(string key, object body, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = JsonContent.Create(body) };
        message.Headers.Add("x-api-key", key);
        message.Headers.Add("anthropic-version", ApiVersion);

        HttpResponseMessage response;
        try
        {
            response = await factory.CreateClient(HttpClientName).SendAsync(message, cancellationToken);
        }
        catch (HttpRequestException)
        {
            logger.LogWarning("Claude request failed before a response arrived.");
            throw new ClaudeException("Could not reach Claude.");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Claude request timed out.");
            throw new ClaudeException("Claude did not answer in time.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                int status = (int)response.StatusCode;
                logger.LogWarning("Claude request failed with status {Status}.", status);
                throw new ClaudeException(DescribeFailure(status), status, RetryAfter(response));
            }

            try
            {
                MessagesResponse? parsed = await response.Content.ReadFromJsonAsync<MessagesResponse>(cancellationToken);
                return parsed ?? throw new ClaudeException("Claude returned an empty answer.");
            }
            catch (JsonException)
            {
                logger.LogWarning("Claude returned an answer that could not be read.");
                throw new ClaudeException("Claude returned an answer that could not be read.");
            }
        }
    }

    internal static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header is null) return null;
        if (header.Delta is { } delta) return delta;
        if (header.Date is { } date) return date - DateTimeOffset.UtcNow is { } d && d > TimeSpan.Zero ? d : TimeSpan.Zero;
        return null;
    }

    private static string DescribeFailure(int status) => status switch
    {
        401 => "Claude rejected the key. Check that the whole key was copied.",
        403 => "The key is not allowed to use this model or API.",
        404 => "The model was not found. Check the model name.",
        429 => "Claude is rate limiting this key. Try again shortly.",
        >= 500 => "Claude is unavailable right now.",
        _ => $"Claude rejected the request (status {status}).",
    };

    private sealed class MessagesResponse
    {
        [JsonPropertyName("content")] public List<Block> Content { get; set; } = [];
        [JsonPropertyName("stop_reason")] public string? StopReason { get; set; }
        [JsonPropertyName("usage")] public UsageBlock? Usage { get; set; }
    }

    private sealed class Block
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        [JsonPropertyName("text")] public string? Text { get; set; }
    }

    private sealed class UsageBlock
    {
        [JsonPropertyName("input_tokens")] public int InputTokens { get; set; }
        [JsonPropertyName("output_tokens")] public int OutputTokens { get; set; }
    }
}
