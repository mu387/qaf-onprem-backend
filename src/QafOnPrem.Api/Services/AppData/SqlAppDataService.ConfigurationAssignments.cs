using System.Data;
using Microsoft.Data.SqlClient;

namespace QafOnPrem.Api.Services.AppData;

public sealed partial class SqlAppDataService
{
    private static bool IsConfigurationExecutionId(long suiteId) => suiteId < 0;

    private static bool IsDatasetExecutionId(long suiteId) => suiteId > DatasetExecutionIdOffset;

    private static long ToConfigurationExecutionId(long assignmentId) => -assignmentId;

    private static long ToConfigurationAssignmentId(long executionId) => Math.Abs(executionId);

    private const long DatasetExecutionIdOffset = 1_000_000_000_000;

    private static long ToDatasetExecutionId(long datasetId) => DatasetExecutionIdOffset + datasetId;

    private static long ToDatasetRowId(long datasetId) => DatasetExecutionIdOffset + datasetId;

    private static long ToDatasetId(long executionId) => executionId - DatasetExecutionIdOffset;

    private static long ResolveExecutionIdentity(long testSuiteId, long? executionId)
    {
        if (executionId.HasValue && executionId.Value != 0)
        {
            return executionId.Value;
        }

        return testSuiteId;
    }

    private async Task EnsurePointBasedConfigurationStateAsync(SqlConnection connection, long? testPlanItemId, CancellationToken cancellationToken, SqlTransaction? transaction = null)
    {
        await EnsureConfigurationAssignmentsTableAsync(connection, cancellationToken, transaction);
        await EnsureTestPlanDatasetVariantsTableAsync(connection, cancellationToken, transaction);
        await EnsureTestLevelDatasetsSchemaAsync(connection, cancellationToken, transaction);
        await EnsureRunnerExecutionIdColumnAsync(connection, cancellationToken, transaction);
        if (testPlanItemId.HasValue && testPlanItemId.Value > 0)
        {
            await MigrateLegacyConfigurationAssignmentsAsync(connection, testPlanItemId.Value, cancellationToken, transaction);
            await SyncTestPlanDatasetVariantsAsync(connection, testPlanItemId.Value, cancellationToken, transaction);
        }
    }

