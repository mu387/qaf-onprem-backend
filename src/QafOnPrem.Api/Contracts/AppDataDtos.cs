using System.Text.Json;
using System.Text.Json.Serialization;
using QafOnPrem.Api.Contracts.Auth;

namespace QafOnPrem.Api.Contracts;

public sealed class PaginationMetaDto
{
    [JsonPropertyName("total")]
    public int Total { get; init; }

    [JsonPropertyName("count")]
    public int Count { get; init; }

    [JsonPropertyName("per_page")]
    public int PerPage { get; init; }
}

public sealed class PagedDataDto<T>
{
    [JsonPropertyName("data")]
    public IReadOnlyList<T> Data { get; init; } = [];

    [JsonPropertyName("meta")]
    public PaginationMetaDto Meta { get; init; } = new();
}

public sealed class RoleListItemDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;
}

public sealed class RoleDetailDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("guard_name")]
    public string GuardName { get; init; } = string.Empty;

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset? UpdatedAt { get; init; }

    [JsonPropertyName("permissions")]
    public IReadOnlyList<PermissionGroupDto> Permissions { get; init; } = [];
}

public sealed class UserRoleDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;
}

public sealed class UserListItemDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("email")]
    public string Email { get; init; } = string.Empty;

    [JsonPropertyName("roles")]
    public IReadOnlyList<UserRoleDto> Roles { get; init; } = [];

    [JsonPropertyName("is_active")]
    public bool IsActive { get; init; }
}

public sealed class UserDetailDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("email")]
    public string Email { get; init; } = string.Empty;

    [JsonPropertyName("phone")]
    public string? Phone { get; init; }

    [JsonPropertyName("job_title")]
    public string? JobTitle { get; init; }

    [JsonPropertyName("department")]
    public string? Department { get; init; }

    [JsonPropertyName("timezone")]
    public string? Timezone { get; init; }

    [JsonPropertyName("roles")]
    public IReadOnlyList<UserRoleDto> Roles { get; init; } = [];

    [JsonPropertyName("is_active")]
    public bool IsActive { get; init; }
}

public sealed class AssignableUserDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("email")]
    public string Email { get; init; } = string.Empty;
}

public sealed class ProjectListItemDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("project_name")]
    public string ProjectName { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("area_path")]
    public string? AreaPath { get; init; }

    [JsonPropertyName("primary_test_management")]
    public string? PrimaryTestManagement { get; init; }

    [JsonPropertyName("primary_ticketing_system")]
    public string? PrimaryTicketingSystem { get; init; }

    [JsonPropertyName("type_id")]
    public long? TypeId { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("status")]
    public bool Status { get; init; }
}

public sealed class ProjectDetailDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("project_name")]
    public string ProjectName { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("area_path")]
    public string? AreaPath { get; init; }

    [JsonPropertyName("primary_test_management")]
    public string? PrimaryTestManagement { get; init; }

    [JsonPropertyName("primary_ticketing_system")]
    public string? PrimaryTicketingSystem { get; init; }

    [JsonPropertyName("type_id")]
    public long? TypeId { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    [JsonPropertyName("status")]
    public bool Status { get; init; }
}

public sealed class ComponentProjectDto
{
    [JsonPropertyName("project_name")]
    public string? ProjectName { get; init; }
}

public sealed class ComponentTypeDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public sealed class ComponentListItemDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("feature")]
    public string? Feature { get; init; }

    [JsonPropertyName("page")]
    public string? Page { get; init; }

    [JsonPropertyName("project")]
    public ComponentProjectDto Project { get; init; } = new();

    [JsonPropertyName("type")]
    public ComponentTypeDto Type { get; init; } = new();

    [JsonPropertyName("status")]
    public bool Status { get; init; }
}

public sealed class ComponentStepDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("expected_output")]
    public string? ExpectedOutput { get; init; }

    [JsonPropertyName("keyword_id")]
    public long? KeywordId { get; init; }

    [JsonPropertyName("global_keyword_id")]
    public long? GlobalKeywordId { get; init; }

    [JsonPropertyName("keyword")]
    public BasicRefDto? Keyword { get; init; }

    [JsonPropertyName("keyword_combination_names")]
    public string? KeywordCombinationNames { get; init; }

    [JsonPropertyName("global_keyword")]
    public BasicRefDto? GlobalKeyword { get; init; }

    [JsonPropertyName("brpg_obj")]
    public string? BrpgObj { get; init; }

    [JsonPropertyName("object_string")]
    public string? ObjectString { get; init; }

    [JsonPropertyName("xpath")]
    public string? XPath { get; init; }

    [JsonPropertyName("before_step")]
    public IReadOnlyList<string> BeforeStep { get; init; } = [];

    [JsonPropertyName("after_step")]
    public IReadOnlyList<string> AfterStep { get; init; } = [];

    [JsonPropertyName("display_id")]
    public int? DisplayId { get; init; }
}

public sealed class ComponentDetailDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("project_id")]
    public long? ProjectId { get; init; }

    [JsonPropertyName("page")]
    public string? Page { get; init; }

    [JsonPropertyName("feature")]
    public string? Feature { get; init; }

    [JsonPropertyName("type_id")]
    public long? TypeId { get; init; }

    [JsonPropertyName("steps")]
    public IReadOnlyList<ComponentStepDto> Steps { get; init; } = [];
}

public sealed class KeywordOptionDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; init; } = string.Empty;
}

public sealed class BeforeAfterStepDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("field")]
    public bool Field { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("rules")]
    public JsonElement? Rules { get; init; }

    [JsonPropertyName("usage_count")]
    public int UsageCount { get; init; }
}

public sealed class VariableTypeDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("method")]
    public string? Method { get; init; }

    [JsonPropertyName("executable_method")]
    public string? ExecutableMethod { get; init; }

    [JsonPropertyName("value")]
    public long? Value { get; init; }

    [JsonPropertyName("params")]
    public string? Params { get; init; }

    [JsonPropertyName("is_encrypted")]
    public bool IsEncrypted { get; init; }
}

public sealed class CustomVariableDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("value")]
    public string? Value { get; init; }

    [JsonPropertyName("resolved_value")]
    public string? ResolvedValue { get; init; }

    [JsonPropertyName("variable_id")]
    public long VariableId { get; init; }

    [JsonPropertyName("test_case_id")]
    public long? TestCaseId { get; init; }

    [JsonPropertyName("is_encrypted")]
    public bool IsEncrypted { get; init; }

    [JsonPropertyName("variable")]
    public VariableTypeDto? Variable { get; init; }
}

public sealed class ConfigurationVariableValueDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public sealed class ConfigurationVariableDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("variable_values")]
    public IReadOnlyList<ConfigurationVariableValueDto> VariableValues { get; init; } = [];
}

public sealed class BasicRefDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public sealed class ConfigurationSelectedVariableDto
{
    [JsonPropertyName("variable")]
    public BasicRefDto? Variable { get; init; }

    [JsonPropertyName("value")]
    public BasicRefDto? Value { get; init; }
}

public sealed class ConfigurationDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("status")]
    public int? Status { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; init; }

    [JsonPropertyName("configuration_variables")]
    public IReadOnlyList<ConfigurationSelectedVariableDto> ConfigurationVariables { get; init; } = [];
}

public sealed class ExecutionDevicePoolDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }
}

public sealed class ExecutionDeviceDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("pool_id")]
    public long? PoolId { get; init; }

    [JsonPropertyName("pool")]
    public BasicRefDto? Pool { get; init; }

    [JsonPropertyName("host")]
    public string? Host { get; init; }

    [JsonPropertyName("api_key")]
    public string? ApiKey { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("health_status")]
    public string? HealthStatus { get; init; }

    [JsonPropertyName("runner_version")]
    public string? RunnerVersion { get; init; }

    [JsonPropertyName("max_concurrency")]
    public int? MaxConcurrency { get; init; }

    [JsonPropertyName("last_seen_at")]
    public DateTimeOffset? LastSeenAt { get; init; }

    [JsonPropertyName("last_health_payload")]
    public JsonElement? LastHealthPayload { get; init; }
}

