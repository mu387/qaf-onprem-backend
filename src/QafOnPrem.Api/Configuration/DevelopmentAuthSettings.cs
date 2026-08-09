namespace QafOnPrem.Api.Configuration;

public sealed class DevelopmentAuthSettings
{
    public const string SectionName = "DevelopmentAuth";

    public bool Enabled { get; init; }
    public List<DevelopmentAuthUser> Users { get; init; } = [];
}

public sealed class DevelopmentAuthUser
{
    public int Id { get; init; }
    public string Email { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Phone { get; init; }
    public string? JobTitle { get; init; }
    public string? Department { get; init; }
    public string? Timezone { get; init; }
    public string? AvatarUrl { get; init; }
    public int? ClientId { get; init; }
    public int IsClient { get; init; }
    public bool IsActive { get; init; }
    public string ClientStatus { get; init; } = "active";
    public bool MustResetPassword { get; init; }
    public string Role { get; init; } = string.Empty;
    public List<DevelopmentPermissionGroup> Permissions { get; init; } = [];
}

public sealed class DevelopmentPermissionGroup
{
    public string Module { get; init; } = string.Empty;
    public List<string> Permissions { get; init; } = [];
}
