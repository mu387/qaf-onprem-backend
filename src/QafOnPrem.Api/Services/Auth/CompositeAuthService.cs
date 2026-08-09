using System.Security.Claims;
using Microsoft.Extensions.Options;
using QafOnPrem.Api.Configuration;
using QafOnPrem.Api.Contracts.Auth;

namespace QafOnPrem.Api.Services.Auth;

public sealed class CompositeAuthService(
    IOptions<SqlIdentitySettings> sqlSettings,
    ISqlIdentityService sqlIdentityService,
    IDevelopmentAuthService developmentAuthService,
    ILogger<CompositeAuthService> logger) : IAuthService
{
    private readonly SqlIdentitySettings _sqlSettings = sqlSettings.Value;
    private readonly ISqlIdentityService _sqlIdentityService = sqlIdentityService;
    private readonly IDevelopmentAuthService _developmentAuthService = developmentAuthService;
    private readonly ILogger<CompositeAuthService> _logger = logger;

    public async Task<AuthenticationResult> AuthenticateClientAsync(string email, string password, string? ipAddress, CancellationToken cancellationToken = default)
    {
        if (_sqlSettings.Enabled)
        {
            try
            {
                return await _sqlIdentityService.AuthenticateClientAsync(email, password, ipAddress, cancellationToken);
            }
            catch (Exception exception) when (_sqlSettings.AllowDevelopmentFallback && _developmentAuthService.Enabled)
            {
                _logger.LogWarning(exception, "SQL-backed authentication failed; falling back to development auth.");
                return AuthenticateWithDevelopmentFallback(email, password);
            }
        }

        if (_developmentAuthService.Enabled)
        {
            return AuthenticateWithDevelopmentFallback(email, password);
        }

        return AuthenticationResult.Failure(StatusCodes.Status501NotImplemented, "Authentication is not configured yet.");
    }

    public async Task<CurrentUserDto?> GetCurrentUserAsync(ClaimsPrincipal principal, string? bearerToken, CancellationToken cancellationToken = default)
    {
        if (_sqlSettings.Enabled)
        {
            try
            {
                return await _sqlIdentityService.GetCurrentUserAsync(principal, bearerToken, cancellationToken);
            }
            catch (Exception exception) when (_sqlSettings.AllowDevelopmentFallback && _developmentAuthService.Enabled)
            {
                _logger.LogWarning(exception, "SQL-backed current-user lookup failed; falling back to development auth.");
                return _developmentAuthService.GetCurrentUser(principal, bearerToken);
            }
        }

        return _developmentAuthService.Enabled
            ? _developmentAuthService.GetCurrentUser(principal, bearerToken)
            : null;
    }

    public async Task<IReadOnlyList<PermissionGroupDto>> GetPermissionsAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        if (_sqlSettings.Enabled)
        {
            try
            {
                return await _sqlIdentityService.GetPermissionsAsync(principal, cancellationToken);
            }
            catch (Exception exception) when (_sqlSettings.AllowDevelopmentFallback && _developmentAuthService.Enabled)
            {
                _logger.LogWarning(exception, "SQL-backed permissions lookup failed; falling back to development auth.");
                return _developmentAuthService.GetPermissions(principal);
            }
        }

        return _developmentAuthService.Enabled
            ? _developmentAuthService.GetPermissions(principal)
            : [];
    }

    private AuthenticationResult AuthenticateWithDevelopmentFallback(string email, string password)
    {
        var user = _developmentAuthService.AuthenticateClient(email, password);
        return user is null
            ? AuthenticationResult.Failure(StatusCodes.Status401Unauthorized, "Invalid Username or password!")
            : AuthenticationResult.Success("User Login Successfully", user);
    }
}