public sealed class ExecutionScheduleDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("cron")]
    public string? Cron { get; init; }

    [JsonPropertyName("timezone")]
    public string? Timezone { get; init; }

    [JsonPropertyName("run_mode")]
    public string? RunMode { get; init; }

    [JsonPropertyName("priority")]
    public string? Priority { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("run_once")]
    public bool? RunOnce { get; init; }

    [JsonPropertyName("last_run_at")]
    public DateTimeOffset? LastRunAt { get; init; }

    [JsonPropertyName("next_run_at")]
    public DateTimeOffset? NextRunAt { get; init; }

    [JsonPropertyName("pool")]
    public BasicRefDto? Pool { get; init; }

    [JsonPropertyName("payload_json")]
    public JsonElement PayloadJson { get; init; }

    [JsonPropertyName("items_count")]
    public int ItemsCount { get; init; }

    [JsonPropertyName("has_items")]
    public bool HasItems { get; init; }
}

public sealed class ExecutionQueueItemDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("last_status")]
    public string? LastStatus { get; init; }

    [JsonPropertyName("last_run_at")]
    public DateTimeOffset? LastRunAt { get; init; }

    [JsonPropertyName("queue_run_id")]
    public long? QueueRunId { get; init; }

    [JsonPropertyName("attempts")]
    public int? Attempts { get; init; }

    [JsonPropertyName("test_suite_id")]
    public long? TestSuiteId { get; init; }

    [JsonPropertyName("execution_id")]
    public long? ExecutionId { get; init; }

    [JsonPropertyName("base_test_suite_id")]
    public long? BaseTestSuiteId { get; init; }

    [JsonPropertyName("test_suite_name")]
    public string? TestSuiteName { get; init; }

    [JsonPropertyName("test_plan_id")]
    public long? TestPlanId { get; init; }

    [JsonPropertyName("test_plan_item_id")]
    public long? TestPlanItemId { get; init; }
}

public sealed class ExecutionQueueDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("queue_code")]
    public string? QueueCode { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("priority")]
    public string? Priority { get; init; }

    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("run_mode")]
    public string? RunMode { get; init; }

    [JsonPropertyName("run_target")]
    public string? RunTarget { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; init; }

    [JsonPropertyName("pool")]
    public BasicRefDto? Pool { get; init; }

    [JsonPropertyName("schedule")]
    public BasicRefDto? Schedule { get; init; }

    [JsonPropertyName("items")]
    public IReadOnlyList<ExecutionQueueItemDto> Items { get; init; } = [];
}

public enum ExecutionMutationOutcome
{
    Success,
    NotFound,
    Forbidden,
    Conflict,
    ValidationFailed
}

public sealed class ExecutionMutationResult<T>
{
    [JsonPropertyName("outcome")]
    public ExecutionMutationOutcome Outcome { get; init; }

    [JsonPropertyName("data")]
    public T? Data { get; init; }

    [JsonPropertyName("error_message")]
    public string? ErrorMessage { get; init; }

    [JsonPropertyName("error_field")]
    public string? ErrorField { get; init; }
}

public sealed class ExecutionQueueBlockedDto
{
    [JsonPropertyName("queue_id")]
    public long QueueId { get; init; }

    [JsonPropertyName("queue_item_id")]
    public long QueueItemId { get; init; }

    [JsonPropertyName("test_suite_id")]
    public long TestSuiteId { get; init; }

    [JsonPropertyName("execution_id")]
    public long? ExecutionId { get; init; }

    [JsonPropertyName("base_test_suite_id")]
    public long? BaseTestSuiteId { get; init; }

    [JsonPropertyName("attempt_no")]
    public int AttemptNo { get; init; }
}

public sealed class ExecutionQueueClaimResponseDto
{
    [JsonPropertyName("claim_token")]
    public string? ClaimToken { get; init; }

    [JsonPropertyName("queue")]
    public ExecutionQueueDto? Queue { get; init; }

    [JsonPropertyName("item")]
    public ExecutionQueueItemDto? Item { get; init; }

    [JsonPropertyName("blocked_by")]
    public ExecutionQueueBlockedDto? BlockedBy { get; init; }
}

public sealed class ExecutionQueueItemAckDto
{
    [JsonPropertyName("queue_id")]
    public long QueueId { get; init; }

    [JsonPropertyName("queue_item_id")]
    public long QueueItemId { get; init; }

    [JsonPropertyName("test_suite_id")]
    public long TestSuiteId { get; init; }

    [JsonPropertyName("execution_id")]
    public long? ExecutionId { get; init; }

    [JsonPropertyName("attempt_no")]
    public int AttemptNo { get; init; }
}

public sealed class RunnerSessionBootstrapDto
{
    [JsonPropertyName("runner_session_token")]
    public string RunnerSessionToken { get; init; } = string.Empty;

    [JsonPropertyName("expires_in_seconds")]
    public int ExpiresInSeconds { get; init; }

    [JsonPropertyName("runner_id")]
    public string RunnerId { get; init; } = string.Empty;
}

public sealed class IntegrationConnectionDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("client_id")]
    public long ClientId { get; init; }

    [JsonPropertyName("project_id")]
    public long? ProjectId { get; init; }

    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("is_enabled")]
    public bool IsEnabled { get; init; }

    [JsonPropertyName("sync_test_cases")]
    public bool SyncTestCases { get; init; }

    [JsonPropertyName("sync_test_plans")]
    public bool SyncTestPlans { get; init; }

    [JsonPropertyName("sync_test_runs")]
    public bool SyncTestRuns { get; init; }

    [JsonPropertyName("sync_defects")]
    public bool SyncDefects { get; init; }

    [JsonPropertyName("auto_sync_test_cases")]
    public bool AutoSyncTestCases { get; init; }

    [JsonPropertyName("auto_sync_test_runs")]
    public bool AutoSyncTestRuns { get; init; }

    [JsonPropertyName("auto_sync_defects")]
    public bool AutoSyncTestDefects { get; init; }

    [JsonPropertyName("config")]
    public JsonElement Config { get; init; }

    [JsonPropertyName("has_credentials")]
    public bool HasCredentials { get; init; }

    [JsonPropertyName("created_by")]
    public long? CreatedBy { get; init; }

    [JsonPropertyName("updated_by")]
    public long? UpdatedBy { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset? UpdatedAt { get; init; }
}

public sealed class IntegrationJobDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("integration_connection_id")]
    public long? IntegrationConnectionId { get; init; }

    [JsonPropertyName("client_id")]
    public long ClientId { get; init; }

    [JsonPropertyName("project_id")]
    public long? ProjectId { get; init; }

    [JsonPropertyName("entity_type")]
    public string? EntityType { get; init; }

    [JsonPropertyName("internal_id")]
    public long? InternalId { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("attempts")]
    public int Attempts { get; init; }

    [JsonPropertyName("max_attempts")]
    public int MaxAttempts { get; init; }

    [JsonPropertyName("last_error")]
    public string? LastError { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; init; }

    [JsonPropertyName("sent_at")]
    public DateTimeOffset? SentAt { get; init; }
}

public sealed class IntegrationMappingDto
{
    [JsonPropertyName("integration_connection_id")]
    public long IntegrationConnectionId { get; init; }

    [JsonPropertyName("entity_type")]
    public string EntityType { get; init; } = string.Empty;

    [JsonPropertyName("field_map_json")]
    public JsonElement FieldMapJson { get; init; }

    [JsonPropertyName("status_map_json")]
    public JsonElement StatusMapJson { get; init; }

    [JsonPropertyName("priority_map_json")]
    public JsonElement PriorityMapJson { get; init; }

    [JsonPropertyName("options_json")]
    public JsonElement OptionsJson { get; init; }
}

public sealed class IntegrationSummaryRowDto
{
    [JsonPropertyName("entity_type")]
    public string? EntityType { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("total")]
    public int Total { get; init; }
}

public sealed class IntegrationErrorReasonDto
{
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("total")]
    public int Total { get; init; }
}

public sealed class IntegrationSummaryDto
{
    [JsonPropertyName("window_days")]
    public int WindowDays { get; init; }

    [JsonPropertyName("by_type_status")]
    public IReadOnlyList<IntegrationSummaryRowDto> ByTypeStatus { get; init; } = [];

    [JsonPropertyName("oldest_pending")]
    public BasicRefDto? OldestPending { get; init; }

    [JsonPropertyName("oldest_pending_minutes")]
    public long? OldestPendingMinutes { get; init; }

    [JsonPropertyName("top_error_reasons")]
    public IReadOnlyList<IntegrationErrorReasonDto> TopErrorReasons { get; init; } = [];
}

public sealed class IntegrationHealthDto
{
    [JsonPropertyName("window_minutes")]
    public int WindowMinutes { get; init; }

    [JsonPropertyName("pending_sla_minutes")]
    public int PendingSlaMinutes { get; init; }

