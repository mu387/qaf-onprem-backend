using Microsoft.AspNetCore.Mvc;
using QafOnPrem.Api.Services.Auth;

namespace QafOnPrem.Api.Controllers;

[ApiController]
[Route("health")]
public sealed class HealthController(ISqlIdentityReadinessService readinessService) : ControllerBase
{
    private readonly ISqlIdentityReadinessService _readinessService = readinessService;

    [HttpGet]
    public IActionResult Get()
    {
        return Ok(new
        {
            ok = true,
            service = "QafOnPrem.Api",
            environment = HttpContext.RequestServices
                .GetRequiredService<IHostEnvironment>()
                .EnvironmentName,
            utc = DateTimeOffset.UtcNow
        });
    }

    [HttpGet("ready")]
    public async Task<IActionResult> Ready(CancellationToken cancellationToken)
    {
        var readiness = await _readinessService.GetStatusAsync(cancellationToken);
        var payload = new
        {
            ok = readiness.CurrentModeReady,
            sql_cutover_ready = readiness.SqlCutoverReady,
            sql_identity_enabled = readiness.SqlIdentityEnabled,
            development_fallback_enabled = readiness.DevelopmentFallbackEnabled,
            development_auth_enabled = readiness.DevelopmentAuthEnabled,
            sql_connection_configured = readiness.SqlConnectionConfigured,
            sql_connection_reachable = readiness.SqlConnectionReachable,
            required_tables_validated = readiness.RequiredTablesValidated,
            required_tables_present = readiness.RequiredTablesPresent,
            missing_tables = readiness.MissingTables,
            auth_mode = readiness.AuthMode,
            message = readiness.Message,
            environment = HttpContext.RequestServices
                .GetRequiredService<IHostEnvironment>()
                .EnvironmentName,
            utc = DateTimeOffset.UtcNow
        };

        return readiness.CurrentModeReady ? Ok(payload) : StatusCode(StatusCodes.Status503ServiceUnavailable, payload);
    }
}
