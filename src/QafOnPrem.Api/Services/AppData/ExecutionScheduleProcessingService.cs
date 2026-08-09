using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using QafOnPrem.Api.Configuration;

namespace QafOnPrem.Api.Services.AppData;

public sealed class ExecutionScheduleProcessingService(
    IServiceScopeFactory scopeFactory,
    IOptions<ScheduleProcessingSettings> scheduleProcessingOptions,
    ILogger<ExecutionScheduleProcessingService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private readonly ScheduleProcessingSettings _scheduleProcessingSettings = scheduleProcessingOptions.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_scheduleProcessingSettings.Enabled)
        {
            logger.LogInformation("Execution schedule processor is disabled by configuration.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var appDataService = scope.ServiceProvider.GetRequiredService<ISqlAppDataService>();
                await appDataService.ProcessDueExecutionSchedulesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Execution schedule processor iteration failed.");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}