    [JsonPropertyName("failure_rate_threshold")]
    public double FailureRateThreshold { get; init; }

    [JsonPropertyName("oldest_pending_minutes")]
    public long OldestPendingMinutes { get; init; }

    [JsonPropertyName("recent_total")]
    public int RecentTotal { get; init; }

    [JsonPropertyName("recent_failed")]
    public int RecentFailed { get; init; }

    [JsonPropertyName("recent_failure_rate")]
    public double RecentFailureRate { get; init; }

    [JsonPropertyName("alerts")]
    public IReadOnlyList<string> Alerts { get; init; } = [];

    [JsonPropertyName("is_healthy")]
    public bool IsHealthy { get; init; }
}

public sealed class IntegrationBulkQueueResultDto
{
    [JsonPropertyName("requested")]
    public int Requested { get; init; }

    [JsonPropertyName("queued")]
    public int Queued { get; init; }

    [JsonPropertyName("jobs")]
    public IReadOnlyList<IntegrationJobDto> Jobs { get; init; } = [];
}

public sealed class CountResultDto
{
    [JsonPropertyName("count")]
    public int Count { get; init; }
}

public sealed class UserBasicDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("email")]
    public string? Email { get; init; }
}

public sealed class TestPlanProjectDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("project_name")]
    public string? ProjectName { get; init; }

    [JsonPropertyName("area_path")]
    public string? AreaPath { get; init; }
}

public sealed class TestPlanDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("area_path")]
    public string? AreaPath { get; init; }

    [JsonPropertyName("iteration_path")]
    public string? IterationPath { get; init; }

    [JsonPropertyName("plan_type")]
    public string? PlanType { get; init; }

    [JsonPropertyName("plan_status")]
    public string? PlanStatus { get; init; }

    [JsonPropertyName("is_active")]
    public bool? IsActive { get; init; }

    [JsonPropertyName("status")]
    public int? Status { get; init; }

    [JsonPropertyName("start_date")]
    public string? StartDate { get; init; }

    [JsonPropertyName("end_date")]
    public string? EndDate { get; init; }

    [JsonPropertyName("last_updated")]
    public string? LastUpdated { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset? UpdatedAt { get; init; }

    [JsonPropertyName("target_version")]
    public string? TargetVersion { get; init; }

    [JsonPropertyName("objective")]
    public string? Objective { get; init; }

    [JsonPropertyName("project_id")]
    public long? ProjectId { get; init; }

    [JsonPropertyName("project")]
    public TestPlanProjectDto? Project { get; init; }

    [JsonPropertyName("owner")]
    public UserBasicDto? Owner { get; init; }

    [JsonPropertyName("users")]
    public IReadOnlyList<UserBasicDto> Users { get; init; } = [];
}

public sealed class TestPlanItemDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("test_plan_id")]
    public long? TestPlanId { get; init; }
}

public sealed class TestStateDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public sealed class TestSuiteListDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("test_title")]
    public string? TestTitle { get; init; }

    [JsonPropertyName("parent_id")]
    public long? ParentId { get; init; }

    [JsonPropertyName("test_suite_type")]
    public int? TestSuiteType { get; init; }

    [JsonPropertyName("test_state_id")]
    public long? TestStateId { get; init; }

    [JsonPropertyName("created_by")]
    public string? CreatedBy { get; init; }

    [JsonPropertyName("updated_by")]
    public string? UpdatedBy { get; init; }

    [JsonPropertyName("state")]
    public BasicRefDto? State { get; init; }

    [JsonPropertyName("project_name")]
    public string? ProjectName { get; init; }

    [JsonPropertyName("tags")]
    public string? Tags { get; init; }

    [JsonPropertyName("priority")]
    public string? Priority { get; init; }

    [JsonPropertyName("last_result")]
    public string? LastResult { get; init; }

    [JsonPropertyName("last_run")]
    public DateTimeOffset? LastRun { get; init; }
}

public sealed class TestSuiteFullDatasetStepDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("dataset_id")]
    public long DatasetId { get; init; }

    [JsonPropertyName("display_id")]
    public int? DisplayId { get; init; }

    [JsonPropertyName("skip_step")]
    public bool SkipStep { get; init; }

    [JsonPropertyName("step_id")]
    public long? StepId { get; init; }

    [JsonPropertyName("internal_step_id")]
    public long? InternalStepId { get; init; }

    [JsonPropertyName("value")]
    public string? Value { get; init; }

    [JsonPropertyName("override")]
    public bool? Override { get; init; }

    [JsonPropertyName("override_value")]
    public string? OverrideValue { get; init; }

    [JsonPropertyName("step_info")]
    public JsonElement StepInfo { get; init; }
}

public sealed class TestSuiteFullDatasetDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("sort_order")]
    public int? SortOrder { get; init; }

    [JsonPropertyName("scenario")]
    public string? Scenario { get; init; }

    [JsonPropertyName("status")]
    public bool Status { get; init; }

    [JsonPropertyName("steps")]
    public IReadOnlyList<TestSuiteFullDatasetStepDto> Steps { get; init; } = [];
}

public sealed class TestSuiteFullComponentEntryDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("project_id")]
    public long? ProjectId { get; init; }

    [JsonPropertyName("test_design_id")]
    public long? TestDesignId { get; init; }

    [JsonPropertyName("component_id")]
    public long? ComponentId { get; init; }

    [JsonPropertyName("status")]
    public bool Status { get; init; }

    [JsonPropertyName("component")]
    public ComponentDetailDto? Component { get; init; }

    [JsonPropertyName("datasets")]
    public IReadOnlyList<TestSuiteFullDatasetDto> Datasets { get; init; } = [];
}

public sealed class TestSuiteFullDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonIgnore]
    public long RuntimeSuiteId { get; init; }

    [JsonIgnore]
    public long? TestPlanItemSuiteId { get; init; }

    [JsonIgnore]
    public long? ConfigurationAssignmentId { get; init; }

    [JsonIgnore]
    public long? SelectedDatasetPlanRowId { get; init; }

    [JsonIgnore]
    public long? SelectedDatasetId { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("test_state_id")]
    public long? TestStateId { get; init; }

    [JsonPropertyName("test_suite_type")]
    public int? TestSuiteType { get; init; }

    [JsonPropertyName("folder_path_id")]
    public long? FolderPathId { get; init; }

    [JsonPropertyName("comment")]
    public string? Comment { get; init; }

    [JsonPropertyName("project_id")]
    public long? ProjectId { get; init; }

    [JsonPropertyName("iteration_path")]
    public string? IterationPath { get; init; }

    [JsonPropertyName("priority")]
    public string? Priority { get; init; }

    [JsonPropertyName("story_id")]
    public string? StoryId { get; init; }

    [JsonPropertyName("test_title")]
    public string? TestTitle { get; init; }

    [JsonPropertyName("tags")]
    public string? Tags { get; init; }

    [JsonPropertyName("parent_id")]
    public long? ParentId { get; init; }

    [JsonPropertyName("configuration_id")]
    public long? ConfigurationId { get; init; }

    [JsonPropertyName("kba_ready")]
    public bool KbaReady { get; init; }

    [JsonPropertyName("training_ready")]
    public bool TrainingReady { get; init; }

    [JsonPropertyName("release_notes_ready")]
    public bool ReleaseNotesReady { get; init; }

    [JsonPropertyName("components")]
    public IReadOnlyList<TestSuiteFullComponentEntryDto> Components { get; init; } = [];

    [JsonPropertyName("datasets")]
    public IReadOnlyList<TestSuiteFullDatasetDto> Datasets { get; init; } = [];
}

public sealed class SaveTestSuiteDetailsRequest
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("test_state_id")]
    public long? TestStateId { get; init; }

    [JsonPropertyName("test_suite_type")]
    public int? TestSuiteType { get; init; }

    [JsonPropertyName("folder_path_id")]
    public long? FolderPathId { get; init; }

    [JsonPropertyName("comment")]
    public string? Comment { get; init; }

    [JsonPropertyName("project_id")]
    public long? ProjectId { get; init; }

    [JsonPropertyName("iteration_path")]
    public string? IterationPath { get; init; }

    [JsonPropertyName("priority")]
    public string? Priority { get; init; }

    [JsonPropertyName("story_id")]
    public string? StoryId { get; init; }

    [JsonPropertyName("test_title")]
    public string? TestTitle { get; init; }

    [JsonPropertyName("tags")]
    public JsonElement? Tags { get; init; }

    [JsonPropertyName("configuration_id")]
    public long? ConfigurationId { get; init; }

    [JsonPropertyName("kba_ready")]
    public bool? KbaReady { get; init; }

    [JsonPropertyName("training_ready")]
    public bool? TrainingReady { get; init; }

    [JsonPropertyName("release_notes_ready")]
    public bool? ReleaseNotesReady { get; init; }
}

