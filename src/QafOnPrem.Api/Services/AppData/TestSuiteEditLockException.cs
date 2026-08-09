namespace QafOnPrem.Api.Services.AppData;

public sealed class TestSuiteEditLockException(string message) : InvalidOperationException(message)
{
}