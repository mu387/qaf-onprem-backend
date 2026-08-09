using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace QafOnPrem.Api.Contracts.Auth;

public sealed class LoginRequest
{
    [JsonPropertyName("email")]
    [Required]
    [EmailAddress]
    public string Email { get; init; } = string.Empty;

    [JsonPropertyName("password")]
    [Required]
    public string Password { get; init; } = string.Empty;
}
