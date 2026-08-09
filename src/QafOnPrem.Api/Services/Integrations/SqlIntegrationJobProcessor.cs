using System.Data;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using QafOnPrem.Api.Services;

namespace QafOnPrem.Api.Services.Integrations;

public sealed class SqlIntegrationJobProcessor(
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    ILogger<SqlIntegrationJobProcessor> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly char[] LowercaseChars = "abcdefghijklmnopqrstuvwxyz".ToCharArray();
    private readonly string _connectionString = configuration.GetConnectionString("SqlServer") ?? string.Empty;
    private bool? _hasIntegrationLinksTable;
    private bool? _hasPointBasedConfigurationAssignmentsTable;

    public async Task<int> ProcessPendingJobsAsync(int batchSize, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            logger.LogWarning("Integration processor skipped: SQL connection string is missing.");
            return 0;
        }

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var processed = 0;
        for (var i = 0; i < batchSize; i++)
        {
            var job = await ClaimNextPendingJobAsync(connection, cancellationToken);
            if (job is null)
            {
                break;
            }

            await ProcessJobAsync(connection, job, cancellationToken);
            processed++;
        }

        return processed;
    }

    private async Task ProcessJobAsync(SqlConnection connection, ClaimedJob job, CancellationToken cancellationToken)
    {
        try
        {
            var integrationConnection = await LoadIntegrationConnectionAsync(connection, job.ClientId, job.IntegrationConnectionId, cancellationToken);
            if (integrationConnection is null)
            {
                await CompleteFailedAsync(connection, job, "Integration connection not found.", cancellationToken);
                return;
            }

            if (!integrationConnection.IsEnabled)
            {
                await CompleteFailedAsync(connection, job, "Integration connection is disabled.", cancellationToken);
                return;
            }

            var payload = job.Payload ?? await BuildPayloadAsync(connection, job, cancellationToken);
            payload = await ApplyMappingAsync(connection, integrationConnection.Id, job.EntityType, payload, cancellationToken);
            payload = await EnrichProviderPayloadAsync(connection, integrationConnection.Id, job.EntityType, payload, cancellationToken);
            var effectiveCredentials = await ResolveEffectiveCredentialsAsync(connection, integrationConnection.Provider, integrationConnection.Credentials, job.CreatedBy, cancellationToken);

            var existingLink = await ResolveExistingLinkAsync(connection, integrationConnection.Id, job.EntityType, job.InternalId, cancellationToken);
            IntegrationResult result = integrationConnection.Provider.Equals("azure_devops", StringComparison.OrdinalIgnoreCase)
                ? await UpsertAzureAsync(job.EntityType, integrationConnection.Config, effectiveCredentials, payload, existingLink, cancellationToken)
                : throw new InvalidOperationException($"Unsupported integration provider: {integrationConnection.Provider}");
            await UpsertIntegrationLinkAsync(connection, integrationConnection.Id, job, payload, result, cancellationToken);

            if (integrationConnection.Provider.Equals("azure_devops", StringComparison.OrdinalIgnoreCase))
            {
                await SyncAzurePlanItemsAsync(connection, integrationConnection.Id, integrationConnection.Config, effectiveCredentials, job, result, cancellationToken);
            }

            await CompleteSentAsync(connection, job, payload, result, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Integration job {JobId} failed for entity {EntityType} (internal {InternalId}).", job.Id, job.EntityType, job.InternalId);
            await CompleteFailedAsync(connection, job, ex.ToString(), cancellationToken);
        }
    }

    private async Task<ClaimedJob?> ClaimNextPendingJobAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            ;WITH next_job AS (
                SELECT TOP (1)
                    id,
                    client_id,
                    integration_connection_id,
                    entity_type,
                    internal_id,
                    created_by,
                                        status,
                    attempts,
                    max_attempts,
                                        updated_at,
                    payload_json
                FROM integration_jobs WITH (READPAST, UPDLOCK, ROWLOCK)
                WHERE status = 'pending'
                  AND (scheduled_at IS NULL OR scheduled_at <= SYSUTCDATETIME())
                ORDER BY scheduled_at, id
            )
            UPDATE next_job
            SET
                status = 'processing',
                attempts = ISNULL(attempts, 0) + 1,
                updated_at = SYSUTCDATETIME()
            OUTPUT
                INSERTED.id,
                INSERTED.client_id,
                INSERTED.integration_connection_id,
                INSERTED.entity_type,
                INSERTED.internal_id,
                INSERTED.created_by,
                INSERTED.attempts,
                INSERTED.max_attempts,
                INSERTED.payload_json;
            """;

        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ClaimedJob
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            ClientId = reader.GetInt64(reader.GetOrdinal("client_id")),
            IntegrationConnectionId = reader.GetInt64(reader.GetOrdinal("integration_connection_id")),
            EntityType = reader.GetString(reader.GetOrdinal("entity_type")),
            InternalId = reader.GetInt64(reader.GetOrdinal("internal_id")),
            CreatedBy = reader.IsDBNull(reader.GetOrdinal("created_by")) ? 0 : reader.GetInt64(reader.GetOrdinal("created_by")),
            Attempts = (int)Math.Min(int.MaxValue, GetInt64(reader, "attempts") ?? 0),
            MaxAttempts = (int)Math.Min(int.MaxValue, GetInt64(reader, "max_attempts") ?? 5),
            Payload = ParseJsonObject(reader, "payload_json")
        };
    }

    private async Task<IntegrationConnectionRecord?> LoadIntegrationConnectionAsync(SqlConnection connection, long clientId, long id, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1)
                id,
                provider,
                CAST(ISNULL(is_enabled, 0) AS bit) AS is_enabled,
                config_json,
                credentials_encrypted
            FROM integration_connections
            WHERE id = @id AND client_id = @clientId;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@clientId", clientId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new IntegrationConnectionRecord
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            Provider = reader.IsDBNull(reader.GetOrdinal("provider")) ? string.Empty : reader.GetString(reader.GetOrdinal("provider")),
            IsEnabled = !reader.IsDBNull(reader.GetOrdinal("is_enabled")) && reader.GetBoolean(reader.GetOrdinal("is_enabled")),
            Config = ParseJsonObject(reader, "config_json") ?? new JsonObject(),
            Credentials = ParseJsonObject(reader, "credentials_encrypted") ?? new JsonObject()
        };
    }

    private async Task<JsonObject> BuildPayloadAsync(SqlConnection connection, ClaimedJob job, CancellationToken cancellationToken)
    {
        return job.EntityType.Trim().ToLowerInvariant() switch
        {
            "test_case" => await BuildTestCasePayloadAsync(connection, job, cancellationToken),
            "test_plan" => await BuildTestPlanPayloadAsync(connection, job, cancellationToken),
            "defect" => await BuildDefectPayloadAsync(connection, job, cancellationToken),
            "test_run" => await BuildTestRunPayloadAsync(connection, job, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported entity type: {job.EntityType}")
        };
    }

    private async Task<JsonObject> BuildTestCasePayloadAsync(SqlConnection connection, ClaimedJob job, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1)
                td.id,
                td.title,
                td.comment,
                td.azure_iteration_path,
                td.priority,
                td.story_id,
                td.tags,
                p.area_path AS project_area_path,
                p.primary_test_management,
                ts.name AS state_name
            FROM test_designs td
            LEFT JOIN projects p ON p.id = td.project_id AND p.deleted_at IS NULL
            LEFT JOIN test_states ts ON ts.id = td.test_state_id
            WHERE td.id = @id AND td.client_id = @clientId AND td.deleted_at IS NULL;
            """;

        await using var payloadConnection = new SqlConnection(_connectionString);
        await payloadConnection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(sql, payloadConnection);
        command.Parameters.AddWithValue("@id", job.InternalId);
        command.Parameters.AddWithValue("@clientId", job.ClientId);
        string title;
        string description;
        string state;
        string priority;
        string storyId;
        string tags;
        string areaPath;
        string iterationPath;
        string primaryTestManagement;

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Test case not found.");
            }

            title = GetString(reader, "title") ?? $"Test Case #{job.InternalId}";
            description = GetString(reader, "comment") ?? string.Empty;
            state = GetString(reader, "state_name") ?? string.Empty;
            priority = GetString(reader, "priority") ?? string.Empty;
            storyId = GetString(reader, "story_id") ?? string.Empty;
            tags = GetString(reader, "tags") ?? string.Empty;
            areaPath = GetString(reader, "project_area_path") ?? string.Empty;
            iterationPath = GetString(reader, "azure_iteration_path") ?? string.Empty;
            primaryTestManagement = GetString(reader, "primary_test_management") ?? string.Empty;
        }

        var manualSteps = await LoadManualStepsForTestCaseAsync(payloadConnection, job.ClientId, job.InternalId, cancellationToken);

        return new JsonObject
        {
            ["internal_id"] = job.InternalId,
            ["title"] = title,
            ["description"] = description,
            ["summary"] = description,
            ["prerequisite"] = description,
            ["state"] = state,
            ["priority"] = priority,
            ["story_id"] = storyId,
            ["tags"] = tags,
            ["area_path"] = areaPath,
            ["iteration_path"] = iterationPath,
            ["project_primary_test_management"] = primaryTestManagement,
            ["manual_steps"] = manualSteps
        };
    }

    private static async Task<JsonArray> LoadManualStepsForTestCaseAsync(SqlConnection connection, long clientId, long testDesignId, CancellationToken cancellationToken)
    {
        var components = await LoadActiveTestCaseComponentsAsync(connection, clientId, testDesignId, cancellationToken);
        if (components.Count == 0)
        {
            return [];
        }

        var variableMaps = await LoadTestCaseVariableMapAsync(connection, clientId, testDesignId, cancellationToken);
        var globalCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var localCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var manualSteps = new JsonArray();
        var stepIndex = 0;

        foreach (var component in components)
        {
            if (component.Steps.Count == 0 || component.Datasets.Count == 0)
            {
                continue;
            }

            foreach (var dataset in component.Datasets)
            {
                if (!dataset.Status)
                {
                    continue;
                }

                var datasetStepMap = dataset.Steps
                    .Where(step => !step.SkipStep && step.StepId.HasValue)
                    .GroupBy(step => step.StepId!.Value)
                    .ToDictionary(group => group.Key, group => group.First());

                foreach (var componentStep in component.Steps)
                {
                    if (!datasetStepMap.TryGetValue(componentStep.Id, out var datasetStep))
                    {
                        continue;
                    }

                    var rawValue = datasetStep.Value?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(rawValue) || string.Equals(rawValue, "skip", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var overrideParts = ParseOverrideParts(datasetStep.OverrideValue);
                    var action = FirstNonEmpty(overrideParts.Description, componentStep.Description, datasetStep.StepDescription);
                    var expected = FirstNonEmpty(overrideParts.ExpectedOutput, componentStep.ExpectedOutput, datasetStep.StepExpectedOutput);
                    var resolvedValue = ResolveRunnerValue(rawValue, testDesignId, variableMaps, globalCache, localCache);

                    stepIndex++;
                    manualSteps.Add(new JsonObject
                    {
                        ["parameter_name"] = $"step-{stepIndex}",
                        ["action"] = action,
                        ["expected"] = expected,
                        ["value"] = resolvedValue
                    });
                }
            }
        }

        return manualSteps;
    }

    private static async Task<IReadOnlyList<IntegrationTestCaseComponent>> LoadActiveTestCaseComponentsAsync(SqlConnection connection, long clientId, long testDesignId, CancellationToken cancellationToken)
    {
        const string componentSql = """
            SELECT id, component_id
            FROM test_components
            WHERE test_design_id = @testDesignId
              AND deleted_at IS NULL
              AND ISNULL(status, 1) = 1
            ORDER BY id;
            """;

        var componentRows = new List<(long Id, long? ComponentId)>();
        await using (var componentCommand = new SqlCommand(componentSql, connection))
        {
            componentCommand.Parameters.AddWithValue("@testDesignId", testDesignId);
            await using var reader = await componentCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                componentRows.Add((reader.GetInt64(reader.GetOrdinal("id")), GetInt64(reader, "component_id")));
            }
        }

        if (componentRows.Count == 0)
        {
            return [];
        }

        var componentIds = componentRows
            .Where(row => row.ComponentId.HasValue)
            .Select(row => row.ComponentId!.Value)
            .Distinct()
            .ToArray();
        var componentStepMap = await LoadComponentStepMapAsync(connection, clientId, componentIds, cancellationToken);
        var datasetMap = await LoadDatasetMapAsync(connection, componentRows.Select(row => row.Id).ToArray(), cancellationToken);

        return componentRows.Select(row => new IntegrationTestCaseComponent(
            row.Id,
            row.ComponentId,
            row.ComponentId.HasValue && componentStepMap.TryGetValue(row.ComponentId.Value, out var steps) ? steps : [],
            datasetMap.TryGetValue(row.Id, out var datasets) ? datasets : [])).ToList();
    }

    private static async Task<Dictionary<long, IReadOnlyList<IntegrationComponentStep>>> LoadComponentStepMapAsync(SqlConnection connection, long clientId, IReadOnlyList<long> componentIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, IReadOnlyList<IntegrationComponentStep>>();
        if (componentIds.Count == 0)
        {
            return result;
        }

        var parameters = new List<SqlParameter> { new("@clientId", clientId) };
        var placeholders = AddIdListParameters(parameters, "@componentId", componentIds);
        var sql = $"""
            SELECT id, component_id, description, expected_output, display_id
            FROM component_steps
            WHERE deleted_at IS NULL
              AND component_id IN ({string.Join(", ", placeholders)})
            ORDER BY component_id, ISNULL(display_id, 2147483647), id;
            """;

        var grouped = new Dictionary<long, List<IntegrationComponentStep>>();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddRange(parameters.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var componentId = GetInt64(reader, "component_id");
            if (!componentId.HasValue)
            {
                continue;
            }

            if (!grouped.TryGetValue(componentId.Value, out var steps))
            {
                steps = [];
                grouped[componentId.Value] = steps;
            }

            steps.Add(new IntegrationComponentStep(
                reader.GetInt64(reader.GetOrdinal("id")),
                GetString(reader, "description"),
                GetString(reader, "expected_output"),
                GetInt64(reader, "display_id") is long displayId ? (int?)displayId : null));
        }

        foreach (var pair in grouped)
        {
            result[pair.Key] = pair.Value;
        }

        return result;
    }

    private static async Task<Dictionary<long, IReadOnlyList<IntegrationDataset>>> LoadDatasetMapAsync(SqlConnection connection, IReadOnlyList<long> testComponentIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, IReadOnlyList<IntegrationDataset>>();
        if (testComponentIds.Count == 0)
        {
            return result;
        }

        var parameters = new List<SqlParameter>();
        var placeholders = AddIdListParameters(parameters, "@testComponentId", testComponentIds);
        var sql = $"""
            SELECT id, test_component_id, CAST(ISNULL(status, 0) AS bit) AS status
            FROM data_sets
            WHERE deleted_at IS NULL
              AND test_component_id IN ({string.Join(", ", placeholders)})
            ORDER BY id;
            """;

        var datasetRows = new List<(long Id, long TestComponentId, bool Status)>();
        await using (var command = new SqlCommand(sql, connection))
        {
            command.Parameters.AddRange(parameters.ToArray());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var testComponentId = GetInt64(reader, "test_component_id");
                if (!testComponentId.HasValue)
                {
                    continue;
                }

                datasetRows.Add((
                    reader.GetInt64(reader.GetOrdinal("id")),
                    testComponentId.Value,
                    GetBoolean(reader, "status") ?? false));
            }
        }

        var datasetIds = datasetRows.Select(row => row.Id).ToArray();
        var stepMap = await LoadDatasetStepMapAsync(connection, datasetIds, cancellationToken);
        foreach (var group in datasetRows.GroupBy(row => row.TestComponentId))
        {
            result[group.Key] = group.Select(row => new IntegrationDataset(
                row.Id,
                row.Status,
                stepMap.TryGetValue(row.Id, out var steps) ? steps : [])).ToList();
        }

        return result;
    }

    private static async Task<Dictionary<long, IReadOnlyList<IntegrationDatasetStep>>> LoadDatasetStepMapAsync(SqlConnection connection, IReadOnlyList<long> datasetIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, IReadOnlyList<IntegrationDatasetStep>>();
        if (datasetIds.Count == 0)
        {
            return result;
        }

        var parameters = new List<SqlParameter>();
        var placeholders = AddIdListParameters(parameters, "@datasetId", datasetIds);
        var sql = $"""
            SELECT
                dataset_id,
                step_id,
                display,
                CAST(ISNULL(skip_step, 0) AS bit) AS skip_step,
                override_value,
                LTRIM(RTRIM(JSON_VALUE(step_info, '$.value'))) AS step_value,
                LTRIM(RTRIM(JSON_VALUE(step_info, '$.description'))) AS step_description,
                LTRIM(RTRIM(JSON_VALUE(step_info, '$.expected_output'))) AS step_expected_output
            FROM data_set_steps
            WHERE dataset_id IN ({string.Join(", ", placeholders)})
            ORDER BY dataset_id, ISNULL(display, 2147483647), step_id;
            """;

        var grouped = new Dictionary<long, List<IntegrationDatasetStep>>();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddRange(parameters.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var datasetId = GetInt64(reader, "dataset_id");
            if (!datasetId.HasValue)
            {
                continue;
            }

            if (!grouped.TryGetValue(datasetId.Value, out var steps))
            {
                steps = [];
                grouped[datasetId.Value] = steps;
            }

            steps.Add(new IntegrationDatasetStep(
                datasetId.Value,
                GetInt64(reader, "step_id"),
                GetBoolean(reader, "skip_step") ?? false,
                GetString(reader, "step_value"),
                GetString(reader, "override_value"),
                GetString(reader, "step_description"),
                GetString(reader, "step_expected_output")));
        }

        foreach (var pair in grouped)
        {
            result[pair.Key] = pair.Value;
        }

        return result;
    }

    private static async Task<IntegrationVariableMaps> LoadTestCaseVariableMapAsync(SqlConnection connection, long clientId, long testDesignId, CancellationToken cancellationToken)
    {
        var global = new Dictionary<string, IntegrationVariableValue>(StringComparer.OrdinalIgnoreCase);
        var local = new Dictionary<long, Dictionary<string, IntegrationVariableValue>>();
        var parameters = new List<SqlParameter> { new("@clientId", clientId), new("@suiteId0", testDesignId) };
        var sql = """
            SELECT
                cv.name,
                cv.value,
                cv.test_case_id,
                CAST(ISNULL(cv.is_encrypted, 0) AS bit) AS is_encrypted,
                vt.executable_method
            FROM custom_variables cv
            LEFT JOIN variable_types vt ON vt.id = cv.variable_id
            WHERE cv.client_id = @clientId
              AND (cv.test_case_id IS NULL OR cv.test_case_id IN (@suiteId0))
            ORDER BY cv.id;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddRange(parameters.ToArray());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = GetString(reader, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var variable = new IntegrationVariableValue(
                GetString(reader, "value"),
                GetString(reader, "executable_method"),
                GetBoolean(reader, "is_encrypted") ?? false);
            var testCaseId = GetInt64(reader, "test_case_id");
            if (testCaseId.HasValue)
            {
                if (!local.TryGetValue(testCaseId.Value, out var localVariables))
                {
                    localVariables = new Dictionary<string, IntegrationVariableValue>(StringComparer.OrdinalIgnoreCase);
                    local[testCaseId.Value] = localVariables;
                }

                localVariables[name] = variable;
            }
            else
            {
                global[name] = variable;
            }
        }

        return new IntegrationVariableMaps(global, local);
    }

    private static string ResolveRunnerValue(string originalValue, long suiteId, IntegrationVariableMaps variableMaps, Dictionary<string, string> globalCache, Dictionary<string, string> localCache)
    {
        var value = originalValue;
        foreach (var token in ExtractVariableTokens(originalValue))
        {
            if (variableMaps.Global.TryGetValue(token, out var globalVariable))
            {
                if (!globalCache.TryGetValue(token, out var generated))
                {
                    generated = GenerateVariableValue(globalVariable);
                    globalCache[token] = generated;
                }

                value = value.Replace(token, generated, StringComparison.Ordinal);
            }

            if (variableMaps.Local.TryGetValue(suiteId, out var localVariables) && localVariables.TryGetValue(token, out var localVariable))
            {
                if (!localCache.TryGetValue(token, out var generated))
                {
                    generated = GenerateVariableValue(localVariable);
                    localCache[token] = generated;
                }

                value = value.Replace(token, generated, StringComparison.Ordinal);
            }
        }

        return value;
    }

    private static List<string> ExtractVariableTokens(string value)
    {
        var tokens = new List<string>();
        var index = 0;
        while (index < value.Length)
        {
            var start = value.IndexOf("{{", index, StringComparison.Ordinal);
            if (start < 0)
            {
                break;
            }

            var end = value.IndexOf("}}", start + 2, StringComparison.Ordinal);
            if (end < 0)
            {
                break;
            }

            tokens.Add(value[start..(end + 2)]);
            index = end + 2;
        }

        return tokens;
    }

    private static string GenerateVariableValue(IntegrationVariableValue variable)
    {
        return VariableValueResolver.Resolve(variable.Value, FirstNonEmpty(variable.ExecutableMethod), variable.IsEncrypted);
    }

    private static ParsedOverrideParts ParseOverrideParts(string? overrideValue)
    {
        var result = new ParsedOverrideParts();
        if (string.IsNullOrWhiteSpace(overrideValue))
        {
            return result;
        }

        var segments = overrideValue.Contains(";;", StringComparison.Ordinal)
            ? overrideValue.Split(";;", StringSplitOptions.None)
            : overrideValue.Contains("<=>", StringComparison.Ordinal)
                ? overrideValue.Split("<=>", StringSplitOptions.None)
                : overrideValue.Split("||", StringSplitOptions.None);

        foreach (var rawSegment in segments)
        {
            var segment = rawSegment.Trim();
            if (string.IsNullOrWhiteSpace(segment))
            {
                continue;
            }

            var separatorIndex = segment.IndexOf('=', StringComparison.Ordinal);
            if (separatorIndex < 0)
            {
                separatorIndex = segment.IndexOf(':', StringComparison.Ordinal);
            }

            if (separatorIndex <= 0 || separatorIndex >= segment.Length - 1)
            {
                continue;
            }

            var key = NormalizeOverrideKey(segment[..separatorIndex]);
            var value = segment[(separatorIndex + 1)..].Trim();
            switch (key)
            {
                case "description":
                    result.Description = value;
                    break;
                case "expected_output":
                    result.ExpectedOutput = value;
                    break;
            }
        }

        return result;
    }

    private static string NormalizeOverrideKey(string rawKey)
    {
        var key = rawKey.Trim().ToLowerInvariant();
        return key switch
        {
            "stepdesc" => "description",
            "expected" => "expected_output",
            _ => key
        };
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static List<string> AddIdListParameters(List<SqlParameter> parameters, string parameterPrefix, IReadOnlyList<long> values)
    {
        var placeholders = new List<string>(values.Count);
        for (var i = 0; i < values.Count; i++)
        {
            var parameterName = $"{parameterPrefix}{i}";
            parameters.Add(new SqlParameter(parameterName, SqlDbType.BigInt) { Value = values[i] });
            placeholders.Add(parameterName);
        }

        return placeholders;
    }

    private async Task<JsonObject> BuildTestPlanPayloadAsync(SqlConnection connection, ClaimedJob job, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1)
                tp.id,
                tp.name,
                tp.objective,
                tp.area_path,
                tp.iteration_path,
                CAST(ISNULL(tp.is_active, CASE WHEN ISNULL(tp.status, 1) = 1 THEN 1 ELSE 0 END) AS bit) AS is_active,
                tp.start_date,
                tp.end_date
            FROM test_plans tp
            WHERE tp.id = @id AND tp.client_id = @clientId;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", job.InternalId);
        command.Parameters.AddWithValue("@clientId", job.ClientId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Test plan not found.");
        }

        return new JsonObject
        {
            ["internal_id"] = job.InternalId,
            ["name"] = GetString(reader, "name") ?? $"Test Plan #{job.InternalId}",
            ["title"] = GetString(reader, "name") ?? $"Test Plan #{job.InternalId}",
            ["description"] = GetString(reader, "objective") ?? string.Empty,
            ["area_path"] = GetString(reader, "area_path") ?? string.Empty,
            ["iteration"] = GetString(reader, "iteration_path") ?? string.Empty,
            ["state"] = (GetBoolean(reader, "is_active") ?? false) ? "Active" : "Inactive",
            ["start_date"] = GetDateString(reader, "start_date"),
            ["end_date"] = GetDateString(reader, "end_date")
        };
    }

    private async Task<JsonObject> BuildDefectPayloadAsync(SqlConnection connection, ClaimedJob job, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1)
                d.id,
                d.title,
                ds.name AS status_name,
                d.assigned_to,
                d.test_runner_item_id
            FROM defects d
            LEFT JOIN defect_statuses ds ON ds.id = d.status_id
            WHERE d.id = @id AND d.client_id = @clientId AND d.deleted_at IS NULL;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", job.InternalId);
        command.Parameters.AddWithValue("@clientId", job.ClientId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Defect not found.");
        }

        return new JsonObject
        {
            ["internal_id"] = job.InternalId,
            ["title"] = GetString(reader, "title") ?? $"Defect #{job.InternalId}",
            ["description"] = $"Linked runner item: {GetInt64(reader, "test_runner_item_id")}",
            ["state"] = GetString(reader, "status_name") ?? string.Empty,
            ["assigned_to"] = GetInt64(reader, "assigned_to")
        };
    }

    private async Task<JsonObject> BuildTestRunPayloadAsync(SqlConnection connection, ClaimedJob job, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1)
                tri.id,
                tri.test_suite_id,
                tri.test_suite_name,
                tri.status_id,
                tri.comment,
                tri.steps,
                tri.created_at,
                tri.updated_at,
                tp.id AS test_plan_id,
                status_ref.name AS status_name
            FROM test_runner_items tri
            INNER JOIN test_runners tr ON tr.id = tri.test_runner_id
            LEFT JOIN test_plan_items tpi ON tpi.id = tr.test_plan_item_id
            LEFT JOIN test_plans tp ON tp.id = tpi.test_plan_id
            LEFT JOIN test_plan_item_suite_statuses status_ref ON status_ref.id = tri.status_id
            WHERE tri.id = @id AND tr.client_id = @clientId;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", job.InternalId);
        command.Parameters.AddWithValue("@clientId", job.ClientId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Test run not found.");
        }

        var steps = ParseJsonArray(GetString(reader, "steps"));
        var failedStep = FindFirstFailedStep(steps);
        var stepCounts = BuildStepCounts(steps);
        var statusId = GetInt64(reader, "status_id") ?? 0;

        var summaryComment = GetString(reader, "comment") ?? string.Empty;
        if (string.IsNullOrWhiteSpace(summaryComment) && failedStep is not null)
        {
            summaryComment = failedStep["comment"]?.ToString() ?? string.Empty;
        }

        var suiteName = GetString(reader, "test_suite_name") ?? $"Run #{job.InternalId}";
        var testSuiteId = GetInt64(reader, "test_suite_id") ?? 0;

        var payload = new JsonObject
        {
            ["internal_id"] = job.InternalId,
            ["name"] = $"Test Run #{job.InternalId}",
            ["title"] = suiteName,
            ["description"] = $"Run sync for suite {suiteName}",
            ["comment"] = summaryComment,
            ["status_id"] = statusId,
            ["status_name"] = GetString(reader, "status_name") ?? string.Empty,
            ["test_case_internal_id"] = testSuiteId,
            ["test_plan_internal_id"] = GetInt64(reader, "test_plan_id") ?? 0,
            ["steps"] = steps,
            ["step_counts"] = stepCounts,
            ["started_at"] = GetDateTimeOffsetString(reader, "created_at"),
            ["executed_at"] = GetDateTimeOffsetString(reader, "updated_at")
        };

        if (failedStep is not null)
        {
            payload["failed_step"] = failedStep;
        }

        return payload;
    }

    private async Task<JsonObject> ApplyMappingAsync(SqlConnection connection, long connectionId, string entityType, JsonObject payload, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1)
                field_map_json,
                status_map_json,
                priority_map_json,
                options_json
            FROM integration_mappings
            WHERE integration_connection_id = @connectionId AND entity_type = @entityType;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@connectionId", connectionId);
        command.Parameters.AddWithValue("@entityType", entityType.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return payload;
        }

        var fieldMap = ParseJsonObject(reader, "field_map_json");
        foreach (var map in fieldMap?.AsObject() ?? [])
        {
            if (map.Value is null)
            {
                continue;
            }

            var sourceField = map.Key;
            var destinationField = map.Value.GetValue<string?>();
            if (string.IsNullOrWhiteSpace(sourceField) || string.IsNullOrWhiteSpace(destinationField))
            {
                continue;
            }

            if (payload.TryGetPropertyValue(sourceField, out var sourceValue))
            {
                payload[destinationField] = sourceValue?.DeepClone();
            }
        }

        ApplyMappedValue(payload, "state", ParseJsonObject(reader, "status_map_json"));
        ApplyMappedValue(payload, "priority", ParseJsonObject(reader, "priority_map_json"));

        var optionsJson = ParseJsonObject(reader, "options_json");
        if (optionsJson? ["static_fields"] is JsonObject staticFields)
        {
            foreach (var item in staticFields)
            {
                payload[item.Key] = item.Value?.DeepClone();
            }
        }

        return payload;
    }

    private async Task<JsonObject> EnrichProviderPayloadAsync(SqlConnection connection, long connectionId, string entityType, JsonObject payload, CancellationToken cancellationToken)
    {
        if (!entityType.Equals("test_run", StringComparison.OrdinalIgnoreCase))
        {
            return payload;
        }

        if (payload["test_plan_internal_id"] is JsonNode testPlanInternalNode
            && TryParseLongNode(testPlanInternalNode, out var testPlanInternalId)
            && testPlanInternalId > 0)
        {
            var planLink = await ResolveExistingLinkAsync(connection, connectionId, "test_plan", testPlanInternalId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(planLink.ExternalId))
            {
                payload["test_plan_external_id"] = planLink.ExternalId;
            }
        }

        if (payload["test_case_internal_id"] is JsonNode testCaseInternalNode
            && TryParseLongNode(testCaseInternalNode, out var testCaseInternalId)
            && testCaseInternalId > 0)
        {
            var caseLink = await ResolveExistingLinkAsync(connection, connectionId, "test_case", testCaseInternalId, cancellationToken);
            if (!string.IsNullOrWhiteSpace(caseLink.ExternalId))
            {
                payload["test_case_external_id"] = caseLink.ExternalId;
            }
        }

        return payload;
    }

    private async Task<ExistingLink> ResolveExistingLinkAsync(SqlConnection connection, long connectionId, string entityType, long internalId, CancellationToken cancellationToken)
    {
        var hasLinksTable = await HasIntegrationLinksTableAsync(connection, cancellationToken);
        if (hasLinksTable)
        {
            const string linkSql = """
                SELECT TOP (1)
                    external_id,
                    external_key
                FROM integration_links
                WHERE integration_connection_id = @connectionId
                  AND entity_type = @entityType
                  AND internal_id = @internalId
                ORDER BY last_synced_at DESC, id DESC;
                """;

            await using var linkCommand = new SqlCommand(linkSql, connection);
            linkCommand.Parameters.AddWithValue("@connectionId", connectionId);
            linkCommand.Parameters.AddWithValue("@entityType", entityType);
            linkCommand.Parameters.AddWithValue("@internalId", internalId);
            await using var linkReader = await linkCommand.ExecuteReaderAsync(cancellationToken);
            if (await linkReader.ReadAsync(cancellationToken))
            {
                return new ExistingLink(GetString(linkReader, "external_id"), GetString(linkReader, "external_key"));
            }
        }

        const string fallbackSql = """
            SELECT TOP (1)
                external_id,
                external_key
            FROM integration_jobs
            WHERE integration_connection_id = @connectionId
              AND entity_type = @entityType
              AND internal_id = @internalId
              AND status = 'sent'
              AND external_id IS NOT NULL
            ORDER BY sent_at DESC, id DESC;
            """;

        await using var command = new SqlCommand(fallbackSql, connection);
        command.Parameters.AddWithValue("@connectionId", connectionId);
        command.Parameters.AddWithValue("@entityType", entityType);
        command.Parameters.AddWithValue("@internalId", internalId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new ExistingLink(null, null);
        }

        return new ExistingLink(GetString(reader, "external_id"), GetString(reader, "external_key"));
    }

    private async Task<IntegrationResult> UpsertAzureAsync(string entityType, JsonObject config, JsonObject credentials, JsonObject payload, ExistingLink existingLink, CancellationToken cancellationToken)
    {
        var organization = config["organization"]?.GetValue<string>()?.Trim();
        var project = config["project"]?.GetValue<string>()?.Trim();
        var pat = credentials["pat"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(organization) || string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(pat))
        {
            throw new InvalidOperationException("Azure integration requires organization, project, and pat.");
        }

        var baseUrl = $"https://dev.azure.com/{organization}/{project}";
        var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}")));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        return entityType.Trim().ToLowerInvariant() switch
        {
            "test_case" => await UpsertAzureWorkItemAsync(client, baseUrl, "$Test Case", payload, existingLink, config, cancellationToken),
            "defect" => await UpsertAzureWorkItemAsync(client, baseUrl, "$Bug", payload, existingLink, config, cancellationToken),
            "test_plan" => await UpsertAzureTestPlanAsync(client, baseUrl, payload, existingLink, config, cancellationToken),
            "test_run" => await UpsertAzureTestRunAsync(client, baseUrl, payload, existingLink, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported entity type for Azure: {entityType}")
        };
    }

    private async Task<JsonObject> ResolveEffectiveCredentialsAsync(SqlConnection connection, string provider, JsonObject baseCredentials, long createdByUserId, CancellationToken cancellationToken)
    {
        var effectiveCredentials = baseCredentials.DeepClone()?.AsObject() ?? new JsonObject();
        if (!provider.Equals("azure_devops", StringComparison.OrdinalIgnoreCase))
        {
            return effectiveCredentials;
        }

        if (createdByUserId <= 0)
        {
            throw new InvalidOperationException("Azure sync failed: missing job creator context. Save the test using a valid user session and retry.");
        }

        var userPat = await LoadUserAzurePatAsync(connection, createdByUserId, cancellationToken);
        if (string.IsNullOrWhiteSpace(userPat))
        {
            throw new InvalidOperationException(
                "Azure sync failed: no user PAT found in profile settings. Set one of integrations.azure_devops.pat, integrations.azure.pat, or azure_devops.pat and retry.");
        }

        // Enforce user-owned PAT only: no fallback to connection PAT.
        effectiveCredentials["pat"] = userPat;

        return effectiveCredentials;
    }

    private async Task<string?> LoadUserAzurePatAsync(SqlConnection connection, long userId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1) settings
            FROM user_settings
            WHERE user_id = @userId
            ORDER BY id DESC;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@userId", userId);
        var raw = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            var node = JsonNode.Parse(raw);
            var pat = node?["integrations"]?["azure_devops"]?["pat"]?.ToString()?.Trim();
            if (!string.IsNullOrWhiteSpace(pat))
            {
                return pat;
            }

            pat = node?["integrations"]?["azure"]?["pat"]?.ToString()?.Trim();
            if (!string.IsNullOrWhiteSpace(pat))
            {
                return pat;
            }

            pat = node?["azure_devops"]?["pat"]?.ToString()?.Trim();
            return string.IsNullOrWhiteSpace(pat) ? null : pat;
        }
        catch
        {
            return null;
        }
    }

    private async Task SyncAzurePlanItemsAsync(
        SqlConnection connection,
        long integrationConnectionId,
        JsonObject config,
        JsonObject credentials,
        ClaimedJob job,
        IntegrationResult result,
        CancellationToken cancellationToken)
    {
        if (!TryCreateAzureContext(config, credentials, out var azureContext))
        {
            return;
        }

        if (job.EntityType.Equals("test_case", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParsePositiveLong(result.ExternalId, out var testCaseExternalId))
            {
                logger.LogWarning(
                    "Azure plan-item sync skipped for job {JobId}: invalid test case external id '{ExternalId}' for internal {InternalId}.",
                    job.Id,
                    result.ExternalId,
                    job.InternalId);
                return;
            }

            var planItems = await LoadPlanItemsForTestCaseAsync(connection, job.ClientId, job.InternalId, cancellationToken);
            logger.LogInformation(
                "Azure plan-item sync start for job {JobId}: test case internal {InternalId}, external {ExternalId}, planItems={PlanItemCount}.",
                job.Id,
                job.InternalId,
                testCaseExternalId,
                planItems.Count);
            await SyncPlanItemsForCaseAsync(connection, integrationConnectionId, azureContext, planItems, testCaseExternalId, cancellationToken);
            return;
        }

        if (job.EntityType.Equals("test_plan", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParsePositiveLong(result.ExternalId, out var planExternalId))
            {
                return;
            }

            var planItems = await LoadPlanItemCasesForPlanAsync(connection, job.ClientId, job.InternalId, cancellationToken);
            await SyncPlanCasesAsync(connection, integrationConnectionId, azureContext, job.InternalId, planExternalId, planItems, cancellationToken);
        }
    }

    private async Task SyncPlanItemsForCaseAsync(
        SqlConnection connection,
        long integrationConnectionId,
        AzureContext azureContext,
        IReadOnlyList<PlanItemRef> planItems,
        long testCaseExternalId,
        CancellationToken cancellationToken)
    {
        var client = CreateAzureClient(azureContext.Pat);
        var planSuitesCache = new Dictionary<long, Dictionary<string, long>>();
        var expectedByPlanExternalId = new Dictionary<long, List<ExpectedSuiteAssignment>>();
        var azureConfigurations = planItems.Count > 0
            ? await LoadAzureConfigurationsByNameAsync(client, azureContext.BaseUrl, cancellationToken)
            : new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var defaultAzureConfigurationId = planItems.Count > 0
            ? await LoadDefaultAzureConfigurationIdAsync(client, azureContext.BaseUrl, cancellationToken)
            : null;

        logger.LogInformation(
            "SyncPlanItemsForCaseAsync for case {TestCaseExternalId}: planItems={PlanItemCount}, azureConfigurations={ConfigurationCount}, defaultConfigurationId={DefaultConfigurationId}.",
            testCaseExternalId,
            planItems.Count,
            azureConfigurations.Count,
            defaultAzureConfigurationId);

        foreach (var item in planItems)
        {
            var planLink = await ResolveExistingLinkAsync(connection, integrationConnectionId, "test_plan", item.PlanInternalId, cancellationToken);
            if (!TryParsePositiveLong(planLink.ExternalId, out var planExternalId))
            {
                continue;
            }

            if (!expectedByPlanExternalId.TryGetValue(planExternalId, out var assignments))
            {
                assignments = [];
                expectedByPlanExternalId[planExternalId] = assignments;
            }

            var configurationId = ResolveAzureConfigurationId(
                item.ConfigurationName,
                azureConfigurations,
                $"plan item '{item.PlanItemName}' (internal {item.PlanItemId})",
                defaultAzureConfigurationId);
            assignments.Add(new ExpectedSuiteAssignment(item.PlanItemName, configurationId, item.ConfigurationName));
        }

        var knownPlanExternalIds = await LoadSyncedAzurePlanExternalIdsAsync(connection, integrationConnectionId, cancellationToken);
        foreach (var planExternalId in expectedByPlanExternalId.Keys)
        {
            knownPlanExternalIds.Add(planExternalId);
        }

        foreach (var planExternalId in knownPlanExternalIds)
        {
            expectedByPlanExternalId.TryGetValue(planExternalId, out var expectedAssignments);
            logger.LogInformation(
                "Reconciling plan {PlanExternalId} for case {TestCaseExternalId} with expectedAssignments={ExpectedCount}.",
                planExternalId,
                testCaseExternalId,
                expectedAssignments?.Count ?? 0);
            await SyncAndReconcileCaseMembershipForPlanAsync(
                client,
                azureContext.BaseUrl,
                planExternalId,
                testCaseExternalId,
                expectedAssignments ?? [],
                planSuitesCache,
                cancellationToken);
        }
    }

    private async Task SyncPlanCasesAsync(
        SqlConnection connection,
        long integrationConnectionId,
        AzureContext azureContext,
        long planInternalId,
        long planExternalId,
        IReadOnlyList<PlanItemCaseRef> planItems,
        CancellationToken cancellationToken)
    {
        if (planItems.Count == 0)
        {
            return;
        }

        var client = CreateAzureClient(azureContext.Pat);
        var planSuitesCache = new Dictionary<long, Dictionary<string, long>>();
        var azureConfigurations = await LoadAzureConfigurationsByNameAsync(client, azureContext.BaseUrl, cancellationToken);
        var defaultAzureConfigurationId = await LoadDefaultAzureConfigurationIdAsync(client, azureContext.BaseUrl, cancellationToken);
        var expectedByCaseExternalId = new Dictionary<long, List<ExpectedSuiteAssignment>>();

        foreach (var item in planItems)
        {
            var caseLink = await ResolveExistingLinkAsync(connection, integrationConnectionId, "test_case", item.TestCaseInternalId, cancellationToken);
            if (!TryParsePositiveLong(caseLink.ExternalId, out var caseExternalId))
            {
                continue;
            }

            if (!expectedByCaseExternalId.TryGetValue(caseExternalId, out var assignments))
            {
                assignments = [];
                expectedByCaseExternalId[caseExternalId] = assignments;
            }

            var configurationId = ResolveAzureConfigurationId(
                item.ConfigurationName,
                azureConfigurations,
                $"plan item '{item.PlanItemName}' (test case internal {item.TestCaseInternalId})",
                defaultAzureConfigurationId);
            assignments.Add(new ExpectedSuiteAssignment(item.PlanItemName, configurationId, item.ConfigurationName));
        }

        foreach (var caseEntry in expectedByCaseExternalId)
        {
            await SyncAndReconcileCaseMembershipForPlanAsync(
                client,
                azureContext.BaseUrl,
                planExternalId,
                caseEntry.Key,
                caseEntry.Value,
                planSuitesCache,
                cancellationToken);
        }
    }

    private async Task SyncAndReconcileCaseMembershipForPlanAsync(
        HttpClient client,
        string baseUrl,
        long planExternalId,
        long testCaseExternalId,
        IReadOnlyList<ExpectedSuiteAssignment> expectedAssignments,
        Dictionary<long, Dictionary<string, long>> planSuitesCache,
        CancellationToken cancellationToken)
    {
        var expectedBySuiteName = new Dictionary<string, List<ExpectedSuiteAssignment>>(StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in expectedAssignments)
        {
            var key = NormalizeSuiteName(assignment.SuiteName);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (!expectedBySuiteName.TryGetValue(key, out var suiteAssignments))
            {
                suiteAssignments = [];
                expectedBySuiteName[key] = suiteAssignments;
            }

            var alreadyPresent = suiteAssignments.Any(existing =>
                string.Equals(NormalizeSuiteName(existing.ConfigurationName), NormalizeSuiteName(assignment.ConfigurationName), StringComparison.OrdinalIgnoreCase)
                && existing.ConfigurationId == assignment.ConfigurationId);
            if (!alreadyPresent)
            {
                suiteAssignments.Add(assignment);
            }
        }

        foreach (var suiteEntry in expectedBySuiteName)
        {
            var assignment = suiteEntry.Value[0];
            var suiteId = await EnsureAzureSuiteForPlanItemAsync(client, baseUrl, planExternalId, assignment.SuiteName, planSuitesCache, cancellationToken);
            if (suiteId <= 0)
            {
                logger.LogWarning(
                    "SyncAndReconcile: failed to resolve suite for plan {PlanExternalId}, case {TestCaseExternalId}, suite '{SuiteName}'.",
                    planExternalId,
                    testCaseExternalId,
                    assignment.SuiteName);
                continue;
            }

            logger.LogInformation(
                "SyncAndReconcile: ensuring case {TestCaseExternalId} in plan {PlanExternalId}, suite {SuiteExternalId} ('{SuiteName}') with expectedConfigurations={ConfigurationCount}.",
                testCaseExternalId,
                planExternalId,
                suiteId,
                assignment.SuiteName,
                suiteEntry.Value.Count);

            await EnsureTestCaseAssignmentsInAzureSuiteAsync(
                client,
                baseUrl,
                planExternalId,
                suiteId,
                testCaseExternalId,
                suiteEntry.Value,
                cancellationToken);
        }

        var suites = await GetAzureSuitesAsync(client, baseUrl, planExternalId, cancellationToken);
        foreach (var suite in suites)
        {
            if (suite.ParentId is null)
            {
                continue;
            }

            var normalizedName = NormalizeSuiteName(suite.Name);
            if (expectedBySuiteName.ContainsKey(normalizedName))
            {
                continue;
            }

            logger.LogInformation(
                "SyncAndReconcile: removing stale suite membership for case {TestCaseExternalId} in plan {PlanExternalId}, suite {SuiteExternalId} ('{SuiteName}').",
                testCaseExternalId,
                planExternalId,
                suite.Id,
                suite.Name);

            await RemoveTestCaseFromAzureSuiteAsync(client, baseUrl, planExternalId, suite.Id, testCaseExternalId, cancellationToken);

            if (await IsTestCaseInAzureSuiteAsync(client, baseUrl, planExternalId, suite.Id, testCaseExternalId, cancellationToken))
            {
                logger.LogWarning(
                    "Stale suite membership still present after first delete attempt for case {TestCaseExternalId} in plan {PlanExternalId}, suite {SuiteExternalId}. Retrying delete once.",
                    testCaseExternalId,
                    planExternalId,
                    suite.Id);

                await RemoveTestCaseFromAzureSuiteAsync(client, baseUrl, planExternalId, suite.Id, testCaseExternalId, cancellationToken);

                if (await IsTestCaseInAzureSuiteAsync(client, baseUrl, planExternalId, suite.Id, testCaseExternalId, cancellationToken))
                {
                    throw new InvalidOperationException(
                        $"Azure stale-membership cleanup failed: case {testCaseExternalId} still exists in suite {suite.Id} for plan {planExternalId} after retry.");
                }
            }
        }
    }

    private async Task<HashSet<long>> LoadSyncedAzurePlanExternalIdsAsync(
        SqlConnection connection,
        long integrationConnectionId,
        CancellationToken cancellationToken)
    {
        var planIds = new HashSet<long>();

        var hasLinksTable = await HasIntegrationLinksTableAsync(connection, cancellationToken);
        if (hasLinksTable)
        {
            const string linksSql = """
                SELECT external_id
                FROM integration_links
                WHERE integration_connection_id = @connectionId
                  AND entity_type = 'test_plan'
                  AND external_id IS NOT NULL;
                """;

            await using var linksCommand = new SqlCommand(linksSql, connection);
            linksCommand.Parameters.AddWithValue("@connectionId", integrationConnectionId);
            await using var linksReader = await linksCommand.ExecuteReaderAsync(cancellationToken);
            while (await linksReader.ReadAsync(cancellationToken))
            {
                var externalId = GetString(linksReader, "external_id");
                if (TryParsePositiveLong(externalId, out var planExternalId))
                {
                    planIds.Add(planExternalId);
                }
            }
        }

        const string jobsSql = """
            SELECT DISTINCT external_id
            FROM integration_jobs
            WHERE integration_connection_id = @connectionId
              AND entity_type = 'test_plan'
              AND status = 'sent'
              AND external_id IS NOT NULL;
            """;

        await using var jobsCommand = new SqlCommand(jobsSql, connection);
        jobsCommand.Parameters.AddWithValue("@connectionId", integrationConnectionId);
        await using var jobsReader = await jobsCommand.ExecuteReaderAsync(cancellationToken);
        while (await jobsReader.ReadAsync(cancellationToken))
        {
            var externalId = GetString(jobsReader, "external_id");
            if (TryParsePositiveLong(externalId, out var planExternalId))
            {
                planIds.Add(planExternalId);
            }
        }

        return planIds;
    }

    private async Task<IReadOnlyList<PlanItemRef>> LoadPlanItemsForTestCaseAsync(SqlConnection connection, long clientId, long testCaseInternalId, CancellationToken cancellationToken)
    {
        var sql = await HasPointBasedConfigurationAssignmentsTableAsync(connection, cancellationToken)
            ? """
                SELECT DISTINCT
                    tp.id AS plan_internal_id,
                    tpi.id AS plan_item_id,
                    tpi.name AS plan_item_name,
                    COALESCE(assign_cfg.name, base_cfg.name) AS configuration_name
                FROM test_plan_item_suites parent
                INNER JOIN test_plan_items tpi ON tpi.id = parent.test_plan_item_id
                INNER JOIN test_plans tp ON tp.id = tpi.test_plan_id
                INNER JOIN test_designs td ON td.id = parent.test_design_id
                LEFT JOIN test_plan_item_suite_configurations assign
                    ON assign.test_plan_item_suite_id = parent.id
                   AND assign.deleted_at IS NULL
                LEFT JOIN configurations assign_cfg ON assign_cfg.id = assign.configuration_id
                LEFT JOIN configurations base_cfg ON base_cfg.id = td.configuration_id
                WHERE parent.test_design_id = @testCaseInternalId
                  AND parent.deleted_at IS NULL
                  AND parent.parent_id IS NULL
                  AND tp.client_id = @clientId;
                """
            : """
                SELECT DISTINCT
                    tp.id AS plan_internal_id,
                    tpi.id AS plan_item_id,
                    tpi.name AS plan_item_name,
                    cfg.name AS configuration_name
                FROM test_plan_item_suites tpis
                INNER JOIN test_plan_items tpi ON tpi.id = tpis.test_plan_item_id
                INNER JOIN test_plans tp ON tp.id = tpi.test_plan_id
                INNER JOIN test_designs td ON td.id = tpis.test_design_id
                LEFT JOIN configurations cfg ON cfg.id = td.configuration_id
                WHERE tpis.test_design_id = @testCaseInternalId
                  AND tpis.deleted_at IS NULL
                  AND tp.client_id = @clientId;
                """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@testCaseInternalId", testCaseInternalId);
        command.Parameters.AddWithValue("@clientId", clientId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var rows = new List<PlanItemRef>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var planInternalId = GetInt64(reader, "plan_internal_id") ?? 0;
            var planItemId = GetInt64(reader, "plan_item_id") ?? 0;
            var planItemName = GetString(reader, "plan_item_name") ?? $"Plan Item #{planItemId}";
            var configurationName = GetString(reader, "configuration_name");
            if (planInternalId <= 0 || planItemId <= 0)
            {
                continue;
            }

            rows.Add(new PlanItemRef(planInternalId, planItemId, planItemName, configurationName));
        }

        return rows;
    }

    private async Task<IReadOnlyList<PlanItemCaseRef>> LoadPlanItemCasesForPlanAsync(SqlConnection connection, long clientId, long planInternalId, CancellationToken cancellationToken)
    {
        var sql = await HasPointBasedConfigurationAssignmentsTableAsync(connection, cancellationToken)
            ? """
                SELECT DISTINCT
                    tpi.id AS plan_item_id,
                    tpi.name AS plan_item_name,
                    parent.test_design_id AS test_case_internal_id,
                    COALESCE(assign_cfg.name, base_cfg.name) AS configuration_name
                FROM test_plan_items tpi
                INNER JOIN test_plan_item_suites parent ON parent.test_plan_item_id = tpi.id
                INNER JOIN test_designs td ON td.id = parent.test_design_id
                LEFT JOIN test_plan_item_suite_configurations assign
                    ON assign.test_plan_item_suite_id = parent.id
                   AND assign.deleted_at IS NULL
                LEFT JOIN configurations assign_cfg ON assign_cfg.id = assign.configuration_id
                LEFT JOIN configurations base_cfg ON base_cfg.id = td.configuration_id
                INNER JOIN test_plans tp ON tp.id = tpi.test_plan_id
                WHERE tpi.test_plan_id = @planInternalId
                  AND parent.deleted_at IS NULL
                  AND parent.parent_id IS NULL
                  AND tp.client_id = @clientId;
                """
            : """
                SELECT DISTINCT
                    tpi.id AS plan_item_id,
                    tpi.name AS plan_item_name,
                    tpis.test_design_id AS test_case_internal_id,
                    cfg.name AS configuration_name
                FROM test_plan_items tpi
                INNER JOIN test_plan_item_suites tpis ON tpis.test_plan_item_id = tpi.id
                INNER JOIN test_designs td ON td.id = tpis.test_design_id
                LEFT JOIN configurations cfg ON cfg.id = td.configuration_id
                INNER JOIN test_plans tp ON tp.id = tpi.test_plan_id
                WHERE tpi.test_plan_id = @planInternalId
                  AND tpis.deleted_at IS NULL
                  AND tp.client_id = @clientId;
                """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@planInternalId", planInternalId);
        command.Parameters.AddWithValue("@clientId", clientId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var rows = new List<PlanItemCaseRef>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var planItemId = GetInt64(reader, "plan_item_id") ?? 0;
            var testCaseInternalId = GetInt64(reader, "test_case_internal_id") ?? 0;
            var planItemName = GetString(reader, "plan_item_name") ?? $"Plan Item #{planItemId}";
            var configurationName = GetString(reader, "configuration_name");
            if (planItemId <= 0 || testCaseInternalId <= 0)
            {
                continue;
            }

            rows.Add(new PlanItemCaseRef(planItemId, planItemName, testCaseInternalId, configurationName));
        }

        return rows;
    }

    private async Task<long> EnsureAzureSuiteForPlanItemAsync(
        HttpClient client,
        string baseUrl,
        long planExternalId,
        string planItemName,
        Dictionary<long, Dictionary<string, long>> planSuitesCache,
        CancellationToken cancellationToken)
    {
        if (!planSuitesCache.TryGetValue(planExternalId, out var byName))
        {
            byName = await LoadPlanSuitesByNameAsync(client, baseUrl, planExternalId, cancellationToken);
            planSuitesCache[planExternalId] = byName;
        }

        var normalizedName = NormalizeSuiteName(planItemName);
        if (byName.TryGetValue(normalizedName, out var existingSuiteId) && existingSuiteId > 0)
        {
            await EnsureSuiteDoesNotInheritConfigurationsAsync(client, baseUrl, planExternalId, existingSuiteId, cancellationToken);
            return existingSuiteId;
        }

        var rootSuiteId = await ResolveRootSuiteIdAsync(client, baseUrl, planExternalId, cancellationToken);
        if (rootSuiteId <= 0)
        {
            return 0;
        }

        var createdSuiteId = await CreateAzureSuiteAsync(client, baseUrl, planExternalId, planItemName, rootSuiteId, cancellationToken);
        if (createdSuiteId > 0)
        {
            await EnsureSuiteDoesNotInheritConfigurationsAsync(client, baseUrl, planExternalId, createdSuiteId, cancellationToken);
            byName[normalizedName] = createdSuiteId;
            return createdSuiteId;
        }

        byName = await LoadPlanSuitesByNameAsync(client, baseUrl, planExternalId, cancellationToken);
        planSuitesCache[planExternalId] = byName;
        return byName.TryGetValue(normalizedName, out var suiteId) ? suiteId : 0;
    }

    private async Task<Dictionary<string, long>> LoadPlanSuitesByNameAsync(HttpClient client, string baseUrl, long planExternalId, CancellationToken cancellationToken)
    {
        var suites = await GetAzureSuitesAsync(client, baseUrl, planExternalId, cancellationToken);
        var byName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var suite in suites)
        {
            if (suite.ParentId is null)
            {
                continue;
            }

            var key = NormalizeSuiteName(suite.Name);
            if (string.IsNullOrWhiteSpace(key) || byName.ContainsKey(key))
            {
                continue;
            }

            byName[key] = suite.Id;
        }

        return byName;
    }

    private async Task<long> ResolveRootSuiteIdAsync(HttpClient client, string baseUrl, long planExternalId, CancellationToken cancellationToken)
    {
        var suites = await GetAzureSuitesAsync(client, baseUrl, planExternalId, cancellationToken);
        var rootSuite = suites.FirstOrDefault(suite => suite.ParentId is null);
        return rootSuite?.Id ?? 0;
    }

    private async Task<IReadOnlyList<AzureSuiteRef>> GetAzureSuitesAsync(HttpClient client, string baseUrl, long planExternalId, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync($"{baseUrl}/_apis/testplan/Plans/{planExternalId}/suites?api-version=7.1", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Azure suites list failed: {(int)response.StatusCode} {body}");
        }

        var json = JsonNode.Parse(body)?.AsObject();
        if (json?["value"] is not JsonArray suitesArray)
        {
            return [];
        }

        var suites = new List<AzureSuiteRef>();
        foreach (var node in suitesArray)
        {
            if (node is not JsonObject suiteObj)
            {
                continue;
            }

            if (!TryParsePositiveLong(suiteObj["id"]?.ToString(), out var suiteId))
            {
                continue;
            }

            long? parentId = null;
            if (TryParsePositiveLong(suiteObj["parentSuite"]?["id"]?.ToString(), out var parsedParentId))
            {
                parentId = parsedParentId;
            }

            suites.Add(new AzureSuiteRef(suiteId, suiteObj["name"]?.ToString() ?? string.Empty, parentId));
        }

        return suites;
    }

    private async Task<long> CreateAzureSuiteAsync(HttpClient client, string baseUrl, long planExternalId, string suiteName, long parentSuiteId, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["name"] = suiteName,
            ["suiteType"] = "StaticTestSuite",
            ["parentSuite"] = new JsonObject { ["id"] = parentSuiteId },
            ["inheritDefaultConfigurations"] = false
        };

        using var response = await client.PostAsync(
            $"{baseUrl}/_apis/testplan/Plans/{planExternalId}/suites?api-version=7.1",
            new StringContent(body.ToJsonString(JsonOptions), Encoding.UTF8, "application/json"),
            cancellationToken);

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Azure suite create failed: {(int)response.StatusCode} {responseBody}");
        }

        var json = JsonNode.Parse(responseBody)?.AsObject();
        return TryParsePositiveLong(json?["id"]?.ToString(), out var suiteId) ? suiteId : 0;
    }

    private async Task EnsureSuiteDoesNotInheritConfigurationsAsync(HttpClient client, string baseUrl, long planExternalId, long suiteExternalId, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, $"{baseUrl}/_apis/testplan/Plans/{planExternalId}/Suites/{suiteExternalId}?api-version=7.1")
        {
            Content = new StringContent("{\"inheritDefaultConfigurations\":false}", Encoding.UTF8, "application/json")
        };

        using var response = await client.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Azure suite update failed: {(int)response.StatusCode} {responseBody}");
        }
    }

    private async Task<Dictionary<string, long>> LoadAzureConfigurationsByNameAsync(HttpClient client, string baseUrl, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync($"{baseUrl}/_apis/testplan/configurations?api-version=7.1", cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Azure configurations list failed: {(int)response.StatusCode} {responseBody}");
        }

        var parsed = JsonNode.Parse(responseBody)?.AsObject();
        if (parsed?["value"] is not JsonArray values)
        {
            return new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        }

        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in values)
        {
            if (node is not JsonObject item)
            {
                continue;
            }

            var name = NormalizeSuiteName(item["name"]?.ToString());
            if (string.IsNullOrWhiteSpace(name) || map.ContainsKey(name))
            {
                continue;
            }

            if (!TryParsePositiveLong(item["id"]?.ToString(), out var id))
            {
                continue;
            }

            map[name] = id;
        }

        return map;
    }

    private long? ResolveAzureConfigurationId(
        string? localConfigurationName,
        IReadOnlyDictionary<string, long> azureConfigurationsByName,
        string context,
        long? defaultAzureConfigurationId)
    {
        var normalized = NormalizeSuiteName(localConfigurationName);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            if (defaultAzureConfigurationId.HasValue && defaultAzureConfigurationId.Value > 0)
            {
                logger.LogInformation(
                    "Azure plan-item sync is using default Azure configuration {ConfigurationId} for {Context} because QAF-OnPrem configuration is missing.",
                    defaultAzureConfigurationId.Value,
                    context);
                return defaultAzureConfigurationId.Value;
            }

            logger.LogWarning(
                "Azure plan-item sync will add suite membership without configuration assignment because QAF-OnPrem configuration is missing for {Context}.",
                context);
            return null;
        }

        if (!azureConfigurationsByName.TryGetValue(normalized, out var configurationId) || configurationId <= 0)
        {
            if (defaultAzureConfigurationId.HasValue && defaultAzureConfigurationId.Value > 0)
            {
                logger.LogInformation(
                    "Azure plan-item sync is using default Azure configuration {ConfigurationId} for {Context} because mapped configuration '{ConfigurationName}' was not found.",
                    defaultAzureConfigurationId.Value,
                    context,
                    normalized);
                return defaultAzureConfigurationId.Value;
            }

            logger.LogWarning(
                "Azure plan-item sync will add suite membership without configuration assignment because Azure configuration '{ConfigurationName}' was not found for {Context}.",
                normalized,
                context);
            return null;
        }

        return configurationId;
    }

    private async Task EnsureTestCaseAssignmentsInAzureSuiteAsync(
        HttpClient client,
        string baseUrl,
        long planExternalId,
        long suiteExternalId,
        long testCaseExternalId,
        IReadOnlyList<ExpectedSuiteAssignment> expectedAssignments,
        CancellationToken cancellationToken)
    {
        var expectedConfigurationIds = expectedAssignments
            .Where(assignment => assignment.ConfigurationId.HasValue && assignment.ConfigurationId.Value > 0)
            .Select(assignment => assignment.ConfigurationId!.Value)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();

        var existingPoints = await GetAzureTestPointsForCaseAsync(client, baseUrl, planExternalId, suiteExternalId, testCaseExternalId, cancellationToken);
        var existingConfigurationIds = existingPoints
            .Where(point => point.ConfigurationId > 0)
            .Select(point => point.ConfigurationId)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        var existingMembership = existingPoints.Count > 0
            || await IsTestCaseInAzureSuiteAsync(client, baseUrl, planExternalId, suiteExternalId, testCaseExternalId, cancellationToken);

        if (expectedConfigurationIds.Length == 0)
        {
            if (existingMembership)
            {
                return;
            }

            var addedWithoutConfigurations = await AddTestCaseToAzureSuiteAsync(
                client,
                baseUrl,
                planExternalId,
                suiteExternalId,
                testCaseExternalId,
                null,
                cancellationToken);
            if (addedWithoutConfigurations)
            {
                return;
            }

            if (await IsTestCaseInAzureSuiteAsync(client, baseUrl, planExternalId, suiteExternalId, testCaseExternalId, cancellationToken))
            {
                return;
            }

            throw new InvalidOperationException(
                $"Azure sync failed: could not confirm unconfigured suite membership for case {testCaseExternalId} in suite {suiteExternalId}.");
        }

        var configurationSetsMatch = existingConfigurationIds.SequenceEqual(expectedConfigurationIds);
        if (existingMembership && configurationSetsMatch && existingPoints.All(point => point.ConfigurationId > 0))
        {
            return;
        }

        if (existingMembership)
        {
            await RemoveTestCaseMembershipWithRetryAsync(client, baseUrl, planExternalId, suiteExternalId, testCaseExternalId, cancellationToken);
        }

        var added = await AddTestCaseToAzureSuiteAsync(
            client,
            baseUrl,
            planExternalId,
            suiteExternalId,
            testCaseExternalId,
            expectedConfigurationIds,
            cancellationToken);

        if (added)
        {
            existingPoints = await GetAzureTestPointsForCaseAsync(client, baseUrl, planExternalId, suiteExternalId, testCaseExternalId, cancellationToken);
            existingConfigurationIds = existingPoints
                .Where(point => point.ConfigurationId > 0)
                .Select(point => point.ConfigurationId)
                .Distinct()
                .OrderBy(id => id)
                .ToArray();

            if (existingConfigurationIds.SequenceEqual(expectedConfigurationIds))
            {
                return;
            }
        }

        existingPoints = await GetAzureTestPointsForCaseAsync(client, baseUrl, planExternalId, suiteExternalId, testCaseExternalId, cancellationToken);
        existingConfigurationIds = existingPoints
            .Where(point => point.ConfigurationId > 0)
            .Select(point => point.ConfigurationId)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        if (existingConfigurationIds.SequenceEqual(expectedConfigurationIds))
        {
            return;
        }

        var expected = string.Join(", ", expectedAssignments.Select(FormatExpectedAssignment));
        var actual = existingPoints.Count == 0
            ? "none"
            : string.Join(", ", existingPoints.Select(point => !string.IsNullOrWhiteSpace(point.ConfigurationName)
                ? $"{point.ConfigurationName} ({point.ConfigurationId})"
                : point.ConfigurationId > 0
                    ? point.ConfigurationId.ToString()
                    : "unconfigured"));
        throw new InvalidOperationException(
            $"Azure sync failed: suite {suiteExternalId} contains case {testCaseExternalId} with Azure point configuration set [{actual}], expected [{expected}].");
    }

    private async Task<bool> AddTestCaseToAzureSuiteAsync(
        HttpClient client,
        string baseUrl,
        long planExternalId,
        long suiteExternalId,
        long testCaseExternalId,
        IReadOnlyList<long>? configurationIds,
        CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["workItem"] = new JsonObject { ["id"] = testCaseExternalId.ToString() }
        };

        var positiveConfigurationIds = configurationIds?
            .Where(id => id > 0)
            .Distinct()
            .ToArray();
        if (positiveConfigurationIds is { Length: > 0 })
        {
            var pointAssignments = new JsonArray();
            foreach (var configurationId in positiveConfigurationIds)
            {
                pointAssignments.Add(new JsonObject
                {
                    ["configurationId"] = configurationId
                });
            }

            payload["pointAssignments"] = pointAssignments;
        }

        var requestBody = new JsonArray
        {
            payload
        };

        using var response = await client.PostAsync(
            $"{baseUrl}/_apis/testplan/Plans/{planExternalId}/Suites/{suiteExternalId}/TestCase?api-version=7.1",
            new StringContent(requestBody.ToJsonString(JsonOptions), Encoding.UTF8, "application/json"),
            cancellationToken);

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            logger.LogInformation(
                "AddTestCaseToAzureSuite succeeded for case {TestCaseExternalId}, plan {PlanExternalId}, suite {SuiteExternalId}, configurationIds={ConfigurationIds}.",
                testCaseExternalId,
                planExternalId,
                suiteExternalId,
                positiveConfigurationIds is null ? "none" : string.Join(",", positiveConfigurationIds));
            return true;
        }

        var loweredBody = responseBody.ToLowerInvariant();
        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest
            && (loweredBody.Contains("already") || loweredBody.Contains("exists") || loweredBody.Contains("duplicate")))
        {
            logger.LogInformation(
                "AddTestCaseToAzureSuite duplicate detected for case {TestCaseExternalId}, plan {PlanExternalId}, suite {SuiteExternalId}.",
                testCaseExternalId,
                planExternalId,
                suiteExternalId);
            return false;
        }

        throw new InvalidOperationException($"Azure suite add test case failed: {(int)response.StatusCode} {responseBody}");
    }

    private async Task RemoveTestCaseFromAzureSuiteAsync(
        HttpClient client,
        string baseUrl,
        long planExternalId,
        long suiteExternalId,
        long testCaseExternalId,
        CancellationToken cancellationToken)
    {
        using var response = await client.DeleteAsync(
            $"{baseUrl}/_apis/test/Plans/{planExternalId}/suites/{suiteExternalId}/testcases/{testCaseExternalId}?api-version=7.1",
            cancellationToken);

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            logger.LogInformation(
                "RemoveTestCaseFromAzureSuite succeeded for case {TestCaseExternalId}, plan {PlanExternalId}, suite {SuiteExternalId}.",
                testCaseExternalId,
                planExternalId,
                suiteExternalId);
            return;
        }

        var lowered = responseBody.ToLowerInvariant();
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound
            || lowered.Contains("could not be found")
            || lowered.Contains("does not exist"))
        {
            return;
        }

        throw new InvalidOperationException($"Azure suite remove test case failed: {(int)response.StatusCode} {responseBody}");
    }

    private async Task<IReadOnlyList<AzurePointRef>> GetAzureTestPointsForCaseAsync(
        HttpClient client,
        string baseUrl,
        long planExternalId,
        long suiteExternalId,
        long testCaseExternalId,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            $"{baseUrl}/_apis/testplan/Plans/{planExternalId}/Suites/{suiteExternalId}/TestPoint?api-version=7.1",
            cancellationToken);

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Azure test point list failed: {(int)response.StatusCode} {responseBody}");
        }

        var root = JsonNode.Parse(responseBody)?.AsObject();
        if (root?["value"] is not JsonArray points)
        {
            return [];
        }

        var result = new List<AzurePointRef>();

        foreach (var node in points)
        {
            if (node is not JsonObject point)
            {
                continue;
            }

            if (!TryParsePositiveLong(point["testCaseReference"]?["id"]?.ToString(), out var caseId) || caseId != testCaseExternalId)
            {
                continue;
            }

            var configurationName = point["configuration"]?["name"]?.ToString();
            var configurationId = TryParsePositiveLong(point["configuration"]?["id"]?.ToString(), out var parsedConfigurationId)
                ? parsedConfigurationId
                : 0;

            result.Add(new AzurePointRef(caseId, configurationId, configurationName));
        }

        return result;
    }

    private async Task RemoveTestCaseMembershipWithRetryAsync(
        HttpClient client,
        string baseUrl,
        long planExternalId,
        long suiteExternalId,
        long testCaseExternalId,
        CancellationToken cancellationToken)
    {
        await RemoveTestCaseFromAzureSuiteAsync(client, baseUrl, planExternalId, suiteExternalId, testCaseExternalId, cancellationToken);
        if (!await IsTestCaseInAzureSuiteAsync(client, baseUrl, planExternalId, suiteExternalId, testCaseExternalId, cancellationToken))
        {
            return;
        }

        logger.LogWarning(
            "Suite membership still present after first delete attempt for case {TestCaseExternalId}, plan {PlanExternalId}, suite {SuiteExternalId}. Retrying delete once.",
            testCaseExternalId,
            planExternalId,
            suiteExternalId);

        await RemoveTestCaseFromAzureSuiteAsync(client, baseUrl, planExternalId, suiteExternalId, testCaseExternalId, cancellationToken);
        if (await IsTestCaseInAzureSuiteAsync(client, baseUrl, planExternalId, suiteExternalId, testCaseExternalId, cancellationToken))
        {
            throw new InvalidOperationException(
                $"Azure stale-membership cleanup failed: case {testCaseExternalId} still exists in suite {suiteExternalId} for plan {planExternalId} after retry.");
        }
    }

    private static string FormatExpectedAssignment(ExpectedSuiteAssignment assignment)
    {
        if (assignment.ConfigurationId.HasValue && assignment.ConfigurationId.Value > 0)
        {
            return !string.IsNullOrWhiteSpace(assignment.ConfigurationName)
                ? $"{assignment.ConfigurationName} ({assignment.ConfigurationId.Value})"
                : assignment.ConfigurationId.Value.ToString();
        }

        return "unconfigured";
    }

    private async Task<bool> IsTestCaseInAzureSuiteAsync(
        HttpClient client,
        string baseUrl,
        long planExternalId,
        long suiteExternalId,
        long testCaseExternalId,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(
            $"{baseUrl}/_apis/testplan/Plans/{planExternalId}/Suites/{suiteExternalId}/TestCase?api-version=7.1",
            cancellationToken);

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Azure suite test case list failed: {(int)response.StatusCode} {responseBody}");
        }

        var root = JsonNode.Parse(responseBody)?.AsObject();
        if (root?["value"] is not JsonArray values)
        {
            return false;
        }

        foreach (var node in values)
        {
            if (node is not JsonObject item)
            {
                continue;
            }

            if (TryParsePositiveLong(item["workItem"]?["id"]?.ToString(), out var caseId) && caseId == testCaseExternalId)
            {
                return true;
            }
        }

        return false;
    }

    private async Task<long?> LoadDefaultAzureConfigurationIdAsync(HttpClient client, string baseUrl, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync($"{baseUrl}/_apis/testplan/configurations?api-version=7.1", cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Azure configurations list failed: {(int)response.StatusCode} {responseBody}");
        }

        var parsed = JsonNode.Parse(responseBody)?.AsObject();
        if (parsed?["value"] is not JsonArray values)
        {
            return null;
        }

        long? firstActive = null;
        foreach (var node in values)
        {
            if (node is not JsonObject item)
            {
                continue;
            }

            if (!TryParsePositiveLong(item["id"]?.ToString(), out var id))
            {
                continue;
            }

            var state = item["state"]?.ToString();
            var isActive = string.Equals(state, "active", StringComparison.OrdinalIgnoreCase);
            if (firstActive is null && isActive)
            {
                firstActive = id;
            }

            var isDefault = item["isDefault"]?.GetValue<bool?>() == true;
            if (isDefault && isActive)
            {
                return id;
            }
        }

        return firstActive;
    }

    private HttpClient CreateAzureClient(string pat)
    {
        var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}")));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private static bool TryCreateAzureContext(JsonObject config, JsonObject credentials, out AzureContext context)
    {
        var organization = config["organization"]?.GetValue<string>()?.Trim();
        var project = config["project"]?.GetValue<string>()?.Trim();
        var pat = credentials["pat"]?.GetValue<string>()?.Trim();

        if (string.IsNullOrWhiteSpace(organization)
            || string.IsNullOrWhiteSpace(project)
            || string.IsNullOrWhiteSpace(pat))
        {
            context = new AzureContext(string.Empty, string.Empty);
            return false;
        }

        context = new AzureContext($"https://dev.azure.com/{organization}/{project}", pat);
        return true;
    }

    private static string NormalizeSuiteName(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    private static bool TryParsePositiveLong(string? raw, out long value)
    {
        value = 0;
        return long.TryParse(raw, out value) && value > 0;
    }

    private static async Task<IntegrationResult> UpsertAzureTestRunAsync(HttpClient client, string baseUrl, JsonObject payload, ExistingLink existingLink, CancellationToken cancellationToken)
    {
        var planExternalId = payload["test_plan_external_id"]?.ToString()?.Trim();
        if (!long.TryParse(planExternalId, out var planId) || planId <= 0)
        {
            throw new InvalidOperationException("Azure run sync requires a synced test plan link.");
        }

        var runId = existingLink.ExternalId?.Trim();
        var statusId = TryParseLongNode(payload["status_id"], out var parsedStatusId) ? parsedStatusId : 0;
        var runState = statusId == 2
            ? "InProgress"
            : (string.IsNullOrWhiteSpace(runId) ? "InProgress" : "Completed");

        var requestBody = new JsonObject
        {
            ["name"] = payload["name"]?.ToString()?.Trim() ?? $"Run #{payload["internal_id"]}",
            ["state"] = runState,
            ["comment"] = payload["comment"]?.ToString() ?? string.Empty
        };

        var startedDate = payload["started_at"]?.ToString()?.Trim();
        if (!string.IsNullOrWhiteSpace(startedDate))
        {
            requestBody["startedDate"] = startedDate;
        }

        var completedDate = payload["executed_at"]?.ToString()?.Trim();
        if (!string.IsNullOrWhiteSpace(completedDate) && runState == "Completed")
        {
            requestBody["completedDate"] = completedDate;
        }

        HttpRequestMessage runRequest;
        if (!string.IsNullOrWhiteSpace(runId))
        {
            runRequest = new HttpRequestMessage(HttpMethod.Patch, $"{baseUrl}/_apis/test/runs/{runId}?api-version=7.0")
            {
                Content = new StringContent(requestBody.ToJsonString(JsonOptions), Encoding.UTF8, "application/json")
            };
        }
        else
        {
            requestBody["plan"] = new JsonObject { ["id"] = planId.ToString() };
            runRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/_apis/test/runs?api-version=7.0")
            {
                Content = new StringContent(requestBody.ToJsonString(JsonOptions), Encoding.UTF8, "application/json")
            };
        }

        using var runResponse = await client.SendAsync(runRequest, cancellationToken);
        var runBody = await runResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!runResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Azure run upsert failed: {(int)runResponse.StatusCode} {runBody}");
        }

        var runJson = JsonNode.Parse(runBody)?.AsObject() ?? new JsonObject();
        runId = runJson["id"]?.ToString() ?? runId;
        if (string.IsNullOrWhiteSpace(runId))
        {
            throw new InvalidOperationException("Azure run upsert failed: missing run id in response.");
        }

        await UpsertAzureRunResultAsync(client, baseUrl, runId, payload, statusId, cancellationToken);
        return new IntegrationResult(runId, runId, runJson);
    }

    private static async Task UpsertAzureRunResultAsync(HttpClient client, string baseUrl, string runId, JsonObject payload, long statusId, CancellationToken cancellationToken)
    {
        if (statusId == 2)
        {
            return;
        }

        var testCaseExternalId = payload["test_case_external_id"]?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(testCaseExternalId))
        {
            return;
        }

        var outcome = statusId switch
        {
            3 => "Passed",
            4 or 5 => "Failed",
            6 => "Blocked",
            _ => "NotExecuted"
        };

        var resultComment = BuildRunResultComment(payload);
        var startedDate = payload["started_at"]?.ToString()?.Trim();
        var completedDate = payload["executed_at"]?.ToString()?.Trim();

        var existingResult = await FindExistingAzureRunResultAsync(client, baseUrl, runId, testCaseExternalId, cancellationToken);
        if (existingResult is not null)
        {
            var updateItem = new JsonObject
            {
                ["id"] = TryParseLongNode(existingResult["id"], out var existingId) ? existingId : 0,
                ["state"] = "Completed",
                ["outcome"] = outcome,
                ["comment"] = resultComment
            };

            if (!string.IsNullOrWhiteSpace(startedDate))
            {
                updateItem["startedDate"] = startedDate;
            }

            if (!string.IsNullOrWhiteSpace(completedDate))
            {
                updateItem["completedDate"] = completedDate;
            }

            var updatePayload = new JsonArray { updateItem };
            using var updateResponse = await client.PatchAsync(
                $"{baseUrl}/_apis/test/runs/{runId}/results?api-version=7.0",
                new StringContent(updatePayload.ToJsonString(JsonOptions), Encoding.UTF8, "application/json"),
                cancellationToken);

            var updateBody = await updateResponse.Content.ReadAsStringAsync(cancellationToken);
            if (!updateResponse.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Azure test result update failed: {(int)updateResponse.StatusCode} {updateBody}");
            }

            return;
        }

        var createItem = new JsonObject
        {
            ["testCase"] = new JsonObject { ["id"] = testCaseExternalId },
            ["testCaseTitle"] = payload["title"]?.ToString() ?? $"Test Case #{testCaseExternalId}",
            ["automatedTestName"] = $"QAF-OnPrem-{payload["internal_id"]}",
            ["state"] = "Completed",
            ["outcome"] = outcome,
            ["comment"] = resultComment
        };

        if (!string.IsNullOrWhiteSpace(startedDate))
        {
            createItem["startedDate"] = startedDate;
        }

        if (!string.IsNullOrWhiteSpace(completedDate))
        {
            createItem["completedDate"] = completedDate;
        }

        var createPayload = new JsonArray { createItem };
        using var createResponse = await client.PostAsync(
            $"{baseUrl}/_apis/test/runs/{runId}/results?api-version=7.0",
            new StringContent(createPayload.ToJsonString(JsonOptions), Encoding.UTF8, "application/json"),
            cancellationToken);

        var createBody = await createResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!createResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Azure test result create failed: {(int)createResponse.StatusCode} {createBody}");
        }
    }

    private static async Task<JsonObject?> FindExistingAzureRunResultAsync(HttpClient client, string baseUrl, string runId, string testCaseExternalId, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync($"{baseUrl}/_apis/test/runs/{runId}/results?api-version=7.0", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var json = JsonNode.Parse(body)?.AsObject();
        if (json?["value"] is not JsonArray valueArray)
        {
            return null;
        }

        foreach (var item in valueArray)
        {
            if (item is not JsonObject obj)
            {
                continue;
            }

            var caseId = obj["testCase"]?["id"]?.ToString()?.Trim();
            if (!string.IsNullOrWhiteSpace(caseId) && string.Equals(caseId, testCaseExternalId, StringComparison.Ordinal))
            {
                return obj;
            }
        }

        return null;
    }

    private static async Task<IntegrationResult> UpsertAzureWorkItemAsync(HttpClient client, string baseUrl, string defaultType, JsonObject payload, ExistingLink existingLink, JsonObject config, CancellationToken cancellationToken)
    {
        var workItemType = payload["work_item_type"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(workItemType))
        {
            workItemType = defaultType;
        }

        var title = payload["title"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            title = "Untitled";
        }

        var summary = payload["summary"]?.GetValue<string>()
            ?? payload["prerequisite"]?.GetValue<string>()
            ?? payload["description"]?.GetValue<string>()
            ?? string.Empty;

        var operations = new JsonArray
        {
            new JsonObject { ["op"] = "add", ["path"] = "/fields/System.Title", ["value"] = title },
            new JsonObject { ["op"] = "add", ["path"] = "/fields/System.Description", ["value"] = summary }
        };

        var isTestCase = workItemType.Equals("$Test Case", StringComparison.OrdinalIgnoreCase)
            || workItemType.Equals("Test Case", StringComparison.OrdinalIgnoreCase);
        if (isTestCase)
        {
            var manualSteps = payload["manual_steps"] as JsonArray;
            var stepsFieldValue = BuildAzureStepsField(manualSteps);
            if (!string.IsNullOrWhiteSpace(stepsFieldValue))
            {
                operations.Add(new JsonObject { ["op"] = "add", ["path"] = "/fields/Microsoft.VSTS.TCM.Steps", ["value"] = stepsFieldValue });
            }

            var parametersFieldValue = BuildAzureParametersField(manualSteps);
            if (!string.IsNullOrWhiteSpace(parametersFieldValue))
            {
                operations.Add(new JsonObject { ["op"] = "add", ["path"] = "/fields/Microsoft.VSTS.TCM.Parameters", ["value"] = parametersFieldValue });
            }

            var localDataSourceFieldValue = BuildAzureLocalDataSourceField(manualSteps);
            if (!string.IsNullOrWhiteSpace(localDataSourceFieldValue))
            {
                operations.Add(new JsonObject { ["op"] = "add", ["path"] = "/fields/Microsoft.VSTS.TCM.LocalDataSource", ["value"] = localDataSourceFieldValue });
            }
        }

        var state = payload["state"]?.GetValue<string>()?.Trim();
        var shouldCreateTestCaseInDesign = isTestCase
            && string.IsNullOrWhiteSpace(existingLink.ExternalId)
            && string.Equals(state, "Ready", StringComparison.OrdinalIgnoreCase);
        var initialState = shouldCreateTestCaseInDesign ? "Design" : state;
        if (!string.IsNullOrWhiteSpace(initialState))
        {
            operations.Add(new JsonObject { ["op"] = "add", ["path"] = "/fields/System.State", ["value"] = initialState });
        }

        var areaPath = payload["area_path"]?.GetValue<string>()?.Trim();
        var iterationPath = payload["iteration_path"]?.GetValue<string>()?.Trim();
        var primaryTestManagement = payload["project_primary_test_management"]?.GetValue<string>()?.Trim();
        if (isTestCase && string.Equals(primaryTestManagement, "Azure", StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(areaPath) || string.IsNullOrWhiteSpace(iterationPath)))
        {
            throw new InvalidOperationException("Azure test case sync requires project area path and test case iteration path.");
        }

        if (!string.IsNullOrWhiteSpace(areaPath))
        {
            operations.Add(new JsonObject { ["op"] = "add", ["path"] = "/fields/System.AreaPath", ["value"] = areaPath });
        }

        if (!string.IsNullOrWhiteSpace(iterationPath))
        {
            operations.Add(new JsonObject { ["op"] = "add", ["path"] = "/fields/System.IterationPath", ["value"] = iterationPath });
        }

        string endpoint;
        HttpMethod method;
        if (!string.IsNullOrWhiteSpace(existingLink.ExternalId))
        {
            endpoint = $"{baseUrl}/_apis/wit/workitems/{existingLink.ExternalId}?api-version=7.0";
            method = HttpMethod.Patch;
        }
        else
        {
            endpoint = $"{baseUrl}/_apis/wit/workitems/{Uri.EscapeDataString(workItemType)}?api-version=7.0";
            method = HttpMethod.Post;
        }

        using var request = new HttpRequestMessage(method, endpoint)
        {
            Content = new StringContent(operations.ToJsonString(JsonOptions), Encoding.UTF8, "application/json-patch+json")
        };

        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Azure work item upsert failed: {(int)response.StatusCode} {body}");
        }

        var json = JsonNode.Parse(body)?.AsObject();
        var externalId = json?["id"]?.ToString();

        if (shouldCreateTestCaseInDesign && !string.IsNullOrWhiteSpace(externalId))
        {
            await UpdateAzureWorkItemStateAsync(client, baseUrl, externalId, state!, cancellationToken);
        }

        return new IntegrationResult(externalId, externalId, json ?? new JsonObject());
    }

    private static async Task UpdateAzureWorkItemStateAsync(HttpClient client, string baseUrl, string externalId, string state, CancellationToken cancellationToken)
    {
        var operations = new JsonArray
        {
            new JsonObject { ["op"] = "add", ["path"] = "/fields/System.State", ["value"] = state }
        };

        using var request = new HttpRequestMessage(HttpMethod.Patch, $"{baseUrl}/_apis/wit/workitems/{externalId}?api-version=7.0")
        {
            Content = new StringContent(operations.ToJsonString(JsonOptions), Encoding.UTF8, "application/json-patch+json")
        };

        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Azure work item state transition failed: {(int)response.StatusCode} {body}");
        }
    }

    private static async Task<IntegrationResult> UpsertAzureTestPlanAsync(HttpClient client, string baseUrl, JsonObject payload, ExistingLink existingLink, JsonObject config, CancellationToken cancellationToken)
    {
        var name = payload["name"]?.GetValue<string>()?.Trim() ?? payload["title"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Azure test plan sync requires a plan name.");
        }

        var requestBody = new JsonObject
        {
            ["name"] = name
        };

        var areaPath = payload["area_path"]?.GetValue<string>()?.Trim();
        if (!string.IsNullOrWhiteSpace(areaPath))
        {
            requestBody["areaPath"] = areaPath;
        }

        var description = payload["description"]?.GetValue<string>()?.Trim();
        if (!string.IsNullOrWhiteSpace(description))
        {
            requestBody["description"] = description;
        }

        var iteration = payload["iteration"]?.GetValue<string>()?.Trim();
        if (!string.IsNullOrWhiteSpace(iteration))
        {
            requestBody["iteration"] = iteration;
        }

        var state = payload["state"]?.GetValue<string>()?.Trim();
        if (!string.IsNullOrWhiteSpace(state))
        {
            requestBody["state"] = state;
        }

        var startDate = payload["start_date"]?.GetValue<string>()?.Trim();
        if (!string.IsNullOrWhiteSpace(startDate))
        {
            requestBody["startDate"] = startDate;
        }

        var endDate = payload["end_date"]?.GetValue<string>()?.Trim();
        if (!string.IsNullOrWhiteSpace(endDate))
        {
            requestBody["endDate"] = endDate;
        }

        HttpRequestMessage request;
        if (!string.IsNullOrWhiteSpace(existingLink.ExternalId))
        {
            var getResponse = await client.GetAsync($"{baseUrl}/_apis/testplan/plans/{existingLink.ExternalId}?api-version=7.1", cancellationToken);
            var getBody = await getResponse.Content.ReadAsStringAsync(cancellationToken);
            if (!getResponse.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Azure test plan lookup failed: {(int)getResponse.StatusCode} {getBody}");
            }

            var currentJson = JsonNode.Parse(getBody)?.AsObject();
            requestBody["revision"] = currentJson?["revision"]?.GetValue<int>() ?? 0;

            request = new HttpRequestMessage(HttpMethod.Patch, $"{baseUrl}/_apis/testplan/plans/{existingLink.ExternalId}?api-version=7.1")
            {
                Content = new StringContent(requestBody.ToJsonString(JsonOptions), Encoding.UTF8, "application/json")
            };
        }
        else
        {
            request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/_apis/testplan/plans?api-version=7.1")
            {
                Content = new StringContent(requestBody.ToJsonString(JsonOptions), Encoding.UTF8, "application/json")
            };
        }

        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Azure test plan upsert failed: {(int)response.StatusCode} {body}");
        }

        var json = JsonNode.Parse(body)?.AsObject();
        var externalId = json?["id"]?.ToString();
        return new IntegrationResult(externalId, externalId, json ?? new JsonObject());
    }

    private async Task CompleteSentAsync(SqlConnection connection, ClaimedJob job, JsonObject payload, IntegrationResult result, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE integration_jobs
            SET
                status = 'sent',
                sent_at = SYSUTCDATETIME(),
                last_error = NULL,
                external_id = @externalId,
                external_key = @externalKey,
                payload_json = @payloadJson,
                updated_at = SYSUTCDATETIME()
            WHERE id = @id;
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", job.Id);
        command.Parameters.AddWithValue("@externalId", (object?)result.ExternalId ?? DBNull.Value);
        command.Parameters.AddWithValue("@externalKey", (object?)result.ExternalKey ?? DBNull.Value);
        command.Parameters.AddWithValue("@payloadJson", payload.ToJsonString(JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task CompleteFailedAsync(SqlConnection connection, ClaimedJob job, string message, CancellationToken cancellationToken)
    {
        var errorText = Truncate(message, 1800);
        if (job.Attempts >= Math.Max(1, job.MaxAttempts))
        {
            const string finalSql = """
                UPDATE integration_jobs
                SET
                    status = 'failed',
                    last_error = @error,
                    updated_at = SYSUTCDATETIME()
                WHERE id = @id;
                """;

            await using var finalCommand = new SqlCommand(finalSql, connection);
            finalCommand.Parameters.AddWithValue("@id", job.Id);
            finalCommand.Parameters.AddWithValue("@error", errorText);
            await finalCommand.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        var backoffSeconds = job.Attempts switch
        {
            1 => 10,
            2 => 30,
            _ => 60
        };

        const string retrySql = """
            UPDATE integration_jobs
            SET
                status = 'pending',
                last_error = @error,
                scheduled_at = DATEADD(SECOND, @backoffSeconds, SYSUTCDATETIME()),
                updated_at = SYSUTCDATETIME()
            WHERE id = @id;
            """;

        await using var retryCommand = new SqlCommand(retrySql, connection);
        retryCommand.Parameters.AddWithValue("@id", job.Id);
        retryCommand.Parameters.AddWithValue("@error", errorText);
        retryCommand.Parameters.AddWithValue("@backoffSeconds", backoffSeconds);
        await retryCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task UpsertIntegrationLinkAsync(SqlConnection connection, long connectionId, ClaimedJob job, JsonObject payload, IntegrationResult result, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(result.ExternalId) || !await HasIntegrationLinksTableAsync(connection, cancellationToken))
        {
            return;
        }

        const string sql = """
            MERGE integration_links AS target
            USING (
                SELECT @connectionId AS integration_connection_id, @entityType AS entity_type, @internalId AS internal_id
            ) AS source
            ON target.integration_connection_id = source.integration_connection_id
               AND target.entity_type = source.entity_type
               AND target.internal_id = source.internal_id
            WHEN MATCHED THEN
                UPDATE SET
                    client_id = @clientId,
                    external_id = @externalId,
                    external_key = @externalKey,
                    last_payload_json = @payloadJson,
                    last_synced_at = SYSUTCDATETIME(),
                    last_sync_status = 'success',
                    last_error = NULL
            WHEN NOT MATCHED THEN
                INSERT
                (
                    client_id,
                    integration_connection_id,
                    entity_type,
                    internal_id,
                    external_id,
                    external_key,
                    last_payload_json,
                    last_synced_at,
                    last_sync_status,
                    last_error
                )
                VALUES
                (
                    @clientId,
                    @connectionId,
                    @entityType,
                    @internalId,
                    @externalId,
                    @externalKey,
                    @payloadJson,
                    SYSUTCDATETIME(),
                    'success',
                    NULL
                );
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@clientId", job.ClientId);
        command.Parameters.AddWithValue("@connectionId", connectionId);
        command.Parameters.AddWithValue("@entityType", job.EntityType);
        command.Parameters.AddWithValue("@internalId", job.InternalId);
        command.Parameters.AddWithValue("@externalId", result.ExternalId!);
        command.Parameters.AddWithValue("@externalKey", (object?)result.ExternalKey ?? DBNull.Value);
        command.Parameters.AddWithValue("@payloadJson", payload.ToJsonString(JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<bool> HasIntegrationLinksTableAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        if (_hasIntegrationLinksTable.HasValue)
        {
            return _hasIntegrationLinksTable.Value;
        }

        const string sql = """
            SELECT CASE WHEN EXISTS (
                SELECT 1
                FROM INFORMATION_SCHEMA.TABLES
                WHERE TABLE_NAME = 'integration_links'
            ) THEN 1 ELSE 0 END;
            """;

        await using var command = new SqlCommand(sql, connection);
        var exists = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
        _hasIntegrationLinksTable = exists;
        return exists;
    }

    private async Task<bool> HasPointBasedConfigurationAssignmentsTableAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        if (_hasPointBasedConfigurationAssignmentsTable.HasValue)
        {
            return _hasPointBasedConfigurationAssignmentsTable.Value;
        }

        const string sql = """
            SELECT CASE WHEN EXISTS (
                SELECT 1
                FROM INFORMATION_SCHEMA.TABLES
                WHERE TABLE_NAME = 'test_plan_item_suite_configurations'
            ) THEN 1 ELSE 0 END;
            """;

        await using var command = new SqlCommand(sql, connection);
        var exists = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
        _hasPointBasedConfigurationAssignmentsTable = exists;
        return exists;
    }

    private static void ApplyMappedValue(JsonObject payload, string fieldName, JsonObject? map)
    {
        if (map is null || !payload.TryGetPropertyValue(fieldName, out var currentValue) || currentValue is null)
        {
            return;
        }

        var currentString = currentValue.ToString();
        if (map.TryGetPropertyValue(currentString, out var mappedValue) && mappedValue is not null)
        {
            payload[fieldName] = mappedValue.DeepClone();
        }
    }

    private static JsonObject? ParseJsonObject(SqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var raw = reader.GetString(ordinal);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(raw)?.AsObject();
        }
        catch
        {
            return null;
        }
    }

    private static string? GetString(SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static bool? GetBoolean(SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            bool b => b,
            byte bt => bt != 0,
            short s => s != 0,
            int i => i != 0,
            long l => l != 0,
            _ => bool.TryParse(value.ToString(), out var parsed) ? parsed : null
        };
    }

    private static long? GetInt64(SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            long l => l,
            int i => i,
            short s => s,
            byte b => b,
            decimal d => (long)d,
            _ => long.TryParse(value.ToString(), out var parsed) ? parsed : null
        };
    }

    private static string GetDateString(SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal))
        {
            return string.Empty;
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateTime dt => dt.ToString("yyyy-MM-dd"),
            DateTimeOffset dto => dto.ToString("yyyy-MM-dd"),
            _ => value.ToString() ?? string.Empty
        };
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength];
    }

    private static string BuildAzureStepsField(JsonArray? manualSteps)
    {
        if (manualSteps is null || manualSteps.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.Append("<steps id=\"0\" last=\"").Append(manualSteps.Count).Append("\">");

        var stepId = 1;
        foreach (var node in manualSteps)
        {
            if (node is not JsonObject step)
            {
                continue;
            }

            var parameterName = XmlEscape(step["parameter_name"]?.ToString()?.Trim() ?? $"step-{stepId}");
            var action = XmlEscape(step["action"]?.ToString()?.Trim() ?? string.Empty);
            var expected = XmlEscape(step["expected"]?.ToString()?.Trim() ?? string.Empty);
            var value = XmlEscape(step["value"]?.ToString()?.Trim() ?? string.Empty);

            var actionText = string.IsNullOrWhiteSpace(value)
                ? action
                : string.IsNullOrWhiteSpace(action)
                    ? $"@{parameterName}"
                    : $"{action} @{parameterName}";

            builder.Append("<step id=\"").Append(stepId).Append("\" type=\"ActionStep\">");
            builder.Append("<parameterizedString isformatted=\"true\">").Append(actionText).Append("</parameterizedString>");
            builder.Append("<parameterizedString isformatted=\"true\">").Append(expected).Append("</parameterizedString>");
            builder.Append("</step>");
            stepId++;
        }

        builder.Append("</steps>");
        return builder.ToString();
    }

    private static string BuildAzureParametersField(JsonArray? manualSteps)
    {
        if (manualSteps is null || manualSteps.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.Append("<parameters>");
        foreach (var node in manualSteps)
        {
            if (node is not JsonObject step)
            {
                continue;
            }

            var value = step["value"]?.ToString()?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var parameterName = XmlEscape(step["parameter_name"]?.ToString()?.Trim() ?? string.Empty);
            if (string.IsNullOrWhiteSpace(parameterName))
            {
                continue;
            }

            builder.Append("<param name=\"").Append(parameterName).Append("\" bind=\"default\" />");
        }

        builder.Append("</parameters>");
        return builder.ToString();
    }

    private static string BuildAzureLocalDataSourceField(JsonArray? manualSteps)
    {
        if (manualSteps is null || manualSteps.Count == 0)
        {
            return string.Empty;
        }

        var hasAnyValue = false;
        var rowBuilder = new StringBuilder();

        foreach (var node in manualSteps)
        {
            if (node is not JsonObject step)
            {
                continue;
            }

            var value = step["value"]?.ToString()?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var parameterName = XmlEscape(step["parameter_name"]?.ToString()?.Trim() ?? string.Empty);
            if (string.IsNullOrWhiteSpace(parameterName))
            {
                continue;
            }

            rowBuilder.Append("<").Append(parameterName).Append(">")
                .Append(XmlEscape(value))
                .Append("</").Append(parameterName).Append(">");
            hasAnyValue = true;
        }

        if (!hasAnyValue)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.Append("<NewDataSet><Table1>");
        builder.Append(rowBuilder);
        builder.Append("</Table1></NewDataSet>");
        return builder.ToString();
    }

    private static string XmlEscape(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);
    }

    private static JsonArray ParseJsonArray(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        try
        {
            return JsonNode.Parse(raw) as JsonArray ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static JsonObject? FindFirstFailedStep(JsonArray steps)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i] is not JsonObject step)
            {
                continue;
            }

            if (step["is_passed"]?.GetValue<bool?>() != false)
            {
                continue;
            }

            return new JsonObject
            {
                ["index"] = i + 1,
                ["description"] = step["description"]?.ToString() ?? string.Empty,
                ["expected_output"] = step["expected_output"]?.ToString() ?? string.Empty,
                ["value"] = step["value"]?.ToString() ?? string.Empty,
                ["xpath"] = step["xPath"]?.ToString() ?? string.Empty,
                ["comment"] = step["comment"]?.ToString() ?? string.Empty
            };
        }

        return null;
    }

    private static JsonObject BuildStepCounts(JsonArray steps)
    {
        var total = 0;
        var passed = 0;
        var failed = 0;

        foreach (var node in steps)
        {
            if (node is not JsonObject step)
            {
                continue;
            }

            total++;
            if (step["is_passed"]?.GetValue<bool?>() == true)
            {
                passed++;
            }
            else if (step["is_passed"]?.GetValue<bool?>() == false)
            {
                failed++;
            }
        }

        return new JsonObject
        {
            ["total"] = total,
            ["passed"] = passed,
            ["failed"] = failed
        };
    }

    private static string BuildRunResultComment(JsonObject payload)
    {
        var parts = new List<string>();
        var comment = payload["comment"]?.ToString()?.Trim();
        if (!string.IsNullOrWhiteSpace(comment))
        {
            parts.Add(comment);
        }

        if (payload["failed_step"] is JsonObject failedStep)
        {
            var index = TryParseLongNode(failedStep["index"], out var idx) ? idx : 0;
            var description = failedStep["description"]?.ToString()?.Trim() ?? string.Empty;
            var value = failedStep["value"]?.ToString()?.Trim() ?? string.Empty;
            var stepComment = failedStep["comment"]?.ToString()?.Trim() ?? string.Empty;

            parts.Add(index > 0
                ? $"Failed Step #{index}{(string.IsNullOrWhiteSpace(description) ? string.Empty : $": {description}")}"
                : $"Failed Step{(string.IsNullOrWhiteSpace(description) ? string.Empty : $": {description}")}");

            if (!string.IsNullOrWhiteSpace(value))
            {
                parts.Add($"Step Data: {value}");
            }

            if (!string.IsNullOrWhiteSpace(stepComment))
            {
                parts.Add($"Step Comment: {stepComment}");
            }
        }

        if (payload["step_counts"] is JsonObject counts)
        {
            var total = TryParseLongNode(counts["total"], out var totalValue) ? totalValue : 0;
            var passed = TryParseLongNode(counts["passed"], out var passedValue) ? passedValue : 0;
            var failed = TryParseLongNode(counts["failed"], out var failedValue) ? failedValue : 0;
            parts.Add($"Steps: total={total}, passed={passed}, failed={failed}");
        }

        return string.Join("\n", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static bool TryParseLongNode(JsonNode? node, out long value)
    {
        value = 0;
        if (node is null)
        {
            return false;
        }

        return long.TryParse(node.ToString(), out value);
    }

    private static string? GetDateTimeOffsetString(SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateTimeOffset dto => dto.ToUniversalTime().ToString("O"),
            DateTime dt => new DateTimeOffset(dt.ToUniversalTime()).ToString("O"),
            _ => value.ToString()
        };
    }

    private sealed record ClaimedJob
    {
        public long Id { get; init; }
        public long ClientId { get; init; }
        public long IntegrationConnectionId { get; init; }
        public string EntityType { get; init; } = string.Empty;
        public long InternalId { get; init; }
        public long CreatedBy { get; init; }
        public int Attempts { get; init; }
        public int MaxAttempts { get; init; }
        public JsonObject? Payload { get; init; }
    }

    private sealed record IntegrationConnectionRecord
    {
        public long Id { get; init; }
        public string Provider { get; init; } = string.Empty;
        public bool IsEnabled { get; init; }
        public JsonObject Config { get; init; } = new();
        public JsonObject Credentials { get; init; } = new();
    }

    private sealed record ExistingLink(string? ExternalId, string? ExternalKey);

    private sealed record IntegrationResult(string? ExternalId, string? ExternalKey, JsonObject Response);

    private sealed record AzureContext(string BaseUrl, string Pat);

    private sealed record PlanItemRef(long PlanInternalId, long PlanItemId, string PlanItemName, string? ConfigurationName);

    private sealed record PlanItemCaseRef(long PlanItemId, string PlanItemName, long TestCaseInternalId, string? ConfigurationName);

    private sealed record AzureSuiteRef(long Id, string Name, long? ParentId);

    private sealed record AzurePointRef(long TestCaseId, long ConfigurationId, string? ConfigurationName);

    private sealed record ExpectedSuiteAssignment(string SuiteName, long? ConfigurationId, string? ConfigurationName);

    private sealed record IntegrationTestCaseComponent(
        long Id,
        long? ComponentId,
        IReadOnlyList<IntegrationComponentStep> Steps,
        IReadOnlyList<IntegrationDataset> Datasets);

    private sealed record IntegrationComponentStep(long Id, string? Description, string? ExpectedOutput, int? DisplayId);

    private sealed record IntegrationDataset(long Id, bool Status, IReadOnlyList<IntegrationDatasetStep> Steps);

    private sealed record IntegrationDatasetStep(
        long DatasetId,
        long? StepId,
        bool SkipStep,
        string? Value,
        string? OverrideValue,
        string? StepDescription,
        string? StepExpectedOutput);

    private sealed record IntegrationVariableMaps(
        Dictionary<string, IntegrationVariableValue> Global,
        Dictionary<long, Dictionary<string, IntegrationVariableValue>> Local);

    private sealed record IntegrationVariableValue(string? Value, string? ExecutableMethod, bool IsEncrypted);

    private sealed class ParsedOverrideParts
    {
        public string? Description { get; set; }

        public string? ExpectedOutput { get; set; }
    }
}
