using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using QafOnPrem.Api.Configuration;

namespace QafOnPrem.Api.Services.Auth;

public sealed class JwtTokenFactory(JwtSettings settings) : ITokenFactory
{
    private readonly JwtSettings _settings = settings;
    private readonly SymmetricSecurityKey _key = new(Encoding.UTF8.GetBytes(settings.SigningKey));

    public string CreateToken(TokenSubject user)
    {
        var credentials = new SigningCredentials(_key, SecurityAlgorithms.HmacSha256);
        var now = DateTimeOffset.UtcNow;

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(ClaimTypes.Name, user.Name),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new("client_id", user.ClientId?.ToString() ?? string.Empty),
            new("is_client", user.IsClient.ToString()),
            new("client_status", user.ClientStatus),
            new(ClaimTypes.Role, user.Role)
        };

        var token = new JwtSecurityToken(
            issuer: _settings.Issuer,
            audience: _settings.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: now.AddMinutes(_settings.ExpiryMinutes).UtcDateTime,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
