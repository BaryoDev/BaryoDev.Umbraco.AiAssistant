using BaryoDev.Umbraco.AiAssistant.Claude;

namespace BaryoDev.Umbraco.AiAssistant.Jobs;

/// <summary>Retries 429 and 5xx. Waits for Retry-After when the API sent it, otherwise backs off from two seconds.</summary>
public sealed class RetryPolicy
{
    public int Attempts { get; init; } = 5;
    public TimeSpan Base { get; init; } = TimeSpan.FromSeconds(2);
    public TimeSpan Max { get; init; } = TimeSpan.FromSeconds(120);
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = (t, ct) => Task.Delay(t, ct);

    public async Task RunAsync(Func<Task> work, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await work();
                return;
            }
            catch (ClaudeException e) when (e.IsRetryable && attempt < Attempts)
            {
                TimeSpan wait = e.RetryAfter ?? TimeSpan.FromTicks(Base.Ticks << (attempt - 1));
                if (wait > Max) wait = Max;
                await Delay(wait, cancellationToken);
            }
        }
    }
}
