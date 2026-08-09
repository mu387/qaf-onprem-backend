using System.Security.Claims;
using QafOnPrem.Api.Contracts.Auth;

namespace QafOnPrem.Api.Services.Auth;

public interface IDevelopmentAuthService
{
    bool Enabled { get; }
    CurrentUserDto? AuthenticateClient(string email, string password);
    CurrentUserDto? GetCurrentUser(ClaimsPrincipal principal, string? bearerToken);
    IReadOnlyList<PermissionGroupDto> GetPermissions(ClaimsPrincipal principal);
}