public sealed class RenameSharedTagRequest
{
    [JsonPropertyName("old_tag")]
    public string? OldTag { get; init; }

    [JsonPropertyName("new_tag")]
    public string? NewTag { get; init; }
}

public sealed class SaveTestSuiteStepRequest
{
    [JsonPropertyName("display_id")]
    public int? DisplayId { get; init; }

    [JsonPropertyName("id")]
    public long? Id { get; init; }

    [JsonPropertyName("value")]
    public string? Value { get; init; }

    [JsonPropertyName("override")]
    public bool? Override { get; init; }

    [JsonPropertyName("override_value")]
    public string? OverrideValue { get; init; }
}

public sealed class SaveTestSuiteDatasetRequest
{
    [JsonPropertyName("dataset_id")]
    public long? DatasetId { get; init; }

    [JsonPropertyName("sort_order")]
    public int? SortOrder { get; init; }

    [JsonPropertyName("scenario")]
    public string? Scenario { get; init; }

    [JsonPropertyName("status")]
    public bool? Status { get; init; }

    [JsonPropertyName("steps")]
    public IReadOnlyList<SaveTestSuiteStepRequest> Steps { get; init; } = [];
}

public sealed class SaveTestSuiteComponentRequest
{
    [JsonPropertyName("component_id")]
    public long? ComponentId { get; init; }

    [JsonPropertyName("project_id")]
    public long? ProjectId { get; init; }

    [JsonPropertyName("status")]
    public bool? Status { get; init; }

    [JsonPropertyName("datasets")]
    public IReadOnlyList<SaveTestSuiteDatasetRequest> Datasets { get; init; } = [];
}

public sealed class TestSuiteComponentDatasetSummaryDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("sort_order")]
    public int? SortOrder { get; init; }

    [JsonPropertyName("scenario")]
    public string? Scenario { get; init; }

    [JsonPropertyName("status")]
    public bool Status { get; init; }
}

public sealed class SaveTestSuiteComponentDatasetsRequest
{
    [JsonPropertyName("datasets")]
    public IReadOnlyList<SaveTestSuiteDatasetRequest> Datasets { get; init; } = [];
}

public sealed class CloneTestSuiteRequest
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }
}

public sealed class SaveTestSuiteRequest
{
    [JsonPropertyName("details")]
    public SaveTestSuiteDetailsRequest? Details { get; init; }

    [JsonPropertyName("designed_components")]
    public IReadOnlyList<SaveTestSuiteComponentRequest> DesignedComponents { get; init; } = [];

    [JsonPropertyName("datasets")]
    public IReadOnlyList<SaveTestSuiteDatasetRequest> Datasets { get; init; } = [];
}

public sealed class SaveTestSuiteFlowComponentRequest
{
    [JsonPropertyName("client_key")]
    public string? ClientKey { get; init; }

    [JsonPropertyName("test_component_id")]
    public long? TestComponentId { get; init; }

    [JsonPropertyName("component_id")]
    public long? ComponentId { get; init; }

    [JsonPropertyName("project_id")]
    public long? ProjectId { get; init; }

    [JsonPropertyName("status")]
    public bool? Status { get; init; }

    [JsonPropertyName("sort_order")]
    public int? SortOrder { get; init; }
}

public sealed class SaveTestSuiteFlowRequest
{
    [JsonPropertyName("components")]
    public IReadOnlyList<SaveTestSuiteFlowComponentRequest> Components { get; init; } = [];
}

public sealed class TestSuiteFlowComponentSummaryDto
{
    [JsonPropertyName("client_key")]
    public string? ClientKey { get; init; }

    [JsonPropertyName("test_component_id")]
    public long TestComponentId { get; init; }

    [JsonPropertyName("component_id")]
    public long ComponentId { get; init; }

    [JsonPropertyName("project_id")]
    public long ProjectId { get; init; }

    [JsonPropertyName("status")]
    public bool Status { get; init; }

    [JsonPropertyName("sort_order")]
    public int SortOrder { get; init; }
}

public sealed class SaveTestSuiteDetailsResult
{
    public SaveTestSuiteOutcome Outcome { get; init; }

    public SaveTestSuiteDetailsRequest? Details { get; init; }

    public string? ErrorField { get; init; }

    public string? ErrorMessage { get; init; }
}

public sealed class SaveTestSuiteFlowResult
{
    public SaveTestSuiteOutcome Outcome { get; init; }

    public IReadOnlyList<TestSuiteFlowComponentSummaryDto> Components { get; init; } = [];

    public string? ErrorField { get; init; }

    public string? ErrorMessage { get; init; }
}

public enum SaveTestSuiteOutcome
{
    Saved,
    NotFound,
    InvalidReference
}

public sealed class SaveTestSuiteResult
{
    public SaveTestSuiteOutcome Outcome { get; init; }

    public TestSuiteFullDto? TestSuite { get; init; }

    public string? ErrorField { get; init; }

    public string? ErrorMessage { get; init; }
}

public sealed class SaveOverrideValueRequest
{
    [JsonPropertyName("value")]
    public string? Value { get; init; }

    [JsonPropertyName("dataset_id")]
    public long DatasetId { get; init; }

    [JsonPropertyName("step_id")]
    public long StepId { get; init; }

    [JsonPropertyName("reset")]
    public bool? Reset { get; init; }
}

public sealed class SaveTestRunnerStepRequest
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("step_id")]
    public long StepId { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("expected_output")]
    public string? ExpectedOutput { get; init; }

    [JsonPropertyName("value")]
    public string? Value { get; init; }

    [JsonPropertyName("dataset_id")]
    public long? DatasetId { get; init; }

    [JsonPropertyName("is_passed")]
    public bool? IsPassed { get; init; }

    [JsonPropertyName("comment")]
    public string? Comment { get; init; }

    [JsonPropertyName("attempt_no")]
    public int? AttemptNo { get; init; }

    [JsonPropertyName("executed_at")]
    public DateTimeOffset? ExecutedAt { get; init; }

    [JsonPropertyName("idempotency_key")]
    public string? IdempotencyKey { get; init; }

    [JsonIgnore]
    public long ResolvedId => Id > 0 ? Id : StepId;
}

public sealed class SaveTestRunnerStepStatusRequest
{
    [JsonPropertyName("test_runner_id")]
    public long? TestRunnerId { get; init; }

    [JsonPropertyName("test_plan_item_id")]
    public long? TestPlanItemId { get; init; }

    [JsonPropertyName("test_suite_id")]
    public long TestSuiteId { get; init; }

    [JsonPropertyName("execution_id")]
    public long? ExecutionId { get; init; }

    [JsonPropertyName("steps")]
    public IReadOnlyList<SaveTestRunnerStepRequest> Steps { get; init; } = [];

    [JsonPropertyName("batch_id")]
    public string? BatchId { get; init; }

    [JsonPropertyName("bulk_update")]
    public bool? BulkUpdate { get; init; }

    [JsonPropertyName("is_passed")]
    public bool? IsPassed { get; init; }
}

public enum SaveTestRunnerStepStatusOutcome
{
    Saved,
    NotFound
}

public sealed class SaveTestRunnerStepStatusResult
{
    public SaveTestRunnerStepStatusOutcome Outcome { get; init; }

    public TestRunnerPayloadDto? Payload { get; init; }

    public TestRunnerStepStatusSummaryDto? Summary { get; init; }

    public string? ErrorMessage { get; init; }
}

public sealed class TestRunnerStepStatusSummaryDto
{
    [JsonPropertyName("accepted")]
    public int Accepted { get; init; }

    [JsonPropertyName("matched")]
    public int Matched { get; init; }

    [JsonPropertyName("updated")]
    public int Updated { get; init; }

    [JsonPropertyName("suite_status")]
    public string SuiteStatus { get; init; } = "In Progress";
}

public sealed class SaveAndCloseTestSuiteRequest
{
    [JsonPropertyName("test_runner_id")]
    public long TestRunnerId { get; init; }

    [JsonPropertyName("test_suite_id")]
    public long TestSuiteId { get; init; }

    [JsonPropertyName("execution_id")]
    public long? ExecutionId { get; init; }

