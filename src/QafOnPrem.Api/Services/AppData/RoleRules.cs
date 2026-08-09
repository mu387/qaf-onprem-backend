using QafOnPrem.Api.Contracts;

namespace QafOnPrem.Api.Services.AppData;

public enum RoleDeletionOutcome
{
    NotFound = 0,
    HasAssignedUsers = 1,
    Deleted = 2,
}

public enum SaveRoleOutcome
{
    Saved = 0,
    NotFound = 1,
    DuplicateName = 2,
    InvalidPermissions = 3,
}

public sealed class SaveRoleResult
{
    public SaveRoleOutcome Outcome { get; init; }

    public RoleDetailDto? Role { get; init; }

    public string? ErrorMessage { get; init; }
}

public static class RoleRules
{
    public static string NormalizeCreatedRoleName(string name, bool isPlatformScope)
    {
        var trimmed = name.Trim();
        if (!isPlatformScope || trimmed.StartsWith("Platform", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        return $"Platform {trimmed}";
    }

    public static RoleDeletionOutcome EvaluateDeletion(bool roleExists, bool hasAssignedUsers)
    {
        if (!roleExists)
        {
            return RoleDeletionOutcome.NotFound;
        }

        return hasAssignedUsers
            ? RoleDeletionOutcome.HasAssignedUsers
            : RoleDeletionOutcome.Deleted;
    }
}