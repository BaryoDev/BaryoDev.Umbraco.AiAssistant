using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Services;

namespace BaryoDev.Umbraco.AiAssistant.Jobs;

/// <summary>Background loop. It waits for Umbraco to be fully running, then works the running job one item at a time.</summary>
public sealed class JobRunner(IServiceProvider services, IRuntimeState runtime, ILogger<JobRunner> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            bool worked = false;
            try
            {
                if (runtime.Level == RuntimeLevel.Run)
                    worked = await services.GetRequiredService<JobProcessor>().RunNextAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogError(e, "The bulk job runner hit an error and will try again.");
            }

            if (!worked)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}
