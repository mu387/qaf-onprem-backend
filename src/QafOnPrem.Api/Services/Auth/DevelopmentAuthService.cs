using System.Security.Claims;
using Microsoft.Extensions.Options;
using QafOnPrem.Api.Configuration;
using QafOnPrem.Api.Contracts.Auth;

namespace QafOnPrem.Api.Services.Auth;

public sealed class DevelopmentAuthService(
    IOptions<DevelopmentAuthSettings> settings,
    ITokenFactory tokenFactory) : IDevelopmentAuthService
{
    private readonly DevelopmentAuthSettings _settings = settings.Value;
    private readonly ITokenFactory _tokenFactory = tokenFactory;

    public bool Enabled => _settings.Enabled;

    public CurrentUserDto? AuthenticateClient(string email, string password)
    {
        var user = _settings.Users.FirstOrDefault(candidate =>
            string.Equals(candidate.Email, email, StringComparison.OrdinalIgnoreCase) &&
            candidate.Password == password);

        if (user is null || user.ClientId is null)
        {
            return null;
        }

        var token = _tokenFactory.CreateToken(new TokenSubject(
            user.Id,
            user.Email,
            user.Name,
            user.ClientId,
            user.IsClient,
            user.ClientStatus,
            user.Role));
        return BuildCurrentUser(user, token);
    }

    public CurrentUserDto? GetCurrentUser(ClaimsPrincipal principal, string? bearerToken)
    {
        var subject = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue(ClaimTypes.Name)
            ?? principal.FindFirstValue(ClaimTypes.Email)
            ?? principal.FindFirstValue("sub");

        if (string.IsNullOrWhiteSpace(subject))
        {
            return null;
        }

        var user = _settings.Users.FirstOrDefault(candidate =>
            string.Equals(candidate.Id.ToString(), subject, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(candidate.Email, subject, StringComparison.OrdinalIgnoreCase));

        if (user is null)
        {
            return null;
        }

        return BuildCurrentUser(user, bearerToken ?? string.Empty);
    }

    public IReadOnlyList<PermissionGroupDto> GetPermissions(ClaimsPrincipal principal)
    {
        return GetCurrentUser(principal, string.Empty)?.UserPermissions ?? [];
    }

    private static CurrentUserDto BuildCurrentUser(DevelopmentAuthUser user, string token)
    {
        var now = DateTimeOffset.UtcNow;
        return new CurrentUserDto
        {
            Id = user.Id,
            Name = user.Name,
            Email = user.Email,
            Phone = user.Phone,
            JobTitle = user.JobTitle,
            Department = user.Department,
            Timezone = user.Timezone,
            AvatarUrl = user.AvatarUrl,
            ClientId = user.ClientId,
            IsClient = user.IsClient,
            IsActive = user.IsActive,
            ClientStatus = user.ClientStatus,
            ClientMaxUsers = null,
            MfaEnabled = false,
            SsoEnabled = false,
            MustResetPassword = user.MustResetPassword,
            EmailVerifiedAt = now,
            DeletedAt = null,
            CreatedAt = now,
            UpdatedAt = now,
            Token = token,
            TicketingSystem = false,
            UserPermissions = user.Permissions.Select(group => new PermissionGroupDto
            {
                Module = group.Module,
                Permissions = group.Permissions
            }).ToList(),
            Settings = new { },
            Role = user.Role
        };
    }
}
