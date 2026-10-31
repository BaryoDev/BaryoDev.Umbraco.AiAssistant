using BaryoDev.Umbraco.AiAssistant.Usage;

namespace BaryoDev.Umbraco.AiAssistant.Claude;

/// <summary>
/// Checks the monthly cap before each call and records the tokens the API reports after it,
/// including calls that were billed and then rejected (refusal, cut off, no text).
/// A call that never got an answer (network error, cancel, timeout) reports no tokens, so none are counted.
/// </summary>
public sealed class MeteredClaudeClient(IClaudeClient inner, SpendService spend) : IClaudeClient
{
    public async Task<ClaudeResponse> CompleteAsync(ClaudeRequest request, CancellationToken cancellationToken)
    {
        spend.EnsureWithinCap();
        try
        {
            ClaudeResponse response = await inner.CompleteAsync(request, cancellationToken);
            if (response.Usage is { } u) spend.Record(u.InputTokens, u.OutputTokens);
            return response;
        }
        catch (ClaudeException e) when (e.Usage is { } u)
        {
            spend.Record(u.InputTokens, u.OutputTokens);
            throw;
        }
    }
}