    [JsonPropertyName("test_plan_item_id")]
    public long? TestPlanItemId { get; init; }
}

public sealed class PauseTestSuiteRequest
{
    [JsonPropertyName("test_runner_id")]
    public long TestRunnerId { get; init; }

    [JsonPropertyName("test_suite_id")]
    public long TestSuiteId { get; init; }

    [JsonPropertyName("execution_id")]
    public long? ExecutionId { get; init; }

    [JsonPropertyName("test_plan_item_id")]
    public long? TestPlanItemId { get; init; }

    [JsonPropertyName("resume_step_id")]
    public long? ResumeStepId { get; init; }

    [JsonPropertyName("resume_step_index")]
    public int? ResumeStepIndex { get; init; }
}

public enum RunnerItemMutationOutcome
{
    Saved,
    NotFound
}

public sealed class RunnerItemMutationResult
{
    public RunnerItemMutationOutcome Outcome { get; init; }

    public string? ErrorMessage { get; init; }
}

public enum SaveOverrideValueOutcome
{
    Saved,
    NotFound,
    ValidationFailed
}

public sealed class SaveOverrideValueResult
{
    public SaveOverrideValueOutcome Outcome { get; init; }

    public string? ErrorField { get; init; }

    public string? ErrorMessage { get; init; }
}

public enum DeleteTestSuiteOutcome
{
    Deleted,
    NotFound,
    ActivePlansBlocked
}

public sealed class DeleteTestSuiteResult
{
    public DeleteTestSuiteOutcome Outcome { get; init; }

    public string? ErrorMessage { get; init; }
}

public sealed class GetTestSuiteStepsRequest
{
    [JsonPropertyName("invoked_via_tests")]
    public bool? InvokedViaTests { get; init; }

    [JsonPropertyName("test_plan_item_id")]
    public long? TestPlanItemId { get; init; }

    [JsonPropertyName("test_suites")]
    public IReadOnlyList<long> TestSuites { get; init; } = [];
}

public sealed class TestRunnerPayloadDto
{
    [JsonPropertyName("test_runner")]
    public RunnerHeaderDto? TestRunner { get; init; }

    [JsonPropertyName("test_runner_steps")]
    public IReadOnlyList<TestRunnerSuiteDto> TestRunnerSteps { get; init; } = [];
}

public sealed class RunnerHeaderDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("test_plan_item_id")]
    public long? TestPlanItemId { get; init; }

    [JsonPropertyName("test_plan_item_name")]
    public string? TestPlanItemName { get; init; }
}

public sealed class TestRunnerSuiteDto
{
    [JsonPropertyName("test_suite")]
    public RunnerSuiteHeaderDto TestSuite { get; init; } = new();

    [JsonPropertyName("steps")]
    public IReadOnlyList<TestRunnerStepDto> Steps { get; init; } = [];
}

public sealed class RunnerSuiteHeaderDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("base_test_suite_id")]
    public long? BaseTestSuiteId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("videos")]
    public JsonElement Videos { get; init; }

    [JsonPropertyName("prereq")]
    public string? Prereq { get; init; }

    [JsonPropertyName("configuration")]
    public SuiteConfigurationDto? Configuration { get; init; }
}

public sealed class RunnerKeywordDto
{
    [JsonPropertyName("id")]
    public long? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("keyword_combination_names")]
    public string? KeywordCombinationNames { get; init; }
}

public sealed class TestRunnerStepDto
{
    [JsonPropertyName("dataset_id")]
    public long DatasetId { get; init; }

    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("expected_output")]
    public string? ExpectedOutput { get; init; }

    [JsonPropertyName("value")]
    public string? Value { get; init; }

    [JsonPropertyName("xPath")]
    public string? XPath { get; init; }

    [JsonPropertyName("keyword_id")]
    public long? KeywordId { get; init; }

    [JsonPropertyName("keyword")]
    public RunnerKeywordDto? Keyword { get; init; }

    [JsonPropertyName("before_step")]
    public IReadOnlyList<Dictionary<string, string>>? BeforeStep { get; init; }

    [JsonPropertyName("after_step")]
    public IReadOnlyList<Dictionary<string, string>>? AfterStep { get; init; }

    [JsonPropertyName("is_passed")]
    public bool? IsPassed { get; init; }

    [JsonPropertyName("comment")]
    public string? Comment { get; init; }

    [JsonPropertyName("images")]
    public JsonElement? Images { get; init; }
}

public sealed class SuiteConfigurationDto
{
    [JsonPropertyName("id")]
    public long? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("configuration_variables")]
    public IReadOnlyList<ConfigurationSelectedVariableDto> ConfigurationVariables { get; init; } = [];
}

public sealed class SuiteLightDto
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("state")]
    public BasicRefDto? State { get; init; }

    [JsonPropertyName("configuration")]
    public SuiteConfigurationDto? Configuration { get; init; }
}

public sealed class TestPlanItemSuiteLightDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("execution_id")]
    public long ExecutionId { get; init; }

    [JsonPropertyName("test_design_id")]
    public long? TestDesignId { get; init; }

    [JsonPropertyName("parent_id")]
    public long? ParentId { get; init; }

    [JsonPropertyName("status")]
    public BasicRefDto? Status { get; init; }

    [JsonPropertyName("is_paused")]
    public bool IsPaused { get; init; }

    [JsonPropertyName("suite")]
    public SuiteLightDto? Suite { get; init; }

    [JsonPropertyName("users")]
    public IReadOnlyList<UserBasicDto> Users { get; init; } = [];
}

public sealed class TestPlanSuitesForItemDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("added_suites")]
    public IReadOnlyList<TestPlanItemSuiteLightDto> AddedSuites { get; init; } = [];
}

public sealed class TestRunnerLogItemDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("defect_id")]
    public long? DefectId { get; init; }

    [JsonPropertyName("test_runner_id")]
    public long? TestRunnerId { get; init; }

    [JsonPropertyName("test_plan_id")]
    public long? TestPlanId { get; init; }

    [JsonPropertyName("test_plan_name")]
    public string? TestPlanName { get; init; }

    [JsonPropertyName("test_plan_item_id")]
    public long? TestPlanItemId { get; init; }

    [JsonPropertyName("test_plan_item_name")]
    public string? TestPlanItemName { get; init; }

    [JsonPropertyName("test_suite_id")]
    public long? TestSuiteId { get; init; }

    [JsonPropertyName("test_suite_name")]
    public string? TestSuiteName { get; init; }

    [JsonPropertyName("configuration_name")]
    public string? ConfigurationName { get; init; }

    [JsonPropertyName("configuration_variables")]
    public IReadOnlyList<ConfigurationSelectedVariableDto> ConfigurationVariables { get; init; } = [];

    [JsonPropertyName("is_favorite")]
    public bool IsFavorite { get; init; }

    [JsonPropertyName("user_id")]
    public long? UserId { get; init; }

    [JsonPropertyName("username")]
    public string? Username { get; init; }

    [JsonPropertyName("email")]
    public string? Email { get; init; }

    [JsonPropertyName("status_id")]
    public long? StatusId { get; init; }

    [JsonPropertyName("status_name")]
    public string? StatusName { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset? UpdatedAt { get; init; }

    [JsonPropertyName("comment")]
    public string? Comment { get; init; }

    [JsonPropertyName("prereq")]
    public string? Prereq { get; init; }

    [JsonPropertyName("videos")]
    public string? Videos { get; init; }

    [JsonPropertyName("CAN_CREATE_DEFECT")]
    public int CanCreateDefect { get; init; }

    [JsonPropertyName("added_steps")]
    public JsonElement AddedSteps { get; init; }
}

public sealed class GlobalKeywordDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("usage_count")]
    public int UsageCount { get; init; }
}

public sealed class SaveCustomVariableRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("variable_id")]
    public long VariableId { get; init; }

    [JsonPropertyName("value")]
    public string? Value { get; init; }

    [JsonPropertyName("test_case_id")]
    public long? TestCaseId { get; init; }
}

public sealed class SaveConfigurationVariableValueRequest
{
    [JsonPropertyName("id")]
    public long? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public sealed class SaveConfigurationVariableRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("values")]
    public IReadOnlyList<SaveConfigurationVariableValueRequest> Values { get; init; } = [];

    [JsonPropertyName("deleted_values")]
    public IReadOnlyList<long> DeletedValues { get; init; } = [];
}

