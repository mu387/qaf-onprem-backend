using System.Security.Claims;
using QafOnPrem.Api.Contracts.Auth;

namespace QafOnPrem.Api.Services.Auth;

public interface IAuthService
{
    Task<AuthenticationResult> AuthenticateClientAsync(string email, string password, string? ipAddress, CancellationToken cancellationToken = default);
    Task<CurrentUserDto?> GetCurrentUserAsync(ClaimsPrincipal principal, string? bearerToken, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PermissionGroupDto>> GetPermissionsAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default);
}
