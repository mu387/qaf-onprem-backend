namespace QafOnPrem.Api.Services.Auth;

public sealed record SqlIdentityReadinessStatus(
    bool CurrentModeReady,
    bool SqlCutoverReady,
    bool SqlIdentityEnabled,
    bool DevelopmentFallbackEnabled,
    bool DevelopmentAuthEnabled,
    bool SqlConnectionConfigured,
    bool SqlConnectionReachable,
    bool RequiredTablesValidated,
    bool RequiredTablesPresent,
    string[] MissingTables,
    string AuthMode,
    string Message);