public sealed class SaveGlobalKeywordRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public sealed class SaveBeforeAfterStepAdminRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("field")]
    public bool? Field { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("rules")]
    public JsonElement? Rules { get; init; }
}

public sealed class SaveIntegrationConnectionRequest
{
    [JsonPropertyName("project_id")]
    public long? ProjectId { get; init; }

    [JsonPropertyName("clear_project_scope")]
    public bool? ClearProjectScope { get; init; }

    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("is_enabled")]
    public bool? IsEnabled { get; init; }

    [JsonPropertyName("sync_test_cases")]
    public bool? SyncTestCases { get; init; }

    [JsonPropertyName("sync_test_plans")]
    public bool? SyncTestPlans { get; init; }

    [JsonPropertyName("sync_test_runs")]
    public bool? SyncTestRuns { get; init; }

    [JsonPropertyName("sync_defects")]
    public bool? SyncDefects { get; init; }

    [JsonPropertyName("auto_sync_test_cases")]
    public bool? AutoSyncTestCases { get; init; }

    [JsonPropertyName("auto_sync_test_runs")]
    public bool? AutoSyncTestRuns { get; init; }

    [JsonPropertyName("auto_sync_defects")]
    public bool? AutoSyncDefects { get; init; }

    [JsonPropertyName("config")]
    public JsonElement? Config { get; init; }

    [JsonPropertyName("credentials")]
    public JsonElement? Credentials { get; init; }
}

public sealed class SaveIntegrationMappingRequest
{
    [JsonPropertyName("field_map_json")]
    public JsonElement? FieldMapJson { get; init; }

    [JsonPropertyName("status_map_json")]
    public JsonElement? StatusMapJson { get; init; }

    [JsonPropertyName("priority_map_json")]
    public JsonElement? PriorityMapJson { get; init; }

    [JsonPropertyName("options_json")]
    public JsonElement? OptionsJson { get; init; }
}

public sealed class QueueIntegrationSyncRequest
{
    [JsonPropertyName("connection_id")]
    public long ConnectionId { get; init; }

    [JsonPropertyName("entity_type")]
    public string? EntityType { get; init; }

    [JsonPropertyName("internal_id")]
    public long InternalId { get; init; }

    [JsonPropertyName("payload")]
    public JsonElement? Payload { get; init; }
}

public sealed class QueueIntegrationBulkSyncRequest
{
    [JsonPropertyName("project_id")]
    public long? ProjectId { get; init; }

    [JsonPropertyName("entity_type")]
    public string? EntityType { get; init; }

    [JsonPropertyName("internal_ids")]
    public IReadOnlyList<long> InternalIds { get; init; } = [];
}

public sealed class ReplayFailedIntegrationJobsRequest
{
    [JsonPropertyName("connection_id")]
    public long? ConnectionId { get; init; }

    [JsonPropertyName("limit")]
    public int? Limit { get; init; }
}

public sealed class UserSettingsDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("user_id")]
    public long UserId { get; init; }

    [JsonPropertyName("settings")]
    public JsonElement Settings { get; init; }
}

public sealed class UpdateUserSettingsRequest
{
    [JsonPropertyName("settings")]
    public JsonElement Settings { get; init; }
}

public sealed class SaveUserRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("email")]
    public string? Email { get; init; }

    [JsonPropertyName("password")]
    public string? Password { get; init; }

    [JsonPropertyName("password_confirmation")]
    public string? PasswordConfirmation { get; init; }

    [JsonPropertyName("role_id")]
    public long? RoleId { get; init; }

    [JsonPropertyName("is_active")]
    public bool? IsActive { get; init; }
}

public sealed class BulkDeleteUsersRequest
{
    [JsonPropertyName("user_ids")]
    public IReadOnlyList<long> UserIds { get; init; } = [];
}

public sealed class SaveRoleRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("permissions")]
    public IReadOnlyList<string> Permissions { get; init; } = [];
}

public sealed class SaveProjectRequest
{
    [JsonPropertyName("project_name")]
    public string? ProjectName { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("area_path")]
    public string? AreaPath { get; init; }

    [JsonPropertyName("primary_test_management")]
    public string? PrimaryTestManagement { get; init; }

    [JsonPropertyName("primary_ticketing_system")]
    public string? PrimaryTicketingSystem { get; init; }

    [JsonPropertyName("type_id")]
    public long? TypeId { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }
}

public sealed class BulkDeleteProjectsRequest
{
    [JsonPropertyName("project_ids")]
    public IReadOnlyList<long> ProjectIds { get; init; } = [];
}

public sealed class UpdateEntityStatusRequest
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("status")]
    public bool Status { get; init; }
}

public sealed class BulkDeleteComponentsRequest
{
    [JsonPropertyName("component_ids")]
    public IReadOnlyList<long> ComponentIds { get; init; } = [];
}

public sealed class ComponentExistsResponseDto
{
    [JsonPropertyName("exists")]
    public bool Exists { get; init; }
}

public sealed class ComponentMetadataCatalogDto
{
    [JsonPropertyName("pages")]
    public IReadOnlyList<string> Pages { get; init; } = [];

    [JsonPropertyName("features")]
    public IReadOnlyList<ComponentFeatureCatalogEntryDto> Features { get; init; } = [];
}

public sealed class ComponentFeatureCatalogEntryDto
{
    [JsonPropertyName("feature")]
    public string Feature { get; init; } = string.Empty;

    [JsonPropertyName("pages")]
    public IReadOnlyList<string> Pages { get; init; } = [];
}

public sealed class SaveComponentStepRequest
{
    [JsonPropertyName("id")]
    public long? Id { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("expected_output")]
    public string? ExpectedOutput { get; init; }

    [JsonPropertyName("keyword_ref")]
    public string? KeywordRef { get; init; }

    [JsonPropertyName("keyword_id")]
    public long? KeywordId { get; init; }

    [JsonPropertyName("keyword_source")]
    public string? KeywordSource { get; init; }

    [JsonPropertyName("brpg_obj")]
    public string? BrpgObj { get; init; }

    [JsonPropertyName("object_string")]
    public string? ObjectString { get; init; }

    [JsonPropertyName("xpath")]
    public string? XPath { get; init; }

    [JsonPropertyName("before_step")]
    public IReadOnlyList<string> BeforeStep { get; init; } = [];

    [JsonPropertyName("after_step")]
    public IReadOnlyList<string> AfterStep { get; init; } = [];

    [JsonPropertyName("display_id")]
    public int? DisplayId { get; init; }
}

public sealed class SaveComponentRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("project_id")]
    public long? ProjectId { get; init; }

    [JsonPropertyName("page")]
    public string? Page { get; init; }

    [JsonPropertyName("feature")]
    public string? Feature { get; init; }

    [JsonPropertyName("type_id")]
    public long? TypeId { get; init; }

    [JsonPropertyName("steps")]
    public IReadOnlyList<SaveComponentStepRequest> Steps { get; init; } = [];

    [JsonPropertyName("deleted_steps")]
    public IReadOnlyList<long> DeletedSteps { get; init; } = [];
}

public sealed class SaveConfigurationSelectionRequest
{
    [JsonPropertyName("variable_id")]
    public long VariableId { get; init; }

    [JsonPropertyName("variable_value_id")]
    public long VariableValueId { get; init; }
}

public sealed class SaveConfigurationRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("status")]
    public int? Status { get; init; }

    [JsonPropertyName("configuration_variables")]
    public IReadOnlyList<SaveConfigurationSelectionRequest> ConfigurationVariables { get; init; } = [];
}

public sealed class SaveTestPlanRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("project_id")]
    public long? ProjectId { get; init; }

    [JsonPropertyName("area_path")]
    public string? AreaPath { get; init; }

    [JsonPropertyName("iteration_path")]
    public string? IterationPath { get; init; }

    [JsonPropertyName("plan_type")]
    public string? PlanType { get; init; }

    [JsonPropertyName("plan_status")]
    public string? PlanStatus { get; init; }

    [JsonPropertyName("is_active")]
    public bool? IsActive { get; init; }

    [JsonPropertyName("start_date")]
    public string? StartDate { get; init; }

    [JsonPropertyName("end_date")]
    public string? EndDate { get; init; }

    [JsonPropertyName("owner_user_id")]
    public long? OwnerUserId { get; init; }

    [JsonPropertyName("target_version")]
    public string? TargetVersion { get; init; }

    [JsonPropertyName("objective")]
    public string? Objective { get; init; }

    [JsonPropertyName("users_id")]
    public IReadOnlyList<long> UsersId { get; init; } = [];

    [JsonPropertyName("type")]
    public int? Type { get; init; }
}