    private async Task EnsureRunnerExecutionIdColumnAsync(SqlConnection connection, CancellationToken cancellationToken, SqlTransaction? transaction = null)
    {
        const string sql = """
            IF COL_LENGTH('dbo.test_runner_items', 'execution_id') IS NULL
            BEGIN
                ALTER TABLE dbo.test_runner_items ADD execution_id BIGINT NULL;
            END;

                        UPDATE dbo.test_runner_items
                        SET execution_id = test_suite_id
                        WHERE execution_id IS NULL
                            AND test_suite_id IS NOT NULL;

            IF EXISTS (
                SELECT 1
                FROM sys.indexes
                WHERE name = N'UX_test_runner_items_uq_test_runner_items_runner_suite'
                  AND object_id = OBJECT_ID(N'dbo.test_runner_items')
            )
            BEGIN
                DROP INDEX UX_test_runner_items_uq_test_runner_items_runner_suite ON dbo.test_runner_items;
            END;

            IF NOT EXISTS (
                SELECT 1
                FROM sys.indexes
                WHERE name = N'UX_test_runner_items_runner_execution'
                  AND object_id = OBJECT_ID(N'dbo.test_runner_items')
            )
            BEGIN
                CREATE UNIQUE INDEX UX_test_runner_items_runner_execution
                    ON dbo.test_runner_items (test_runner_id, execution_id)
                    WHERE execution_id IS NOT NULL;
            END;
            """;

        await using var command = CreateCommand(connection, sql);
        command.Transaction = transaction;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureConfigurationAssignmentsTableAsync(SqlConnection connection, CancellationToken cancellationToken, SqlTransaction? transaction = null)
    {
        const string sql = """
            IF OBJECT_ID(N'dbo.test_plan_item_suite_configurations', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.test_plan_item_suite_configurations
                (
                    id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    test_plan_item_suite_id BIGINT NOT NULL,
                    configuration_id BIGINT NOT NULL,
                    status_id BIGINT NOT NULL CONSTRAINT DF_test_plan_item_suite_configurations_status_id DEFAULT (1),
                    created_at DATETIME2(7) NOT NULL CONSTRAINT DF_test_plan_item_suite_configurations_created_at DEFAULT SYSUTCDATETIME(),
                    updated_at DATETIME2(7) NOT NULL CONSTRAINT DF_test_plan_item_suite_configurations_updated_at DEFAULT SYSUTCDATETIME(),
                    deleted_at DATETIME2(7) NULL
                );
            END;

            IF NOT EXISTS (
                SELECT 1
                FROM sys.indexes
                WHERE name = N'UX_tpisc_active_assignment'
                  AND object_id = OBJECT_ID(N'dbo.test_plan_item_suite_configurations')
            )
            BEGIN
                CREATE UNIQUE INDEX UX_tpisc_active_assignment
                    ON dbo.test_plan_item_suite_configurations (test_plan_item_suite_id, configuration_id)
                    WHERE deleted_at IS NULL;
            END;
            """;

        await using var command = CreateCommand(connection, sql);
        command.Transaction = transaction;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureTestPlanDatasetVariantsTableAsync(SqlConnection connection, CancellationToken cancellationToken, SqlTransaction? transaction = null)
    {
        const string sql = """
            IF OBJECT_ID(N'dbo.test_plan_item_suite_datasets', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.test_plan_item_suite_datasets
                (
                    id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    test_plan_item_suite_id BIGINT NOT NULL,
                    test_design_dataset_id BIGINT NOT NULL,
                    status_id BIGINT NOT NULL CONSTRAINT DF_test_plan_item_suite_datasets_status_id DEFAULT (1),
                    created_at DATETIME2(7) NOT NULL CONSTRAINT DF_test_plan_item_suite_datasets_created_at DEFAULT SYSUTCDATETIME(),
                    updated_at DATETIME2(7) NOT NULL CONSTRAINT DF_test_plan_item_suite_datasets_updated_at DEFAULT SYSUTCDATETIME(),
                    deleted_at DATETIME2(7) NULL
                );
            END;

            IF NOT EXISTS (
                SELECT 1
                FROM sys.indexes
                WHERE name = N'UX_tpisd_active_dataset_variant'
                  AND object_id = OBJECT_ID(N'dbo.test_plan_item_suite_datasets')
            )
            BEGIN
                CREATE UNIQUE INDEX UX_tpisd_active_dataset_variant
                    ON dbo.test_plan_item_suite_datasets (test_plan_item_suite_id, test_design_dataset_id)
                    WHERE deleted_at IS NULL;
            END;
            """;

        await using var command = CreateCommand(connection, sql);
        command.Transaction = transaction;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task SyncTestPlanDatasetVariantsAsync(SqlConnection connection, long testPlanItemId, CancellationToken cancellationToken, SqlTransaction? transaction = null)
    {
        const string deleteSql = """
            DELETE variant
            FROM test_plan_item_suite_datasets variant
            INNER JOIN test_plan_item_suites parent ON parent.id = variant.test_plan_item_suite_id
            LEFT JOIN test_design_datasets ds ON ds.id = variant.test_design_dataset_id AND ds.deleted_at IS NULL
            WHERE parent.test_plan_item_id = @testPlanItemId
              AND parent.deleted_at IS NULL
              AND ds.id IS NULL;
            """;

        await using (var deleteCommand = CreateCommand(connection, deleteSql))
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.Parameters.AddWithValue("@testPlanItemId", testPlanItemId);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        const string insertSql = """
            INSERT INTO test_plan_item_suite_datasets
            (
                test_plan_item_suite_id,
                test_design_dataset_id,
                status_id,
                created_at,
                updated_at,
                deleted_at
            )
            SELECT
                parent.id,
                ds.id,
                1,
                SYSUTCDATETIME(),
                SYSUTCDATETIME(),
                NULL
            FROM test_plan_item_suites parent
            INNER JOIN test_design_datasets ds ON ds.test_design_id = parent.test_design_id
            WHERE parent.test_plan_item_id = @testPlanItemId
              AND parent.deleted_at IS NULL
              AND ds.deleted_at IS NULL
              AND NOT EXISTS (
                    SELECT 1
                    FROM test_plan_item_suite_datasets existing
                    WHERE existing.test_plan_item_suite_id = parent.id
                      AND existing.test_design_dataset_id = ds.id
                      AND existing.deleted_at IS NULL
              );
            """;

        await using var insertCommand = CreateCommand(connection, insertSql);
        insertCommand.Transaction = transaction;
        insertCommand.Parameters.AddWithValue("@testPlanItemId", testPlanItemId);
        await insertCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task MigrateLegacyConfigurationAssignmentsAsync(SqlConnection connection, long testPlanItemId, CancellationToken cancellationToken, SqlTransaction? transaction = null)
    {
        const string sql = """
            INSERT INTO test_plan_item_suite_configurations
            (
                test_plan_item_suite_id,
                configuration_id,
                status_id,
                created_at,
                updated_at,
                deleted_at
            )
            SELECT
                child.parent_id,
                td.configuration_id,
                ISNULL(child.status_id, 1),
                SYSUTCDATETIME(),
                SYSUTCDATETIME(),
                NULL
            FROM test_plan_item_suites child
            INNER JOIN test_designs td ON td.id = child.test_design_id
            WHERE child.test_plan_item_id = @testPlanItemId
              AND child.parent_id IS NOT NULL
              AND child.deleted_at IS NULL
              AND td.deleted_at IS NULL
              AND td.configuration_id IS NOT NULL
              AND NOT EXISTS (
                    SELECT 1
                    FROM test_plan_item_suite_configurations existing
                    WHERE existing.test_plan_item_suite_id = child.parent_id
                      AND existing.configuration_id = td.configuration_id
                      AND existing.deleted_at IS NULL
              );
            """;

        await using var command = CreateCommand(connection, sql);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@testPlanItemId", testPlanItemId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<ConfigurationAssignmentRow>> LoadConfigurationAssignmentsForPlanItemAsync(SqlConnection connection, long clientId, long testPlanItemId, CancellationToken cancellationToken, SqlTransaction? transaction = null)
    {
        const string sql = """
            SELECT
                assign.id,
                assign.test_plan_item_suite_id,
                assign.configuration_id,
                assign.status_id,
                status_ref.name AS status_name,
                parent.test_design_id AS base_test_design_id,
                parent.sort_order AS parent_sort_order,
                td.title,
                ts.id AS state_ref_id,
                ts.name AS state_name,
                cfg.name AS configuration_name
            FROM test_plan_item_suite_configurations assign
            INNER JOIN test_plan_item_suites parent ON parent.id = assign.test_plan_item_suite_id
            INNER JOIN test_plan_items tpi ON tpi.id = parent.test_plan_item_id
            INNER JOIN test_plans tp ON tp.id = tpi.test_plan_id
            INNER JOIN test_designs td ON td.id = parent.test_design_id
            LEFT JOIN test_states ts ON ts.id = td.test_state_id
            LEFT JOIN test_plan_item_suite_statuses status_ref ON status_ref.id = assign.status_id
            LEFT JOIN configurations cfg ON cfg.id = assign.configuration_id
            WHERE tp.client_id = @clientId
              AND parent.test_plan_item_id = @testPlanItemId
              AND parent.deleted_at IS NULL
              AND assign.deleted_at IS NULL
            ORDER BY ISNULL(parent.sort_order, 2147483647), parent.id, assign.id;
            """;

        var result = new List<ConfigurationAssignmentRow>();
        await using var command = CreateCommand(connection, sql);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@clientId", clientId);
        command.Parameters.AddWithValue("@testPlanItemId", testPlanItemId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ConfigurationAssignmentRow(
                reader.GetInt64(reader.GetOrdinal("id")),
                reader.GetInt64(reader.GetOrdinal("test_plan_item_suite_id")),
                reader.GetInt64(reader.GetOrdinal("base_test_design_id")),
                reader.GetInt64(reader.GetOrdinal("configuration_id")),
                GetInt64(reader, "status_id"),
                GetString(reader, "status_name"),
                GetString(reader, "title"),
                GetInt64(reader, "state_ref_id"),
                GetString(reader, "state_name"),
                GetString(reader, "configuration_name")));
        }

        return result;
    }

    private async Task<IReadOnlyList<TestDesignDatasetPlanRow>> LoadTestDesignDatasetPlanRowsAsync(SqlConnection connection, long clientId, long testPlanItemId, CancellationToken cancellationToken, SqlTransaction? transaction = null)
    {
        const string sql = """
            SELECT
                                variant.id AS dataset_plan_row_id,
                                ds.id AS dataset_id,
                parent.id AS parent_suite_link_id,
                parent.test_design_id AS base_test_design_id,
                                variant.status_id,
                                status_ref.name AS status_name,
                td.title,
                td.test_state_id AS state_ref_id,
                ts.name AS state_name,
                td.configuration_id,
                cfg.name AS configuration_name,
                ds.scenario
                        FROM test_plan_item_suite_datasets variant
                        INNER JOIN test_plan_item_suites parent ON parent.id = variant.test_plan_item_suite_id
                        INNER JOIN test_design_datasets ds ON ds.id = variant.test_design_dataset_id
            INNER JOIN test_plan_items tpi ON tpi.id = parent.test_plan_item_id
            INNER JOIN test_plans tp ON tp.id = tpi.test_plan_id
            INNER JOIN test_designs td ON td.id = parent.test_design_id
            LEFT JOIN test_states ts ON ts.id = td.test_state_id
                        LEFT JOIN test_plan_item_suite_statuses status_ref ON status_ref.id = variant.status_id
            LEFT JOIN configurations cfg ON cfg.id = td.configuration_id
            WHERE tp.client_id = @clientId
              AND parent.test_plan_item_id = @testPlanItemId
              AND parent.deleted_at IS NULL
                            AND variant.deleted_at IS NULL
              AND ds.deleted_at IS NULL
            ORDER BY ISNULL(parent.sort_order, 2147483647), parent.id, ISNULL(ds.sort_order, 2147483647), ds.id;
            """;

        var result = new List<TestDesignDatasetPlanRow>();
        await using var command = CreateCommand(connection, sql);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@clientId", clientId);
        command.Parameters.AddWithValue("@testPlanItemId", testPlanItemId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new TestDesignDatasetPlanRow(
                reader.GetInt64(reader.GetOrdinal("dataset_plan_row_id")),
                reader.GetInt64(reader.GetOrdinal("dataset_id")),
                reader.GetInt64(reader.GetOrdinal("parent_suite_link_id")),
                reader.GetInt64(reader.GetOrdinal("base_test_design_id")),
                GetInt64(reader, "status_id"),
                GetString(reader, "status_name"),
                GetString(reader, "title"),
                GetInt64(reader, "state_ref_id"),
                GetString(reader, "state_name"),
                GetInt64(reader, "configuration_id"),
                GetString(reader, "configuration_name"),
                GetString(reader, "scenario")));
        }

        return result;
    }

    private async Task<IReadOnlyList<ExecutionSuiteContext>> LoadExecutionSuiteContextsAsync(SqlConnection connection, long clientId, IReadOnlyList<long> suiteIds, CancellationToken cancellationToken, SqlTransaction? transaction = null)
    {
        var result = new List<ExecutionSuiteContext>();
        var distinctIds = suiteIds.Where(id => id != 0).Distinct().ToArray();
        if (distinctIds.Length == 0)
        {
            return result;
        }

        foreach (var suiteId in distinctIds.Where(id => id > 0 && !IsDatasetExecutionId(id)))
        {
            result.Add(new ExecutionSuiteContext(suiteId, suiteId, null, null, null, null, null));
        }

                var datasetPlanRowIds = distinctIds.Where(IsDatasetExecutionId).Select(ToDatasetId).Distinct().ToArray();
                if (datasetPlanRowIds.Length > 0)
        {
                        var datasetParameters = AddIdListParameterValues(datasetPlanRowIds, "@datasetVariantId");
            var datasetSql = $"""
                SELECT
                                        variant.id,
                                        ds.id AS dataset_id,
                    ds.test_design_id,
                    ds.scenario
                                FROM test_plan_item_suite_datasets variant
                                INNER JOIN test_design_datasets ds ON ds.id = variant.test_design_dataset_id
                INNER JOIN test_designs td ON td.id = ds.test_design_id
                WHERE td.client_id = @clientId
                  AND td.deleted_at IS NULL
                  AND ds.deleted_at IS NULL
                                    AND variant.deleted_at IS NULL
                                    AND variant.id IN ({string.Join(", ", datasetParameters.Select(parameter => parameter.ParameterName))});
                """;

            await using (var command = CreateCommand(connection, datasetSql))
            {
                command.Transaction = transaction;
                command.Parameters.AddWithValue("@clientId", clientId);
                AddParameters(command, datasetParameters);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var datasetPlanRowId = reader.GetInt64(reader.GetOrdinal("id"));
                    result.Add(new ExecutionSuiteContext(
                        ToDatasetExecutionId(datasetPlanRowId),
                        reader.GetInt64(reader.GetOrdinal("test_design_id")),
                        null,
                        null,
                        null,
                        GetInt64(reader, "dataset_id"),
                        GetString(reader, "scenario")));
                }
            }
        }

        var assignmentIds = distinctIds.Where(IsConfigurationExecutionId).Select(ToConfigurationAssignmentId).Distinct().ToArray();
        if (assignmentIds.Length == 0)
        {
            return result;
        }

        var parameters = AddIdListParameterValues(assignmentIds, "@assignmentId");
        var sql = $"""
            SELECT
                assign.id,
                assign.test_plan_item_suite_id,
                assign.configuration_id,
                parent.test_design_id AS base_test_design_id
            FROM test_plan_item_suite_configurations assign
            INNER JOIN test_plan_item_suites parent ON parent.id = assign.test_plan_item_suite_id
            INNER JOIN test_plan_items tpi ON tpi.id = parent.test_plan_item_id
            INNER JOIN test_plans tp ON tp.id = tpi.test_plan_id
            WHERE tp.client_id = @clientId
              AND assign.deleted_at IS NULL
              AND parent.deleted_at IS NULL
              AND assign.id IN ({string.Join(", ", parameters.Select(parameter => parameter.ParameterName))});
            """;

        await using (var command = CreateCommand(connection, sql))
        {
            command.Transaction = transaction;
            command.Parameters.AddWithValue("@clientId", clientId);
            AddParameters(command, parameters);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var assignmentId = reader.GetInt64(reader.GetOrdinal("id"));
                result.Add(new ExecutionSuiteContext(
                    ToConfigurationExecutionId(assignmentId),
                    reader.GetInt64(reader.GetOrdinal("base_test_design_id")),
                    GetInt64(reader, "test_plan_item_suite_id"),
                    assignmentId,
                    GetInt64(reader, "configuration_id"),
                    null,
                    null));
            }
        }

        return result;
    }

    private async Task<bool> ExecutionSuitesBelongToClientByIdentityAsync(SqlConnection connection, long clientId, IReadOnlyList<long> suiteIds, CancellationToken cancellationToken)
    {
        var contexts = await LoadExecutionSuiteContextsAsync(connection, clientId, suiteIds, cancellationToken);
        return contexts.Count == suiteIds.Where(id => id != 0).Distinct().Count();
    }

    private async Task<Dictionary<long, int>> LoadExecutionSuiteTypesByIdentityAsync(SqlConnection connection, long clientId, IReadOnlyList<long> suiteIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, int>();
        var contexts = await LoadExecutionSuiteContextsAsync(connection, clientId, suiteIds, cancellationToken);
        var baseSuiteIds = contexts.Select(row => row.BaseTestDesignId).Distinct().ToArray();
        if (baseSuiteIds.Length == 0)
        {
            return result;
        }

        var parameters = AddIdListParameterValues(baseSuiteIds, "@suiteId");
        var sql = $"SELECT id, test_suite_type FROM test_designs WHERE id IN ({string.Join(", ", parameters.Select(parameter => parameter.ParameterName))});";
        var typeByBaseId = new Dictionary<long, int>();
        await using (var command = CreateCommand(connection, sql))
        {
            AddParameters(command, parameters);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                typeByBaseId[reader.GetInt64(reader.GetOrdinal("id"))] = GetInt32(reader, "test_suite_type") ?? 0;
            }
        }

        foreach (var context in contexts)
        {
            result[context.ExecutionId] = typeByBaseId.GetValueOrDefault(context.BaseTestDesignId);
        }

        return result;
    }

