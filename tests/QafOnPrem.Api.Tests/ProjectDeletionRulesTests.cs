using Xunit;
using QafOnPrem.Api.Services.AppData;

namespace QafOnPrem.Api.Tests;

public sealed class ProjectDeletionRulesTests
{
    [Fact]
    public void Evaluate_ReturnsNotFound_WhenProjectDoesNotExist()
    {
        var result = ProjectDeletionRules.Evaluate(projectExists: false, attachedComponentCount: 0);

        Assert.Equal(ProjectDeletionOutcome.NotFound, result);
    }

    [Fact]
    public void Evaluate_ReturnsConflict_WhenProjectHasAttachedComponents()
    {
        var result = ProjectDeletionRules.Evaluate(projectExists: true, attachedComponentCount: 1);

        Assert.Equal(ProjectDeletionOutcome.HasAttachedComponents, result);
    }

    [Fact]
    public void Evaluate_ReturnsDeleted_WhenProjectExistsWithoutAttachedComponents()
    {
        var result = ProjectDeletionRules.Evaluate(projectExists: true, attachedComponentCount: 0);

        Assert.Equal(ProjectDeletionOutcome.Deleted, result);
    }
}