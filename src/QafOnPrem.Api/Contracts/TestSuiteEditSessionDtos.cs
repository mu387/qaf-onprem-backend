using System.Text.Json.Serialization;

namespace QafOnPrem.Api.Contracts;

public sealed class TestSuiteEditSessionRequest
{
    [JsonPropertyName("session_id")]
    public string? SessionId { get; init; }

    [JsonPropertyName("request_edit")]
    public bool? RequestEdit { get; init; }
}

public sealed class TestSuiteEditSessionStatusDto
{
    [JsonPropertyName("test_suite_id")]
    public long TestSuiteId { get; init; }

    [JsonPropertyName("can_edit")]
    public bool CanEdit { get; init; }

    [JsonPropertyName("is_editor")]
    public bool IsEditor { get; init; }

    [JsonPropertyName("editor")]
    public UserBasicDto? Editor { get; init; }

    [JsonPropertyName("viewers")]
    public IReadOnlyList<UserBasicDto> Viewers { get; init; } = [];

    [JsonPropertyName("viewer_count")]
    public int ViewerCount { get; init; }

    [JsonPropertyName("active_user_count")]
    public int ActiveUserCount { get; init; }
}