    private async Task<Dictionary<long, string>> LoadSuiteNamesByIdentityAsync(SqlConnection connection, long clientId, IReadOnlyList<long> suiteIds, CancellationToken cancellationToken, SqlTransaction? transaction = null)
    {
        var result = new Dictionary<long, string>();
        var contexts = await LoadExecutionSuiteContextsAsync(connection, clientId, suiteIds, cancellationToken, transaction);
        if (contexts.Count == 0)
        {
            return result;
        }

        var baseSuiteIds = contexts.Select(row => row.BaseTestDesignId).Distinct().ToArray();
        var baseParameters = AddIdListParameterValues(baseSuiteIds, "@suiteId");
        var baseSql = $"SELECT id, title FROM test_designs WHERE id IN ({string.Join(", ", baseParameters.Select(parameter => parameter.ParameterName))});";
        var titleMap = new Dictionary<long, string>(baseSuiteIds.Length);
        await using (var command = CreateCommand(connection, baseSql))
        {
            command.Transaction = transaction;
            AddParameters(command, baseParameters);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                titleMap[reader.GetInt64(reader.GetOrdinal("id"))] = GetString(reader, "title") ?? string.Empty;
            }
        }

        var configurationIds = contexts.Where(row => row.ConfigurationId.HasValue).Select(row => row.ConfigurationId!.Value).Distinct().ToArray();
        var configMap = new Dictionary<long, string>();
        if (configurationIds.Length > 0)
        {
            var configParameters = AddIdListParameterValues(configurationIds, "@configurationId");
            var configSql = $"SELECT id, name FROM configurations WHERE id IN ({string.Join(", ", configParameters.Select(parameter => parameter.ParameterName))});";
            await using var command = CreateCommand(connection, configSql);
            command.Transaction = transaction;
            AddParameters(command, configParameters);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                configMap[reader.GetInt64(reader.GetOrdinal("id"))] = GetString(reader, "name") ?? string.Empty;
            }
        }

        foreach (var context in contexts)
        {
            var title = titleMap.GetValueOrDefault(context.BaseTestDesignId) ?? string.Empty;
            if (context.DatasetId.HasValue)
            {
                var scenario = string.IsNullOrWhiteSpace(context.DatasetScenario) ? $"Dataset {context.DatasetId.Value}" : context.DatasetScenario;
                result[context.ExecutionId] = $"{title} [{scenario}]";
            }
            else if (context.ConfigurationId.HasValue && configMap.TryGetValue(context.ConfigurationId.Value, out var configurationName) && !string.IsNullOrWhiteSpace(configurationName))
            {
                result[context.ExecutionId] = $"{title} [{configurationName}]";
            }
            else
            {
                result[context.ExecutionId] = title;
            }
        }

        return result;
    }

    private async Task UpdateExecutionStatusAsync(SqlConnection connection, SqlTransaction transaction, long testPlanItemId, long executionId, int statusId, CancellationToken cancellationToken)
    {
        if (IsConfigurationExecutionId(executionId))
        {
            const string assignmentSql = """
                UPDATE assign
                SET status_id = @statusId,
                    updated_at = SYSUTCDATETIME()
                FROM test_plan_item_suite_configurations assign
                INNER JOIN test_plan_item_suites parent ON parent.id = assign.test_plan_item_suite_id
                WHERE assign.id = @assignmentId
                  AND assign.deleted_at IS NULL
                  AND parent.test_plan_item_id = @testPlanItemId
                  AND parent.deleted_at IS NULL;
                """;
            await using var assignmentCommand = CreateCommand(connection, assignmentSql);
            assignmentCommand.Transaction = transaction;
            assignmentCommand.Parameters.AddWithValue("@statusId", statusId);
            assignmentCommand.Parameters.AddWithValue("@assignmentId", ToConfigurationAssignmentId(executionId));
            assignmentCommand.Parameters.AddWithValue("@testPlanItemId", testPlanItemId);
            await assignmentCommand.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        if (IsDatasetExecutionId(executionId))
        {
            const string datasetSql = """
                UPDATE variant
                SET status_id = @statusId,
                    updated_at = SYSUTCDATETIME()
                FROM test_plan_item_suite_datasets variant
                INNER JOIN test_plan_item_suites tpis ON tpis.id = variant.test_plan_item_suite_id
                WHERE variant.id = @datasetVariantId
                  AND variant.deleted_at IS NULL
                  AND tpis.test_plan_item_id = @testPlanItemId
                  AND tpis.deleted_at IS NULL;
                """;
            await using var datasetCommand = CreateCommand(connection, datasetSql);
            datasetCommand.Transaction = transaction;
            datasetCommand.Parameters.AddWithValue("@statusId", statusId);
            datasetCommand.Parameters.AddWithValue("@datasetVariantId", ToDatasetId(executionId));
            datasetCommand.Parameters.AddWithValue("@testPlanItemId", testPlanItemId);
            await datasetCommand.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        const string suiteSql = """
            UPDATE test_plan_item_suites
            SET status_id = @statusId,
                updated_at = SYSUTCDATETIME()
            WHERE test_plan_item_id = @testPlanItemId
              AND test_design_id = @testSuiteId
              AND deleted_at IS NULL;
            """;
        await using var suiteCommand = CreateCommand(connection, suiteSql);
        suiteCommand.Transaction = transaction;
        suiteCommand.Parameters.AddWithValue("@statusId", statusId);
        suiteCommand.Parameters.AddWithValue("@testPlanItemId", testPlanItemId);
        suiteCommand.Parameters.AddWithValue("@testSuiteId", executionId);
        await suiteCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private readonly record struct ConfigurationAssignmentRow(
        long AssignmentId,
        long ParentSuiteLinkId,
        long BaseTestDesignId,
        long ConfigurationId,
        long? StatusId,
        string? StatusName,
        string? SuiteTitle,
        long? StateId,
        string? StateName,
        string? ConfigurationName);

    private readonly record struct TestDesignDatasetPlanRow(
        long DatasetPlanRowId,
        long DatasetId,
        long ParentSuiteLinkId,
        long BaseTestDesignId,
        long? StatusId,
        string? StatusName,
        string? SuiteTitle,
        long? StateId,
        string? StateName,
        long? ConfigurationId,
        string? ConfigurationName,
        string? Scenario);

    private readonly record struct ExecutionSuiteContext(
        long ExecutionId,
        long BaseTestDesignId,
        long? TestPlanItemSuiteId,
        long? ConfigurationAssignmentId,
        long? ConfigurationId,
        long? DatasetId,
        string? DatasetScenario);
}
