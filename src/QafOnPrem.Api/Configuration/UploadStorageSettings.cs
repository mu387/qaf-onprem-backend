namespace QafOnPrem.Api.Configuration;

public sealed class UploadStorageSettings
{
    public const string SectionName = "Uploads";

    public string? RootPath { get; init; }
}