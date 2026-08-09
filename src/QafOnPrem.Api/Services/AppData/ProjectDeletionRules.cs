namespace QafOnPrem.Api.Services.AppData;

public enum ProjectDeletionOutcome
{
    NotFound = 0,
    HasAttachedComponents = 1,
    Deleted = 2,
}

public static class ProjectDeletionRules
{
    public static ProjectDeletionOutcome Evaluate(bool projectExists, int attachedComponentCount)
    {
        if (!projectExists)
        {
            return ProjectDeletionOutcome.NotFound;
        }

        return attachedComponentCount > 0
            ? ProjectDeletionOutcome.HasAttachedComponents
            : ProjectDeletionOutcome.Deleted;
    }
}