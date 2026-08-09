using System.Text.Json.Serialization;

namespace QafOnPrem.Api.Contracts.Auth;

public sealed class CurrentUserDto
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("email")]
    public string Email { get; init; } = string.Empty;

    [JsonPropertyName("phone")]
    public string? Phone { get; init; }

    [JsonPropertyName("job_title")]
    public string? JobTitle { get; init; }

    [JsonPropertyName("department")]
    public string? Department { get; init; }

    [JsonPropertyName("timezone")]
    public string? Timezone { get; init; }

    [JsonPropertyName("avatar_url")]
    public string? AvatarUrl { get; init; }

    [JsonPropertyName("client_id")]
    public int? ClientId { get; init; }

    [JsonPropertyName("is_client")]
    public int IsClient { get; init; }

    [JsonPropertyName("is_active")]
    public bool IsActive { get; init; }

    [JsonPropertyName("client_status")]
    public string? ClientStatus { get; init; }

    [JsonPropertyName("client_max_users")]
    public int? ClientMaxUsers { get; init; }

    [JsonPropertyName("mfa_enabled")]
    public bool MfaEnabled { get; init; }

    [JsonPropertyName("sso_enabled")]
    public bool SsoEnabled { get; init; }

    [JsonPropertyName("must_reset_password")]
    public bool MustResetPassword { get; init; }

    [JsonPropertyName("email_verified_at")]
    public DateTimeOffset? EmailVerifiedAt { get; init; }

    [JsonPropertyName("deleted_at")]
    public DateTimeOffset? DeletedAt { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset? UpdatedAt { get; init; }

    [JsonPropertyName("token")]
    public string Token { get; init; } = string.Empty;

    [JsonPropertyName("ticketing_system")]
    public bool TicketingSystem { get; init; }

    [JsonPropertyName("user_permissions")]
    public IReadOnlyList<PermissionGroupDto> UserPermissions { get; init; } = [];

    [JsonPropertyName("settings")]
    public object? Settings { get; init; }

    [JsonPropertyName("role")]
    public string? Role { get; init; }
}

