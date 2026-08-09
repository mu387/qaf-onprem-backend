using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace QafOnPrem.Api.Contracts.Auth;

public sealed class ForgotPasswordRequest
{
    [Required]
    [EmailAddress]
    [JsonPropertyName("email")]
    public string Email { get; init; } = string.Empty;
}

public sealed class ResetPasswordRequest
{
    [Required]
    [EmailAddress]
    [JsonPropertyName("email")]
    public string Email { get; init; } = string.Empty;

    [Required]
    [MinLength(8)]
    [JsonPropertyName("password")]
    public string Password { get; init; } = string.Empty;

    [Required]
    [JsonPropertyName("password_confirmation")]
    public string PasswordConfirmation { get; init; } = string.Empty;

    [Required]
    [JsonPropertyName("token")]
    public string Token { get; init; } = string.Empty;
}
