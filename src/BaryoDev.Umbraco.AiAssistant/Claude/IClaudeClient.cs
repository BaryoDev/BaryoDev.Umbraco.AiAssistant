namespace BaryoDev.Umbraco.AiAssistant.Claude;

public interface IClaudeClient
{
    Task<ClaudeResponse> CompleteAsync(ClaudeRequest request, CancellationToken cancellationToken);
}

/// <summary>Checks a key with one tiny call. Used by the dashboard Test button.</summary>
public interface IClaudeKeyTester
{
    Task<KeyTestResult> TestAsync(string? key, CancellationToken cancellationToken);
}

public sealed record KeyTestResult(bool Ok, string Message);

public sealed record ClaudeImage(string MediaType, string Base64);

/// <summary>User is the data block text. The image, when set, is sent before it.</summary>
public sealed record ClaudeRequest(string System, string User, ClaudeImage? Image = null, int? MaxTokens = null, string? Effort = null);

public sealed record ClaudeUsage(int InputTokens, int OutputTokens);

public sealed record ClaudeResponse(string Text, ClaudeUsage? Usage = null);

/// <summary>Message never contains the key or the raw response body.</summary>
public class ClaudeException(string message, int? statusCode = null, TimeSpan? retryAfter = null) : Exception(message)
{
    public int? StatusCode { get; } = statusCode;

    /// <summary>From the retry-after header when the API sent one.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;

    /// <summary>Tokens the API reported for a call that was billed but then rejected here (refusal, cut off, no text).</summary>
    public ClaudeUsage? Usage { get; init; }

    public bool IsRetryable => StatusCode is 429 or >= 500;
}
