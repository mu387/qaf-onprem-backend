using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QafOnPrem.Api.Configuration;

namespace QafOnPrem.Api.Services.Integrations;

public sealed class IntegrationJobProcessingService(
    IServiceScopeFactory scopeFactory,
    IOptions<IntegrationProcessingSettings> options,
    ILogger<IntegrationJobProcessingService> logger) : BackgroundService
{
    private readonly IntegrationProcessingSettings _settings = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled)
        {
            logger.LogInformation("Integration job processor is disabled by configuration.");
            return;
        }

        var pollSeconds = Math.Max(3, _settings.PollIntervalSeconds);
        var batchSize = Math.Clamp(_settings.BatchSize, 1, 100);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var processor = scope.ServiceProvider.GetRequiredService<SqlIntegrationJobProcessor>();
                var processed = await processor.ProcessPendingJobsAsync(batchSize, stoppingToken);
                if (processed > 0)
                {
                    logger.LogInformation("Integration processor handled {Count} job(s).", processed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Integration processor iteration failed.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(pollSeconds), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
