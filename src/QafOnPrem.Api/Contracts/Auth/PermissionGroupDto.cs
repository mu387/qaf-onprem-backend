using System.Text.Json.Serialization;

namespace QafOnPrem.Api.Contracts.Auth;

public sealed class PermissionGroupDto
{
    [JsonPropertyName("module")]
    public string Module { get; init; } = string.Empty;

    [JsonPropertyName("permissions")]
    public IReadOnlyList<string> Permissions { get; init; } = [];
}
