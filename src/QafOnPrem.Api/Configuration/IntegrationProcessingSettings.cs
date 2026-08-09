namespace QafOnPrem.Api.Configuration;

public sealed class IntegrationProcessingSettings
{
    public const string SectionName = "IntegrationProcessing";

    public bool Enabled { get; init; } = true;

    public int PollIntervalSeconds { get; init; } = 15;

    public int BatchSize { get; init; } = 10;
}