public sealed class SaveTestPlanItemRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("test_plan_id")]
    public long? TestPlanId { get; init; }
}

public sealed class AddSuitesToPlanItemRequest
{
    [JsonPropertyName("test_plan_item_id")]
    public long TestPlanItemId { get; init; }

    [JsonPropertyName("test_design_ids")]
    public IReadOnlyList<long> TestDesignIds { get; init; } = [];
}

public sealed class RemovePlanItemSuitesRequest
{
    [JsonPropertyName("ids")]
    public IReadOnlyList<long> Ids { get; init; } = [];
}

public sealed class UpdatePlanItemSuiteUsersRequest
{
    [JsonPropertyName("test_plan_item_suite_id")]
    public long TestPlanItemSuiteId { get; init; }

    [JsonPropertyName("users")]
    public IReadOnlyList<long> Users { get; init; } = [];
}

public sealed class AssignConfigurationsToSuiteRequest
{
    [JsonPropertyName("test_suite_id")]
    public long TestSuiteId { get; init; }

    [JsonPropertyName("test_plan_item_id")]
    public long TestPlanItemId { get; init; }

    [JsonPropertyName("configurations_id")]
    public IReadOnlyList<long> ConfigurationsId { get; init; } = [];
}

public sealed class SaveKeywordAliasRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("keyword_combination_ids")]
    public IReadOnlyList<long> KeywordCombinationIds { get; init; } = [];
}

public sealed class DeleteKeywordsRequest
{
    [JsonPropertyName("keywords_ids")]
    public IReadOnlyList<long> KeywordsIds { get; init; } = [];
}

public sealed class ChangeSuiteToNotStartedRequest
{
    [JsonPropertyName("test_plan_item_id")]
    public long TestPlanItemId { get; init; }

    [JsonPropertyName("test_suite_id")]
    public long TestSuiteId { get; init; }
}

public sealed class ImportComponentsResultDto
{
    [JsonPropertyName("created_components")]
    public int CreatedComponents { get; init; }

    [JsonPropertyName("created_steps")]
    public int CreatedSteps { get; init; }

    [JsonPropertyName("skipped_components")]
    public int SkippedComponents { get; init; }
}

public sealed class TestSuiteMatrixValidationDto
{
    [JsonPropertyName("tdid_count")]
    public int TdidCount { get; init; }

    [JsonPropertyName("component_groups")]
    public int ComponentGroups { get; init; }

    [JsonPropertyName("dataset_rows")]
    public int DatasetRows { get; init; }
}

public sealed class ImportTestSuitesResultDto
{
    [JsonPropertyName("created_tests")]
    public int CreatedTests { get; init; }

    [JsonPropertyName("updated_tests")]
    public int UpdatedTests { get; init; }

    [JsonPropertyName("created_datasets")]
    public int CreatedDatasets { get; init; }
}

public sealed class DashboardKpisDto
{
    [JsonPropertyName("total_suites")]
    public int TotalSuites { get; init; }

    [JsonPropertyName("executed_suites")]
    public int ExecutedSuites { get; init; }

    [JsonPropertyName("execution_coverage")]
    public decimal ExecutionCoverage { get; init; }

    [JsonPropertyName("pass_rate")]
    public decimal PassRate { get; init; }

    [JsonPropertyName("open_defects")]
    public int OpenDefects { get; init; }

    [JsonPropertyName("passed_runs")]
    public int PassedRuns { get; init; }

    [JsonPropertyName("failed_runs")]
    public int FailedRuns { get; init; }

    [JsonPropertyName("failed_suites")]
    public int FailedSuites { get; init; }

    [JsonPropertyName("passed_suites")]
    public int PassedSuites { get; init; }

    [JsonPropertyName("not_run_suites")]
    public int NotRunSuites { get; init; }

    [JsonPropertyName("readiness_score")]
    public int ReadinessScore { get; init; }
}

public sealed class DashboardExecutionCountsDto
{
    [JsonPropertyName("passed")]
    public int Passed { get; init; }

    [JsonPropertyName("failed")]
    public int Failed { get; init; }

    [JsonPropertyName("glitch")]
    public int Glitch { get; init; }

    [JsonPropertyName("retest")]
    public int Retest { get; init; }

    [JsonPropertyName("in_progress")]
    public int InProgress { get; init; }

    [JsonPropertyName("not_started")]
    public int NotStarted { get; init; }
}

public sealed class DashboardExecutionTrendRowDto
{
    [JsonPropertyName("date")]
    public string Date { get; init; } = string.Empty;

    [JsonPropertyName("passed")]
    public int Passed { get; init; }

    [JsonPropertyName("failed")]
    public int Failed { get; init; }

    [JsonPropertyName("retest")]
    public int Retest { get; init; }

    [JsonPropertyName("in_progress")]
    public int InProgress { get; init; }
}

public sealed class DashboardDefectTrendRowDto
{
    [JsonPropertyName("date")]
    public string Date { get; init; } = string.Empty;

    [JsonPropertyName("created")]
    public int Created { get; init; }

    [JsonPropertyName("closed")]
    public int Closed { get; init; }
}

public sealed class DashboardDefectStatusDto
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("count")]
    public int Count { get; init; }
}

public sealed class DashboardAgingBucketsDto
{
    [JsonPropertyName("0_2")]
    public int ZeroToTwo { get; init; }

    [JsonPropertyName("3_7")]
    public int ThreeToSeven { get; init; }

    [JsonPropertyName("8_14")]
    public int EightToFourteen { get; init; }

    [JsonPropertyName("15_plus")]
    public int FifteenPlus { get; init; }
}

public sealed class DashboardTopFailingSuiteDto
{
    [JsonPropertyName("test_suite_id")]
    public long TestSuiteId { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("failures")]
    public int Failures { get; init; }
}

public sealed class DashboardSummaryDto
{
    [JsonPropertyName("kpis")]
    public DashboardKpisDto Kpis { get; init; } = new();

    [JsonPropertyName("execution_counts")]
    public DashboardExecutionCountsDto ExecutionCounts { get; init; } = new();

    [JsonPropertyName("execution_trend")]
    public IReadOnlyList<DashboardExecutionTrendRowDto> ExecutionTrend { get; init; } = [];

    [JsonPropertyName("defect_trend")]
    public IReadOnlyList<DashboardDefectTrendRowDto> DefectTrend { get; init; } = [];

    [JsonPropertyName("defect_statuses")]
    public IReadOnlyList<DashboardDefectStatusDto> DefectStatuses { get; init; } = [];

    [JsonPropertyName("aging_buckets")]
    public DashboardAgingBucketsDto AgingBuckets { get; init; } = new();

    [JsonPropertyName("top_failing_suites")]
    public IReadOnlyList<DashboardTopFailingSuiteDto> TopFailingSuites { get; init; } = [];
}

public sealed class DefectUserRefDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("email")]
    public string? Email { get; init; }
}

public sealed class DefectStatusDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;
}

public sealed class DefectTestSuiteDto
{
    [JsonPropertyName("comment")]
    public string? Comment { get; init; }
}

public sealed class DefectRunnerItemDto
{
    [JsonPropertyName("test_plan_name")]
    public string? TestPlanName { get; init; }

    [JsonPropertyName("test_plan_item_id")]
    public long? TestPlanItemId { get; init; }

    [JsonPropertyName("test_plan_item_name")]
    public string? TestPlanItemName { get; init; }

    [JsonPropertyName("test_suite_name")]
    public string? TestSuiteName { get; init; }

    [JsonPropertyName("configuration_name")]
    public string? ConfigurationName { get; init; }

    [JsonPropertyName("configuration_variables")]
    public IReadOnlyList<ConfigurationSelectedVariableDto> ConfigurationVariables { get; init; } = [];

    [JsonPropertyName("added_steps")]
    public JsonElement AddedSteps { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; init; }

    [JsonPropertyName("user")]
    public DefectUserRefDto? User { get; init; }

    [JsonPropertyName("test_suite")]
    public DefectTestSuiteDto? TestSuite { get; init; }
}

public sealed class DefectAttachmentDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("file_name")]
    public string FileName { get; init; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    [JsonPropertyName("content_type")]
    public string? ContentType { get; init; }

    [JsonPropertyName("file_size")]
    public long FileSize { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; init; }
}

