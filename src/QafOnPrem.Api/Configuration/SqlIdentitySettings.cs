namespace QafOnPrem.Api.Configuration;

public sealed class SqlIdentitySettings
{
    public const string SectionName = "SqlIdentity";

    public bool Enabled { get; init; }
    public bool AllowDevelopmentFallback { get; init; } = true;
    public bool ValidateRequiredTables { get; init; } = true;
    public bool FailStartupWhenInvalid { get; init; }
    public string UserModelType { get; init; } = "App\\Models\\User";
    public int CommandTimeoutSeconds { get; init; } = 30;
    public string[] RequiredTables { get; init; } =
    [
        "users",
        "clients",
        "permissions",
        "roles",
        "model_has_roles",
        "role_has_permissions",
        "user_settings",
        "ticketing_systems"
    ];
}
