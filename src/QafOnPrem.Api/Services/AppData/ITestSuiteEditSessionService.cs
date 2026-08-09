using System.Security.Claims;
using QafOnPrem.Api.Contracts;

namespace QafOnPrem.Api.Services.AppData;

public interface ITestSuiteEditSessionService
{
    TestSuiteEditSessionStatusDto AcquireOrRefresh(ClaimsPrincipal principal, long testSuiteId, TestSuiteEditSessionRequest request);

    void Release(ClaimsPrincipal principal, long testSuiteId, string sessionId);

    void EnsureCanEdit(ClaimsPrincipal principal, long testSuiteId);
}