namespace QafOnPrem.Api.Configuration;

public sealed class JwtSettings
{
    public const string SectionName = "Jwt";

    public string Issuer { get; init; } = "QafOnPrem.Api";
    public string Audience { get; init; } = "QAF-OnPrem.Frontend";
    public string SigningKey { get; init; } = "development-signing-key-change-before-shared-use-12345";
    public int ExpiryMinutes { get; init; } = 7200;
}
