using BaryoDev.Umbraco.AiAssistant.Settings;

namespace BaryoDev.Umbraco.AiAssistant.Usage;

/// <summary>Turns token counts into an estimated cost and enforces the monthly cap. Every cost here is an estimate.</summary>
public sealed class SpendService(IEffectiveSettings settings, UsageStore usage)
{
    public decimal? Cost(long inputTokens, long outputTokens)
    {
        if (settings.InputPrice is not { } i || settings.OutputPrice is not { } o) return null;
        return (inputTokens * i + outputTokens * o) / 1_000_000m;
    }

    /// <summary>Counts a call at the prices in force now and adds it to the month.</summary>
    public void Record(int inputTokens, int outputTokens) => usage.Record(inputTokens, outputTokens, Cost(inputTokens, outputTokens));

    public decimal SpentThisMonth() => usage.Current().Cost;

    public bool PricesUsable => settings.InputPrice is > 0 && settings.OutputPrice is > 0;

    /// <summary>Throws before a call when the cap is set and already reached, or when it is set but cannot be measured.</summary>
    public void EnsureWithinCap()
    {
        if (settings.MonthlyCap is not { } cap) return;
        if (!PricesUsable)
            throw new SpendCapException("A monthly spend cap is set, but there is no price per million tokens above zero to measure it. Enter the prices in Settings.");
        decimal spent = SpentThisMonth();
        if (spent >= cap)
            throw new SpendCapException($"The monthly spend cap of {cap:0.00} USD is reached ({spent:0.00} USD estimated this month). Raise the cap in Settings or wait for next month.");
    }

    /// <summary>True when a planned run fits under the cap, or no cap is set.</summary>
    public bool Fits(decimal estimatedCost, out string? reason)
    {
        reason = null;
        if (settings.MonthlyCap is not { } cap) return true;
        if (!PricesUsable)
        {
            reason = "A monthly spend cap is set, but there is no price per million tokens above zero to measure it. Enter the prices in Settings.";
            return false;
        }
        decimal spent = SpentThisMonth();
        if (spent + estimatedCost <= cap) return true;
        reason = $"This run is estimated at {estimatedCost:0.00} USD. With {spent:0.00} USD already used this month it would go over the cap of {cap:0.00} USD.";
        return false;
    }
}

public sealed class SpendCapException(string message) : Claude.ClaudeException(message);
