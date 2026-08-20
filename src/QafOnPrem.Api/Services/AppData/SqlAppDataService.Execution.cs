using System.Data;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using QafOnPrem.Api.Contracts;

namespace QafOnPrem.Api.Services.AppData;

public sealed partial class SqlAppDataService
{
    public async Task<ExecutionMutationResult<ExecutionDevicePoolDto>> CreateExecutionDevicePoolAsync(ClaimsPrincipal principal, SaveExecutionDevicePoolRequest request, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return ForbiddenResult<ExecutionDevicePoolDto>("Client context missing. Please refresh and sign in again.");
        }

        var name = NormalizeOptionalText(request.Name);
        if (string.IsNullOrWhiteSpace(name))
        {
            return ValidationResult<ExecutionDevicePoolDto>("name", "The name field is required.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = """
            INSERT INTO execution_device_pools (client_id, name, status, created_at, updated_at)
            OUTPUT INSERTED.id
            VALUES (@clientId, @name, @status, SYSUTCDATETIME(), SYSUTCDATETIME());
            """;

        long id;
        await using (var command = CreateCommand(connection, sql))
        {
            command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
            command.Parameters.AddWithValue("@name", name);
            command.Parameters.AddWithValue("@status", NormalizeOptionalText(request.Status) ?? "active");
            id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        }

        var dto = await GetExecutionDevicePoolByIdAsync(connection, context.ClientId.Value, id, cancellationToken);
        return SuccessResult(dto!);
    }

    public async Task<ExecutionMutationResult<ExecutionDevicePoolDto>> UpdateExecutionDevicePoolAsync(ClaimsPrincipal principal, long id, SaveExecutionDevicePoolRequest request, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return ForbiddenResult<ExecutionDevicePoolDto>("Client context missing. Please refresh and sign in again.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var existing = await GetExecutionDevicePoolByIdAsync(connection, context.ClientId.Value, id, cancellationToken);
        if (existing is null)
        {
            return NotFoundResult<ExecutionDevicePoolDto>("Device pool not found.");
        }

        var name = NormalizeOptionalText(request.Name) ?? existing.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            return ValidationResult<ExecutionDevicePoolDto>("name", "The name field is required.");
        }

        const string sql = """
            UPDATE execution_device_pools
            SET name = @name,
                status = @status,
                updated_at = SYSUTCDATETIME()
            WHERE id = @id AND client_id = @clientId;
            """;

        await using (var command = CreateCommand(connection, sql))
        {
            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
            command.Parameters.AddWithValue("@name", name);
            command.Parameters.AddWithValue("@status", NormalizeOptionalText(request.Status) ?? existing.Status ?? "active");
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var dto = await GetExecutionDevicePoolByIdAsync(connection, context.ClientId.Value, id, cancellationToken);
        return SuccessResult(dto!);
    }

    public async Task<ExecutionMutationResult<bool>> DeleteExecutionDevicePoolAsync(ClaimsPrincipal principal, long id, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return ForbiddenResult<bool>("Client context missing. Please refresh and sign in again.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = "DELETE FROM execution_device_pools WHERE id = @id AND client_id = @clientId;";
        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
        var rows = await command.ExecuteNonQueryAsync(cancellationToken);
        return rows == 0 ? NotFoundResult<bool>("Device pool not found.") : SuccessResult(true);
    }

    public async Task<ExecutionMutationResult<ExecutionDeviceDto>> CreateExecutionDeviceAsync(ClaimsPrincipal principal, SaveExecutionDeviceRequest request, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return ForbiddenResult<ExecutionDeviceDto>("Client context missing. Please refresh and sign in again.");
        }

        var name = NormalizeOptionalText(request.Name);
        if (string.IsNullOrWhiteSpace(name))
        {
            return ValidationResult<ExecutionDeviceDto>("name", "The name field is required.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        if (request.PoolId.HasValue && !await ExecutionPoolBelongsToClientAsync(connection, context.ClientId.Value, request.PoolId.Value, cancellationToken))
        {
            return ForbiddenResult<ExecutionDeviceDto>("Device pool does not belong to client.");
        }

        const string sql = """
            INSERT INTO execution_devices
            (
                client_id,
                pool_id,
                name,
                host,
                api_key,
                status,
                health_status,
                runner_version,
                max_concurrency,
                last_seen_at,
                last_health_payload,
                created_at,
                updated_at
            )
            OUTPUT INSERTED.id
            VALUES
            (
                @clientId,
                @poolId,
                @name,
                @host,
                @apiKey,
                @status,
                @healthStatus,
                @runnerVersion,
                @maxConcurrency,
                @lastSeenAt,
                @lastHealthPayload,
                SYSUTCDATETIME(),
                SYSUTCDATETIME()
            );
            """;

        long id;
        await using (var command = CreateCommand(connection, sql))
        {
            command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
            command.Parameters.AddWithValue("@poolId", (object?)request.PoolId ?? DBNull.Value);
            command.Parameters.AddWithValue("@name", name);
            command.Parameters.AddWithValue("@host", (object?)NormalizeOptionalText(request.Host) ?? DBNull.Value);
            command.Parameters.AddWithValue("@apiKey", (object?)NormalizeOptionalText(request.ApiKey) ?? DBNull.Value);
            command.Parameters.AddWithValue("@status", NormalizeOptionalText(request.Status) ?? "idle");
            command.Parameters.AddWithValue("@healthStatus", NormalizeOptionalText(request.HealthStatus) ?? "ready");
            command.Parameters.AddWithValue("@runnerVersion", (object?)NormalizeOptionalText(request.RunnerVersion) ?? DBNull.Value);
            command.Parameters.AddWithValue("@maxConcurrency", Math.Max(1, request.MaxConcurrency ?? 1));
            command.Parameters.AddWithValue("@lastSeenAt", request.LastSeenAt?.UtcDateTime ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@lastHealthPayload", SerializeJsonElement(request.LastHealthPayload) ?? (object)DBNull.Value);
            id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        }

        var dto = await GetExecutionDeviceByIdAsync(connection, context.ClientId.Value, id, cancellationToken);
        return SuccessResult(dto!);
    }

    public async Task<ExecutionMutationResult<ExecutionDeviceDto>> UpdateExecutionDeviceAsync(ClaimsPrincipal principal, long id, SaveExecutionDeviceRequest request, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return ForbiddenResult<ExecutionDeviceDto>("Client context missing. Please refresh and sign in again.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var existing = await GetExecutionDeviceByIdAsync(connection, context.ClientId.Value, id, cancellationToken);
        if (existing is null)
        {
            return NotFoundResult<ExecutionDeviceDto>("Device not found.");
        }

        if (request.PoolId.HasValue && !await ExecutionPoolBelongsToClientAsync(connection, context.ClientId.Value, request.PoolId.Value, cancellationToken))
        {
            return ForbiddenResult<ExecutionDeviceDto>("Device pool does not belong to client.");
        }

        var name = NormalizeOptionalText(request.Name) ?? existing.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            return ValidationResult<ExecutionDeviceDto>("name", "The name field is required.");
        }

        const string sql = """
            UPDATE execution_devices
            SET name = @name,
                pool_id = @poolId,
                host = @host,
                api_key = @apiKey,
                status = @status,
                health_status = @healthStatus,
                runner_version = @runnerVersion,
                max_concurrency = @maxConcurrency,
                last_seen_at = @lastSeenAt,
                last_health_payload = @lastHealthPayload,
                updated_at = SYSUTCDATETIME()
            WHERE id = @id AND client_id = @clientId;
            """;

        await using (var command = CreateCommand(connection, sql))
        {
            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
            command.Parameters.AddWithValue("@name", name);
            command.Parameters.AddWithValue("@poolId", (object?)request.PoolId ?? (object?)existing.PoolId ?? DBNull.Value);
            command.Parameters.AddWithValue("@host", (object?)NormalizeOptionalText(request.Host) ?? existing.Host ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@apiKey", (object?)NormalizeOptionalText(request.ApiKey) ?? existing.ApiKey ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@status", NormalizeOptionalText(request.Status) ?? existing.Status ?? "idle");
            command.Parameters.AddWithValue("@healthStatus", NormalizeOptionalText(request.HealthStatus) ?? existing.HealthStatus ?? "ready");
            command.Parameters.AddWithValue("@runnerVersion", (object?)NormalizeOptionalText(request.RunnerVersion) ?? existing.RunnerVersion ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@maxConcurrency", Math.Max(1, request.MaxConcurrency ?? existing.MaxConcurrency ?? 1));
            command.Parameters.AddWithValue("@lastSeenAt", request.LastSeenAt?.UtcDateTime ?? existing.LastSeenAt?.UtcDateTime ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@lastHealthPayload", SerializeJsonElement(request.LastHealthPayload) ?? SerializeJsonElement(existing.LastHealthPayload) ?? (object)DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var dto = await GetExecutionDeviceByIdAsync(connection, context.ClientId.Value, id, cancellationToken);
        return SuccessResult(dto!);
    }

    public async Task<ExecutionMutationResult<bool>> DeleteExecutionDeviceAsync(ClaimsPrincipal principal, long id, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return ForbiddenResult<bool>("Client context missing. Please refresh and sign in again.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = "DELETE FROM execution_devices WHERE id = @id AND client_id = @clientId;";
        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
        var rows = await command.ExecuteNonQueryAsync(cancellationToken);
        return rows == 0 ? NotFoundResult<bool>("Device not found.") : SuccessResult(true);
    }

    public async Task<ExecutionMutationResult<ExecutionDeviceDto>> CheckExecutionDeviceHealthAsync(ClaimsPrincipal principal, long id, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return ForbiddenResult<ExecutionDeviceDto>("Client context missing. Please refresh and sign in again.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var device = await GetExecutionDeviceByIdAsync(connection, context.ClientId.Value, id, cancellationToken);
        if (device is null)
        {
            return NotFoundResult<ExecutionDeviceDto>("Device not found.");
        }

        if (string.IsNullOrWhiteSpace(device.Host))
        {
            return ValidationResult<ExecutionDeviceDto>("host", "Device host is not configured.");
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            if (!string.IsNullOrWhiteSpace(device.ApiKey))
            {
                client.DefaultRequestHeaders.Add("x-runner-key", device.ApiKey);
            }

            var url = $"{device.Host.TrimEnd('/')}/health";
            using var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                await UpdateExecutionDeviceHealthAsync(connection, context.ClientId.Value, id, "offline", "offline", null, null, cancellationToken);
                var updatedOffline = await GetExecutionDeviceByIdAsync(connection, context.ClientId.Value, id, cancellationToken);
                return new ExecutionMutationResult<ExecutionDeviceDto>
                {
                    Outcome = ExecutionMutationOutcome.Conflict,
                    Data = updatedOffline,
                    ErrorMessage = "Runner is not reachable."
                };
            }

            var payloadText = await response.Content.ReadAsStringAsync(cancellationToken);
            var payload = ParseNullableJsonElement(payloadText);
            var root = payload.HasValue ? payload.Value : default;
            var serverUp = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("server_up", out var serverUpProperty) && serverUpProperty.ValueKind == JsonValueKind.True;
            var screenSelected = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("screen_selected", out var screenProperty) && screenProperty.ValueKind == JsonValueKind.True;
            var runnerVersion = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("runner_version", out var versionProperty)
                ? versionProperty.ToString()
                : device.RunnerVersion;

            var normalizedDeviceStatus = NormalizeOptionalText(device.Status)?.ToLowerInvariant();
            var nextStatus = serverUp
                ? (string.Equals(normalizedDeviceStatus, "offline", StringComparison.OrdinalIgnoreCase)
                    ? "idle"
                    : (normalizedDeviceStatus ?? "idle"))
                : "offline";

            await UpdateExecutionDeviceHealthAsync(
                connection,
                context.ClientId.Value,
                id,
                nextStatus,
                screenSelected ? "ready" : "warning",
                runnerVersion,
                payload,
                cancellationToken);

            var updated = await GetExecutionDeviceByIdAsync(connection, context.ClientId.Value, id, cancellationToken);
            return SuccessResult(updated!);
        }
        catch
        {
            await UpdateExecutionDeviceHealthAsync(connection, context.ClientId.Value, id, "offline", "offline", null, null, cancellationToken);
            var updated = await GetExecutionDeviceByIdAsync(connection, context.ClientId.Value, id, cancellationToken);
            return new ExecutionMutationResult<ExecutionDeviceDto>
            {
                Outcome = ExecutionMutationOutcome.Conflict,
                Data = updated,
                ErrorMessage = "Runner health check failed."
            };
        }
    }

    public async Task<ExecutionMutationResult<ExecutionScheduleDto>> CreateExecutionScheduleAsync(ClaimsPrincipal principal, SaveExecutionScheduleRequest request, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return ForbiddenResult<ExecutionScheduleDto>("Client context missing. Please refresh and sign in again.");
        }

        var name = NormalizeOptionalText(request.Name);
        if (string.IsNullOrWhiteSpace(name))
        {
            return ValidationResult<ExecutionScheduleDto>("name", "The name field is required.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var payloadViolation = await ValidateExecutionSchedulePayloadAsync(connection, context.ClientId.Value, request.PayloadJson, cancellationToken);
        if (payloadViolation is not null)
        {
            return payloadViolation;
        }

        if (request.PoolId.HasValue && !await ExecutionPoolBelongsToClientAsync(connection, context.ClientId.Value, request.PoolId.Value, cancellationToken))
        {
            return ForbiddenResult<ExecutionScheduleDto>("Device pool does not belong to client.");
        }

        var timezone = NormalizeOptionalText(request.Timezone) ?? "UTC";
        var enabled = request.Enabled ?? true;
        var nextRunAt = ResolveNextRunAt(request.Cron, timezone, request.NextRunAt, enabled);
        const string sql = """
            INSERT INTO execution_schedules
            (
                client_id,
                name,
                cron,
                timezone,
                run_mode,
                priority,
                enabled,
                last_run_at,
                next_run_at,
                pool_id,
                payload_json,
                created_by,
                updated_by,
                created_at,
                updated_at
            )
            OUTPUT INSERTED.id
            VALUES
            (
                @clientId,
                @name,
                @cron,
                @timezone,
                @runMode,
                @priority,
                @enabled,
                NULL,
                @nextRunAt,
                @poolId,
                @payloadJson,
                @createdBy,
                @updatedBy,
                SYSUTCDATETIME(),
                SYSUTCDATETIME()
            );
            """;

        long id;
        await using (var command = CreateCommand(connection, sql))
        {
            command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
            command.Parameters.AddWithValue("@name", name);
            command.Parameters.AddWithValue("@cron", (object?)NormalizeOptionalText(request.Cron) ?? DBNull.Value);
            command.Parameters.AddWithValue("@timezone", timezone);
            command.Parameters.AddWithValue("@runMode", NormalizeOptionalText(request.RunMode) ?? "automation");
            command.Parameters.AddWithValue("@priority", NormalizeOptionalText(request.Priority) ?? "normal");
            command.Parameters.AddWithValue("@enabled", enabled);
            command.Parameters.AddWithValue("@nextRunAt", nextRunAt?.UtcDateTime ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@poolId", (object?)request.PoolId ?? DBNull.Value);
            command.Parameters.AddWithValue("@payloadJson", SerializeJsonElement(request.PayloadJson) ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@createdBy", (object?)NormalizeOptionalText(GetUserDisplayName(principal)) ?? DBNull.Value);
            command.Parameters.AddWithValue("@updatedBy", (object?)NormalizeOptionalText(GetUserDisplayName(principal)) ?? DBNull.Value);
            id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        }

        var dto = await GetExecutionScheduleByIdAsync(connection, context.ClientId.Value, id, cancellationToken);
        return SuccessResult(dto!);
    }

    public async Task<ExecutionMutationResult<ExecutionScheduleDto>> UpdateExecutionScheduleAsync(ClaimsPrincipal principal, long id, SaveExecutionScheduleRequest request, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return ForbiddenResult<ExecutionScheduleDto>("Client context missing. Please refresh and sign in again.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var existing = await GetExecutionScheduleRecordAsync(connection, context.ClientId.Value, id, cancellationToken);
        if (existing is not { } existingSchedule)
        {
            return NotFoundResult<ExecutionScheduleDto>("Schedule not found.");
        }

        var payloadJson = request.PayloadJson.HasValue ? request.PayloadJson : existingSchedule.PayloadJson;
        var payloadViolation = await ValidateExecutionSchedulePayloadAsync(connection, context.ClientId.Value, payloadJson, cancellationToken);
        if (payloadViolation is not null)
        {
            return payloadViolation;
        }

        var poolId = request.PoolId.HasValue ? request.PoolId : existingSchedule.PoolId;
        if (poolId.HasValue && !await ExecutionPoolBelongsToClientAsync(connection, context.ClientId.Value, poolId.Value, cancellationToken))
        {
            return ForbiddenResult<ExecutionScheduleDto>("Device pool does not belong to client.");
        }

        var name = NormalizeOptionalText(request.Name) ?? existingSchedule.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            return ValidationResult<ExecutionScheduleDto>("name", "The name field is required.");
        }

        var cron = request.Cron ?? existingSchedule.Cron;
        var timezone = NormalizeOptionalText(request.Timezone) ?? existingSchedule.Timezone ?? "UTC";
        var enabled = request.Enabled ?? existingSchedule.Enabled;
        var nextRunAt = !enabled
            ? null
            : ResolveNextRunAt(cron, timezone, request.NextRunAt, enabled) ?? existingSchedule.NextRunAt;

        const string sql = """
            UPDATE execution_schedules
            SET name = @name,
                cron = @cron,
                timezone = @timezone,
                run_mode = @runMode,
                priority = @priority,
                enabled = @enabled,
                next_run_at = @nextRunAt,
                pool_id = @poolId,
                payload_json = @payloadJson,
                updated_by = @updatedBy,
                updated_at = SYSUTCDATETIME()
            WHERE id = @id AND client_id = @clientId AND deleted_at IS NULL;
            """;

        await using (var command = CreateCommand(connection, sql))
        {
            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
            command.Parameters.AddWithValue("@name", name);
            command.Parameters.AddWithValue("@cron", (object?)NormalizeOptionalText(cron) ?? DBNull.Value);
            command.Parameters.AddWithValue("@timezone", timezone);
            command.Parameters.AddWithValue("@runMode", NormalizeOptionalText(request.RunMode) ?? existingSchedule.RunMode ?? "automation");
            command.Parameters.AddWithValue("@priority", NormalizeOptionalText(request.Priority) ?? existingSchedule.Priority ?? "normal");
            command.Parameters.AddWithValue("@enabled", enabled);
            command.Parameters.AddWithValue("@nextRunAt", nextRunAt?.UtcDateTime ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@poolId", (object?)poolId ?? DBNull.Value);
            command.Parameters.AddWithValue("@payloadJson", SerializeJsonElement(payloadJson) ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@updatedBy", (object?)NormalizeOptionalText(GetUserDisplayName(principal)) ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var dto = await GetExecutionScheduleByIdAsync(connection, context.ClientId.Value, id, cancellationToken);
        return SuccessResult(dto!);
    }

    public async Task<ExecutionMutationResult<bool>> DeleteExecutionScheduleAsync(ClaimsPrincipal principal, long id, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return ForbiddenResult<bool>("Client context missing. Please refresh and sign in again.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = "UPDATE execution_schedules SET deleted_at = SYSUTCDATETIME(), updated_at = SYSUTCDATETIME() WHERE id = @id AND client_id = @clientId AND deleted_at IS NULL;";
        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
        var rows = await command.ExecuteNonQueryAsync(cancellationToken);
        return rows == 0 ? NotFoundResult<bool>("Schedule not found.") : SuccessResult(true);
    }

    public async Task<ExecutionMutationResult<ExecutionQueueDto>> RunExecutionScheduleNowAsync(ClaimsPrincipal principal, long id, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return ForbiddenResult<ExecutionQueueDto>("Client context missing. Please refresh and sign in again.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var schedule = await GetExecutionScheduleRecordAsync(connection, context.ClientId.Value, id, cancellationToken);
        if (schedule is not { } scheduleRecord)
        {
            return NotFoundResult<ExecutionQueueDto>("Schedule not found.");
        }

        var payload = scheduleRecord.PayloadJson.HasValue ? scheduleRecord.PayloadJson.Value : default;
        if (!payload.ValueKind.Equals(JsonValueKind.Object) || !payload.TryGetProperty("items", out var itemsElement) || itemsElement.ValueKind != JsonValueKind.Array || itemsElement.GetArrayLength() == 0)
        {
            return ValidationResult<ExecutionQueueDto>("payload_json", "No queue items are assigned to this schedule.");
        }

        var queueRequest = BuildQueueRequestFromSchedule(scheduleRecord);
        var result = await CreateExecutionQueueAsync(principal, queueRequest, cancellationToken);
        if (result.Outcome != ExecutionMutationOutcome.Success || result.Data is null)
        {
            return result;
        }

        const string updateScheduleSql = "UPDATE execution_schedules SET last_run_at = SYSUTCDATETIME(), updated_at = SYSUTCDATETIME() WHERE id = @id AND client_id = @clientId;";
        await using var updateCommand = CreateCommand(connection, updateScheduleSql);
        updateCommand.Parameters.AddWithValue("@id", id);
        updateCommand.Parameters.AddWithValue("@clientId", context.ClientId.Value);
        await updateCommand.ExecuteNonQueryAsync(cancellationToken);

        return result;
    }

    public async Task<int> ProcessDueExecutionSchedulesAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var processed = 0;
        const string schedulerLockResource = "QAF-OnPrem:execution-schedule-processor";
        const int schedulerLockTimeoutMs = 0;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var lockAcquired = false;
        try
        {
            lockAcquired = await TryAcquireSessionAppLockAsync(connection, schedulerLockResource, schedulerLockTimeoutMs, cancellationToken);
            if (!lockAcquired)
            {
                _logger.LogDebug("Execution schedule processor skipped because another node holds the scheduler lock.");
                return 0;
            }

            const string sql = """
                SELECT id, client_id, name, cron, timezone, run_mode, priority, CAST(ISNULL(enabled, 1) AS bit) AS enabled, next_run_at, pool_id, payload_json
                FROM execution_schedules
                WHERE deleted_at IS NULL
                  AND CAST(ISNULL(enabled, 1) AS bit) = 1
                ORDER BY next_run_at, id;
                """;

            var schedules = new List<ExecutionScheduleRecord>();
            await using (var command = CreateCommand(connection, sql))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    schedules.Add(new ExecutionScheduleRecord(
                        reader.GetInt64(reader.GetOrdinal("id")),
                        GetInt64(reader, "client_id") ?? 0,
                        GetString(reader, "name"),
                        GetString(reader, "cron"),
                        GetString(reader, "timezone"),
                        GetString(reader, "run_mode"),
                        GetString(reader, "priority"),
                        GetBoolean(reader, "enabled") ?? true,
                        GetDateTimeOffset(reader, "next_run_at"),
                        GetInt64(reader, "pool_id"),
                        ParseNullableJsonElement(GetString(reader, "payload_json"))));
                }
            }

            foreach (var schedule in schedules)
            {
                if (!schedule.Enabled || schedule.ClientId <= 0)
                {
                    continue;
                }

                var nextRunAt = schedule.NextRunAt;
                if (!nextRunAt.HasValue)
                {
                    nextRunAt = ResolveNextRunAt(schedule.Cron, schedule.Timezone, null, true, now);
                    if (nextRunAt.HasValue)
                    {
                        await UpdateScheduleTimingAsync(connection, schedule.Id, schedule.ClientId, nextRunAt, schedule.Enabled, now, cancellationToken);
                    }

                    continue;
                }

                if (nextRunAt.Value > now)
                {
                    continue;
                }

                var queueRequest = BuildQueueRequestFromSchedule(schedule);
                if (queueRequest.TestPlanId <= 0 || queueRequest.TestPlanItemId <= 0 || queueRequest.TestSuiteIds.Count == 0)
                {
                    var recomputed = ResolveNextRunAt(schedule.Cron, schedule.Timezone, null, true, now.AddSeconds(1));
                    await UpdateScheduleTimingAsync(connection, schedule.Id, schedule.ClientId, recomputed, schedule.Enabled, null, cancellationToken);
                    continue;
                }

                try
                {
                    var result = await CreateExecutionQueueAsync(BuildSystemPrincipal(schedule.ClientId), queueRequest, cancellationToken);
                    if (result.Outcome != ExecutionMutationOutcome.Success || result.Data is null)
                    {
                        continue;
                    }

                    var runOnce = string.IsNullOrWhiteSpace(schedule.Cron) && schedule.NextRunAt.HasValue;
                    var enabled = !runOnce;
                    var recomputed = runOnce ? null : ResolveNextRunAt(schedule.Cron, schedule.Timezone, null, true, now.AddSeconds(1));
                    await UpdateScheduleTimingAsync(connection, schedule.Id, schedule.ClientId, recomputed, enabled, now, cancellationToken);
                    processed++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Execution schedule processor failed for schedule {ScheduleId}.", schedule.Id);
                }
            }
        }
        finally
        {
            if (lockAcquired)
            {
                await ReleaseSessionAppLockAsync(connection, schedulerLockResource, cancellationToken);
            }
        }

        return processed;
    }

    private async Task<bool> TryAcquireSessionAppLockAsync(SqlConnection connection, string resource, int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        const string sql = """
            DECLARE @result int;
            EXEC @result = sp_getapplock
                @Resource = @resource,
                @LockMode = 'Exclusive',
                @LockOwner = 'Session',
                @LockTimeout = @timeoutMilliseconds;
            SELECT @result;
            """;

        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@resource", resource);
        command.Parameters.AddWithValue("@timeoutMilliseconds", timeoutMilliseconds);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        var result = scalar is null || scalar is DBNull ? int.MinValue : Convert.ToInt32(scalar);
        return result >= 0;
    }

    private async Task ReleaseSessionAppLockAsync(SqlConnection connection, string resource, CancellationToken cancellationToken)
    {
        const string sql = """
            EXEC sp_releaseapplock
                @Resource = @resource,
                @LockOwner = 'Session';
            """;

        try
        {
            await using var command = CreateCommand(connection, sql);
            command.Parameters.AddWithValue("@resource", resource);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to release scheduler application lock {Resource}.", resource);
        }
    }

    public async Task<ExecutionMutationResult<ExecutionQueueDto>> CreateExecutionQueueAsync(ClaimsPrincipal principal, CreateExecutionQueueRequest request, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return ForbiddenResult<ExecutionQueueDto>("Client context missing. Please refresh and sign in again.");
        }

        if (request.TestPlanId <= 0)
        {
            return ValidationResult<ExecutionQueueDto>("test_plan_id", "The test_plan_id field is required.");
        }

        if (request.TestPlanItemId <= 0)
        {
            return ValidationResult<ExecutionQueueDto>("test_plan_item_id", "The test_plan_item_id field is required.");
        }

        if (request.TestSuiteIds.Count == 0)
        {
            return ValidationResult<ExecutionQueueDto>("test_suite_ids", "The test_suite_ids field is required.");
        }

        var requestedSuiteIds = request.TestSuiteIds.Where(idValue => idValue != 0).Distinct().ToArray();
        if (requestedSuiteIds.Length == 0)
        {
            return ValidationResult<ExecutionQueueDto>("test_suite_ids", "The test_suite_ids field is required.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        if (!await ExecutionPlanBelongsToClientAsync(connection, context.ClientId.Value, request.TestPlanId, cancellationToken))
        {
            return ForbiddenResult<ExecutionQueueDto>("Test plan does not belong to client.");
        }

        if (!await ExecutionPlanItemBelongsToClientAsync(connection, context.ClientId.Value, request.TestPlanId, request.TestPlanItemId, cancellationToken))
        {
            return ForbiddenResult<ExecutionQueueDto>("Suite group does not belong to client.");
        }

        await EnsurePointBasedConfigurationStateAsync(connection, request.TestPlanItemId, cancellationToken);

        if (!await ExecutionSuitesBelongToClientByIdentityAsync(connection, context.ClientId.Value, requestedSuiteIds, cancellationToken))
        {
            return ForbiddenResult<ExecutionQueueDto>("One or more test suites do not belong to client.");
        }

        if (request.PoolId.HasValue && !await ExecutionPoolBelongsToClientAsync(connection, context.ClientId.Value, request.PoolId.Value, cancellationToken))
        {
            return ForbiddenResult<ExecutionQueueDto>("Device pool does not belong to client.");
        }

        if (request.ScheduleId.HasValue && !await ExecutionScheduleBelongsToClientAsync(connection, context.ClientId.Value, request.ScheduleId.Value, cancellationToken))
        {
            return ForbiddenResult<ExecutionQueueDto>("Schedule does not belong to client.");
        }

        var suiteTypes = await LoadExecutionSuiteTypesByIdentityAsync(connection, context.ClientId.Value, requestedSuiteIds, cancellationToken);
        var automatedSuiteIds = requestedSuiteIds.Where(idValue => suiteTypes.TryGetValue(idValue, out var type) && type == 2).ToArray();
        var blockedNonAutomatedIds = requestedSuiteIds.Where(idValue => !automatedSuiteIds.Contains(idValue)).ToArray();
        if (automatedSuiteIds.Length == 0)
        {
            return ValidationResult<ExecutionQueueDto>("test_suite_ids", "Only automated tests can be queued for execution.");
        }

        var blockedNonAutomatedNames = await LoadSuiteNamesByIdentityAsync(connection, context.ClientId.Value, blockedNonAutomatedIds, cancellationToken);
        var runTarget = NormalizeExecutionRunTarget(request.RunTarget);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var queueId = runTarget == "local"
                ? await UpsertExecutionLocalQueueAsync(connection, transaction, principal, context.ClientId.Value, request, automatedSuiteIds, blockedNonAutomatedIds, blockedNonAutomatedNames, cancellationToken)
                : await CreateExecutionQueueEntryAsync(connection, transaction, principal, context.ClientId.Value, request, automatedSuiteIds, blockedNonAutomatedIds, blockedNonAutomatedNames, runTarget, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            var dto = await GetExecutionQueueAsync(principal, queueId, cancellationToken);
            return SuccessResult(dto!);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<ExecutionMutationResult<ExecutionQueueDto>> CancelExecutionQueueAsync(ClaimsPrincipal principal, long id, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return ForbiddenResult<ExecutionQueueDto>("Client context missing. Please refresh and sign in again.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        if (!await ExecutionQueueBelongsToClientAsync(connection, context.ClientId.Value, id, cancellationToken))
        {
            return NotFoundResult<ExecutionQueueDto>("Execution queue not found.");
        }

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await ResetExecutionStatusesForQueueIdsAsync(connection, transaction, context.ClientId.Value, [id], activeOnly: true, cancellationToken);

            const string queueSql = "UPDATE execution_queues SET status = 'canceled' WHERE id = @id AND client_id = @clientId;";
            await using (var queueCommand = CreateCommand(connection, queueSql))
            {
                queueCommand.Transaction = transaction;
                queueCommand.Parameters.AddWithValue("@id", id);
                queueCommand.Parameters.AddWithValue("@clientId", context.ClientId.Value);
                await queueCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            const string itemSql = """
                UPDATE execution_queue_items
                SET status = 'canceled',
                    last_status = 'canceled',
                    last_run_at = SYSUTCDATETIME(),
                    claimed_at = NULL,
                    claim_token = NULL,
                    queue_run_id = NULL,
                    updated_at = SYSUTCDATETIME()
                WHERE execution_queue_id = @id AND status IN ('running', 'interrupted', 'queued', 'not_started');
                """;
            await using (var itemCommand = CreateCommand(connection, itemSql))
            {
                itemCommand.Transaction = transaction;
                itemCommand.Parameters.AddWithValue("@id", id);
                await itemCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            var dto = await GetExecutionQueueAsync(principal, id, cancellationToken);
            return SuccessResult(dto!);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<ExecutionMutationResult<ExecutionQueueDto>> RetryExecutionQueueAsync(ClaimsPrincipal principal, long id, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return ForbiddenResult<ExecutionQueueDto>("Client context missing. Please refresh and sign in again.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var queue = await GetExecutionQueueRecordAsync(connection, context.ClientId.Value, id, cancellationToken);
        if (queue is not { } queueRecord)
        {
            return NotFoundResult<ExecutionQueueDto>("Execution queue not found.");
        }

        if (string.Equals(queueRecord.Status, "canceled", StringComparison.OrdinalIgnoreCase))
        {
            return ConflictResult<ExecutionQueueDto>("Queue is canceled/killed.");
        }

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await ResetExecutionStatusesForQueueIdsAsync(connection, transaction, context.ClientId.Value, [id], activeOnly: false, cancellationToken);

            const string queueSql = "UPDATE execution_queues SET status = 'queued', updated_at = SYSUTCDATETIME() WHERE id = @id AND client_id = @clientId;";
            await using (var queueCommand = CreateCommand(connection, queueSql))
            {
                queueCommand.Transaction = transaction;
                queueCommand.Parameters.AddWithValue("@id", id);
                queueCommand.Parameters.AddWithValue("@clientId", context.ClientId.Value);
                await queueCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            const string itemSql = """
                UPDATE execution_queue_items
                SET status = 'not_started',
                    claim_token = NULL,
                    claimed_at = NULL,
                    queue_run_id = NULL,
                    updated_at = SYSUTCDATETIME()
                WHERE execution_queue_id = @id;
                """;
            await using (var itemCommand = CreateCommand(connection, itemSql))
            {
                itemCommand.Transaction = transaction;
                itemCommand.Parameters.AddWithValue("@id", id);
                await itemCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            var dto = await GetExecutionQueueAsync(principal, id, cancellationToken);
            return SuccessResult(dto!);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<ExecutionMutationResult<bool>> BulkDeleteExecutionQueuesAsync(ClaimsPrincipal principal, IReadOnlyList<long> ids, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return ForbiddenResult<bool>("Client context missing. Please refresh and sign in again.");
        }

        var uniqueIds = ids.Where(id => id > 0).Distinct().ToArray();
        if (uniqueIds.Length == 0)
        {
            return ValidationResult<bool>("ids", "The ids field is required.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var parameters = AddIdListParameterValues(uniqueIds, "@id");
        var countSql = $"SELECT COUNT(*) FROM execution_queues WHERE client_id = @clientId AND id IN ({string.Join(", ", parameters.Select(parameter => parameter.ParameterName))});";
        var countParameters = new List<SqlParameter> { new("@clientId", context.ClientId.Value) };
        countParameters.AddRange(parameters);
        var ownedCount = await ExecuteCountAsync(connection, countSql, countParameters, cancellationToken);
        if (ownedCount != uniqueIds.Length)
        {
            return ForbiddenResult<bool>("One or more queue entries do not belong to client.");
        }

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await ResetExecutionStatusesForQueueIdsAsync(connection, transaction, context.ClientId.Value, uniqueIds, activeOnly: true, cancellationToken);

            var deleteSql = $"DELETE FROM execution_queues WHERE client_id = @clientId AND id IN ({string.Join(", ", parameters.Select(parameter => parameter.ParameterName))});";
            await using (var deleteCommand = CreateCommand(connection, deleteSql))
            {
                deleteCommand.Transaction = transaction;
                deleteCommand.Parameters.AddWithValue("@clientId", context.ClientId.Value);
                AddParameters(deleteCommand, parameters);
                await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return SuccessResult(true);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<ExecutionMutationResult<object>> UpdateExecutionQueueItemsStatusAsync(ClaimsPrincipal principal, long id, ExecutionQueueItemsStatusRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Results.Count == 0)
        {
            return ValidationResult<object>("results", "The results field is required.");
        }

        if (string.IsNullOrWhiteSpace(request.ClaimToken))
        {
            return ValidationResult<object>("claim_token", "The claim_token field is required.");
        }

        return await ApplyExecutionQueueItemResultsAsync(principal, id, request.Results, request.ClaimToken!, request.AttemptNo, request.QueueRunId, cancellationToken);
    }

    public async Task<ExecutionMutationResult<ExecutionQueueItemAckDto>> StartExecutionQueueItemAsync(ClaimsPrincipal principal, long id, ExecutionQueueItemLifecycleRequest request, CancellationToken cancellationToken = default)
    {
        var validation = ValidateExecutionQueueLifecycleRequest<ExecutionQueueItemAckDto>(request, requireReason: false);
        if (validation is not null)
        {
            return validation;
        }

        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return ForbiddenResult<ExecutionQueueItemAckDto>("Client context missing. Please refresh and sign in again.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var queue = await GetExecutionQueueRecordAsync(connection, context.ClientId.Value, id, cancellationToken);
        if (queue is not { } queueRecord)
        {
            return NotFoundResult<ExecutionQueueItemAckDto>("Execution queue not found.");
        }

        if (string.Equals(queueRecord.Status, "canceled", StringComparison.OrdinalIgnoreCase))
        {
            return ConflictResult<ExecutionQueueItemAckDto>("Queue is canceled/killed.");
        }

        var executionId = ResolveExecutionIdentity(request.TestSuiteId, request.ExecutionId);
        var item = await ResolveClaimedExecutionQueueItemAsync(connection, id, executionId, request.ClaimToken!, request.AttemptNo, cancellationToken);
        if (item is not { } queueItem)
        {
            return ConflictResult<ExecutionQueueItemAckDto>("Invalid or stale claim token for queue item.");
        }

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            const string itemSql = """
                UPDATE execution_queue_items
                SET status = 'running',
                    last_run_at = SYSUTCDATETIME(),
                    claimed_at = SYSUTCDATETIME(),
                    queue_run_id = @queueRunId,
                    updated_at = SYSUTCDATETIME()
                WHERE id = @itemId;
                """;
            await using (var itemCommand = CreateCommand(connection, itemSql))
            {
                itemCommand.Transaction = transaction;
                itemCommand.Parameters.AddWithValue("@itemId", queueItem.Id);
                itemCommand.Parameters.AddWithValue("@queueRunId", (object?)request.QueueRunId ?? (object?)queueItem.QueueRunId ?? DBNull.Value);
                await itemCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            if (!string.Equals(queueRecord.Status, "running", StringComparison.OrdinalIgnoreCase))
            {
                const string queueSql = "UPDATE execution_queues SET status = 'running', updated_at = SYSUTCDATETIME() WHERE id = @id;";
                await using var queueCommand = CreateCommand(connection, queueSql);
                queueCommand.Transaction = transaction;
                queueCommand.Parameters.AddWithValue("@id", id);
                await queueCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return SuccessResult(new ExecutionQueueItemAckDto
            {
                QueueId = id,
                QueueItemId = queueItem.Id,
                TestSuiteId = request.TestSuiteId,
                ExecutionId = executionId,
                AttemptNo = request.AttemptNo ?? queueItem.Attempts ?? 0
            });
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<ExecutionMutationResult<object>> HeartbeatExecutionQueueItemAsync(ClaimsPrincipal principal, long id, ExecutionQueueItemLifecycleRequest request, CancellationToken cancellationToken = default)
    {
        var validation = ValidateExecutionQueueLifecycleRequest<object>(request, requireReason: false);
        if (validation is not null)
        {
            return validation;
        }

        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return ForbiddenResult<object>("Client context missing. Please refresh and sign in again.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var queue = await GetExecutionQueueRecordAsync(connection, context.ClientId.Value, id, cancellationToken);
        if (queue is not { } queueRecord)
        {
            return NotFoundResult<object>("Execution queue not found.");
        }

        if (string.Equals(queueRecord.Status, "canceled", StringComparison.OrdinalIgnoreCase))
        {
            return ConflictResult<object>("Queue is canceled/killed.");
        }

        var executionId = ResolveExecutionIdentity(request.TestSuiteId, request.ExecutionId);
        var item = await ResolveClaimedExecutionQueueItemAsync(connection, id, executionId, request.ClaimToken!, request.AttemptNo, cancellationToken);
        if (item is not { } queueItem)
        {
            return ConflictResult<object>("Invalid or stale claim token for queue item.");
        }

        const string sql = """
            UPDATE execution_queue_items
            SET last_run_at = SYSUTCDATETIME(),
                claimed_at = SYSUTCDATETIME(),
                queue_run_id = @queueRunId,
                updated_at = SYSUTCDATETIME()
            WHERE id = @itemId;
            """;
        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@itemId", queueItem.Id);
        command.Parameters.AddWithValue("@queueRunId", (object?)request.QueueRunId ?? (object?)queueItem.QueueRunId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return SuccessResult<object>(Array.Empty<object>());
    }

    public async Task<ExecutionMutationResult<object>> InterruptExecutionQueueItemAsync(ClaimsPrincipal principal, long id, ExecutionQueueItemLifecycleRequest request, CancellationToken cancellationToken = default)
    {
        var validation = ValidateExecutionQueueLifecycleRequest<object>(request, requireReason: true);
        if (validation is not null)
        {
            return validation;
        }

        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return ForbiddenResult<object>("Client context missing. Please refresh and sign in again.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var queue = await GetExecutionQueueRecordAsync(connection, context.ClientId.Value, id, cancellationToken);
        if (queue is not { } queueRecord)
        {
            return NotFoundResult<object>("Execution queue not found.");
        }

        if (string.Equals(queueRecord.Status, "canceled", StringComparison.OrdinalIgnoreCase))
        {
            return ConflictResult<object>("Queue is canceled/killed.");
        }

        var executionId = ResolveExecutionIdentity(request.TestSuiteId, request.ExecutionId);
        var item = await ResolveClaimedExecutionQueueItemAsync(connection, id, executionId, request.ClaimToken!, request.AttemptNo, cancellationToken);
        if (item is not { } queueItem)
        {
            return ConflictResult<object>("Invalid or stale claim token for queue item.");
        }

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            const string sql = """
                UPDATE execution_queue_items
                SET status = 'interrupted',
                    last_run_at = SYSUTCDATETIME(),
                    claimed_at = SYSUTCDATETIME(),
                    queue_run_id = @queueRunId,
                    updated_at = SYSUTCDATETIME()
                WHERE id = @itemId;
                """;
            await using (var command = CreateCommand(connection, sql))
            {
                command.Transaction = transaction;
                command.Parameters.AddWithValue("@itemId", queueItem.Id);
                command.Parameters.AddWithValue("@queueRunId", (object?)request.QueueRunId ?? (object?)queueItem.QueueRunId ?? DBNull.Value);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            if (queueItem.TestPlanItemId.HasValue)
            {
                await UpdateExecutionPlanSuiteStatusAsync(connection, transaction, queueItem.TestPlanItemId.Value, executionId, NotStartedStatusId, cancellationToken);
            }

            await RefreshExecutionQueueStatusAsync(connection, transaction, id, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return SuccessResult<object>(Array.Empty<object>());
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<ExecutionMutationResult<object>> FinishExecutionQueueItemAsync(ClaimsPrincipal principal, long id, ExecutionQueueItemFinishRequest request, CancellationToken cancellationToken = default)
    {
        var executionId = ResolveExecutionIdentity(request.TestSuiteId, request.ExecutionId);
        if (executionId == 0)
        {
            return ValidationResult<object>("test_suite_id", "The test_suite_id field is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Status))
        {
            return ValidationResult<object>("status", "The status field is required.");
        }

        if (string.IsNullOrWhiteSpace(request.ClaimToken))
        {
            return ValidationResult<object>("claim_token", "The claim_token field is required.");
        }

        return await ApplyExecutionQueueItemResultsAsync(
            principal,
            id,
            [new ExecutionQueueResultItemRequest { TestSuiteId = request.TestSuiteId, ExecutionId = executionId, Status = request.Status }],
            request.ClaimToken!,
            request.AttemptNo,
            request.QueueRunId,
            cancellationToken);
    }

    public async Task<ExecutionMutationResult<ExecutionQueueClaimResponseDto?>> ClaimLocalExecutionQueueAsync(ClaimsPrincipal principal, ClaimExecutionQueueRequest request, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return ForbiddenResult<ExecutionQueueClaimResponseDto?>("Unauthorized");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            const string inFlightSql = """
                SELECT TOP 1 qi.id, qi.execution_queue_id, qi.test_suite_id, qi.attempts
                FROM execution_queue_items qi
                INNER JOIN execution_queues q ON q.id = qi.execution_queue_id
                WHERE q.client_id = @clientId
                  AND q.run_target = 'local'
                  AND q.status IN ('queued', 'running')
                  AND qi.status IN ('running', 'interrupted')
                  AND qi.claim_token IS NOT NULL
                ORDER BY qi.claimed_at, qi.id;
                """;

            await using (var inFlightCommand = CreateCommand(connection, inFlightSql))
            {
                inFlightCommand.Transaction = transaction;
                inFlightCommand.Parameters.AddWithValue("@clientId", context.ClientId.Value);
                await using var inFlightReader = await inFlightCommand.ExecuteReaderAsync(cancellationToken);
                if (await inFlightReader.ReadAsync(cancellationToken))
                {
                    var blockedExecutionId = GetInt64(inFlightReader, "test_suite_id") ?? 0;
                    var blockedBaseTestSuiteId = await ResolveBaseTestSuiteIdForExecutionAsync(connection, context.ClientId.Value, blockedExecutionId, cancellationToken, transaction);
                    var blocked = new ExecutionQueueClaimResponseDto
                    {
                        BlockedBy = new ExecutionQueueBlockedDto
                        {
                            QueueId = GetInt64(inFlightReader, "execution_queue_id") ?? 0,
                            QueueItemId = GetInt64(inFlightReader, "id") ?? 0,
                            TestSuiteId = blockedExecutionId,
                            ExecutionId = blockedExecutionId,
                            BaseTestSuiteId = blockedBaseTestSuiteId,
                            AttemptNo = GetInt32(inFlightReader, "attempts") ?? 0
                        }
                    };
                    await transaction.CommitAsync(cancellationToken);
                    return SuccessResult<ExecutionQueueClaimResponseDto?>(blocked);
                }
            }

            const string itemSql = """
                SELECT TOP 1
                    qi.id,
                    qi.execution_queue_id,
                    qi.test_suite_id,
                    qi.test_suite_name,
                    qi.test_plan_id,
                    qi.test_plan_item_id,
                    qi.queue_run_id,
                    qi.attempts,
                    q.priority,
                    q.status AS queue_status,
                    q.created_at
                FROM execution_queue_items qi
                INNER JOIN execution_queues q ON q.id = qi.execution_queue_id
                WHERE q.client_id = @clientId
                  AND q.run_target = 'local'
                  AND q.status IN ('queued', 'running')
                  AND qi.status IN ('not_started', 'queued')
                ORDER BY CASE q.priority WHEN 'high' THEN 0 WHEN 'normal' THEN 1 ELSE 2 END,
                         q.created_at,
                         qi.id;
                """;

            ExecutionQueueItemDto? item = null;
            long? queueId = null;
            await using (var itemCommand = CreateCommand(connection, itemSql))
            {
                itemCommand.Transaction = transaction;
                itemCommand.Parameters.AddWithValue("@clientId", context.ClientId.Value);
                await using var reader = await itemCommand.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    queueId = GetInt64(reader, "execution_queue_id");
                    item = new ExecutionQueueItemDto
                    {
                        Id = GetInt64(reader, "id") ?? 0,
                        Status = "running",
                        TestSuiteId = GetInt64(reader, "test_suite_id"),
                        ExecutionId = GetInt64(reader, "test_suite_id"),
                        TestSuiteName = GetString(reader, "test_suite_name"),
                        TestPlanId = GetInt64(reader, "test_plan_id"),
                        TestPlanItemId = GetInt64(reader, "test_plan_item_id"),
                        QueueRunId = GetInt64(reader, "queue_run_id"),
                        Attempts = GetInt32(reader, "attempts")
                    };
                }
            }

            if (item is null || !queueId.HasValue)
            {
                await transaction.CommitAsync(cancellationToken);
                return SuccessResult<ExecutionQueueClaimResponseDto?>(null);
            }

            var nextAttempt = (item.Attempts ?? 0) + 1;
            var claimToken = Guid.NewGuid().ToString();
            var queueRunId = request.QueueRunId ?? ((item.Id * 1000) + nextAttempt);
            const string updateItemSql = """
                UPDATE execution_queue_items
                SET status = 'running',
                    last_run_at = SYSUTCDATETIME(),
                    claimed_at = SYSUTCDATETIME(),
                    claim_token = @claimToken,
                    attempts = @attempts,
                    queue_run_id = @queueRunId,
                    updated_at = SYSUTCDATETIME()
                WHERE id = @id;
                """;

            await using (var updateItemCommand = CreateCommand(connection, updateItemSql))
            {
                updateItemCommand.Transaction = transaction;
                updateItemCommand.Parameters.AddWithValue("@id", item.Id);
                updateItemCommand.Parameters.AddWithValue("@claimToken", claimToken);
                updateItemCommand.Parameters.AddWithValue("@attempts", nextAttempt);
                updateItemCommand.Parameters.AddWithValue("@queueRunId", queueRunId);
                await updateItemCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            const string updateQueueSql = "UPDATE execution_queues SET status = 'running', updated_at = SYSUTCDATETIME() WHERE id = @id AND status <> 'running';";
            await using (var updateQueueCommand = CreateCommand(connection, updateQueueSql))
            {
                updateQueueCommand.Transaction = transaction;
                updateQueueCommand.Parameters.AddWithValue("@id", queueId.Value);
                await updateQueueCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            var queue = await GetExecutionQueueAsync(principal, queueId.Value, cancellationToken);
            var claimedItem = queue?.Items.FirstOrDefault(queueItem => queueItem.Id == item.Id) ?? new ExecutionQueueItemDto
            {
                Id = item.Id,
                Status = "running",
                QueueRunId = queueRunId,
                Attempts = nextAttempt,
                TestSuiteId = item.TestSuiteId,
                ExecutionId = item.ExecutionId,
                BaseTestSuiteId = item.BaseTestSuiteId,
                TestSuiteName = item.TestSuiteName,
                TestPlanId = item.TestPlanId,
                TestPlanItemId = item.TestPlanItemId
            };

            return SuccessResult<ExecutionQueueClaimResponseDto?>(new ExecutionQueueClaimResponseDto
            {
                ClaimToken = claimToken,
                Queue = queue,
                Item = claimedItem
            });
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<ExecutionMutationResult<object>> ApplyExecutionQueueItemResultsAsync(ClaimsPrincipal principal, long id, IReadOnlyList<ExecutionQueueResultItemRequest> results, string claimToken, int? attemptNo, long? queueRunId, CancellationToken cancellationToken)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return ForbiddenResult<object>("Client context missing. Please refresh and sign in again.");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var queue = await GetExecutionQueueRecordAsync(connection, context.ClientId.Value, id, cancellationToken);
        if (queue is not { } queueRecord)
        {
            return NotFoundResult<object>("Execution queue not found.");
        }

        if (string.Equals(queueRecord.Status, "canceled", StringComparison.OrdinalIgnoreCase))
        {
            return ConflictResult<object>("Queue is canceled/killed.");
        }

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var result in results)
            {
                var status = NormalizeExecutionItemStatus(result.Status);
                var executionId = ResolveExecutionIdentity(result.TestSuiteId, result.ExecutionId);
                if (executionId == 0 || string.IsNullOrWhiteSpace(status))
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return ValidationResult<object>("results", "Invalid queue item status payload.");
                }

                var claimedItem = await ResolveClaimedExecutionQueueItemAsync(connection, id, executionId, claimToken, attemptNo, cancellationToken, transaction);
                if (claimedItem is not { } resolvedItem)
                {
                    var existing = await GetExecutionQueueItemBySuiteAsync(connection, id, executionId, cancellationToken, transaction);
                    var duplicate = existing is { } existingItem
                        && string.IsNullOrWhiteSpace(existingItem.ClaimToken)
                        && string.Equals(existingItem.Status, status, StringComparison.OrdinalIgnoreCase)
                        && (attemptNo is null || existingItem.Attempts == attemptNo);
                    if (!duplicate)
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        return ConflictResult<object>("Invalid or stale claim token for queue item.");
                    }

                    continue;
                }

                const string updateSql = """
                    UPDATE execution_queue_items
                    SET status = @status,
                        last_status = @lastStatus,
                        last_run_at = SYSUTCDATETIME(),
                        queue_run_id = @queueRunId,
                        claimed_at = NULL,
                        claim_token = NULL,
                        updated_at = SYSUTCDATETIME()
                    WHERE id = @id;
                    """;
                await using (var command = CreateCommand(connection, updateSql))
                {
                    command.Transaction = transaction;
                    command.Parameters.AddWithValue("@id", resolvedItem.Id);
                    command.Parameters.AddWithValue("@status", status);
                    command.Parameters.AddWithValue("@lastStatus", (object?)resolvedItem.LastStatus ?? DBNull.Value);
                    command.Parameters.AddWithValue("@queueRunId", (object?)queueRunId ?? (object?)resolvedItem.QueueRunId ?? DBNull.Value);
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }

                if (resolvedItem.TestPlanItemId.HasValue)
                {
                    await UpdateExecutionPlanSuiteStatusAsync(connection, transaction, resolvedItem.TestPlanItemId.Value, executionId, MapExecutionItemStatusId(status), cancellationToken);
                }
            }

            await RefreshExecutionQueueStatusAsync(connection, transaction, id, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return SuccessResult<object>(Array.Empty<object>());
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static ExecutionMutationResult<T> SuccessResult<T>(T data) => new() { Outcome = ExecutionMutationOutcome.Success, Data = data };

    private static ExecutionMutationResult<T> ValidationResult<T>(string field, string message) => new() { Outcome = ExecutionMutationOutcome.ValidationFailed, ErrorField = field, ErrorMessage = message };

    private static ExecutionMutationResult<T> NotFoundResult<T>(string message) => new() { Outcome = ExecutionMutationOutcome.NotFound, ErrorMessage = message };

    private static ExecutionMutationResult<T> ForbiddenResult<T>(string message) => new() { Outcome = ExecutionMutationOutcome.Forbidden, ErrorMessage = message };

    private static ExecutionMutationResult<T> ConflictResult<T>(string message) => new() { Outcome = ExecutionMutationOutcome.Conflict, ErrorMessage = message };

    private static ExecutionMutationResult<T>? ValidateExecutionQueueLifecycleRequest<T>(ExecutionQueueItemLifecycleRequest request, bool requireReason)
    {
        if (ResolveExecutionIdentity(request.TestSuiteId, request.ExecutionId) == 0)
        {
            return ValidationResult<T>("test_suite_id", "The test_suite_id field is required.");
        }

        if (string.IsNullOrWhiteSpace(request.ClaimToken))
        {
            return ValidationResult<T>("claim_token", "The claim_token field is required.");
        }

        if (requireReason && string.IsNullOrWhiteSpace(request.Reason))
        {
            return ValidationResult<T>("reason", "The reason field is required.");
        }

        return null;
    }

    private async Task<ExecutionMutationResult<ExecutionScheduleDto>?> ValidateExecutionSchedulePayloadAsync(SqlConnection connection, long clientId, JsonElement? payloadJson, CancellationToken cancellationToken)
    {
        if (!payloadJson.HasValue || payloadJson.Value.ValueKind == JsonValueKind.Null || payloadJson.Value.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        if (payloadJson.Value.ValueKind != JsonValueKind.Object)
        {
            return new ExecutionMutationResult<ExecutionScheduleDto>
            {
                Outcome = ExecutionMutationOutcome.ValidationFailed,
                ErrorField = "payload_json",
                ErrorMessage = "The payload_json field must be an object."
            };
        }

        if (!payloadJson.Value.TryGetProperty("items", out var itemsElement))
        {
            return null;
        }

        if (itemsElement.ValueKind != JsonValueKind.Array)
        {
            return new ExecutionMutationResult<ExecutionScheduleDto>
            {
                Outcome = ExecutionMutationOutcome.ValidationFailed,
                ErrorField = "payload_json.items",
                ErrorMessage = "payload_json.items must be an array."
            };
        }

        var allSuiteIds = new List<long>();
        foreach (var item in itemsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                return new ExecutionMutationResult<ExecutionScheduleDto>
                {
                    Outcome = ExecutionMutationOutcome.ValidationFailed,
                    ErrorField = "payload_json.items",
                    ErrorMessage = "Each payload_json.items entry must be an object."
                };
            }

            var testPlanId = item.TryGetProperty("test_plan_id", out var planProperty) && planProperty.ValueKind != JsonValueKind.Null ? planProperty.GetInt64() : (long?)null;
            var testPlanItemId = item.TryGetProperty("test_plan_item_id", out var planItemProperty) && planItemProperty.ValueKind != JsonValueKind.Null ? planItemProperty.GetInt64() : (long?)null;
            if (testPlanItemId.HasValue && !testPlanId.HasValue)
            {
                return new ExecutionMutationResult<ExecutionScheduleDto>
                {
                    Outcome = ExecutionMutationOutcome.ValidationFailed,
                    ErrorField = "payload_json.items.test_plan_id",
                    ErrorMessage = "test_plan_id is required when test_plan_item_id is provided."
                };
            }

            if (testPlanId.HasValue && !await ExecutionPlanBelongsToClientAsync(connection, clientId, testPlanId.Value, cancellationToken))
            {
                return ForbiddenResult<ExecutionScheduleDto>("Test plan does not belong to client.");
            }

            if (testPlanItemId.HasValue && !await ExecutionPlanItemBelongsToClientAsync(connection, clientId, testPlanId!.Value, testPlanItemId.Value, cancellationToken))
            {
                return ForbiddenResult<ExecutionScheduleDto>("Suite group does not belong to client.");
            }

            if (!item.TryGetProperty("test_suite_ids", out var suiteIdsElement) || suiteIdsElement.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            if (suiteIdsElement.ValueKind != JsonValueKind.Array)
            {
                return new ExecutionMutationResult<ExecutionScheduleDto>
                {
                    Outcome = ExecutionMutationOutcome.ValidationFailed,
                    ErrorField = "payload_json.items.test_suite_ids",
                    ErrorMessage = "test_suite_ids must be an array."
                };
            }

            foreach (var suiteIdValue in suiteIdsElement.EnumerateArray())
            {
                if (suiteIdValue.ValueKind != JsonValueKind.Number || !suiteIdValue.TryGetInt64(out var suiteId))
                {
                    return new ExecutionMutationResult<ExecutionScheduleDto>
                    {
                        Outcome = ExecutionMutationOutcome.ValidationFailed,
                        ErrorField = "payload_json.items.test_suite_ids",
                        ErrorMessage = "test_suite_ids must contain integers only."
                    };
                }

                allSuiteIds.Add(suiteId);
            }
        }

        var distinctSuiteIds = allSuiteIds.Distinct().ToArray();
        if (distinctSuiteIds.Length > 0)
        {
            await EnsurePointBasedConfigurationStateAsync(connection, null, cancellationToken);
            if (!await ExecutionSuitesBelongToClientByIdentityAsync(connection, clientId, distinctSuiteIds, cancellationToken))
            {
                return ForbiddenResult<ExecutionScheduleDto>("One or more test suites do not belong to client.");
            }
        }

        return null;
    }

    private async Task<long> CreateExecutionQueueEntryAsync(SqlConnection connection, SqlTransaction transaction, ClaimsPrincipal principal, long clientId, CreateExecutionQueueRequest request, IReadOnlyList<long> suiteIds, IReadOnlyList<long> blockedNonAutomatedIds, IReadOnlyDictionary<long, string> blockedNonAutomatedNames, string runTarget, CancellationToken cancellationToken)
    {
        var payloadJson = BuildExecutionQueuePayload(request, suiteIds, blockedNonAutomatedIds, blockedNonAutomatedNames, blockedAlreadyQueuedIds: [], addedCount: suiteIds.Count);
        const string sql = """
            INSERT INTO execution_queues
            (
                client_id,
                queue_code,
                status,
                priority,
                source,
                run_mode,
                run_target,
                pool_id,
                schedule_id,
                payload_json,
                created_by,
                test_plan_id,
                test_plan_item_id,
                idempotency_key,
                created_at,
                updated_at
            )
            OUTPUT INSERTED.id
            VALUES
            (
                @clientId,
                NULL,
                'queued',
                @priority,
                @source,
                @runMode,
                @runTarget,
                @poolId,
                @scheduleId,
                @payloadJson,
                @createdBy,
                @testPlanId,
                @testPlanItemId,
                @idempotencyKey,
                SYSUTCDATETIME(),
                SYSUTCDATETIME()
            );
            """;

        long queueId;
        await using (var command = CreateCommand(connection, sql))
        {
            command.Transaction = transaction;
            command.Parameters.AddWithValue("@clientId", clientId);
            command.Parameters.AddWithValue("@priority", NormalizeOptionalText(request.Priority) ?? "normal");
            command.Parameters.AddWithValue("@source", NormalizeOptionalText(request.Source) ?? "ui");
            command.Parameters.AddWithValue("@runMode", NormalizeOptionalText(request.RunMode) ?? "automation");
            command.Parameters.AddWithValue("@runTarget", runTarget);
            command.Parameters.AddWithValue("@poolId", (object?)request.PoolId ?? DBNull.Value);
            command.Parameters.AddWithValue("@scheduleId", (object?)request.ScheduleId ?? DBNull.Value);
            command.Parameters.AddWithValue("@payloadJson", payloadJson);
            command.Parameters.AddWithValue("@createdBy", (object?)NormalizeOptionalText(GetUserDisplayName(principal)) ?? DBNull.Value);
            command.Parameters.AddWithValue("@testPlanId", request.TestPlanId);
            command.Parameters.AddWithValue("@testPlanItemId", request.TestPlanItemId);
            command.Parameters.AddWithValue("@idempotencyKey", (object?)NormalizeOptionalText(request.IdempotencyKey) ?? DBNull.Value);
            queueId = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        }

        const string queueCodeSql = "UPDATE execution_queues SET queue_code = @queueCode WHERE id = @id;";
        await using (var queueCodeCommand = CreateCommand(connection, queueCodeSql))
        {
            queueCodeCommand.Transaction = transaction;
            queueCodeCommand.Parameters.AddWithValue("@queueCode", $"Q-{1000 + queueId}");
            queueCodeCommand.Parameters.AddWithValue("@id", queueId);
            await queueCodeCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertExecutionQueueItemsAsync(connection, transaction, clientId, queueId, request.TestPlanId, request.TestPlanItemId, suiteIds, cancellationToken);
        await SeedSchedulePayloadIfNeededAsync(connection, transaction, request, suiteIds, runTarget, cancellationToken);
        return queueId;
    }

    private async Task<long> UpsertExecutionLocalQueueAsync(SqlConnection connection, SqlTransaction transaction, ClaimsPrincipal principal, long clientId, CreateExecutionQueueRequest request, IReadOnlyList<long> suiteIds, IReadOnlyList<long> blockedNonAutomatedIds, IReadOnlyDictionary<long, string> blockedNonAutomatedNames, CancellationToken cancellationToken)
    {
        var blockedAlreadyQueuedIds = await LoadExecutionAlreadyQueuedSuiteIdsAsync(connection, transaction, clientId, request.TestPlanId, request.TestPlanItemId, suiteIds, cancellationToken);
        var suiteIdsToAdd = suiteIds.Where(id => !blockedAlreadyQueuedIds.Contains(id)).ToArray();
        var queue = await GetReusableLocalExecutionQueueAsync(connection, transaction, clientId, request.TestPlanId, request.TestPlanItemId, cancellationToken);
        if (queue is not { } reusableQueue)
        {
            var queueId = await CreateExecutionQueueEntryAsync(connection, transaction, principal, clientId, request, suiteIdsToAdd.Length > 0 ? suiteIdsToAdd : suiteIds.ToArray(), blockedNonAutomatedIds, blockedNonAutomatedNames, "local", cancellationToken);
            var payloadJson = BuildExecutionQueuePayload(request, suiteIdsToAdd.Length > 0 ? suiteIdsToAdd : suiteIds, blockedNonAutomatedIds, blockedNonAutomatedNames, blockedAlreadyQueuedIds, suiteIdsToAdd.Length > 0 ? suiteIdsToAdd.Length : suiteIds.Count);
            const string payloadSql = "UPDATE execution_queues SET payload_json = @payloadJson, updated_at = SYSUTCDATETIME() WHERE id = @id;";
            await using var command = CreateCommand(connection, payloadSql);
            command.Transaction = transaction;
            command.Parameters.AddWithValue("@id", queueId);
            command.Parameters.AddWithValue("@payloadJson", payloadJson);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return queueId;
        }

        if (suiteIdsToAdd.Length > 0)
        {
            await InsertExecutionQueueItemsAsync(connection, transaction, clientId, reusableQueue.Id, request.TestPlanId, request.TestPlanItemId, suiteIdsToAdd, cancellationToken);
        }

        const string updateSql = """
            UPDATE execution_queues
            SET priority = @priority,
                source = @source,
                run_mode = @runMode,
                pool_id = @poolId,
                idempotency_key = @idempotencyKey,
                payload_json = @payloadJson,
                updated_at = SYSUTCDATETIME()
            WHERE id = @id;
            """;
        await using var updateCommand = CreateCommand(connection, updateSql);
        updateCommand.Transaction = transaction;
        updateCommand.Parameters.AddWithValue("@id", reusableQueue.Id);
        updateCommand.Parameters.AddWithValue("@priority", NormalizeOptionalText(request.Priority) ?? reusableQueue.Priority ?? "normal");
        updateCommand.Parameters.AddWithValue("@source", NormalizeOptionalText(request.Source) ?? reusableQueue.Source ?? "ui");
        updateCommand.Parameters.AddWithValue("@runMode", NormalizeOptionalText(request.RunMode) ?? reusableQueue.RunMode ?? "automation");
        updateCommand.Parameters.AddWithValue("@poolId", (object?)request.PoolId ?? (object?)reusableQueue.PoolId ?? DBNull.Value);
        updateCommand.Parameters.AddWithValue("@idempotencyKey", (object?)NormalizeOptionalText(request.IdempotencyKey) ?? reusableQueue.IdempotencyKey ?? (object)DBNull.Value);
        updateCommand.Parameters.AddWithValue("@payloadJson", BuildExecutionQueuePayload(request, suiteIdsToAdd, blockedNonAutomatedIds, blockedNonAutomatedNames, blockedAlreadyQueuedIds, suiteIdsToAdd.Length));
        await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        return reusableQueue.Id;
    }

    private async Task InsertExecutionQueueItemsAsync(SqlConnection connection, SqlTransaction transaction, long clientId, long queueId, long testPlanId, long testPlanItemId, IReadOnlyList<long> suiteIds, CancellationToken cancellationToken)
    {
        if (suiteIds.Count == 0)
        {
            return;
        }

        var suiteNames = await LoadSuiteNamesByIdentityAsync(connection, clientId, suiteIds, cancellationToken, transaction);
        const string sql = """
            INSERT INTO execution_queue_items
            (
                client_id,
                execution_queue_id,
                test_suite_id,
                test_suite_name,
                test_plan_id,
                test_plan_item_id,
                attempts,
                status,
                created_at,
                updated_at
            )
            VALUES
            (
                @clientId,
                @queueId,
                @testSuiteId,
                @testSuiteName,
                @testPlanId,
                @testPlanItemId,
                0,
                'not_started',
                SYSUTCDATETIME(),
                SYSUTCDATETIME()
            );
            """;

        foreach (var suiteId in suiteIds)
        {
            await using var command = CreateCommand(connection, sql);
            command.Transaction = transaction;
            command.Parameters.AddWithValue("@clientId", clientId);
            command.Parameters.AddWithValue("@queueId", queueId);
            command.Parameters.AddWithValue("@testSuiteId", suiteId);
            command.Parameters.AddWithValue("@testSuiteName", (object?)suiteNames.GetValueOrDefault(suiteId) ?? DBNull.Value);
            command.Parameters.AddWithValue("@testPlanId", testPlanId);
            command.Parameters.AddWithValue("@testPlanItemId", testPlanItemId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task SeedSchedulePayloadIfNeededAsync(SqlConnection connection, SqlTransaction transaction, CreateExecutionQueueRequest request, IReadOnlyList<long> suiteIds, string runTarget, CancellationToken cancellationToken)
    {
        if (!request.ScheduleId.HasValue)
        {
            return;
        }

        const string selectSql = "SELECT payload_json FROM execution_schedules WHERE id = @id AND deleted_at IS NULL;";
        string? existingPayload;
        await using (var selectCommand = CreateCommand(connection, selectSql))
        {
            selectCommand.Transaction = transaction;
            selectCommand.Parameters.AddWithValue("@id", request.ScheduleId.Value);
            existingPayload = await selectCommand.ExecuteScalarAsync(cancellationToken) as string;
        }

        if (!string.IsNullOrWhiteSpace(existingPayload))
        {
            return;
        }

        var payloadNode = BuildExecutionBasePayloadNode(request, suiteIds, runTarget);
        const string updateSql = "UPDATE execution_schedules SET payload_json = @payloadJson, updated_at = SYSUTCDATETIME() WHERE id = @id;";
        await using var updateCommand = CreateCommand(connection, updateSql);
        updateCommand.Transaction = transaction;
        updateCommand.Parameters.AddWithValue("@id", request.ScheduleId.Value);
        updateCommand.Parameters.AddWithValue("@payloadJson", payloadNode.ToJsonString());
        await updateCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private string BuildExecutionQueuePayload(CreateExecutionQueueRequest request, IReadOnlyList<long> suiteIds, IReadOnlyList<long> blockedNonAutomatedIds, IReadOnlyDictionary<long, string> blockedNonAutomatedNames, IReadOnlyList<long> blockedAlreadyQueuedIds, int addedCount)
    {
        var payloadNode = BuildExecutionBasePayloadNode(request, suiteIds, NormalizeExecutionRunTarget(request.RunTarget));
        payloadNode["enqueue_summary"] = new JsonObject
        {
            ["requested"] = request.TestSuiteIds.Distinct().Count(),
            ["added"] = addedCount,
            ["blocked"] = blockedAlreadyQueuedIds.Count + blockedNonAutomatedIds.Count,
            ["blocked_already_queued"] = blockedAlreadyQueuedIds.Count,
            ["blocked_already_queued_test_suite_ids"] = new JsonArray(blockedAlreadyQueuedIds.Select(id => JsonValue.Create(id)).ToArray()),
            ["blocked_non_automated"] = blockedNonAutomatedIds.Count,
            ["blocked_non_automated_test_suite_ids"] = new JsonArray(blockedNonAutomatedIds.Select(id => JsonValue.Create(id)).ToArray()),
            ["blocked_non_automated_test_suite_names"] = new JsonArray(blockedNonAutomatedIds.Select(id => JsonValue.Create(blockedNonAutomatedNames.GetValueOrDefault(id))).ToArray())
        };

        return payloadNode.ToJsonString();
    }

    private JsonObject BuildExecutionBasePayloadNode(CreateExecutionQueueRequest request, IReadOnlyList<long> suiteIds, string runTarget)
    {
        JsonObject payloadNode;
        if (request.PayloadJson.HasValue && request.PayloadJson.Value.ValueKind == JsonValueKind.Object)
        {
            payloadNode = JsonNode.Parse(request.PayloadJson.Value.GetRawText()) as JsonObject ?? new JsonObject();
        }
        else
        {
            payloadNode = new JsonObject();
        }

        payloadNode["items"] = new JsonArray(new JsonObject
        {
            ["test_plan_id"] = request.TestPlanId,
            ["test_plan_item_id"] = request.TestPlanItemId,
            ["test_suite_ids"] = new JsonArray(suiteIds.Select(id => JsonValue.Create(id)).ToArray())
        });
        payloadNode["pool_id"] = request.PoolId.HasValue ? JsonValue.Create(request.PoolId.Value) : null;
        payloadNode["run_mode"] = NormalizeOptionalText(request.RunMode) ?? "automation";
        payloadNode["run_target"] = runTarget;
        payloadNode["priority"] = NormalizeOptionalText(request.Priority) ?? "normal";
        payloadNode["source"] = NormalizeOptionalText(request.Source) ?? (request.ScheduleId.HasValue ? "schedule" : "ui");
        return payloadNode;
    }

    private static string NormalizeExecutionRunTarget(string? runTarget)
    {
        return string.Equals(NormalizeOptionalText(runTarget), "cloud", StringComparison.OrdinalIgnoreCase) ? "cloud" : "local";
    }

    private static string NormalizeExecutionItemStatus(string? status)
    {
        var normalized = NormalizeOptionalText(status)?.ToLowerInvariant();
        return normalized switch
        {
            "passed" => "passed",
            "failed" => "failed",
            "glitch" => "glitch",
            "running" => "running",
            "interrupted" => "interrupted",
            "queued" => "queued",
            "not_started" => "not_started",
            "not-started" => "not_started",
            "canceled" => "canceled",
            "cancelled" => "canceled",
            _ => string.Empty
        };
    }

    private static int MapExecutionItemStatusId(string status)
    {
        return status switch
        {
            "passed" => PassedStatusId,
            "failed" => FailedStatusId,
            "glitch" => GlitchStatusId,
            "running" => InProgressStatusId,
            "interrupted" => InProgressStatusId,
            _ => NotStartedStatusId
        };
    }

    private static string? SerializeJsonElement(JsonElement? element)
    {
        return element.HasValue && element.Value.ValueKind != JsonValueKind.Null && element.Value.ValueKind != JsonValueKind.Undefined
            ? element.Value.GetRawText()
            : null;
    }

    private async Task<bool> ExecutionPoolBelongsToClientAsync(SqlConnection connection, long clientId, long poolId, CancellationToken cancellationToken)
    {
        const string sql = "SELECT COUNT(*) FROM execution_device_pools WHERE id = @id AND client_id = @clientId;";
        return await ExecuteCountAsync(connection, sql, [new SqlParameter("@id", poolId), new SqlParameter("@clientId", clientId)], cancellationToken) > 0;
    }

    private async Task<bool> ExecutionScheduleBelongsToClientAsync(SqlConnection connection, long clientId, long scheduleId, CancellationToken cancellationToken)
    {
        const string sql = "SELECT COUNT(*) FROM execution_schedules WHERE id = @id AND client_id = @clientId AND deleted_at IS NULL;";
        return await ExecuteCountAsync(connection, sql, [new SqlParameter("@id", scheduleId), new SqlParameter("@clientId", clientId)], cancellationToken) > 0;
    }

    private async Task<bool> ExecutionPlanBelongsToClientAsync(SqlConnection connection, long clientId, long testPlanId, CancellationToken cancellationToken)
    {
        const string sql = "SELECT COUNT(*) FROM test_plans WHERE id = @id AND client_id = @clientId;";
        return await ExecuteCountAsync(connection, sql, [new SqlParameter("@id", testPlanId), new SqlParameter("@clientId", clientId)], cancellationToken) > 0;
    }

    private async Task<bool> ExecutionPlanItemBelongsToClientAsync(SqlConnection connection, long clientId, long testPlanId, long testPlanItemId, CancellationToken cancellationToken)
    {
        const string sql = "SELECT COUNT(*) FROM test_plan_items WHERE id = @id AND test_plan_id = @testPlanId AND client_id = @clientId;";
        return await ExecuteCountAsync(connection, sql, [new SqlParameter("@id", testPlanItemId), new SqlParameter("@testPlanId", testPlanId), new SqlParameter("@clientId", clientId)], cancellationToken) > 0;
    }

    private async Task<IReadOnlyList<long>> LoadExecutionAlreadyQueuedSuiteIdsAsync(SqlConnection connection, SqlTransaction transaction, long clientId, long testPlanId, long testPlanItemId, IReadOnlyList<long> suiteIds, CancellationToken cancellationToken)
    {
        if (suiteIds.Count == 0)
        {
            return [];
        }

        var parameters = AddIdListParameterValues(suiteIds, "@suiteId");
        var sql = $"""
            SELECT qi.test_suite_id
            FROM execution_queue_items qi
            INNER JOIN execution_queues q ON q.id = qi.execution_queue_id
            WHERE q.client_id = @clientId
              AND q.run_target = 'local'
              AND q.test_plan_id = @testPlanId
              AND q.test_plan_item_id = @testPlanItemId
              AND q.status IN ('queued', 'running')
              AND qi.status IN ('not_started', 'queued', 'running')
              AND qi.test_suite_id IN ({string.Join(", ", parameters.Select(parameter => parameter.ParameterName))});
            """;
        await using var command = CreateCommand(connection, sql);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@clientId", clientId);
        command.Parameters.AddWithValue("@testPlanId", testPlanId);
        command.Parameters.AddWithValue("@testPlanItemId", testPlanItemId);
        AddParameters(command, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<long>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(GetInt64(reader, "test_suite_id") ?? 0);
        }

        return rows.Where(id => id > 0).Distinct().ToArray();
    }

    private async Task<ExecutionQueueRecord?> GetReusableLocalExecutionQueueAsync(SqlConnection connection, SqlTransaction transaction, long clientId, long testPlanId, long testPlanItemId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP 1 id, status, priority, source, run_mode, pool_id, idempotency_key
            FROM execution_queues
            WHERE client_id = @clientId
              AND run_target = 'local'
              AND test_plan_id = @testPlanId
              AND test_plan_item_id = @testPlanItemId
              AND status IN ('queued', 'running')
            ORDER BY CASE status WHEN 'running' THEN 0 ELSE 1 END, id DESC;
            """;
        await using var command = CreateCommand(connection, sql);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@clientId", clientId);
        command.Parameters.AddWithValue("@testPlanId", testPlanId);
        command.Parameters.AddWithValue("@testPlanItemId", testPlanItemId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ExecutionQueueRecord(
            reader.GetInt64(reader.GetOrdinal("id")),
            GetString(reader, "status"),
            GetString(reader, "priority"),
            GetString(reader, "source"),
            GetString(reader, "run_mode"),
            GetInt64(reader, "pool_id"),
            GetString(reader, "idempotency_key"));
    }

    private async Task<ExecutionDevicePoolDto?> GetExecutionDevicePoolByIdAsync(SqlConnection connection, long clientId, long id, CancellationToken cancellationToken)
    {
        const string sql = "SELECT TOP 1 id, name, status FROM execution_device_pools WHERE id = @id AND client_id = @clientId;";
        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@clientId", clientId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ExecutionDevicePoolDto
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            Name = GetString(reader, "name"),
            Status = GetString(reader, "status")
        };
    }

    private async Task<ExecutionDeviceDto?> GetExecutionDeviceByIdAsync(SqlConnection connection, long clientId, long id, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                d.id,
                d.name,
                d.pool_id,
                d.host,
                d.api_key,
                d.status,
                d.health_status,
                d.runner_version,
                d.max_concurrency,
                d.last_seen_at,
                d.last_health_payload,
                p.id AS pool_ref_id,
                p.name AS pool_name
            FROM execution_devices d
            LEFT JOIN execution_device_pools p ON p.id = d.pool_id
            WHERE d.id = @id AND d.client_id = @clientId;
            """;
        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@clientId", clientId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ExecutionDeviceDto
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            Name = GetString(reader, "name"),
            PoolId = GetInt64(reader, "pool_id"),
            Pool = GetInt64(reader, "pool_ref_id") is long poolRefId ? new BasicRefDto { Id = poolRefId, Name = GetString(reader, "pool_name") } : null,
            Host = GetString(reader, "host"),
            ApiKey = GetString(reader, "api_key"),
            Status = GetString(reader, "status"),
            HealthStatus = GetString(reader, "health_status"),
            RunnerVersion = GetString(reader, "runner_version"),
            MaxConcurrency = GetInt32(reader, "max_concurrency"),
            LastSeenAt = GetDateTimeOffset(reader, "last_seen_at"),
            LastHealthPayload = ParseNullableJsonElement(GetString(reader, "last_health_payload"))
        };
    }

    private async Task UpdateExecutionDeviceHealthAsync(SqlConnection connection, long clientId, long id, string status, string healthStatus, string? runnerVersion, JsonElement? payload, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE execution_devices
            SET status = @status,
                health_status = @healthStatus,
                runner_version = COALESCE(@runnerVersion, runner_version),
                last_seen_at = SYSUTCDATETIME(),
                last_health_payload = @payload,
                updated_at = SYSUTCDATETIME()
            WHERE id = @id AND client_id = @clientId;
            """;
        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@clientId", clientId);
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@healthStatus", healthStatus);
        command.Parameters.AddWithValue("@runnerVersion", (object?)NormalizeOptionalText(runnerVersion) ?? DBNull.Value);
        command.Parameters.AddWithValue("@payload", SerializeJsonElement(payload) ?? (object)DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<ExecutionScheduleRecord?> GetExecutionScheduleRecordAsync(SqlConnection connection, long clientId, long id, CancellationToken cancellationToken)
    {
        const string sql = "SELECT TOP 1 id, name, cron, timezone, run_mode, priority, CAST(ISNULL(enabled, 1) AS bit) AS enabled, next_run_at, pool_id, payload_json FROM execution_schedules WHERE id = @id AND client_id = @clientId AND deleted_at IS NULL;";
        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@clientId", clientId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ExecutionScheduleRecord(
            reader.GetInt64(reader.GetOrdinal("id")),
            clientId,
            GetString(reader, "name"),
            GetString(reader, "cron"),
            GetString(reader, "timezone"),
            GetString(reader, "run_mode"),
            GetString(reader, "priority"),
            GetBoolean(reader, "enabled") ?? true,
            GetDateTimeOffset(reader, "next_run_at"),
            GetInt64(reader, "pool_id"),
            ParseNullableJsonElement(GetString(reader, "payload_json")));
    }

    private async Task<ExecutionScheduleDto?> GetExecutionScheduleByIdAsync(SqlConnection connection, long clientId, long id, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                s.id,
                s.name,
                s.cron,
                s.timezone,
                s.run_mode,
                s.priority,
                CAST(ISNULL(s.enabled, 1) AS bit) AS enabled,
                s.last_run_at,
                s.next_run_at,
                s.payload_json,
                p.id AS pool_ref_id,
                p.name AS pool_name
            FROM execution_schedules s
            LEFT JOIN execution_device_pools p ON p.id = s.pool_id
            WHERE s.id = @id AND s.client_id = @clientId AND s.deleted_at IS NULL;
            """;
        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@clientId", clientId);
        string? name;
        string? cron;
        string? timezone;
        string? runMode;
        string? priority;
        bool enabled;
        DateTimeOffset? lastRunAt;
        DateTimeOffset? nextRunAt;
        string? payloadJson;
        long? poolRefId;
        string? poolName;

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            name = GetString(reader, "name");
            cron = GetString(reader, "cron");
            timezone = GetString(reader, "timezone");
            runMode = GetString(reader, "run_mode");
            priority = GetString(reader, "priority");
            enabled = GetBoolean(reader, "enabled") ?? true;
            lastRunAt = GetDateTimeOffset(reader, "last_run_at");
            nextRunAt = GetDateTimeOffset(reader, "next_run_at");
            payloadJson = GetString(reader, "payload_json");
            poolRefId = GetInt64(reader, "pool_ref_id");
            poolName = GetString(reader, "pool_name");
        }

        var payload = await HydrateSchedulePayloadAsync(connection, payloadJson, cancellationToken);
        return new ExecutionScheduleDto
        {
            Id = id,
            Name = name,
            Cron = cron,
            Timezone = timezone,
            RunMode = runMode,
            Priority = priority,
            Enabled = enabled,
            RunOnce = string.IsNullOrWhiteSpace(cron) && nextRunAt.HasValue,
            LastRunAt = lastRunAt,
            NextRunAt = nextRunAt,
            Pool = poolRefId.HasValue ? new BasicRefDto { Id = poolRefId.Value, Name = poolName } : null,
            PayloadJson = payload.Payload,
            ItemsCount = payload.ItemsCount,
            HasItems = payload.HasItems
        };
    }

    private static DateTimeOffset? ResolveNextRunAt(string? cron, string? timezone, string? explicitNextRunAt, bool enabled, DateTimeOffset? fromUtc = null)
    {
        if (!enabled)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(explicitNextRunAt))
        {
            if (DateTimeOffset.TryParse(explicitNextRunAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedOffset))
            {
                return parsedOffset.ToUniversalTime();
            }

            var explicitTimeZone = ResolveTimeZoneInfo(timezone);
            if (DateTime.TryParseExact(explicitNextRunAt, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedLocal))
            {
                var localOffset = new DateTimeOffset(DateTime.SpecifyKind(parsedLocal, DateTimeKind.Unspecified), explicitTimeZone.GetUtcOffset(parsedLocal));
                return localOffset.ToUniversalTime();
            }

            if (DateTime.TryParse(explicitNextRunAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsedLocal))
            {
                var localOffset = new DateTimeOffset(DateTime.SpecifyKind(parsedLocal, DateTimeKind.Unspecified), explicitTimeZone.GetUtcOffset(parsedLocal));
                return localOffset.ToUniversalTime();
            }

            return null;
        }

        var normalizedCron = NormalizeOptionalText(cron);
        if (string.IsNullOrWhiteSpace(normalizedCron))
        {
            return null;
        }

        var parts = normalizedCron.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 6)
        {
            parts = parts[1..];
        }

        if (parts.Length != 5 || !int.TryParse(parts[0], out var minute) || !int.TryParse(parts[1], out var hour))
        {
            return null;
        }

        var now = fromUtc ?? DateTimeOffset.UtcNow;
        var timeZone = ResolveTimeZoneInfo(timezone);
        var localNow = TimeZoneInfo.ConvertTime(now, timeZone);
        for (var dayOffset = 0; dayOffset <= 370; dayOffset++)
        {
            var candidateDate = localNow.Date.AddDays(dayOffset);
            if (!CronDayMatches(parts[4], candidateDate.DayOfWeek))
            {
                continue;
            }

            var candidate = new DateTime(candidateDate.Year, candidateDate.Month, candidateDate.Day, hour, minute, 0, DateTimeKind.Unspecified);
            var localCandidateOffset = new DateTimeOffset(candidate, timeZone.GetUtcOffset(candidate));
            if (localCandidateOffset > localNow)
            {
                return localCandidateOffset.ToUniversalTime();
            }
        }

        return null;
    }

    private static TimeZoneInfo ResolveTimeZoneInfo(string? timezone)
    {
        if (string.IsNullOrWhiteSpace(timezone))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezone);
        }
        catch
        {
            return TimeZoneInfo.Utc;
        }
    }

    private static bool CronDayMatches(string cronDow, DayOfWeek dayOfWeek)
    {
        var normalized = cronDow.Trim();
        if (normalized == "*")
        {
            return true;
        }

        var dayNumber = dayOfWeek == DayOfWeek.Sunday ? 0 : (int)dayOfWeek;
        if (normalized == "1-5")
        {
            return dayNumber is >= 1 and <= 5;
        }

        return int.TryParse(normalized, out var value) && value == dayNumber;
    }

    private CreateExecutionQueueRequest BuildQueueRequestFromSchedule(ExecutionScheduleRecord schedule)
    {
        var items = new List<long>();
        long testPlanId = 0;
        long testPlanItemId = 0;
        if (schedule.PayloadJson.HasValue && schedule.PayloadJson.Value.ValueKind == JsonValueKind.Object && schedule.PayloadJson.Value.TryGetProperty("items", out var itemsElement) && itemsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in itemsElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (testPlanId == 0 && item.TryGetProperty("test_plan_id", out var testPlanIdProperty) && testPlanIdProperty.TryGetInt64(out var planId))
                {
                    testPlanId = planId;
                }

                if (testPlanItemId == 0 && item.TryGetProperty("test_plan_item_id", out var testPlanItemIdProperty) && testPlanItemIdProperty.TryGetInt64(out var planItemId))
                {
                    testPlanItemId = planItemId;
                }

                if (!item.TryGetProperty("test_suite_ids", out var suitesElement) || suitesElement.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var suiteId in suitesElement.EnumerateArray())
                {
                    if (suiteId.TryGetInt64(out var parsed))
                    {
                        items.Add(parsed);
                    }
                }
            }
        }

        return new CreateExecutionQueueRequest
        {
            TestPlanId = testPlanId,
            TestPlanItemId = testPlanItemId,
            TestSuiteIds = items.Distinct().ToArray(),
            PoolId = schedule.PoolId,
            Priority = schedule.Priority,
            RunMode = schedule.RunMode,
            RunTarget = schedule.PayloadJson.HasValue && schedule.PayloadJson.Value.ValueKind == JsonValueKind.Object && schedule.PayloadJson.Value.TryGetProperty("run_target", out var runTargetElement)
                ? runTargetElement.ToString()
                : "local",
            Source = schedule.PayloadJson.HasValue && schedule.PayloadJson.Value.ValueKind == JsonValueKind.Object && schedule.PayloadJson.Value.TryGetProperty("source", out var sourceElement)
                ? sourceElement.ToString()
                : "schedule",
            ScheduleId = schedule.Id,
            PayloadJson = schedule.PayloadJson
        };
    }

    private async Task UpdateScheduleTimingAsync(SqlConnection connection, long scheduleId, long clientId, DateTimeOffset? nextRunAt, bool enabled, DateTimeOffset? lastRunAt, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE execution_schedules
            SET next_run_at = @nextRunAt,
                enabled = @enabled,
                last_run_at = COALESCE(@lastRunAt, last_run_at),
                updated_by = @updatedBy,
                updated_at = SYSUTCDATETIME()
            WHERE id = @id AND client_id = @clientId AND deleted_at IS NULL;
            """;
        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@id", scheduleId);
        command.Parameters.AddWithValue("@clientId", clientId);
        command.Parameters.AddWithValue("@nextRunAt", nextRunAt?.UtcDateTime ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@enabled", enabled);
        command.Parameters.AddWithValue("@lastRunAt", lastRunAt?.UtcDateTime ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@updatedBy", "schedule-engine");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static ClaimsPrincipal BuildSystemPrincipal(long clientId)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "0"),
            new Claim("sub", "0"),
            new Claim("client_id", clientId.ToString(CultureInfo.InvariantCulture)),
            new Claim(ClaimTypes.Name, "schedule-engine")
        ], "schedule-engine"));
    }

    private async Task<ExecutionQueueRecord?> GetExecutionQueueRecordAsync(SqlConnection connection, long clientId, long id, CancellationToken cancellationToken)
    {
        const string sql = "SELECT TOP 1 id, status, priority, source, run_mode, run_target, pool_id, test_plan_id, test_plan_item_id, idempotency_key FROM execution_queues WHERE id = @id AND client_id = @clientId;";
        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@clientId", clientId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ExecutionQueueRecord(
            reader.GetInt64(reader.GetOrdinal("id")),
            GetString(reader, "status"),
            GetString(reader, "priority"),
            GetString(reader, "source"),
            GetString(reader, "run_mode"),
            GetInt64(reader, "pool_id"),
            GetString(reader, "idempotency_key"));
    }

