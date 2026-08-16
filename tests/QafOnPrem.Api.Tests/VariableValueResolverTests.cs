using QafOnPrem.Api.Services;
using Xunit;

namespace QafOnPrem.Api.Tests;

public sealed class VariableValueResolverTests
{
    [Fact]
    public void Resolve_ExcelFormula_SupportsWeekdayBasedDateMath()
    {
        var resolved = VariableValueResolver.Resolve(
            @"=TEXT(TODAY()-WEEKDAY(TODAY(),3)-15,""mm/dd/yyyy"")",
            "excel",
            isEncrypted: false);

        Assert.Matches(@"^\d{2}/\d{2}/\d{4}$", resolved);
    }

    [Fact]
    public void Resolve_ExcelFormula_SupportsWeekdayWithTwoArguments()
    {
        var resolved = VariableValueResolver.Resolve(
            "=WEEKDAY(TODAY(),3)",
            "excel",
            isEncrypted: false);

        Assert.True(int.TryParse(resolved, out var weekday));
        Assert.InRange(weekday, 0, 6);
    }

    [Fact]
    public void Resolve_ExcelFormula_PreservesExistingTextFormattingBehavior()
    {
        var resolved = VariableValueResolver.Resolve(
            @"=TEXT(1234.5,""0.00"")",
            "excel",
            isEncrypted: false);

        Assert.Equal("1234.50", resolved);
    }

    [Fact]
    public void Resolve_ExcelFormula_FallsBackToRawValueWhenFormulaCannotBeResolved()
    {
        const string formula = "=UNSUPPORTED_FN(1)";

        var resolved = VariableValueResolver.Resolve(
            formula,
            "excel",
            isEncrypted: false);

        Assert.Equal(formula, resolved);
    }
}
