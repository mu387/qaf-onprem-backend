namespace QafOnPrem.Api.Services.Auth;

public interface ISqlIdentityReadinessService
{
    Task<SqlIdentityReadinessStatus> GetStatusAsync(CancellationToken cancellationToken = default);
}
