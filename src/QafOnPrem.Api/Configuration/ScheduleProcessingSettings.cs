namespace QafOnPrem.Api.Configuration;

public sealed class ScheduleProcessingSettings
{
    public const string SectionName = "ScheduleProcessing";

    public bool Enabled { get; init; } = true;
}