    private async Task<bool> ExecutionQueueBelongsToClientAsync(SqlConnection connection, long clientId, long id, CancellationToken cancellationToken)
    {
        const string sql = "SELECT COUNT(*) FROM execution_queues WHERE id = @id AND client_id = @clientId;";
        return await ExecuteCountAsync(connection, sql, [new SqlParameter("@id", id), new SqlParameter("@clientId", clientId)], cancellationToken) > 0;
    }

    private async Task<ExecutionQueueItemRecord?> ResolveClaimedExecutionQueueItemAsync(SqlConnection connection, long queueId, long testSuiteId, string claimToken, int? attemptNo, CancellationToken cancellationToken, SqlTransaction? transaction = null)
    {
        const string sql = """
            SELECT TOP 1 id, status, last_status, queue_run_id, attempts, test_plan_item_id, claim_token
            FROM execution_queue_items
            WHERE execution_queue_id = @queueId
              AND test_suite_id = @testSuiteId
              AND claim_token = @claimToken
              AND status IN ('running', 'interrupted')
              AND (@attemptNo IS NULL OR attempts = @attemptNo)
            ORDER BY id DESC;
            """;
        await using var command = CreateCommand(connection, sql);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@queueId", queueId);
        command.Parameters.AddWithValue("@testSuiteId", testSuiteId);
        command.Parameters.AddWithValue("@claimToken", claimToken);
        command.Parameters.AddWithValue("@attemptNo", (object?)attemptNo ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ExecutionQueueItemRecord(
            reader.GetInt64(reader.GetOrdinal("id")),
            GetString(reader, "status"),
            GetString(reader, "last_status"),
            GetInt64(reader, "queue_run_id"),
            GetInt32(reader, "attempts"),
            GetInt64(reader, "test_plan_item_id"),
            GetString(reader, "claim_token"));
    }

    private async Task<ExecutionQueueItemRecord?> GetExecutionQueueItemBySuiteAsync(SqlConnection connection, long queueId, long testSuiteId, CancellationToken cancellationToken, SqlTransaction? transaction = null)
    {
        const string sql = "SELECT TOP 1 id, status, last_status, queue_run_id, attempts, test_plan_item_id, claim_token FROM execution_queue_items WHERE execution_queue_id = @queueId AND test_suite_id = @testSuiteId ORDER BY id DESC;";
        await using var command = CreateCommand(connection, sql);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@queueId", queueId);
        command.Parameters.AddWithValue("@testSuiteId", testSuiteId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ExecutionQueueItemRecord(
            reader.GetInt64(reader.GetOrdinal("id")),
            GetString(reader, "status"),
            GetString(reader, "last_status"),
            GetInt64(reader, "queue_run_id"),
            GetInt32(reader, "attempts"),
            GetInt64(reader, "test_plan_item_id"),
            GetString(reader, "claim_token"));
    }

    private async Task ResetExecutionStatusesForQueueIdsAsync(SqlConnection connection, SqlTransaction transaction, long clientId, IReadOnlyList<long> queueIds, bool activeOnly, CancellationToken cancellationToken)
    {
        var distinctQueueIds = queueIds.Where(queueId => queueId > 0).Distinct().ToArray();
        if (distinctQueueIds.Length == 0)
        {
            return;
        }

        var queueIdParameters = AddIdListParameterValues(distinctQueueIds, "@queueId");
        var queueIdList = string.Join(", ", queueIdParameters.Select(parameter => parameter.ParameterName));
        var statusFilter = activeOnly ? "AND qi.status IN ('running', 'interrupted', 'queued', 'not_started')" : string.Empty;

        var suiteSql = $"""
            UPDATE tpis
            SET status_id = @statusId,
                updated_at = SYSUTCDATETIME()
            FROM test_plan_item_suites tpis
            INNER JOIN execution_queue_items qi
                ON qi.test_plan_item_id = tpis.test_plan_item_id
               AND qi.test_suite_id = tpis.test_design_id
            INNER JOIN execution_queues q
                ON q.id = qi.execution_queue_id
            WHERE q.client_id = @clientId
              AND q.id IN ({queueIdList})
              AND qi.test_suite_id > 0
              AND qi.test_suite_id < @datasetExecutionIdOffset
              {statusFilter}
              AND tpis.deleted_at IS NULL;
            """;
        await using (var suiteCommand = CreateCommand(connection, suiteSql))
        {
            suiteCommand.Transaction = transaction;
            suiteCommand.Parameters.AddWithValue("@clientId", clientId);
            suiteCommand.Parameters.AddWithValue("@statusId", NotStartedStatusId);
            suiteCommand.Parameters.AddWithValue("@datasetExecutionIdOffset", DatasetExecutionIdOffset);
            AddParameters(suiteCommand, queueIdParameters);
            await suiteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var configurationSql = $"""
            UPDATE assign
            SET status_id = @statusId,
                updated_at = SYSUTCDATETIME()
            FROM test_plan_item_suite_configurations assign
            INNER JOIN test_plan_item_suites parent ON parent.id = assign.test_plan_item_suite_id
            INNER JOIN execution_queue_items qi
                ON qi.test_plan_item_id = parent.test_plan_item_id
               AND qi.test_suite_id = -assign.id
            INNER JOIN execution_queues q ON q.id = qi.execution_queue_id
            WHERE q.client_id = @clientId
              AND q.id IN ({queueIdList})
              {statusFilter}
              AND assign.deleted_at IS NULL
              AND parent.deleted_at IS NULL;
            """;
        await using (var configurationCommand = CreateCommand(connection, configurationSql))
        {
            configurationCommand.Transaction = transaction;
            configurationCommand.Parameters.AddWithValue("@clientId", clientId);
            configurationCommand.Parameters.AddWithValue("@statusId", NotStartedStatusId);
            AddParameters(configurationCommand, queueIdParameters);
            await configurationCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var datasetSql = $"""
            UPDATE variant
            SET status_id = @statusId,
                updated_at = SYSUTCDATETIME()
            FROM test_plan_item_suite_datasets variant
            INNER JOIN test_plan_item_suites parent ON parent.id = variant.test_plan_item_suite_id
            INNER JOIN execution_queue_items qi
                ON qi.test_plan_item_id = parent.test_plan_item_id
               AND qi.test_suite_id = @datasetExecutionIdOffset + variant.id
            INNER JOIN execution_queues q ON q.id = qi.execution_queue_id
            WHERE q.client_id = @clientId
              AND q.id IN ({queueIdList})
              {statusFilter}
              AND variant.deleted_at IS NULL
              AND parent.deleted_at IS NULL;
            """;
        await using (var datasetCommand = CreateCommand(connection, datasetSql))
        {
            datasetCommand.Transaction = transaction;
            datasetCommand.Parameters.AddWithValue("@clientId", clientId);
            datasetCommand.Parameters.AddWithValue("@statusId", NotStartedStatusId);
            datasetCommand.Parameters.AddWithValue("@datasetExecutionIdOffset", DatasetExecutionIdOffset);
            AddParameters(datasetCommand, queueIdParameters);
            await datasetCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task UpdateExecutionPlanSuiteStatusAsync(SqlConnection connection, SqlTransaction transaction, long testPlanItemId, long executionId, int statusId, CancellationToken cancellationToken)
    {
        var targetId = executionId;
        string sql;
        if (IsConfigurationExecutionId(executionId))
        {
            targetId = ToConfigurationAssignmentId(executionId);
            sql = """
                UPDATE assign
                SET status_id = @statusId,
                    updated_at = SYSUTCDATETIME()
                FROM test_plan_item_suite_configurations assign
                INNER JOIN test_plan_item_suites parent ON parent.id = assign.test_plan_item_suite_id
                WHERE parent.test_plan_item_id = @testPlanItemId
                  AND assign.id = @targetId
                  AND assign.deleted_at IS NULL
                  AND parent.deleted_at IS NULL;
                """;
        }
        else if (IsDatasetExecutionId(executionId))
        {
            targetId = ToDatasetId(executionId);
            sql = """
                UPDATE variant
                SET status_id = @statusId,
                    updated_at = SYSUTCDATETIME()
                FROM test_plan_item_suite_datasets variant
                INNER JOIN test_plan_item_suites parent ON parent.id = variant.test_plan_item_suite_id
                WHERE parent.test_plan_item_id = @testPlanItemId
                  AND variant.id = @targetId
                  AND variant.deleted_at IS NULL
                  AND parent.deleted_at IS NULL;
                """;
        }
        else
        {
            sql = """
                UPDATE test_plan_item_suites
                SET status_id = @statusId,
                    updated_at = SYSUTCDATETIME()
                WHERE test_plan_item_id = @testPlanItemId
                  AND test_design_id = @targetId
                  AND deleted_at IS NULL;
                """;
        }

        await using var command = CreateCommand(connection, sql);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@statusId", statusId);
        command.Parameters.AddWithValue("@testPlanItemId", testPlanItemId);
        command.Parameters.AddWithValue("@targetId", targetId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<long?> ResolveBaseTestSuiteIdForExecutionAsync(SqlConnection connection, long clientId, long executionId, CancellationToken cancellationToken, SqlTransaction? transaction = null)
    {
        var contexts = await LoadExecutionSuiteContextsAsync(connection, clientId, [executionId], cancellationToken, transaction);
        return contexts.FirstOrDefault().BaseTestDesignId;
    }

    private async Task RefreshExecutionQueueStatusAsync(SqlConnection connection, SqlTransaction transaction, long queueId, CancellationToken cancellationToken)
    {
        const string loadSql = "SELECT status FROM execution_queue_items WHERE execution_queue_id = @queueId;";
        var statuses = new List<string>();
        await using (var loadCommand = CreateCommand(connection, loadSql))
        {
            loadCommand.Transaction = transaction;
            loadCommand.Parameters.AddWithValue("@queueId", queueId);
            await using var reader = await loadCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                statuses.Add(GetString(reader, "status") ?? string.Empty);
            }
        }

        if (statuses.Count == 0)
        {
            return;
        }

        var anyFailed = statuses.Any(status => status is "failed" or "glitch");
        var anyRunning = statuses.Any(status => status == "running");
        var anyInterrupted = statuses.Any(status => status == "interrupted");
        var anyQueued = statuses.Any(status => status is "queued" or "not_started");
        var allComplete = statuses.All(status => status is "passed" or "failed" or "glitch");
        var queueStatus = anyRunning
            ? "running"
            : anyInterrupted
                ? "interrupted"
                : anyQueued
                    ? "running"
            : allComplete && anyFailed
                ? "failed"
                : allComplete
                    ? "completed"
                    : "running";

        const string updateSql = "UPDATE execution_queues SET status = @status, updated_at = SYSUTCDATETIME() WHERE id = @queueId;";
        await using var updateCommand = CreateCommand(connection, updateSql);
        updateCommand.Transaction = transaction;
        updateCommand.Parameters.AddWithValue("@status", queueStatus);
        updateCommand.Parameters.AddWithValue("@queueId", queueId);
        await updateCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private readonly record struct ExecutionQueueRecord(long Id, string? Status, string? Priority, string? Source, string? RunMode, long? PoolId, string? IdempotencyKey);

    private readonly record struct ExecutionQueueItemRecord(long Id, string? Status, string? LastStatus, long? QueueRunId, int? Attempts, long? TestPlanItemId, string? ClaimToken);

    private readonly record struct ExecutionScheduleRecord(long Id, long ClientId, string? Name, string? Cron, string? Timezone, string? RunMode, string? Priority, bool Enabled, DateTimeOffset? NextRunAt, long? PoolId, JsonElement? PayloadJson);
}
