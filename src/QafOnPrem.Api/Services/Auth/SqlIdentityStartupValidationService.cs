using Microsoft.Extensions.Options;
using QafOnPrem.Api.Configuration;

namespace QafOnPrem.Api.Services.Auth;

public sealed class SqlIdentityStartupValidationService(
    IServiceProvider serviceProvider,
    IOptions<SqlIdentitySettings> sqlSettings,
    ILogger<SqlIdentityStartupValidationService> logger) : IHostedService
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly SqlIdentitySettings _sqlSettings = sqlSettings.Value;
    private readonly ILogger<SqlIdentityStartupValidationService> _logger = logger;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_sqlSettings.Enabled)
        {
            _logger.LogInformation("SQL identity startup validation skipped because SQL identity is disabled.");
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var readinessService = scope.ServiceProvider.GetRequiredService<ISqlIdentityReadinessService>();
        var status = await readinessService.GetStatusAsync(cancellationToken);

        if (status.SqlCutoverReady)
        {
            _logger.LogInformation("SQL identity startup validation passed.");
            return;
        }

        if (_sqlSettings.FailStartupWhenInvalid)
        {
            throw new InvalidOperationException($"SQL identity startup validation failed: {status.Message}");
        }

        _logger.LogWarning("SQL identity startup validation did not pass. {Message}", status.Message);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}