using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using QafOnPrem.Api.Configuration;

namespace QafOnPrem.Api.Services.Auth;

public sealed class SqlIdentityReadinessService(
    IConfiguration configuration,
    IOptions<SqlIdentitySettings> sqlSettings,
    IOptions<DevelopmentAuthSettings> developmentAuthSettings) : ISqlIdentityReadinessService
{
    private readonly string _connectionString = configuration.GetConnectionString("SqlServer") ?? string.Empty;
    private readonly SqlIdentitySettings _sqlSettings = sqlSettings.Value;
    private readonly DevelopmentAuthSettings _developmentAuthSettings = developmentAuthSettings.Value;

    public async Task<SqlIdentityReadinessStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var devAuthEnabled = _developmentAuthSettings.Enabled;
        var fallbackEnabled = _sqlSettings.AllowDevelopmentFallback && devAuthEnabled;
        var sqlConfigured = !string.IsNullOrWhiteSpace(_connectionString);

        if (!_sqlSettings.Enabled)
        {
            return new SqlIdentityReadinessStatus(
                CurrentModeReady: devAuthEnabled,
                SqlCutoverReady: false,
                SqlIdentityEnabled: false,
                DevelopmentFallbackEnabled: fallbackEnabled,
                DevelopmentAuthEnabled: devAuthEnabled,
                SqlConnectionConfigured: sqlConfigured,
                SqlConnectionReachable: false,
                RequiredTablesValidated: false,
                RequiredTablesPresent: false,
                MissingTables: [],
                AuthMode: devAuthEnabled ? "development-fallback" : "disabled",
                Message: devAuthEnabled
                    ? "SQL identity is disabled. Application is currently serving auth through the development fallback profile."
                    : "SQL identity is disabled and no development fallback is enabled.");
        }

        if (!sqlConfigured)
        {
            return new SqlIdentityReadinessStatus(
                CurrentModeReady: fallbackEnabled,
                SqlCutoverReady: false,
                SqlIdentityEnabled: true,
                DevelopmentFallbackEnabled: fallbackEnabled,
                DevelopmentAuthEnabled: devAuthEnabled,
                SqlConnectionConfigured: false,
                SqlConnectionReachable: false,
                RequiredTablesValidated: false,
                RequiredTablesPresent: false,
                MissingTables: [],
                AuthMode: fallbackEnabled ? "sql-preferred-with-development-fallback" : "sql-only",
                Message: fallbackEnabled
                    ? "SQL identity is enabled but the SQL Server connection string is missing. Development fallback is currently covering auth."
                    : "SQL identity is enabled but the SQL Server connection string is missing.");
        }

        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            await command.ExecuteScalarAsync(cancellationToken);

            var missingTables = _sqlSettings.ValidateRequiredTables
                ? await GetMissingTablesAsync(connection, cancellationToken)
                : [];
            var requiredTablesPresent = missingTables.Length == 0;
            var sqlCutoverReady = !_sqlSettings.ValidateRequiredTables || requiredTablesPresent;
            var message = BuildSuccessMessage(requiredTablesPresent, missingTables);

            return new SqlIdentityReadinessStatus(
                CurrentModeReady: true,
                SqlCutoverReady: sqlCutoverReady,
                SqlIdentityEnabled: true,
                DevelopmentFallbackEnabled: fallbackEnabled,
                DevelopmentAuthEnabled: devAuthEnabled,
                SqlConnectionConfigured: true,
                SqlConnectionReachable: true,
                RequiredTablesValidated: _sqlSettings.ValidateRequiredTables,
                RequiredTablesPresent: requiredTablesPresent,
                MissingTables: missingTables,
                AuthMode: "sql",
                Message: message);
        }
        catch (Exception exception)
        {
            return new SqlIdentityReadinessStatus(
                CurrentModeReady: fallbackEnabled,
                SqlCutoverReady: false,
                SqlIdentityEnabled: true,
                DevelopmentFallbackEnabled: fallbackEnabled,
                DevelopmentAuthEnabled: devAuthEnabled,
                SqlConnectionConfigured: true,
                SqlConnectionReachable: false,
                RequiredTablesValidated: false,
                RequiredTablesPresent: false,
                MissingTables: [],
                AuthMode: fallbackEnabled ? "sql-preferred-with-development-fallback" : "sql-only",
                Message: fallbackEnabled
                    ? $"SQL identity is enabled but the configured SQL Server connection is not reachable yet. Development fallback is still active. Error: {exception.Message}"
                    : $"SQL identity is enabled but the configured SQL Server connection is not reachable. Error: {exception.Message}");
        }
    }

    private async Task<string[]> GetMissingTablesAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        if (_sqlSettings.RequiredTables.Length == 0)
        {
            return [];
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CONCAT(s.name, '.', t.name)
            FROM sys.tables AS t
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id;
            """;

        var actualTables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            actualTables.Add(reader.GetString(0));
        }

        return _sqlSettings.RequiredTables
            .Where(required => !ContainsTable(actualTables, required))
            .ToArray();
    }

    private static bool ContainsTable(IEnumerable<string> actualTables, string requiredTable)
    {
        var normalizedRequiredTable = requiredTable.Trim().ToLowerInvariant();
        var requiresSchemaMatch = normalizedRequiredTable.Contains('.');

        foreach (var actualTable in actualTables)
        {
            var normalizedActualTable = actualTable.Trim().ToLowerInvariant();
            if (requiresSchemaMatch)
            {
                if (normalizedActualTable == normalizedRequiredTable)
                {
                    return true;
                }

                continue;
            }

            var tableName = normalizedActualTable.Split('.').Last();
            if (tableName == normalizedRequiredTable)
            {
                return true;
            }
        }

        return false;
    }

    private string BuildSuccessMessage(bool requiredTablesPresent, IReadOnlyCollection<string> missingTables)
    {
        if (!_sqlSettings.ValidateRequiredTables)
        {
            return "SQL identity is enabled and the configured SQL Server connection is reachable. Required table validation is disabled.";
        }

        if (requiredTablesPresent)
        {
            return "SQL identity is enabled, the configured SQL Server connection is reachable, and all required identity tables are present.";
        }

        var missingList = string.Join(", ", missingTables);
        return $"SQL identity is enabled and the configured SQL Server connection is reachable, but required identity tables are missing: {missingList}.";
    }
}