public sealed class DefectListItemDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("expected")]
    public string? Expected { get; init; }

    [JsonPropertyName("actual")]
    public string? Actual { get; init; }

    [JsonPropertyName("test_runner_item_id")]
    public long? TestRunnerItemId { get; init; }

    [JsonPropertyName("assigned")]
    public DefectUserRefDto? Assigned { get; init; }

    [JsonPropertyName("status")]
    public DefectStatusDto? Status { get; init; }

    [JsonPropertyName("created_by")]
    public DefectUserRefDto? CreatedBy { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; init; }

    [JsonPropertyName("test_plan_item")]
    public DefectRunnerItemDto? TestPlanItem { get; init; }

    [JsonPropertyName("attachments")]
    public IReadOnlyList<DefectAttachmentDto> Attachments { get; init; } = [];
}

public sealed class ProjectTypeDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset? UpdatedAt { get; init; }
}

public sealed class HealthPollConfigDto
{
    [JsonPropertyName("minutes")]
    public int Minutes { get; init; }
}

public sealed class SystemSettingDto
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [JsonPropertyName("value")]
    public string? Value { get; init; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset? UpdatedAt { get; init; }
}

public sealed class SaveSystemSettingRequest
{
    [JsonPropertyName("key")]
    public string Key { get; init; } = string.Empty;

    [JsonPropertyName("value")]
    public string? Value { get; init; }
}

public sealed class SaveExecutionDevicePoolRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }
}

public sealed class SaveExecutionDeviceRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("pool_id")]
    public long? PoolId { get; init; }

    [JsonPropertyName("host")]
    public string? Host { get; init; }

    [JsonPropertyName("api_key")]
    public string? ApiKey { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("health_status")]
    public string? HealthStatus { get; init; }

    [JsonPropertyName("runner_version")]
    public string? RunnerVersion { get; init; }

    [JsonPropertyName("max_concurrency")]
    public int? MaxConcurrency { get; init; }

    [JsonPropertyName("last_seen_at")]
    public DateTimeOffset? LastSeenAt { get; init; }

    [JsonPropertyName("last_health_payload")]
    public JsonElement? LastHealthPayload { get; init; }
}

public sealed class SaveExecutionScheduleRequest
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("cron")]
    public string? Cron { get; init; }

    [JsonPropertyName("timezone")]
    public string? Timezone { get; init; }

    [JsonPropertyName("run_mode")]
    public string? RunMode { get; init; }

    [JsonPropertyName("priority")]
    public string? Priority { get; init; }

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; init; }

    [JsonPropertyName("pool_id")]
    public long? PoolId { get; init; }

    [JsonPropertyName("payload_json")]
    public JsonElement? PayloadJson { get; init; }

    [JsonPropertyName("next_run_at")]
    public string? NextRunAt { get; init; }

    [JsonPropertyName("run_once")]
    public bool? RunOnce { get; init; }
}

public sealed class CreateExecutionQueueRequest
{
    [JsonPropertyName("priority")]
    public string? Priority { get; init; }

    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("run_mode")]
    public string? RunMode { get; init; }

    [JsonPropertyName("run_target")]
    public string? RunTarget { get; init; }

    [JsonPropertyName("pool_id")]
    public long? PoolId { get; init; }

    [JsonPropertyName("schedule_id")]
    public long? ScheduleId { get; init; }

    [JsonPropertyName("idempotency_key")]
    public string? IdempotencyKey { get; init; }

    [JsonPropertyName("payload_json")]
    public JsonElement? PayloadJson { get; init; }

    [JsonPropertyName("test_plan_id")]
    public long TestPlanId { get; init; }

    [JsonPropertyName("test_plan_item_id")]
    public long TestPlanItemId { get; init; }

    [JsonPropertyName("test_suite_ids")]
    public IReadOnlyList<long> TestSuiteIds { get; init; } = [];
}

public sealed class BulkDeleteExecutionQueuesRequest
{
    [JsonPropertyName("ids")]
    public IReadOnlyList<long> Ids { get; init; } = [];
}

public sealed class ExecutionQueueResultItemRequest
{
    [JsonPropertyName("test_suite_id")]
    public long TestSuiteId { get; init; }

    [JsonPropertyName("execution_id")]
    public long? ExecutionId { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }
}

public sealed class ExecutionQueueItemsStatusRequest
{
    [JsonPropertyName("results")]
    public IReadOnlyList<ExecutionQueueResultItemRequest> Results { get; init; } = [];

    [JsonPropertyName("queue_run_id")]
    public long? QueueRunId { get; init; }

    [JsonPropertyName("claim_token")]
    public string? ClaimToken { get; init; }

    [JsonPropertyName("attempt_no")]
    public int? AttemptNo { get; init; }
}

public sealed class ExecutionQueueItemLifecycleRequest
{
    [JsonPropertyName("test_suite_id")]
    public long TestSuiteId { get; init; }

    [JsonPropertyName("execution_id")]
    public long? ExecutionId { get; init; }

    [JsonPropertyName("claim_token")]
    public string? ClaimToken { get; init; }

    [JsonPropertyName("attempt_no")]
    public int? AttemptNo { get; init; }

    [JsonPropertyName("queue_run_id")]
    public long? QueueRunId { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed class ExecutionQueueItemFinishRequest
{
    [JsonPropertyName("test_suite_id")]
    public long TestSuiteId { get; init; }

    [JsonPropertyName("execution_id")]
    public long? ExecutionId { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("queue_run_id")]
    public long? QueueRunId { get; init; }

    [JsonPropertyName("claim_token")]
    public string? ClaimToken { get; init; }

    [JsonPropertyName("attempt_no")]
    public int? AttemptNo { get; init; }
}

public sealed class ClaimExecutionQueueRequest
{
    [JsonPropertyName("queue_run_id")]
    public long? QueueRunId { get; init; }
}

public sealed class ExecutionRunnerBootstrapRequest
{
    [JsonPropertyName("runner_id")]
    public string? RunnerId { get; init; }

    [JsonPropertyName("runner_version")]
    public string? RunnerVersion { get; init; }
}

public sealed class UpdateDefectRequest
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("expected")]
    public string? Expected { get; init; }

    [JsonPropertyName("actual")]
    public string? Actual { get; init; }

    [JsonPropertyName("assigned_to")]
    public long? AssignedTo { get; init; }
}

public sealed class CreateDefectRequest
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("expected")]
    public string? Expected { get; init; }

    [JsonPropertyName("actual")]
    public string? Actual { get; init; }

    [JsonPropertyName("assigned_to")]
    public long? AssignedTo { get; init; }

    [JsonPropertyName("test_runner_item_id")]
    public long? TestRunnerItemId { get; init; }
}

public sealed class CreateManualDefectRequest
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("expected")]
    public string? Expected { get; init; }

    [JsonPropertyName("actual")]
    public string? Actual { get; init; }

    [JsonPropertyName("assigned_to")]
    public long? AssignedTo { get; init; }

    [JsonPropertyName("test_runner_item_id")]
    public long? TestRunnerItemId { get; init; }
}

public sealed class DefectAttachmentFileInput
{
    [JsonPropertyName("file_name")]
    public string FileName { get; init; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    [JsonPropertyName("content_type")]
    public string? ContentType { get; init; }

    [JsonPropertyName("file_size")]
    public long FileSize { get; init; }
}

public sealed class UpdateDefectStatusRequest
{
    [JsonPropertyName("status_id")]
    public long StatusId { get; init; }
}

public sealed class ToggleFailedStatusRequest
{
    [JsonPropertyName("comment")]
    public string? Comment { get; init; }
}

public sealed class ToggleTestRunnerFavoriteRequest
{
    [JsonPropertyName("is_favorite")]
    public bool IsFavorite { get; init; }
}

public sealed class EnsureTestComponentDatasetRequest
{
    [JsonPropertyName("test_component_id")]
    public long? TestComponentId { get; init; }

    [JsonPropertyName("component_id")]
    public long? ComponentId { get; init; }

    [JsonPropertyName("project_id")]
    public long? ProjectId { get; init; }

    [JsonPropertyName("scenario")]
    public string? Scenario { get; init; }

    [JsonPropertyName("status")]
    public bool? Status { get; init; }

    [JsonPropertyName("steps")]
    public IReadOnlyList<SaveTestSuiteStepRequest> Steps { get; init; } = [];
}

public sealed class EnsureTestComponentDatasetResponse
{
    [JsonPropertyName("test_component_id")]
    public long TestComponentId { get; init; }

    [JsonPropertyName("dataset")]
    public TestSuiteFullDatasetDto Dataset { get; init; } = new();
}
