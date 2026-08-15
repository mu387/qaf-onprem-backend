using System.Data;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using Microsoft.Data.SqlClient;
using QafOnPrem.Api.Contracts;

namespace QafOnPrem.Api.Services.AppData;

public sealed partial class SqlAppDataService
{
    public async Task<ImportComponentsResultDto> ImportComponentsAsync(ClaimsPrincipal principal, Stream stream, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return new ImportComponentsResultDto();
        }

        using var reader = new StreamReader(stream, Encoding.UTF8, true, leaveOpen: true);
        var header = await reader.ReadLineAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(header))
        {
            return new ImportComponentsResultDto();
        }

        var headerColumns = ParseCsvLine(header)
            .Select(static column => column.Trim().ToLowerInvariant())
            .ToList();

        var projectPageFeatureIndex = headerColumns.IndexOf("project_page_feature");
        if (projectPageFeatureIndex < 0)
        {
            projectPageFeatureIndex = 0;
        }

        var hasLegacyInputColumn = headerColumns.Contains("input");
        var displayIdIndex = headerColumns.IndexOf("display_id");
        if (displayIdIndex < 0)
        {
            displayIdIndex = hasLegacyInputColumn ? 2 : 1;
        }

        var actionIndex = headerColumns.IndexOf("action");
        if (actionIndex < 0)
        {
            actionIndex = hasLegacyInputColumn ? 3 : 2;
        }

        var expectedIndex = headerColumns.IndexOf("expected");
        if (expectedIndex < 0)
        {
            expectedIndex = hasLegacyInputColumn ? 4 : 3;
        }

        var beforeStepIndex = headerColumns.IndexOf("beforestep");
        if (beforeStepIndex < 0)
        {
            beforeStepIndex = 5;
        }

        var keywordIndex = headerColumns.IndexOf("keyword");
        if (keywordIndex < 0)
        {
            keywordIndex = hasLegacyInputColumn ? 6 : 4;
        }

        var locatorIndex = headerColumns.IndexOf("locator");
        if (locatorIndex < 0)
        {
            locatorIndex = hasLegacyInputColumn ? 7 : 6;
        }

        var afterStepIndex = headerColumns.IndexOf("afterstep");
        if (afterStepIndex < 0)
        {
            afterStepIndex = hasLegacyInputColumn ? 8 : 7;
        }

        var minimumRequiredIndex = new[]
        {
            projectPageFeatureIndex,
            displayIdIndex,
            actionIndex,
            expectedIndex,
            beforeStepIndex,
            keywordIndex,
            locatorIndex,
            afterStepIndex
        }.Max();

        var groups = new Dictionary<string, List<string[]>>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var columns = ParseCsvLine(line);
            if (columns.Count <= minimumRequiredIndex || string.IsNullOrWhiteSpace(columns.ElementAtOrDefault(projectPageFeatureIndex)))
            {
                continue;
            }

            var key = columns[projectPageFeatureIndex].Trim();
            if (!groups.TryGetValue(key, out var rows))
            {
                rows = [];
                groups[key] = rows;
            }

            rows.Add(columns.ToArray());
        }

        if (groups.Count == 0)
        {
            return new ImportComponentsResultDto();
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var projectMap = await LoadProjectNameMapAsync(connection, context.ClientId.Value, cancellationToken);
        var keywordMap = await LoadKeywordNameMapAsync(connection, context.ClientId.Value, cancellationToken);

        var createdComponents = 0;
        var createdSteps = 0;
        var skippedComponents = 0;

        foreach (var pair in groups)
        {
            var parts = pair.Key.Split('_', 3, StringSplitOptions.TrimEntries);
            if (parts.Length < 3 || !projectMap.TryGetValue(parts[0], out var projectId))
            {
                skippedComponents++;
                continue;
            }

            var page = parts[1];
            var feature = parts[2];
            if (await ComponentExistsAsync(principal, projectId, page, feature, null, cancellationToken))
            {
                skippedComponents++;
                continue;
            }

            var steps = new List<SaveComponentStepRequest>();
            foreach (var row in pair.Value)
            {
                var keywordName = row.ElementAtOrDefault(keywordIndex)?.Trim();
                keywordMap.TryGetValue(keywordName ?? string.Empty, out var keywordRef);
                steps.Add(new SaveComponentStepRequest
                {
                    Description = row.ElementAtOrDefault(actionIndex)?.Trim(),
                    ExpectedOutput = NormalizeOptionalText(row.ElementAtOrDefault(expectedIndex)),
                    KeywordRef = keywordRef,
                    BeforeStep = ParsePipeList(row.ElementAtOrDefault(beforeStepIndex)),
                    XPath = NormalizeOptionalText(row.ElementAtOrDefault(locatorIndex)),
                    AfterStep = ParsePipeList(row.ElementAtOrDefault(afterStepIndex)),
                    DisplayId = int.TryParse(row.ElementAtOrDefault(displayIdIndex), NumberStyles.Integer, CultureInfo.InvariantCulture, out var displayId) ? displayId : steps.Count + 1
                });
            }

            var saved = await CreateComponentAsync(principal, new SaveComponentRequest
            {
                Name = feature,
                ProjectId = projectId,
                Page = page,
                Feature = feature,
                TypeId = 1,
                Steps = steps
            }, cancellationToken);

            if (saved is null)
            {
                skippedComponents++;
                continue;
            }

            createdComponents++;
            createdSteps += steps.Count;
        }

        return new ImportComponentsResultDto
        {
            CreatedComponents = createdComponents,
            CreatedSteps = createdSteps,
            SkippedComponents = skippedComponents
        };
    }

    public async Task<byte[]> ExportComponentsAsync(ClaimsPrincipal principal, string? name, string? pageName, string? feature, string? projectIds, string? typeIds, bool? status, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return Encoding.UTF8.GetBytes("project_page_feature,input,display_id,action,expected,beforestep,keyword,locator,afterstep\n");
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var whereClauses = new List<string>
        {
            "c.deleted_at IS NULL",
            "c.client_id = @clientId",
            "cs.deleted_at IS NULL"
        };
        var parameters = new List<SqlParameter> { new("@clientId", context.ClientId.Value) };

        if (!string.IsNullOrWhiteSpace(name))
        {
            whereClauses.Add("c.name LIKE @name");
            parameters.Add(new SqlParameter("@name", $"%{name.Trim()}%"));
        }

        if (!string.IsNullOrWhiteSpace(pageName))
        {
            whereClauses.Add("c.page LIKE @pageName");
            parameters.Add(new SqlParameter("@pageName", $"%{pageName.Trim()}%"));
        }

        if (!string.IsNullOrWhiteSpace(feature))
        {
            whereClauses.Add("c.feature LIKE @feature");
            parameters.Add(new SqlParameter("@feature", $"%{feature.Trim()}%"));
        }

        AddCsvFilter(projectIds, "c.project_id", "projectId", whereClauses, parameters);
        AddCsvFilter(typeIds, "c.type_id", "typeId", whereClauses, parameters);

        if (status.HasValue)
        {
            whereClauses.Add("ISNULL(c.status, 1) = @status");
            parameters.Add(new SqlParameter("@status", status.Value));
        }

        var sql = $"""
            SELECT
                p.project_name,
                c.page,
                c.feature,
                cs.display_id,
                cs.description,
                cs.expected_output,
                ck.name AS custom_keyword_name,
                gk.name AS global_keyword_name,
                cs.before_step,
                cs.xpath,
                cs.after_step
            FROM components c
            INNER JOIN projects p ON p.id = c.project_id AND p.deleted_at IS NULL
            INNER JOIN component_steps cs ON cs.component_id = c.id
            LEFT JOIN component_keywords ck ON ck.id = cs.keyword_id
            LEFT JOIN global_keywords gk ON gk.id = cs.global_keyword_id
            WHERE {string.Join(" AND ", whereClauses)}
            ORDER BY p.project_name, c.page, c.feature, ISNULL(cs.display_id, 2147483647), cs.id;
            """;

        var builder = new StringBuilder();
        builder.AppendLine("project_page_feature,input,display_id,action,expected,beforestep,keyword,locator,afterstep");

        await using var command = CreateCommand(connection, sql);
        AddParameters(command, parameters);
        await using var dbReader = await command.ExecuteReaderAsync(cancellationToken);
        while (await dbReader.ReadAsync(cancellationToken))
        {
            var projectPageFeature = $"{GetString(dbReader, "project_name")}_{GetString(dbReader, "page")}_{GetString(dbReader, "feature")}";
            var keyword = GetString(dbReader, "custom_keyword_name") ?? GetString(dbReader, "global_keyword_name") ?? string.Empty;
            var row = new[]
            {
                projectPageFeature,
                string.Empty,
                GetInt32(dbReader, "display_id")?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                GetString(dbReader, "description") ?? string.Empty,
                GetString(dbReader, "expected_output") ?? string.Empty,
                string.Join('|', ParseStringArray(GetString(dbReader, "before_step"))),
                keyword,
                GetString(dbReader, "xpath") ?? string.Empty,
                string.Join('|', ParseStringArray(GetString(dbReader, "after_step")))
            };
            builder.AppendLine(string.Join(',', row.Select(EscapeCsv)));
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    public async Task<byte[]> ExportTestSuitesMatrixAsync(ClaimsPrincipal principal, string? query, string? tags, long? projectId, long? testStateId, int? testSuiteType, CancellationToken cancellationToken = default)
    {
        var suitesPayload = await GetTestSuitesAsync(principal, query, tags, projectId, testStateId, testSuiteType, null, 1, 0, true, cancellationToken);
        var suites = suitesPayload as IReadOnlyList<TestSuiteListDto> ?? [];
        var requirementsRows = new List<RequirementsSheetRow>();
        var testDataRows = new List<TestDataSheetRow>();
        var maxStepColumns = 1;
        var serial = 1;

        foreach (var suite in suites.OrderBy(item => item.Id))
        {
            var full = await GetTestSuiteFullAsync(principal, suite.Id, cancellationToken);
            if (full is null)
            {
                continue;
            }

            var tcid = $"TC{full.Id.ToString(CultureInfo.InvariantCulture)}";
            requirementsRows.Add(new RequirementsSheetRow
            {
                TCID = tcid,
                Ser = serial.ToString(CultureInfo.InvariantCulture),
                Requirement = full.Title ?? string.Empty,
                RunFlag = "N",
                Status = string.Empty
            });
            serial++;

            foreach (var component in full.Components)
            {
                var projectSegment = NormalizeOptionalText(suite.ProjectName)
                    ?? component.Component?.ProjectId?.ToString(CultureInfo.InvariantCulture)
                    ?? full.ProjectId?.ToString(CultureInfo.InvariantCulture)
                    ?? "PROJECT";
                var pageSegment = NormalizeOptionalText(component.Component?.Page) ?? "PAGE";
                var featureSegment = NormalizeOptionalText(component.Component?.Feature)
                    ?? NormalizeOptionalText(component.Component?.Name)
                    ?? $"COMP_{component.ComponentId?.ToString(CultureInfo.InvariantCulture) ?? "UNKNOWN"}";
                var tsid = $"{projectSegment}_{pageSegment}_{featureSegment}";

                var orderedSteps = component.Component?.Steps
                    .OrderBy(step => step.DisplayId ?? int.MaxValue)
                    .ThenBy(step => step.Id)
                    .ToList()
                    ?? [];

                if (orderedSteps.Count == 0)
                {
                    // If component defaults are unavailable, derive step slots from dataset rows.
                    var distinctDatasetSlots = component.Datasets
                        .SelectMany(dataset => dataset.Steps)
                        .Select(step => step.DisplayId ?? 0)
                        .Where(display => display > 0)
                        .Distinct()
                        .OrderBy(display => display)
                        .ToArray();

                    orderedSteps = distinctDatasetSlots
                        .Select(display => new ComponentStepDto
                        {
                            Id = display,
                            DisplayId = display,
                            Description = $"Step {display}"
                        })
                        .ToList();
                }

                var stepLabels = orderedSteps
                    .Select(step => NormalizeOptionalText(step.Description) ?? $"Step {(step.DisplayId ?? 0)}")
                    .ToList();

                if (stepLabels.Count == 0)
                {
                    stepLabels.Add(string.Empty);
                }

                maxStepColumns = Math.Max(maxStepColumns, stepLabels.Count);

                testDataRows.Add(new TestDataSheetRow
                {
                    TDID = tcid,
                    TSID = tsid,
                    E = "N",
                    TestCaseDescription = "FN",
                    Expected = string.Empty,
                    Actual = string.Empty,
                    Result = string.Empty,
                    StepValues = stepLabels
                });

                foreach (var dataset in component.Datasets)
                {
                    var values = new List<string>();
                    foreach (var step in orderedSteps)
                    {
                        var datasetStep = dataset.Steps.FirstOrDefault(item =>
                            (step.DisplayId.HasValue && item.DisplayId == step.DisplayId)
                            || item.StepId == step.Id
                            || item.InternalStepId == step.Id);

                        values.Add(BuildExportStepCellValue(step, datasetStep));
                    }

                    var description = NormalizeOptionalText(dataset.Scenario);
                    if (string.Equals(description, "FN", StringComparison.OrdinalIgnoreCase))
                    {
                        description = "Dataset";
                    }

                    testDataRows.Add(new TestDataSheetRow
                    {
                        TDID = tcid,
                        TSID = tsid,
                        E = dataset.Status ? "Y" : "N",
                        TestCaseDescription = description ?? "Dataset",
                        Expected = string.Empty,
                        Actual = string.Empty,
                        Result = string.Empty,
                        StepValues = values
                    });

                    maxStepColumns = Math.Max(maxStepColumns, values.Count);
                }
            }
        }

        return BuildTwoSheetWorkbook(requirementsRows, testDataRows, maxStepColumns);
    }

    public async Task<TestSuiteMatrixValidationDto> ValidateTestSuitesMatrixAsync(ClaimsPrincipal principal, Stream stream, CancellationToken cancellationToken = default)
    {
        var payload = await ReadAllBytesAsync(stream, cancellationToken);

        await using var workbookStream = new MemoryStream(payload, writable: false);
        if (await TryParseTwoSheetWorkbookAsync(workbookStream, cancellationToken) is TwoSheetWorkbookImport workbook)
        {
            var tdidSet = workbook.Requirements
                .Select(item => item.TCID)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var componentGroupSet = workbook.TestData
                .Where(item => !string.IsNullOrWhiteSpace(item.TDID) && !string.IsNullOrWhiteSpace(item.TSID))
                .Select(item => $"{item.TDID}:{item.TSID}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var datasetRows = workbook.TestData
                .Count(item => !IsReferenceRow(item));

            return new TestSuiteMatrixValidationDto
            {
                TdidCount = tdidSet.Count,
                ComponentGroups = componentGroupSet.Count,
                DatasetRows = datasetRows
            };
        }

        _ = principal;
        await using var legacyStream = new MemoryStream(payload, writable: false);
        var rows = await ParseTestSuiteMatrixRowsAsync(legacyStream, cancellationToken);

        var tdidLegacy = new HashSet<long>();
        var componentLegacy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var datasetLegacy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            if (row.TestSuiteId.HasValue)
            {
                tdidLegacy.Add(row.TestSuiteId.Value);
            }

            if (row.ComponentId.HasValue)
            {
                var suiteKey = row.TestSuiteId?.ToString(CultureInfo.InvariantCulture) ?? $"{row.ProjectId}:{row.Title}";
                var componentKey = $"{suiteKey}:{row.ComponentId.Value}";
                componentLegacy.Add(componentKey);

                var datasetIndex = row.DatasetIndex ?? 1;
                datasetLegacy.Add($"{componentKey}:{datasetIndex}");
            }
        }

        return new TestSuiteMatrixValidationDto
        {
            TdidCount = tdidLegacy.Count,
            ComponentGroups = componentLegacy.Count,
            DatasetRows = datasetLegacy.Count
        };
    }

    public async Task<ImportTestSuitesResultDto> ImportTestSuitesMatrixAsync(ClaimsPrincipal principal, Stream stream, CancellationToken cancellationToken = default)
    {
        var payload = await ReadAllBytesAsync(stream, cancellationToken);

        await using var workbookStream = new MemoryStream(payload, writable: false);
        if (await TryParseTwoSheetWorkbookAsync(workbookStream, cancellationToken) is TwoSheetWorkbookImport workbook)
        {
            return await ImportTwoSheetWorkbookAsync(principal, workbook, cancellationToken);
        }

        await using var legacyStream = new MemoryStream(payload, writable: false);
        var rows = await ParseTestSuiteMatrixRowsAsync(legacyStream, cancellationToken);
        var models = BuildTestSuiteImportModels(rows);
        if (models.Count == 0)
        {
            return new ImportTestSuitesResultDto();
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var defaultStateId = await LoadDefaultTestStateIdAsync(connection, cancellationToken);

        var createdTests = 0;
        var updatedTests = 0;
        var createdDatasets = 0;

        foreach (var model in models)
        {
            var details = new SaveTestSuiteDetailsRequest
            {
                Title = model.Title,
                ProjectId = model.ProjectId,
                TestStateId = model.TestStateId ?? defaultStateId,
                TestSuiteType = model.TestSuiteType is 2 ? 2 : 1,
                Priority = NormalizeOptionalText(model.Priority),
                StoryId = NormalizeOptionalText(model.StoryId),
                TestTitle = NormalizeOptionalText(model.TestTitle),
                Tags = CreateTagsJsonElement(model.Tags),
                Comment = NormalizeOptionalText(model.Comment),
                KbaReady = false,
                TrainingReady = false,
                ReleaseNotesReady = false
            };

            var designedComponents = model.Components
                .Select(component => new SaveTestSuiteComponentRequest
                {
                    ComponentId = component.ComponentId,
                    ProjectId = component.ProjectId ?? model.ProjectId,
                    Status = component.Status,
                    Datasets = component.Datasets.Select(dataset => new SaveTestSuiteDatasetRequest
                    {
                        Scenario = NormalizeOptionalText(dataset.Scenario),
                        Status = dataset.Status,
                        Steps = dataset.Steps
                            .Where(step => step.StepId.HasValue)
                            .Select(step => new SaveTestSuiteStepRequest
                            {
                                DisplayId = step.DisplayId,
                                Id = step.StepId,
                                Value = step.Value,
                                Override = step.Override,
                                OverrideValue = step.OverrideValue
                            })
                            .ToList()
                    }).ToList()
                })
                .ToList();

            var request = new SaveTestSuiteRequest
            {
                Details = details,
                DesignedComponents = designedComponents
            };

            createdDatasets += designedComponents.Sum(component => component.Datasets.Count);

            if (model.TestSuiteId.HasValue)
            {
                var update = await UpdateTestSuiteAsync(principal, model.TestSuiteId.Value, request, cancellationToken);
                if (update.Outcome == SaveTestSuiteOutcome.Saved)
                {
                    updatedTests++;
                    continue;
                }

                if (update.Outcome != SaveTestSuiteOutcome.NotFound)
                {
                    throw new InvalidOperationException(update.ErrorMessage ?? "Unable to import test suite row due to invalid references.");
                }
            }

            var create = await CreateTestSuiteAsync(principal, request, cancellationToken);
            if (create.Outcome != SaveTestSuiteOutcome.Saved)
            {
                throw new InvalidOperationException(create.ErrorMessage ?? "Unable to import test suite row due to invalid references.");
            }

            createdTests++;
        }

        return new ImportTestSuitesResultDto
        {
            CreatedTests = createdTests,
            UpdatedTests = updatedTests,
            CreatedDatasets = createdDatasets
        };
    }

    public async Task<ComponentDetailDto?> CreateComponentAsync(ClaimsPrincipal principal, SaveComponentRequest request, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var auditUserId = await ResolveAuditUserIdAsync(connection, context.ClientId.Value, context.UserId, cancellationToken);
        var displayName = NormalizeOptionalText(GetUserDisplayName(principal)) ?? $"User {context.UserId}";
        var updatedBy = $"{displayName}-{DateTime.Now:M-d-yy h:mm tt}";

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            const string insertSql = """
                INSERT INTO components
                (
                    client_id,
                    project_id,
                    name,
                    page,
                    feature,
                    type_id,
                    locked,
                    status,
                    created_by_id,
                    updated_by_id,
                    created_by,
                    updated_by,
                    created_at,
                    updated_at
                )
                OUTPUT INSERTED.id
                VALUES
                (
                    @clientId,
                    @projectId,
                    @name,
                    @page,
                    @feature,
                    @typeId,
                    0,
                    1,
                    @createdById,
                    @updatedById,
                    @createdBy,
                    @updatedBy,
                    SYSUTCDATETIME(),
                    SYSUTCDATETIME()
                );
                """;

            long componentId;
            await using (var command = CreateCommand(connection, insertSql))
            {
                command.Transaction = transaction;
                command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
                command.Parameters.AddWithValue("@projectId", request.ProjectId!.Value);
                command.Parameters.AddWithValue("@name", request.Name!.Trim());
                command.Parameters.AddWithValue("@page", request.Page!.Trim());
                command.Parameters.AddWithValue("@feature", request.Feature!.Trim());
                command.Parameters.AddWithValue("@typeId", request.TypeId ?? 1L);
                command.Parameters.AddWithValue("@createdById", (object?)auditUserId ?? DBNull.Value);
                command.Parameters.AddWithValue("@updatedById", (object?)auditUserId ?? DBNull.Value);
                command.Parameters.AddWithValue("@createdBy", displayName);
                command.Parameters.AddWithValue("@updatedBy", updatedBy);
                componentId = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
            }

            await InsertComponentStepsAsync(connection, transaction, componentId, request.Steps, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return await GetComponentAsync(principal, componentId, cancellationToken);
        }
        catch
        {
            try
            {
                if (transaction.Connection is not null)
                {
                    await transaction.RollbackAsync(cancellationToken);
                }
            }
            catch
            {
                // Preserve the original create/update failure when SQL Server has already completed the transaction.
            }
            throw;
        }
    }

    public async Task<ComponentDetailDto?> UpdateComponentAsync(ClaimsPrincipal principal, long componentId, SaveComponentRequest request, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var auditUserId = await ResolveAuditUserIdAsync(connection, context.ClientId.Value, context.UserId, cancellationToken);
        var displayName = NormalizeOptionalText(GetUserDisplayName(principal)) ?? $"User {context.UserId}";
        var updatedBy = $"{displayName}-{DateTime.Now:M-d-yy h:mm tt}";

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            const string updateSql = """
                UPDATE components
                SET project_id = @projectId,
                    name = @name,
                    page = @page,
                    feature = @feature,
                    type_id = @typeId,
                    updated_by_id = @updatedById,
                    updated_by = @updatedBy,
                    updated_at = SYSUTCDATETIME()
                WHERE id = @id AND client_id = @clientId AND deleted_at IS NULL;
                """;

            await using (var command = CreateCommand(connection, updateSql))
            {
                command.Transaction = transaction;
                command.Parameters.AddWithValue("@id", componentId);
                command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
                command.Parameters.AddWithValue("@projectId", request.ProjectId!.Value);
                command.Parameters.AddWithValue("@name", request.Name!.Trim());
                command.Parameters.AddWithValue("@page", request.Page!.Trim());
                command.Parameters.AddWithValue("@feature", request.Feature!.Trim());
                command.Parameters.AddWithValue("@typeId", request.TypeId ?? 1L);
                command.Parameters.AddWithValue("@updatedById", (object?)auditUserId ?? DBNull.Value);
                command.Parameters.AddWithValue("@updatedBy", updatedBy);
                var affected = await command.ExecuteNonQueryAsync(cancellationToken);
                if (affected == 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return null;
                }
            }

            var existingSteps = request.Steps.Where(step => step.Id.HasValue && step.Id.Value > 0).ToList();
            var newSteps = request.Steps.Where(step => !step.Id.HasValue || step.Id.Value <= 0).ToList();

            await UpdateComponentStepsAsync(connection, transaction, componentId, existingSteps, cancellationToken);
            await InsertComponentStepsAsync(connection, transaction, componentId, newSteps, cancellationToken);
            await DeleteComponentStepsAsync(connection, transaction, componentId, request.DeletedSteps, cancellationToken);

            var syncResult = await SyncLinkedComponentDatasetsAsync(connection, transaction, context.ClientId.Value, [componentId], cancellationToken);
            if (syncResult.AffectedSuites.Count > 0)
            {
                await TouchSyncedSuitesAsync(connection, transaction, context.ClientId.Value, syncResult.AffectedSuites, updatedBy, cancellationToken);
                await ResetLinkedPlanStatusesAsync(connection, transaction, syncResult.AffectedSuites.Select(item => item.SuiteId).ToArray(), cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);

            if (syncResult.AffectedSuites.Count > 0)
            {
                foreach (var projectGroup in syncResult.AffectedSuites.GroupBy(item => item.ProjectId))
                {
                    await QueueAutoSyncTestCaseJobsAsync(
                        connection,
                        context,
                        projectGroup.Select(item => item.SuiteId).Distinct().ToArray(),
                        projectGroup.Key,
                        cancellationToken);
                }
            }

            return await GetComponentAsync(principal, componentId, cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<CountResultDto> SyncComponentDatasetsAsync(ClaimsPrincipal principal, long? componentId = null, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return new CountResultDto();
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var displayName = NormalizeOptionalText(GetUserDisplayName(principal)) ?? $"User {context.UserId}";
        var updatedBy = $"{displayName}-{DateTime.Now:M-d-yy h:mm tt}";
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var componentIds = componentId.HasValue && componentId.Value > 0
                ? (IReadOnlyList<long>)[componentId.Value]
                : [];

            var syncResult = await SyncLinkedComponentDatasetsAsync(connection, transaction, context.ClientId.Value, componentIds, cancellationToken);
            if (syncResult.AffectedSuites.Count > 0)
            {
                await TouchSyncedSuitesAsync(connection, transaction, context.ClientId.Value, syncResult.AffectedSuites, updatedBy, cancellationToken);
                await ResetLinkedPlanStatusesAsync(connection, transaction, syncResult.AffectedSuites.Select(item => item.SuiteId).ToArray(), cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);

            if (syncResult.AffectedSuites.Count > 0)
            {
                foreach (var projectGroup in syncResult.AffectedSuites.GroupBy(item => item.ProjectId))
                {
                    await QueueAutoSyncTestCaseJobsAsync(
                        connection,
                        context,
                        projectGroup.Select(item => item.SuiteId).Distinct().ToArray(),
                        projectGroup.Key,
                        cancellationToken);
                }
            }

            return new CountResultDto { Count = syncResult.AffectedSuites.Count };
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<bool> DeleteComponentAsync(ClaimsPrincipal principal, long componentId, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return false;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        if (await ComponentHasLiveAssociationsAsync(connection, componentId, cancellationToken))
        {
            return false;
        }

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var stepCommand = CreateCommand(connection, "UPDATE component_steps SET deleted_at = SYSUTCDATETIME() WHERE component_id = @componentId AND deleted_at IS NULL;"))
            {
                stepCommand.Transaction = transaction;
                stepCommand.Parameters.AddWithValue("@componentId", componentId);
                await stepCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var componentCommand = CreateCommand(connection, "UPDATE components SET deleted_at = SYSUTCDATETIME(), updated_at = SYSUTCDATETIME() WHERE id = @componentId AND client_id = @clientId AND deleted_at IS NULL;"))
            {
                componentCommand.Transaction = transaction;
                componentCommand.Parameters.AddWithValue("@componentId", componentId);
                componentCommand.Parameters.AddWithValue("@clientId", context.ClientId.Value);
                var affected = await componentCommand.ExecuteNonQueryAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return affected > 0;
            }
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<bool> BulkDeleteComponentsAsync(ClaimsPrincipal principal, IReadOnlyList<long> componentIds, CancellationToken cancellationToken = default)
    {
        if (componentIds.Count == 0)
        {
            return true;
        }

        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return false;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        foreach (var componentId in componentIds)
        {
            if (await ComponentHasLiveAssociationsAsync(connection, componentId, cancellationToken))
            {
                return false;
            }
        }

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var parameters = AddIdListParameterValues(componentIds, "@componentId");
            var placeholders = string.Join(", ", parameters.Select(parameter => parameter.ParameterName));

            await using (var stepCommand = CreateCommand(connection, $"UPDATE component_steps SET deleted_at = SYSUTCDATETIME() WHERE component_id IN ({placeholders}) AND deleted_at IS NULL;"))
            {
                stepCommand.Transaction = transaction;
                AddParameters(stepCommand, parameters);
                await stepCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var componentCommand = CreateCommand(connection, $"UPDATE components SET deleted_at = SYSUTCDATETIME(), updated_at = SYSUTCDATETIME() WHERE client_id = @clientId AND id IN ({placeholders}) AND deleted_at IS NULL;"))
            {
                componentCommand.Transaction = transaction;
                componentCommand.Parameters.AddWithValue("@clientId", context.ClientId.Value);
                AddParameters(componentCommand, parameters);
                await componentCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<bool> UpdateComponentStatusAsync(ClaimsPrincipal principal, long componentId, bool status, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return false;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateCommand(connection, "UPDATE components SET status = @status, updated_at = SYSUTCDATETIME() WHERE id = @id AND client_id = @clientId AND deleted_at IS NULL;");
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@id", componentId);
        command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<bool> ComponentExistsAsync(ClaimsPrincipal principal, long projectId, string page, string feature, long? excludeId, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return false;
        }

        const string sql = """
            SELECT COUNT(*)
            FROM components
            WHERE client_id = @clientId
              AND project_id = @projectId
              AND deleted_at IS NULL
              AND LOWER(page) = LOWER(@page)
              AND LOWER(feature) = LOWER(@feature)
              AND (@excludeId IS NULL OR id <> @excludeId);
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var count = await ExecuteCountAsync(connection, sql, [
            new SqlParameter("@clientId", context.ClientId.Value),
            new SqlParameter("@projectId", projectId),
            new SqlParameter("@page", page.Trim()),
            new SqlParameter("@feature", feature.Trim()),
            new SqlParameter("@excludeId", (object?)excludeId ?? DBNull.Value)
        ], cancellationToken);

        return count > 0;
    }

    public async Task<ComponentMetadataCatalogDto?> GetComponentMetadataCatalogAsync(ClaimsPrincipal principal, long projectId, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return null;
        }

        const string sql = """
            SELECT DISTINCT
                LTRIM(RTRIM(page)) AS page,
                LTRIM(RTRIM(feature)) AS feature
            FROM components
            WHERE client_id = @clientId
              AND project_id = @projectId
              AND deleted_at IS NULL
              AND NULLIF(LTRIM(RTRIM(page)), '') IS NOT NULL
              AND NULLIF(LTRIM(RTRIM(feature)), '') IS NOT NULL;
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
        command.Parameters.AddWithValue("@projectId", projectId);

        var pages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var featurePages = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var page = GetString(reader, "page");
            var feature = GetString(reader, "feature");
            if (string.IsNullOrWhiteSpace(page) || string.IsNullOrWhiteSpace(feature))
            {
                continue;
            }

            pages.Add(page);
            if (!featurePages.TryGetValue(feature, out var projectPages))
            {
                projectPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                featurePages[feature] = projectPages;
            }

            projectPages.Add(page);
        }

        return new ComponentMetadataCatalogDto
        {
            Pages = pages.OrderBy(static item => item, StringComparer.OrdinalIgnoreCase).ToArray(),
            Features = featurePages
                .OrderBy(static entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static entry => new ComponentFeatureCatalogEntryDto
                {
                    Feature = entry.Key,
                    Pages = entry.Value.OrderBy(static item => item, StringComparer.OrdinalIgnoreCase).ToArray()
                })
                .ToArray()
        };
    }

    public async Task<bool> BulkDeleteProjectsAsync(ClaimsPrincipal principal, IReadOnlyList<long> projectIds, CancellationToken cancellationToken = default)
    {
        if (projectIds.Count == 0)
        {
            return true;
        }

        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return false;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        foreach (var projectId in projectIds)
        {
            if (await ProjectHasComponentsAsync(connection, context.ClientId.Value, projectId, cancellationToken))
            {
                return false;
            }
        }

        var parameters = AddIdListParameterValues(projectIds, "@projectId");
        var placeholders = string.Join(", ", parameters.Select(parameter => parameter.ParameterName));
        await using var command = CreateCommand(connection, $"UPDATE projects SET deleted_at = SYSUTCDATETIME(), updated_at = SYSUTCDATETIME() WHERE client_id = @clientId AND id IN ({placeholders}) AND deleted_at IS NULL;");
        command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
        AddParameters(command, parameters);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return true;
    }

    public async Task<bool> UpdateProjectStatusAsync(ClaimsPrincipal principal, long projectId, bool status, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return false;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateCommand(connection, "UPDATE projects SET status = @status, updated_at = SYSUTCDATETIME() WHERE id = @id AND client_id = @clientId AND deleted_at IS NULL;");
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@id", projectId);
        command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<ConfigurationDto?> CreateConfigurationAsync(ClaimsPrincipal principal, SaveConfigurationRequest request, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            const string sql = """
                INSERT INTO configurations (name, description, status, client_id, created_at, updated_at)
                OUTPUT INSERTED.id
                VALUES (@name, @description, @status, @clientId, SYSUTCDATETIME(), SYSUTCDATETIME());
                """;

            long id;
            await using (var command = CreateCommand(connection, sql))
            {
                command.Transaction = transaction;
                command.Parameters.AddWithValue("@name", request.Name!.Trim());
                command.Parameters.AddWithValue("@description", request.Description!.Trim());
                command.Parameters.AddWithValue("@status", request.Status ?? 1);
                command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
                id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
            }

            await ReplaceConfigurationSelectionsAsync(connection, transaction, id, request.ConfigurationVariables, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return await GetConfigurationByIdAsync(connection, context.ClientId.Value, id, cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<ConfigurationDto?> UpdateConfigurationAsync(ClaimsPrincipal principal, long id, SaveConfigurationRequest request, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            const string sql = """
                UPDATE configurations
                SET name = @name,
                    description = @description,
                    status = @status,
                    updated_at = SYSUTCDATETIME()
                WHERE id = @id AND client_id = @clientId;
                """;

            await using (var command = CreateCommand(connection, sql))
            {
                command.Transaction = transaction;
                command.Parameters.AddWithValue("@id", id);
                command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
                command.Parameters.AddWithValue("@name", request.Name!.Trim());
                command.Parameters.AddWithValue("@description", request.Description!.Trim());
                command.Parameters.AddWithValue("@status", request.Status ?? 1);
                var affected = await command.ExecuteNonQueryAsync(cancellationToken);
                if (affected == 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return null;
                }
            }

            await ReplaceConfigurationSelectionsAsync(connection, transaction, id, request.ConfigurationVariables, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return await GetConfigurationByIdAsync(connection, context.ClientId.Value, id, cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<bool> DeleteConfigurationAsync(ClaimsPrincipal principal, long id, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return false;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var deleteSelections = CreateCommand(connection, "DELETE FROM configurations_selected_variables WHERE configuration_id = @id;"))
            {
                deleteSelections.Transaction = transaction;
                deleteSelections.Parameters.AddWithValue("@id", id);
                await deleteSelections.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var deleteConfiguration = CreateCommand(connection, "DELETE FROM configurations WHERE id = @id AND client_id = @clientId;");
            deleteConfiguration.Transaction = transaction;
            deleteConfiguration.Parameters.AddWithValue("@id", id);
            deleteConfiguration.Parameters.AddWithValue("@clientId", context.ClientId.Value);
            var affected = await deleteConfiguration.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return affected > 0;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyList<TestPlanSuitesForItemDto>> AssignConfigurationsToSuiteAsync(ClaimsPrincipal principal, AssignConfigurationsToSuiteRequest request, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return [];
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await EnsurePointBasedConfigurationStateAsync(connection, request.TestPlanItemId, cancellationToken);
        var existing = await GetSuitesForPlanItemLightAsync(principal, request.TestPlanItemId, cancellationToken);
        var allRows = existing.SelectMany(item => item.AddedSuites).ToList();
        var parentRow = allRows.FirstOrDefault(row => (row.ParentId is null || row.ParentId == 0) && (row.TestDesignId == request.TestSuiteId || row.Id == request.TestSuiteId));
        if (parentRow is null)
        {
            return existing;
        }
        var selectedConfigIds = request.ConfigurationsId.Where(id => id > 0).Distinct().ToHashSet();

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await EnsurePointBasedConfigurationStateAsync(connection, request.TestPlanItemId, cancellationToken, transaction);

            var currentAssignments = await LoadConfigurationAssignmentsForPlanItemAsync(connection, context.ClientId.Value, request.TestPlanItemId, cancellationToken, transaction);
            var currentByConfigId = currentAssignments
                .Where(row => row.ParentSuiteLinkId == parentRow.Id)
                .ToDictionary(row => row.ConfigurationId);

            var desiredNewConfigs = selectedConfigIds.Where(id => !currentByConfigId.ContainsKey(id)).ToArray();
            var removedConfigs = currentByConfigId.Keys.Where(id => !selectedConfigIds.Contains(id)).ToArray();

            foreach (var configId in removedConfigs)
            {
                await using var deleteAssignment = CreateCommand(connection, "UPDATE test_plan_item_suite_configurations SET deleted_at = SYSUTCDATETIME(), updated_at = SYSUTCDATETIME() WHERE id = @id AND deleted_at IS NULL;");
                deleteAssignment.Transaction = transaction;
                deleteAssignment.Parameters.AddWithValue("@id", currentByConfigId[configId].AssignmentId);
                await deleteAssignment.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var configId in desiredNewConfigs)
            {
                await using var insertAssignment = CreateCommand(connection, "INSERT INTO test_plan_item_suite_configurations (test_plan_item_suite_id, configuration_id, status_id, created_at, updated_at) VALUES (@suiteLinkId, @configurationId, 1, SYSUTCDATETIME(), SYSUTCDATETIME());");
                insertAssignment.Transaction = transaction;
                insertAssignment.Parameters.AddWithValue("@suiteLinkId", parentRow.Id);
                insertAssignment.Parameters.AddWithValue("@configurationId", configId);
                await insertAssignment.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        return await GetSuitesForPlanItemLightAsync(principal, request.TestPlanItemId, cancellationToken);
    }

    public async Task<TestPlanDto?> CreateTestPlanAsync(ClaimsPrincipal principal, SaveTestPlanRequest request, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            const string sql = """
                INSERT INTO test_plans
                (
                    name,
                    project_id,
                    area_path,
                    iteration_path,
                    client_id,
                    type,
                    plan_type,
                    plan_status,
                    is_active,
                    status,
                    owner_user_id,
                    start_date,
                    end_date,
                    target_version,
                    objective,
                    created_at,
                    updated_at
                )
                OUTPUT INSERTED.id
                VALUES
                (
                    @name,
                    @projectId,
                    @areaPath,
                    @iterationPath,
                    @clientId,
                    @type,
                    @planType,
                    @planStatus,
                    @isActive,
                    @status,
                    @ownerUserId,
                    @startDate,
                    @endDate,
                    @targetVersion,
                    @objective,
                    SYSUTCDATETIME(),
                    SYSUTCDATETIME()
                );
                """;

            long id;
            await using (var command = CreateCommand(connection, sql))
            {
                command.Transaction = transaction;
                command.Parameters.AddWithValue("@name", request.Name!.Trim());
                command.Parameters.AddWithValue("@projectId", request.ProjectId!.Value);
                command.Parameters.AddWithValue("@areaPath", (object?)NormalizeOptionalText(request.AreaPath) ?? DBNull.Value);
                command.Parameters.AddWithValue("@iterationPath", (object?)NormalizeOptionalText(request.IterationPath) ?? DBNull.Value);
                command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
                command.Parameters.AddWithValue("@type", request.Type ?? 1);
                command.Parameters.AddWithValue("@planType", (object?)NormalizeOptionalText(request.PlanType) ?? "Sprint");
                command.Parameters.AddWithValue("@planStatus", (object?)NormalizeOptionalText(request.PlanStatus) ?? "Draft");
                command.Parameters.AddWithValue("@isActive", request.IsActive ?? true);
                command.Parameters.AddWithValue("@status", (request.IsActive ?? true) ? 1 : 0);
                command.Parameters.AddWithValue("@ownerUserId", (object?)request.OwnerUserId ?? DBNull.Value);
                command.Parameters.AddWithValue("@startDate", (object?)NormalizeOptionalText(request.StartDate) ?? DBNull.Value);
                command.Parameters.AddWithValue("@endDate", (object?)NormalizeOptionalText(request.EndDate) ?? DBNull.Value);
                command.Parameters.AddWithValue("@targetVersion", (object?)NormalizeOptionalText(request.TargetVersion) ?? DBNull.Value);
                command.Parameters.AddWithValue("@objective", (object?)NormalizeOptionalText(request.Objective) ?? DBNull.Value);
                id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
            }

            await ReplaceTestPlanUsersAsync(connection, transaction, id, request.UsersId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return await GetTestPlanAsync(principal, id, cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<TestPlanDto?> UpdateTestPlanAsync(ClaimsPrincipal principal, long id, SaveTestPlanRequest request, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var lastUpdated = $"{principal.FindFirstValue(ClaimTypes.Email) ?? GetUserDisplayName(principal) ?? $"User {context.UserId}"}-{DateTime.Now:O}";
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            const string sql = """
                UPDATE test_plans
                SET name = @name,
                    project_id = @projectId,
                    area_path = @areaPath,
                    iteration_path = @iterationPath,
                    type = @type,
                    plan_type = @planType,
                    plan_status = @planStatus,
                    is_active = @isActive,
                    status = @status,
                    owner_user_id = @ownerUserId,
                    start_date = @startDate,
                    end_date = @endDate,
                    target_version = @targetVersion,
                    objective = @objective,
                    last_updated = @lastUpdated,
                    updated_at = SYSUTCDATETIME()
                WHERE id = @id AND client_id = @clientId;
                """;

            await using (var command = CreateCommand(connection, sql))
            {
                command.Transaction = transaction;
                command.Parameters.AddWithValue("@id", id);
                command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
                command.Parameters.AddWithValue("@name", request.Name!.Trim());
                command.Parameters.AddWithValue("@projectId", request.ProjectId!.Value);
                command.Parameters.AddWithValue("@areaPath", (object?)NormalizeOptionalText(request.AreaPath) ?? DBNull.Value);
                command.Parameters.AddWithValue("@iterationPath", (object?)NormalizeOptionalText(request.IterationPath) ?? DBNull.Value);
                command.Parameters.AddWithValue("@type", request.Type ?? 1);
                command.Parameters.AddWithValue("@planType", (object?)NormalizeOptionalText(request.PlanType) ?? "Sprint");
                command.Parameters.AddWithValue("@planStatus", (object?)NormalizeOptionalText(request.PlanStatus) ?? "Draft");
                command.Parameters.AddWithValue("@isActive", request.IsActive ?? true);
                command.Parameters.AddWithValue("@status", (request.IsActive ?? true) ? 1 : 0);
                command.Parameters.AddWithValue("@ownerUserId", (object?)request.OwnerUserId ?? DBNull.Value);
                command.Parameters.AddWithValue("@startDate", (object?)NormalizeOptionalText(request.StartDate) ?? DBNull.Value);
                command.Parameters.AddWithValue("@endDate", (object?)NormalizeOptionalText(request.EndDate) ?? DBNull.Value);
                command.Parameters.AddWithValue("@targetVersion", (object?)NormalizeOptionalText(request.TargetVersion) ?? DBNull.Value);
                command.Parameters.AddWithValue("@objective", (object?)NormalizeOptionalText(request.Objective) ?? DBNull.Value);
                command.Parameters.AddWithValue("@lastUpdated", lastUpdated);
                var affected = await command.ExecuteNonQueryAsync(cancellationToken);
                if (affected == 0)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return null;
                }
            }

            await ReplaceTestPlanUsersAsync(connection, transaction, id, request.UsersId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return await GetTestPlanAsync(principal, id, cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<bool> DeleteTestPlanAsync(ClaimsPrincipal principal, long id, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return false;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string suiteCheckSql = """
            SELECT COUNT(*)
            FROM test_plan_item_suites tpis
            INNER JOIN test_plan_items tpi ON tpi.id = tpis.test_plan_item_id
            INNER JOIN test_plans tp ON tp.id = tpi.test_plan_id
            WHERE tp.id = @id AND tp.client_id = @clientId;
            """;
        if (await ExecuteCountAsync(connection, suiteCheckSql, [new SqlParameter("@id", id), new SqlParameter("@clientId", context.ClientId.Value)], cancellationToken) > 0)
        {
            return false;
        }

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var sql in new[]
            {
                "DELETE FROM test_plan_users WHERE test_plan_id = @id;",
                "DELETE FROM test_plan_items WHERE test_plan_id = @id;",
                "DELETE FROM test_plans WHERE id = @id AND client_id = @clientId;"
            })
            {
                await using var command = CreateCommand(connection, sql);
                command.Transaction = transaction;
                command.Parameters.AddWithValue("@id", id);
                command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<bool> UpdateTestPlanStatusAsync(ClaimsPrincipal principal, long id, bool status, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return false;
        }

        var lastUpdated = $"{principal.FindFirstValue(ClaimTypes.Email) ?? GetUserDisplayName(principal) ?? $"User {context.UserId}"}-{DateTime.Now:O}";
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = CreateCommand(connection, "UPDATE test_plans SET status = @status, is_active = @status, last_updated = @lastUpdated, updated_at = SYSUTCDATETIME() WHERE id = @id AND client_id = @clientId;");
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@lastUpdated", lastUpdated);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<TestPlanItemDto?> CreateTestPlanItemAsync(ClaimsPrincipal principal, SaveTestPlanItemRequest request, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = """
            INSERT INTO test_plan_items (name, test_plan_id, client_id, created_at, updated_at)
            OUTPUT INSERTED.id
            VALUES (@name, @testPlanId, @clientId, SYSUTCDATETIME(), SYSUTCDATETIME());
            """;

        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@name", request.Name!.Trim());
        command.Parameters.AddWithValue("@testPlanId", request.TestPlanId!.Value);
        command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
        var id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        return new TestPlanItemDto { Id = id, Name = request.Name.Trim(), TestPlanId = request.TestPlanId };
    }

    public async Task<TestPlanItemDto?> UpdateTestPlanItemAsync(ClaimsPrincipal principal, long id, SaveTestPlanItemRequest request, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = """
            UPDATE tpi
            SET tpi.name = @name,
                tpi.updated_at = SYSUTCDATETIME()
            FROM test_plan_items tpi
            INNER JOIN test_plans tp ON tp.id = tpi.test_plan_id
            WHERE tpi.id = @id AND tp.client_id = @clientId;
            """;

        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@name", request.Name!.Trim());
        command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return affected == 0 ? null : await GetTestPlanItemByIdAsync(connection, context.ClientId.Value, id, cancellationToken);
    }

    public async Task<bool> DeleteTestPlanItemAsync(ClaimsPrincipal principal, long id, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return false;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        if (await ExecuteCountAsync(connection, "SELECT COUNT(*) FROM test_plan_item_suites WHERE test_plan_item_id = @id;", [new SqlParameter("@id", id)], cancellationToken) > 0)
        {
            return false;
        }

        await using var command = CreateCommand(connection, "DELETE tpi FROM test_plan_items tpi INNER JOIN test_plans tp ON tp.id = tpi.test_plan_id WHERE tpi.id = @id AND tp.client_id = @clientId;");
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public Task<IReadOnlyList<TestPlanSuitesForItemDto>> GetSuitesForPlanItemAsync(ClaimsPrincipal principal, long testPlanItemId, CancellationToken cancellationToken = default)
    {
        return GetSuitesForPlanItemLightAsync(principal, testPlanItemId, cancellationToken);
    }

    public async Task<IReadOnlyList<TestPlanSuitesForItemDto>> AddSuitesToPlanItemAsync(ClaimsPrincipal principal, AddSuitesToPlanItemRequest request, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return [];
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        long? projectId = null;
        var addedSuiteIds = new HashSet<long>();
        try
        {
            projectId = await LoadPlanItemProjectIdAsync(connection, transaction, request.TestPlanItemId, cancellationToken);

            foreach (var suiteId in request.TestDesignIds.Distinct())
            {
                const string existsSql = "SELECT COUNT(*) FROM test_plan_item_suites WHERE test_plan_item_id = @testPlanItemId AND test_design_id = @testDesignId AND deleted_at IS NULL;";
                int exists;
                await using (var existsCommand = CreateCommand(connection, existsSql))
                {
                    existsCommand.Transaction = transaction;
                    existsCommand.Parameters.AddWithValue("@testPlanItemId", request.TestPlanItemId);
                    existsCommand.Parameters.AddWithValue("@testDesignId", suiteId);
                    exists = Convert.ToInt32(await existsCommand.ExecuteScalarAsync(cancellationToken));
                }
                if (exists > 0)
                {
                    continue;
                }

                long linkId;
                await using (var insertLink = CreateCommand(connection, "INSERT INTO test_plan_item_suites (test_plan_item_id, test_design_id, status_id, created_at, updated_at) OUTPUT INSERTED.id VALUES (@testPlanItemId, @testDesignId, 1, SYSUTCDATETIME(), SYSUTCDATETIME());"))
                {
                    insertLink.Transaction = transaction;
                    insertLink.Parameters.AddWithValue("@testPlanItemId", request.TestPlanItemId);
                    insertLink.Parameters.AddWithValue("@testDesignId", suiteId);
                    linkId = Convert.ToInt64(await insertLink.ExecuteScalarAsync(cancellationToken));
                }

                await CopyPlanUsersToSuiteAsync(connection, transaction, request.TestPlanItemId, linkId, cancellationToken);
                if (suiteId > 0)
                {
                    addedSuiteIds.Add(suiteId);
                }
            }

            await transaction.CommitAsync(cancellationToken);

            if (addedSuiteIds.Count > 0)
            {
                await QueueAutoSyncTestCaseJobsAsync(connection, context, addedSuiteIds, projectId, cancellationToken);
            }
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        return await GetSuitesForPlanItemLightAsync(principal, request.TestPlanItemId, cancellationToken);
    }

    private async Task<long?> LoadPlanItemProjectIdAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long testPlanItemId,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT tp.project_id
            FROM test_plan_items tpi
            INNER JOIN test_plans tp ON tp.id = tpi.test_plan_id
            WHERE tpi.id = @testPlanItemId;
            """;

        await using var command = CreateCommand(connection, sql);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@testPlanItemId", testPlanItemId);

        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null || value is DBNull)
        {
            return null;
        }

        return Convert.ToInt64(value);
    }

    public async Task<bool> RemoveSuitesFromPlanItemAsync(ClaimsPrincipal principal, IReadOnlyList<long> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
        {
            return true;
        }

        var context = GetRequestContext(principal);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await EnsurePointBasedConfigurationStateAsync(connection, null, cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        List<(long TestDesignId, long? ProjectId)> removedSuites;
        try
        {
            var assignmentIds = ids.Where(IsConfigurationExecutionId).Select(ToConfigurationAssignmentId).Distinct().ToArray();
            var suiteLinkIds = ids.Where(id => !IsConfigurationExecutionId(id)).Distinct().ToArray();

            if (assignmentIds.Length > 0)
            {
                var assignmentParameters = AddIdListParameterValues(assignmentIds, "@assignmentId");
                var assignmentSql = $"UPDATE test_plan_item_suite_configurations SET deleted_at = SYSUTCDATETIME(), updated_at = SYSUTCDATETIME() WHERE deleted_at IS NULL AND id IN ({string.Join(", ", assignmentParameters.Select(parameter => parameter.ParameterName))});";
                await using var assignmentCommand = CreateCommand(connection, assignmentSql);
                assignmentCommand.Transaction = transaction;
                AddParameters(assignmentCommand, assignmentParameters);
                await assignmentCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            if (suiteLinkIds.Length == 0)
            {
                await transaction.CommitAsync(cancellationToken);
                return true;
            }

            removedSuites = await LoadSuiteRemovalTargetsAsync(connection, transaction, suiteLinkIds, cancellationToken);
            var linkedChildren = await LoadSuiteLinksAsync(connection, transaction, suiteLinkIds, cancellationToken);
            var childDesignIds = linkedChildren.Where(item => item.ParentId.HasValue && item.TestDesignId.HasValue).Select(item => item.TestDesignId!.Value).ToList();

            var parameters = AddIdListParameterValues(suiteLinkIds, "@id");
            var placeholders = string.Join(", ", parameters.Select(parameter => parameter.ParameterName));
            await using (var deleteAssignments = CreateCommand(connection, $"UPDATE test_plan_item_suite_configurations SET deleted_at = SYSUTCDATETIME(), updated_at = SYSUTCDATETIME() WHERE deleted_at IS NULL AND test_plan_item_suite_id IN ({placeholders});"))
            {
                deleteAssignments.Transaction = transaction;
                AddParameters(deleteAssignments, parameters);
                await deleteAssignments.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var deleteUsers = CreateCommand(connection, $"DELETE FROM test_plan_item_suite_users WHERE test_plan_item_suite_id IN ({placeholders});"))
            {
                deleteUsers.Transaction = transaction;
                AddParameters(deleteUsers, parameters);
                await deleteUsers.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var deleteLinks = CreateCommand(connection, $"DELETE FROM test_plan_item_suites WHERE id IN ({placeholders});"))
            {
                deleteLinks.Transaction = transaction;
                AddParameters(deleteLinks, parameters);
                await deleteLinks.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var childDesignId in childDesignIds)
            {
                await DeleteTestSuiteTreeAsync(connection, transaction, childDesignId, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);

            if (context.ClientId.HasValue && removedSuites.Count > 0)
            {
                foreach (var projectGroup in removedSuites
                    .Where(item => item.TestDesignId > 0)
                    .GroupBy(item => item.ProjectId))
                {
                    var suiteIds = projectGroup
                        .Select(item => item.TestDesignId)
                        .Distinct()
                        .ToArray();

                    await QueueAutoSyncTestCaseJobsAsync(connection, context, suiteIds, projectGroup.Key, cancellationToken);
                }
            }

            return true;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<List<(long TestDesignId, long? ProjectId)>> LoadSuiteRemovalTargetsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken)
    {
        var parameters = AddIdListParameterValues(ids, "@id");
        var placeholders = string.Join(", ", parameters.Select(parameter => parameter.ParameterName));

        await using var command = CreateCommand(connection, $"""
            SELECT DISTINCT
                tpis.test_design_id,
                tp.project_id
            FROM test_plan_item_suites tpis
            INNER JOIN test_plan_items tpi ON tpi.id = tpis.test_plan_item_id
            INNER JOIN test_plans tp ON tp.id = tpi.test_plan_id
            WHERE tpis.id IN ({placeholders})
              AND tpis.test_design_id IS NOT NULL;
            """);
        command.Transaction = transaction;
        AddParameters(command, parameters);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<(long TestDesignId, long? ProjectId)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var testDesignId = reader.GetInt64(reader.GetOrdinal("test_design_id"));
            var projectId = GetInt64(reader, "project_id");
            rows.Add((testDesignId, projectId));
        }

        return rows;
    }

    public async Task<bool> SortSuitesForPlanItemAsync(ClaimsPrincipal principal, IReadOnlyList<long> ids, CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0)
        {
            return true;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            for (var index = 0; index < ids.Count; index++)
            {
                await using var command = CreateCommand(connection, "UPDATE test_plan_item_suites SET sort_order = @sortOrder, updated_at = SYSUTCDATETIME() WHERE id = @id;");
                command.Transaction = transaction;
                command.Parameters.AddWithValue("@sortOrder", index + 1);
                command.Parameters.AddWithValue("@id", ids[index]);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<bool> UpdatePlanItemSuiteUsersAsync(ClaimsPrincipal principal, UpdatePlanItemSuiteUsersRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var deleteUsers = CreateCommand(connection, "DELETE FROM test_plan_item_suite_users WHERE test_plan_item_suite_id = @id;"))
            {
                deleteUsers.Transaction = transaction;
                deleteUsers.Parameters.AddWithValue("@id", request.TestPlanItemSuiteId);
                await deleteUsers.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var userId in request.Users.Distinct())
            {
                await using var insertUser = CreateCommand(connection, "INSERT INTO test_plan_item_suite_users (test_plan_item_suite_id, user_id, created_at, updated_at) VALUES (@suiteId, @userId, SYSUTCDATETIME(), SYSUTCDATETIME());");
                insertUser.Transaction = transaction;
                insertUser.Parameters.AddWithValue("@suiteId", request.TestPlanItemSuiteId);
                insertUser.Parameters.AddWithValue("@userId", userId);
                await insertUser.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<bool> ChangeSuiteToNotStartedAsync(ClaimsPrincipal principal, ChangeSuiteToNotStartedRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await EnsurePointBasedConfigurationStateAsync(connection, request.TestPlanItemId, cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await UpdateExecutionStatusAsync(connection, transaction, request.TestPlanItemId, request.TestSuiteId, NotStartedStatusId, cancellationToken);

                        await using (var deleteRunner = CreateCommand(connection, """
                                DELETE tri
                                FROM test_runner_items tri
                                INNER JOIN test_runners tr ON tr.id = tri.test_runner_id
                                WHERE tr.test_plan_item_id = @testPlanItemId
                                    AND COALESCE(tri.execution_id, tri.test_suite_id) = @testSuiteId
                                    AND tri.status_id = 2;
                                """))
            {
                deleteRunner.Transaction = transaction;
                deleteRunner.Parameters.AddWithValue("@testPlanItemId", request.TestPlanItemId);
                deleteRunner.Parameters.AddWithValue("@testSuiteId", request.TestSuiteId);
                await deleteRunner.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task InsertComponentStepsAsync(SqlConnection connection, SqlTransaction transaction, long componentId, IReadOnlyList<SaveComponentStepRequest> steps, CancellationToken cancellationToken)
    {
        foreach (var step in steps)
        {
            var keyword = await ResolveKeywordReferenceAsync(connection, transaction, step.KeywordRef, step.KeywordId, step.KeywordSource, cancellationToken);
            await using var command = CreateCommand(connection, "INSERT INTO component_steps (component_id, description, expected_output, before_step, keyword_id, global_keyword_id, brpg_obj, object_string, xpath, after_step, display_id, created_at, updated_at) VALUES (@componentId, @description, @expectedOutput, @beforeStep, @keywordId, @globalKeywordId, @brpgObj, @objectString, @xpath, @afterStep, @displayId, SYSUTCDATETIME(), SYSUTCDATETIME());");
            command.Transaction = transaction;
            command.Parameters.AddWithValue("@componentId", componentId);
            command.Parameters.AddWithValue("@description", step.Description!.Trim());
            command.Parameters.AddWithValue("@expectedOutput", (object?)NormalizeOptionalText(step.ExpectedOutput) ?? DBNull.Value);
            command.Parameters.AddWithValue("@beforeStep", step.BeforeStep.Count > 0 ? JsonSerializer.Serialize(step.BeforeStep) : DBNull.Value);
            command.Parameters.AddWithValue("@keywordId", (object?)keyword.CustomId ?? DBNull.Value);
            command.Parameters.AddWithValue("@globalKeywordId", (object?)keyword.GlobalId ?? DBNull.Value);
            command.Parameters.AddWithValue("@brpgObj", (object?)NormalizeOptionalText(step.BrpgObj) ?? DBNull.Value);
            command.Parameters.AddWithValue("@objectString", (object?)NormalizeOptionalText(step.ObjectString) ?? DBNull.Value);
            command.Parameters.AddWithValue("@xpath", (object?)NormalizeOptionalText(step.XPath) ?? DBNull.Value);
            command.Parameters.AddWithValue("@afterStep", step.AfterStep.Count > 0 ? JsonSerializer.Serialize(step.AfterStep) : DBNull.Value);
            command.Parameters.AddWithValue("@displayId", (object?)step.DisplayId ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task UpdateComponentStepsAsync(SqlConnection connection, SqlTransaction transaction, long componentId, IReadOnlyList<SaveComponentStepRequest> steps, CancellationToken cancellationToken)
    {
        foreach (var step in steps)
        {
            var keyword = await ResolveKeywordReferenceAsync(connection, transaction, step.KeywordRef, step.KeywordId, step.KeywordSource, cancellationToken);
            await using var command = CreateCommand(connection, "UPDATE component_steps SET description = @description, expected_output = @expectedOutput, before_step = @beforeStep, keyword_id = @keywordId, global_keyword_id = @globalKeywordId, brpg_obj = @brpgObj, object_string = @objectString, xpath = @xpath, after_step = @afterStep, display_id = @displayId, updated_at = SYSUTCDATETIME() WHERE id = @id AND component_id = @componentId; ");
            command.Transaction = transaction;
            command.Parameters.AddWithValue("@id", step.Id!.Value);
            command.Parameters.AddWithValue("@componentId", componentId);
            command.Parameters.AddWithValue("@description", step.Description!.Trim());
            command.Parameters.AddWithValue("@expectedOutput", (object?)NormalizeOptionalText(step.ExpectedOutput) ?? DBNull.Value);
            command.Parameters.AddWithValue("@beforeStep", step.BeforeStep.Count > 0 ? JsonSerializer.Serialize(step.BeforeStep) : DBNull.Value);
            command.Parameters.AddWithValue("@keywordId", (object?)keyword.CustomId ?? DBNull.Value);
            command.Parameters.AddWithValue("@globalKeywordId", (object?)keyword.GlobalId ?? DBNull.Value);
            command.Parameters.AddWithValue("@brpgObj", (object?)NormalizeOptionalText(step.BrpgObj) ?? DBNull.Value);
            command.Parameters.AddWithValue("@objectString", (object?)NormalizeOptionalText(step.ObjectString) ?? DBNull.Value);
            command.Parameters.AddWithValue("@xpath", (object?)NormalizeOptionalText(step.XPath) ?? DBNull.Value);
            command.Parameters.AddWithValue("@afterStep", step.AfterStep.Count > 0 ? JsonSerializer.Serialize(step.AfterStep) : DBNull.Value);
            command.Parameters.AddWithValue("@displayId", (object?)step.DisplayId ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task<(long? CustomId, long? GlobalId)> ResolveKeywordReferenceAsync(SqlConnection connection, SqlTransaction transaction, string? keywordRef, long? keywordId, string? keywordSource, CancellationToken cancellationToken)
    {
        long? customId = null;
        long? globalId = null;

        if (!string.IsNullOrWhiteSpace(keywordRef))
        {
            var parts = keywordRef.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && string.Equals(parts[0], "b", StringComparison.OrdinalIgnoreCase))
            {
                globalId = await EnsureBuiltInKeywordAsync(connection, transaction, parts[1], cancellationToken);
            }
            else if (parts.Length == 2 && long.TryParse(parts[1], out var parsedId))
            {
                if (string.Equals(parts[0], "g", StringComparison.OrdinalIgnoreCase))
                {
                    globalId = parsedId;
                }
                else if (string.Equals(parts[0], "c", StringComparison.OrdinalIgnoreCase))
                {
                    customId = parsedId;
                }
            }
        }
        else if (keywordId.HasValue)
        {
            if (string.Equals(keywordSource, "global", StringComparison.OrdinalIgnoreCase))
            {
                globalId = keywordId.Value;
            }
            else
            {
                customId = keywordId.Value;
            }
        }

        return (customId, globalId);
    }

    private async Task<long?> EnsureBuiltInKeywordAsync(SqlConnection connection, SqlTransaction transaction, string? rawKeywordName, CancellationToken cancellationToken)
    {
        var keywordName = NormalizeOptionalText(rawKeywordName);
        if (string.IsNullOrWhiteSpace(keywordName))
        {
            return null;
        }

        var matchingBuiltIn = BuiltInBrowserKeywords.FirstOrDefault(keyword => string.Equals(keyword.Name, keywordName, StringComparison.OrdinalIgnoreCase));
        if (matchingBuiltIn == default)
        {
            return null;
        }

        await using var selectCommand = CreateCommand(connection, "SELECT TOP 1 id FROM global_keywords WHERE LOWER(name) = LOWER(@name);");
        selectCommand.Transaction = transaction;
        selectCommand.Parameters.AddWithValue("@name", matchingBuiltIn.Name);
        var existingId = await selectCommand.ExecuteScalarAsync(cancellationToken);
        if (existingId is long longId)
        {
            return longId;
        }

        await using var insertCommand = CreateCommand(connection, "INSERT INTO global_keywords (name, created_at, updated_at) OUTPUT INSERTED.id VALUES (@name, SYSUTCDATETIME(), SYSUTCDATETIME());");
        insertCommand.Transaction = transaction;
        insertCommand.Parameters.AddWithValue("@name", matchingBuiltIn.Name);
        var insertedId = await insertCommand.ExecuteScalarAsync(cancellationToken);
        return insertedId is long insertedLongId ? insertedLongId : null;
    }

    private async Task DeleteComponentStepsAsync(SqlConnection connection, SqlTransaction transaction, long componentId, IReadOnlyList<long> deletedSteps, CancellationToken cancellationToken)
    {
        if (deletedSteps.Count == 0)
        {
            return;
        }

        var parameters = AddIdListParameterValues(deletedSteps, "@stepId");
        var placeholders = string.Join(", ", parameters.Select(parameter => parameter.ParameterName));
        await using var command = CreateCommand(connection, $"DELETE FROM component_steps WHERE component_id = @componentId AND id IN ({placeholders});");
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@componentId", componentId);
        AddParameters(command, parameters);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<ComponentDatasetSyncResult> SyncLinkedComponentDatasetsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long clientId,
        IReadOnlyList<long> componentIds,
        CancellationToken cancellationToken)
    {
        var linkedComponents = await LoadLinkedTestComponentsForSyncAsync(connection, transaction, clientId, componentIds, cancellationToken);
        if (linkedComponents.Count == 0)
        {
            return new ComponentDatasetSyncResult();
        }

        var liveStepsByComponent = await LoadComponentStepsForSyncAsync(
            connection,
            transaction,
            linkedComponents.Select(item => item.ComponentId).Distinct().ToArray(),
            cancellationToken);

        var datasets = await LoadDatasetsForSyncAsync(
            connection,
            transaction,
            linkedComponents.Select(item => item.TestComponentId).ToArray(),
            cancellationToken);

        var datasetSteps = await LoadDatasetStepsForSyncAsync(
            connection,
            transaction,
            datasets.Select(item => item.DatasetId).ToArray(),
            cancellationToken);

        var linkedComponentMap = linkedComponents.ToDictionary(item => item.TestComponentId);
        var affectedSuites = new Dictionary<long, long?>();
        foreach (var dataset in datasets)
        {
            if (!linkedComponentMap.TryGetValue(dataset.TestComponentId, out var linkedComponent))
            {
                continue;
            }

            var liveSteps = liveStepsByComponent.TryGetValue(linkedComponent.ComponentId, out var componentSteps)
                ? componentSteps
                : [];
            var existingSteps = datasetSteps.TryGetValue(dataset.DatasetId, out var rows)
                ? rows
                : [];

            var changed = await SyncDatasetStepsAsync(connection, transaction, dataset.DatasetId, liveSteps, existingSteps, cancellationToken);
            if (!changed)
            {
                continue;
            }

            affectedSuites[linkedComponent.TestDesignId] = linkedComponent.ProjectId;
            await TouchDatasetAsync(connection, transaction, dataset.DatasetId, cancellationToken);
        }

        return new ComponentDatasetSyncResult(
            affectedSuites
                .Select(item => new SyncedSuiteRow(item.Key, item.Value))
                .OrderBy(item => item.SuiteId)
                .ToList());
    }

    private async Task<List<LinkedTestComponentSyncRow>> LoadLinkedTestComponentsForSyncAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long clientId,
        IReadOnlyList<long> componentIds,
        CancellationToken cancellationToken)
    {
        var parameters = new List<SqlParameter> { new("@clientId", clientId) };
        var whereClauses = new List<string>
        {
            "td.client_id = @clientId",
            "td.deleted_at IS NULL",
            "tc.deleted_at IS NULL",
            "c.deleted_at IS NULL"
        };

        if (componentIds.Count > 0)
        {
            var componentPlaceholders = AddIdListParameters(parameters, "@componentId", componentIds);
            whereClauses.Add($"tc.component_id IN ({string.Join(", ", componentPlaceholders)})");
        }

        var sql = $"""
            SELECT
                tc.id,
                tc.component_id,
                tc.test_design_id,
                td.project_id
            FROM test_components tc
            INNER JOIN test_designs td ON td.id = tc.test_design_id
            INNER JOIN components c ON c.id = tc.component_id
            WHERE {string.Join(" AND ", whereClauses)}
            ORDER BY tc.id;
            """;

        await using var command = CreateCommand(connection, sql);
        command.Transaction = transaction;
        AddParameters(command, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var rows = new List<LinkedTestComponentSyncRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var componentId = GetInt64(reader, "component_id");
            var testDesignId = GetInt64(reader, "test_design_id");
            if (!componentId.HasValue || !testDesignId.HasValue)
            {
                continue;
            }

            rows.Add(new LinkedTestComponentSyncRow(
                reader.GetInt64(reader.GetOrdinal("id")),
                componentId.Value,
                testDesignId.Value,
                GetInt64(reader, "project_id")));
        }

        return rows;
    }

    private async Task<Dictionary<long, IReadOnlyList<ComponentStepSyncRow>>> LoadComponentStepsForSyncAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlyList<long> componentIds,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, IReadOnlyList<ComponentStepSyncRow>>();
        if (componentIds.Count == 0)
        {
            return result;
        }

        var parameters = AddIdListParameterValues(componentIds, "@componentId");
        var sql = $"""
            SELECT component_id, id, display_id
            FROM component_steps
            WHERE deleted_at IS NULL AND component_id IN ({string.Join(", ", parameters.Select(parameter => parameter.ParameterName))})
            ORDER BY component_id, ISNULL(display_id, 2147483647), id;
            """;

        await using var command = CreateCommand(connection, sql);
        command.Transaction = transaction;
        AddParameters(command, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var grouped = new Dictionary<long, List<ComponentStepSyncRow>>();
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

            steps.Add(new ComponentStepSyncRow(
                reader.GetInt64(reader.GetOrdinal("id")),
                GetInt32(reader, "display_id")));
        }

        foreach (var pair in grouped)
        {
            result[pair.Key] = pair.Value;
        }

        return result;
    }

    private async Task<List<DatasetSyncRow>> LoadDatasetsForSyncAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlyList<long> testComponentIds,
        CancellationToken cancellationToken)
    {
        if (testComponentIds.Count == 0)
        {
            return [];
        }

        var parameters = AddIdListParameterValues(testComponentIds, "@testComponentId");
        var sql = $"""
            SELECT id, test_component_id
            FROM data_sets
            WHERE deleted_at IS NULL AND test_component_id IN ({string.Join(", ", parameters.Select(parameter => parameter.ParameterName))})
            ORDER BY id;
            """;

        await using var command = CreateCommand(connection, sql);
        command.Transaction = transaction;
        AddParameters(command, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var rows = new List<DatasetSyncRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var testComponentId = GetInt64(reader, "test_component_id");
            if (!testComponentId.HasValue)
            {
                continue;
            }

            rows.Add(new DatasetSyncRow(
                reader.GetInt64(reader.GetOrdinal("id")),
                testComponentId.Value));
        }

        return rows;
    }

    private async Task<Dictionary<long, IReadOnlyList<DatasetStepSyncRow>>> LoadDatasetStepsForSyncAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        IReadOnlyList<long> datasetIds,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, IReadOnlyList<DatasetStepSyncRow>>();
        if (datasetIds.Count == 0)
        {
            return result;
        }

        var parameters = AddIdListParameterValues(datasetIds, "@datasetId");
        var sql = $"""
            SELECT id, dataset_id, step_id, display, skip_step, step_info, [override], override_value
            FROM data_set_steps
            WHERE dataset_id IN ({string.Join(", ", parameters.Select(parameter => parameter.ParameterName))})
            ORDER BY dataset_id, ISNULL(display, 2147483647), id;
            """;

        await using var command = CreateCommand(connection, sql);
        command.Transaction = transaction;
        AddParameters(command, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var grouped = new Dictionary<long, List<DatasetStepSyncRow>>();
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

            var stepInfo = ParseJsonElement(GetString(reader, "step_info"));
            steps.Add(new DatasetStepSyncRow(
                reader.GetInt64(reader.GetOrdinal("id")),
                datasetId.Value,
                GetInt64(reader, "step_id"),
                GetInt32(reader, "display"),
                GetBoolean(reader, "skip_step") ?? false,
                GetJsonStringProperty(stepInfo, "value"),
                GetBoolean(reader, "override") ?? false,
                GetString(reader, "override_value")));
        }

        foreach (var pair in grouped)
        {
            result[pair.Key] = pair.Value;
        }

        return result;
    }

    private async Task<bool> SyncDatasetStepsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long datasetId,
        IReadOnlyList<ComponentStepSyncRow> liveSteps,
        IReadOnlyList<DatasetStepSyncRow> existingSteps,
        CancellationToken cancellationToken)
    {
        var changed = false;
        var duplicatesToDelete = new List<long>();
        var existingByStepId = new Dictionary<long, DatasetStepSyncRow>();
        foreach (var existingStep in existingSteps)
        {
            if (existingStep.StepId.HasValue && !existingByStepId.ContainsKey(existingStep.StepId.Value))
            {
                existingByStepId[existingStep.StepId.Value] = existingStep;
                continue;
            }

            duplicatesToDelete.Add(existingStep.Id);
        }

        var liveStepIds = liveSteps.Select(step => step.StepId).ToHashSet();
        foreach (var stepId in existingByStepId.Keys.Where(stepId => !liveStepIds.Contains(stepId)).ToArray())
        {
            duplicatesToDelete.Add(existingByStepId[stepId].Id);
        }

        foreach (var rowId in duplicatesToDelete.Distinct())
        {
            await DeleteDatasetStepAsync(connection, transaction, rowId, cancellationToken);
            changed = true;
        }

        foreach (var liveStep in liveSteps)
        {
            if (existingByStepId.TryGetValue(liveStep.StepId, out var existingStep))
            {
                var normalizedValue = NormalizeDatasetStepValue(existingStep.Value, existingStep.SkipStep);
                var shouldSkip = string.Equals(normalizedValue, SkipStepValue, StringComparison.OrdinalIgnoreCase);
                if (existingStep.DisplayId != liveStep.DisplayId || existingStep.SkipStep != shouldSkip)
                {
                    await UpdateDatasetStepAsync(
                        connection,
                        transaction,
                        existingStep.Id,
                        liveStep.StepId,
                        liveStep.DisplayId,
                        normalizedValue,
                        shouldSkip,
                        existingStep.Override,
                        existingStep.OverrideValue,
                        cancellationToken);
                    changed = true;
                }

                continue;
            }

            await InsertDatasetStepAsync(
                connection,
                transaction,
                datasetId,
                liveStep.StepId,
                liveStep.DisplayId,
                SkipStepValue,
                skipStep: true,
                hasOverride: false,
                overrideValue: null,
                cancellationToken);
            changed = true;
        }

        return changed;
    }

    private async Task InsertDatasetStepAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long datasetId,
        long stepId,
        int? displayId,
        string value,
        bool skipStep,
        bool hasOverride,
        string? overrideValue,
        CancellationToken cancellationToken)
    {
        var stepInfo = BuildDatasetStepInfo(stepId, displayId, value, hasOverride, overrideValue);
        const string sql = """
            INSERT INTO data_set_steps (dataset_id, step_id, display, skip_step, step_info, [override], override_value, created_at, updated_at)
            VALUES (@datasetId, @stepId, @display, @skipStep, @stepInfo, @override, @overrideValue, SYSUTCDATETIME(), SYSUTCDATETIME());
            """;

        await using var command = CreateCommand(connection, sql);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@datasetId", datasetId);
        command.Parameters.AddWithValue("@stepId", stepId);
        command.Parameters.AddWithValue("@display", (object?)displayId ?? DBNull.Value);
        command.Parameters.AddWithValue("@skipStep", skipStep);
        command.Parameters.AddWithValue("@stepInfo", stepInfo.ToJsonString());
        command.Parameters.AddWithValue("@override", hasOverride);
        command.Parameters.AddWithValue("@overrideValue", hasOverride && overrideValue is not null ? overrideValue : DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task UpdateDatasetStepAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long rowId,
        long stepId,
        int? displayId,
        string value,
        bool skipStep,
        bool hasOverride,
        string? overrideValue,
        CancellationToken cancellationToken)
    {
        var stepInfo = BuildDatasetStepInfo(stepId, displayId, value, hasOverride, overrideValue);
        const string sql = """
            UPDATE data_set_steps
            SET step_id = @stepId,
                display = @display,
                skip_step = @skipStep,
                step_info = @stepInfo,
                [override] = @override,
                override_value = @overrideValue,
                updated_at = SYSUTCDATETIME()
            WHERE id = @id;
            """;

        await using var command = CreateCommand(connection, sql);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@id", rowId);
        command.Parameters.AddWithValue("@stepId", stepId);
        command.Parameters.AddWithValue("@display", (object?)displayId ?? DBNull.Value);
        command.Parameters.AddWithValue("@skipStep", skipStep);
        command.Parameters.AddWithValue("@stepInfo", stepInfo.ToJsonString());
        command.Parameters.AddWithValue("@override", hasOverride);
        command.Parameters.AddWithValue("@overrideValue", hasOverride && overrideValue is not null ? overrideValue : DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task DeleteDatasetStepAsync(SqlConnection connection, SqlTransaction transaction, long rowId, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, "DELETE FROM data_set_steps WHERE id = @id;");
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@id", rowId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task TouchDatasetAsync(SqlConnection connection, SqlTransaction transaction, long datasetId, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, "UPDATE data_sets SET updated_at = SYSUTCDATETIME() WHERE id = @id;");
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@id", datasetId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task TouchSyncedSuitesAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        long clientId,
        IReadOnlyList<SyncedSuiteRow> suites,
        string updatedBy,
        CancellationToken cancellationToken)
    {
        if (suites.Count == 0)
        {
            return;
        }

        var parameters = AddIdListParameterValues(suites.Select(item => item.SuiteId).Distinct().ToArray(), "@suiteId");
        var sql = $"""
            UPDATE test_designs
            SET updated_by = @updatedBy,
                updated_at = SYSUTCDATETIME()
            WHERE client_id = @clientId AND id IN ({string.Join(", ", parameters.Select(parameter => parameter.ParameterName))});
            """;

        await using var command = CreateCommand(connection, sql);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@updatedBy", updatedBy);
        command.Parameters.AddWithValue("@clientId", clientId);
        AddParameters(command, parameters);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string NormalizeDatasetStepValue(string? value, bool skipStep)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return skipStep ? SkipStepValue : string.Empty;
        }

        return value;
    }

    private static JsonObject BuildDatasetStepInfo(long stepId, int? displayId, string value, bool hasOverride, string? overrideValue)
    {
        var stepInfo = new JsonObject
        {
            ["display_id"] = displayId,
            ["id"] = stepId,
            ["value"] = value
        };

        if (hasOverride)
        {
            stepInfo["override"] = true;
            stepInfo["override_value"] = overrideValue;
        }

        return stepInfo;
    }

    private sealed record ComponentDatasetSyncResult(IReadOnlyList<SyncedSuiteRow> AffectedSuites)
    {
        public ComponentDatasetSyncResult() : this([])
        {
        }
    }

    private sealed record SyncedSuiteRow(long SuiteId, long? ProjectId);

    private sealed record LinkedTestComponentSyncRow(long TestComponentId, long ComponentId, long TestDesignId, long? ProjectId);

    private sealed record ComponentStepSyncRow(long StepId, int? DisplayId);

    private sealed record DatasetSyncRow(long DatasetId, long TestComponentId);

    private sealed record DatasetStepSyncRow(
        long Id,
        long DatasetId,
        long? StepId,
        int? DisplayId,
        bool SkipStep,
        string? Value,
        bool Override,
        string? OverrideValue);

    private async Task<bool> ComponentHasLiveAssociationsAsync(SqlConnection connection, long componentId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT(*)
            FROM test_components tc
            INNER JOIN test_designs td ON td.id = tc.test_design_id
            WHERE tc.component_id = @componentId
              AND tc.deleted_at IS NULL
              AND td.deleted_at IS NULL;
            """;

        return await ExecuteCountAsync(connection, sql, [new SqlParameter("@componentId", componentId)], cancellationToken) > 0;
    }

    private async Task<bool> ProjectHasComponentsAsync(SqlConnection connection, long clientId, long projectId, CancellationToken cancellationToken)
    {
        const string sql = "SELECT COUNT(*) FROM components WHERE project_id = @projectId AND client_id = @clientId AND deleted_at IS NULL;";
        return await ExecuteCountAsync(connection, sql, [new SqlParameter("@projectId", projectId), new SqlParameter("@clientId", clientId)], cancellationToken) > 0;
    }

    private async Task ReplaceConfigurationSelectionsAsync(SqlConnection connection, SqlTransaction transaction, long configurationId, IReadOnlyList<SaveConfigurationSelectionRequest> selections, CancellationToken cancellationToken)
    {
        await using (var deleteCommand = CreateCommand(connection, "DELETE FROM configurations_selected_variables WHERE configuration_id = @configurationId;"))
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.Parameters.AddWithValue("@configurationId", configurationId);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var selection in selections)
        {
            await using var insertCommand = CreateCommand(connection, "INSERT INTO configurations_selected_variables (configuration_id, variable_id, variable_value_id, created_at, updated_at) VALUES (@configurationId, @variableId, @variableValueId, SYSUTCDATETIME(), SYSUTCDATETIME());");
            insertCommand.Transaction = transaction;
            insertCommand.Parameters.AddWithValue("@configurationId", configurationId);
            insertCommand.Parameters.AddWithValue("@variableId", selection.VariableId);
            insertCommand.Parameters.AddWithValue("@variableValueId", selection.VariableValueId);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task<ConfigurationDto?> GetConfigurationByIdAsync(SqlConnection connection, long clientId, long id, CancellationToken cancellationToken)
    {
        const string sql = "SELECT TOP 1 id, name, description, status, created_at FROM configurations WHERE id = @id AND client_id = @clientId;";
        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@clientId", clientId);
        ConfigurationDto? dto = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            dto = new ConfigurationDto
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                Name = GetString(reader, "name"),
                Description = GetString(reader, "description"),
                Status = GetInt32(reader, "status"),
                CreatedAt = GetDateTimeOffset(reader, "created_at")
            };
        }

        if (dto is null)
        {
            return null;
        }

        var selectionMap = await LoadConfigurationSelectionsAsync(connection, [id], cancellationToken);
        return new ConfigurationDto
        {
            Id = dto.Id,
            Name = dto.Name,
            Description = dto.Description,
            Status = dto.Status,
            CreatedAt = dto.CreatedAt,
            ConfigurationVariables = selectionMap.TryGetValue(id, out var values) ? values : []
        };
    }

    private async Task ReplaceTestPlanUsersAsync(SqlConnection connection, SqlTransaction transaction, long testPlanId, IReadOnlyList<long> userIds, CancellationToken cancellationToken)
    {
        await using (var deleteCommand = CreateCommand(connection, "DELETE FROM test_plan_users WHERE test_plan_id = @testPlanId;"))
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.Parameters.AddWithValue("@testPlanId", testPlanId);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var userId in userIds.Distinct())
        {
            await using var insertCommand = CreateCommand(connection, "INSERT INTO test_plan_users (test_plan_id, user_id, created_at, updated_at) VALUES (@testPlanId, @userId, SYSUTCDATETIME(), SYSUTCDATETIME());");
            insertCommand.Transaction = transaction;
            insertCommand.Parameters.AddWithValue("@testPlanId", testPlanId);
            insertCommand.Parameters.AddWithValue("@userId", userId);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task<TestPlanItemDto?> GetTestPlanItemByIdAsync(SqlConnection connection, long clientId, long id, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT tpi.id, tpi.name, tpi.test_plan_id
            FROM test_plan_items tpi
            INNER JOIN test_plans tp ON tp.id = tpi.test_plan_id
            WHERE tpi.id = @id AND tp.client_id = @clientId;
            """;

        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@clientId", clientId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new TestPlanItemDto
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            Name = GetString(reader, "name"),
            TestPlanId = GetInt64(reader, "test_plan_id")
        };
    }

    private async Task CopyPlanUsersToSuiteAsync(SqlConnection connection, SqlTransaction transaction, long testPlanItemId, long suiteLinkId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT tpu.user_id
            FROM test_plan_items tpi
            INNER JOIN test_plan_users tpu ON tpu.test_plan_id = tpi.test_plan_id
            WHERE tpi.id = @testPlanItemId;
            """;

        var userIds = new List<long>();
        await using (var command = CreateCommand(connection, sql))
        {
            command.Transaction = transaction;
            command.Parameters.AddWithValue("@testPlanItemId", testPlanItemId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                userIds.Add(reader.GetInt64(reader.GetOrdinal("user_id")));
            }
        }

        foreach (var userId in userIds.Distinct())
        {
            await using var insertCommand = CreateCommand(connection, "INSERT INTO test_plan_item_suite_users (test_plan_item_suite_id, user_id, created_at, updated_at) VALUES (@suiteLinkId, @userId, SYSUTCDATETIME(), SYSUTCDATETIME());");
            insertCommand.Transaction = transaction;
            insertCommand.Parameters.AddWithValue("@suiteLinkId", suiteLinkId);
            insertCommand.Parameters.AddWithValue("@userId", userId);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task<Dictionary<long, (long SuiteLinkId, long TestDesignId)>> LoadChildConfigurationSuiteMapAsync(SqlConnection connection, long parentSuiteLinkId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT tpis.id, tpis.test_design_id, td.configuration_id
            FROM test_plan_item_suites tpis
            INNER JOIN test_designs td ON td.id = tpis.test_design_id
            WHERE tpis.parent_id = @parentId AND tpis.deleted_at IS NULL AND td.deleted_at IS NULL AND td.configuration_id IS NOT NULL;
            """;

        var result = new Dictionary<long, (long SuiteLinkId, long TestDesignId)>();
        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@parentId", parentSuiteLinkId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result[reader.GetInt64(reader.GetOrdinal("configuration_id"))] = (
                reader.GetInt64(reader.GetOrdinal("id")),
                reader.GetInt64(reader.GetOrdinal("test_design_id")));
        }

        return result;
    }

    private static SaveTestSuiteRequest BuildCloneRequest(TestSuiteFullDto suite, long? configurationId, string? titleOverride = null, long? testStateIdOverride = null)
    {
        return new SaveTestSuiteRequest
        {
            Details = new SaveTestSuiteDetailsRequest
            {
                Title = titleOverride ?? suite.Title,
                TestStateId = testStateIdOverride ?? suite.TestStateId,
                TestSuiteType = suite.TestSuiteType,
                FolderPathId = suite.FolderPathId,
                Comment = suite.Comment,
                ProjectId = suite.ProjectId,
                IterationPath = suite.IterationPath,
                Priority = suite.Priority,
                StoryId = suite.StoryId,
                TestTitle = titleOverride ?? suite.TestTitle,
                Tags = JsonSerializer.SerializeToElement(ParseTags(suite.Tags)),
                KbaReady = suite.KbaReady,
                TrainingReady = suite.TrainingReady,
                ReleaseNotesReady = suite.ReleaseNotesReady,
                ConfigurationId = configurationId
            },
            DesignedComponents = suite.Components.Select(component => new SaveTestSuiteComponentRequest
            {
                ComponentId = component.ComponentId,
                ProjectId = component.ProjectId,
                Status = component.Status,
                Datasets = component.Datasets.Select(dataset => new SaveTestSuiteDatasetRequest
                {
                    DatasetId = dataset.Id,
                    Scenario = dataset.Scenario,
                    Status = dataset.Status,
                    Steps = dataset.Steps.Select(step => new SaveTestSuiteStepRequest
                    {
                        DisplayId = step.DisplayId,
                        Id = step.StepId ?? step.InternalStepId,
                        Value = step.Value,
                        Override = step.Override,
                        OverrideValue = step.OverrideValue
                    }).ToList()
                }).ToList()
            }).ToList(),
            Datasets = suite.Datasets.Select(dataset => new SaveTestSuiteDatasetRequest
            {
                DatasetId = dataset.Id,
                SortOrder = dataset.SortOrder,
                Scenario = dataset.Scenario,
                Status = dataset.Status,
                Steps = dataset.Steps.Select(step => new SaveTestSuiteStepRequest
                {
                    DisplayId = step.DisplayId,
                    Id = step.StepId ?? step.InternalStepId,
                    Value = step.Value,
                    Override = step.Override,
                    OverrideValue = step.OverrideValue
                }).ToList()
            }).ToList()
        };
    }

    private static IReadOnlyList<string> ParseTags(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags))
        {
            return [];
        }

        var trimmed = tags.Trim();
        if (trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<string[]>(trimmed);
                if (parsed is { Length: > 0 })
                {
                    return parsed
                        .Select(tag => tag?.Trim())
                        .Where(tag => !string.IsNullOrWhiteSpace(tag))
                        .Cast<string>()
                        .ToArray();
                }
            }
            catch
            {
            }
        }

        return tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private async Task DeleteTestSuiteTreeAsync(SqlConnection connection, SqlTransaction transaction, long testDesignId, CancellationToken cancellationToken)
    {
        foreach (var sql in new[]
        {
            "DELETE FROM data_set_steps WHERE dataset_id IN (SELECT ds.id FROM data_sets ds INNER JOIN test_components tc ON tc.id = ds.test_component_id WHERE tc.test_design_id = @testDesignId);",
            "DELETE FROM data_sets WHERE test_component_id IN (SELECT id FROM test_components WHERE test_design_id = @testDesignId);",
            "DELETE FROM test_components WHERE test_design_id = @testDesignId;",
            "DELETE FROM test_designs WHERE id = @testDesignId;"
        })
        {
            await using var command = CreateCommand(connection, sql);
            command.Transaction = transaction;
            command.Parameters.AddWithValue("@testDesignId", testDesignId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task<IReadOnlyList<(long Id, long? ParentId, long? TestDesignId)>> LoadSuiteLinksAsync(SqlConnection connection, SqlTransaction transaction, IReadOnlyList<long> ids, CancellationToken cancellationToken)
    {
        var parameters = AddIdListParameterValues(ids, "@id");
        var placeholders = string.Join(", ", parameters.Select(parameter => parameter.ParameterName));
        await using var command = CreateCommand(connection, $"SELECT id, parent_id, test_design_id FROM test_plan_item_suites WHERE id IN ({placeholders});");
        command.Transaction = transaction;
        AddParameters(command, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<(long Id, long? ParentId, long? TestDesignId)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add((reader.GetInt64(reader.GetOrdinal("id")), GetInt64(reader, "parent_id"), GetInt64(reader, "test_design_id")));
        }

        return rows;
    }

    private static string EscapeCsv(string? value)
    {
        var text = value ?? string.Empty;
        if (text.Contains(',') || text.Contains('"') || text.Contains('\n') || text.Contains('\r'))
        {
            return $"\"{text.Replace("\"", "\"\"")}\"";
        }

        return text;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var values = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        for (var index = 0; index < line.Length; index++)
        {
            var ch = line[index];
            if (ch == '"')
            {
                if (inQuotes && index + 1 < line.Length && line[index + 1] == '"')
                {
                    current.Append('"');
                    index++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (ch == ',' && !inQuotes)
            {
                values.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        values.Add(current.ToString());
        return values;
    }

    private static IReadOnlyList<string> ParsePipeList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static byte[] BuildTwoSheetWorkbook(IReadOnlyList<RequirementsSheetRow> requirementsRows, IReadOnlyList<TestDataSheetRow> testDataRows, int maxStepColumns)
    {
        var requirementSheetRows = new List<IReadOnlyList<string>>
        {
            new List<string> { "TCID", "Ser", "Requirement", "RunFlag", "Status" }
        };
        requirementSheetRows.AddRange(requirementsRows.Select(row => (IReadOnlyList<string>)new List<string>
        {
            row.TCID,
            row.Ser,
            row.Requirement,
            row.RunFlag,
            row.Status
        }));

        var header = new List<string>
        {
            "TDID",
            "TSID",
            "E",
            "Test_Case_Description",
            "Expected",
            "Actual",
            "Result"
        };

        var stepColumnCount = Math.Max(1, maxStepColumns);
        for (var index = 0; index < stepColumnCount; index++)
        {
            header.Add($"IP{7 + index}");
        }

        var testDataSheetRows = new List<IReadOnlyList<string>> { header };
        foreach (var row in testDataRows)
        {
            var output = new List<string>
            {
                row.TDID,
                row.TSID,
                row.E,
                row.TestCaseDescription,
                row.Expected,
                row.Actual,
                row.Result
            };

            for (var index = 0; index < stepColumnCount; index++)
            {
                output.Add(index < row.StepValues.Count ? row.StepValues[index] : string.Empty);
            }

            testDataSheetRows.Add(output);
        }

        return BuildWorkbookBytes(
            new WorksheetExport("Requirements", requirementSheetRows),
            new WorksheetExport("TestData", testDataSheetRows));
    }

    private static string BuildExportStepCellValue(ComponentStepDto step, TestSuiteFullDatasetStepDto? datasetStep)
    {
        var stepData = datasetStep?.Value ?? SkipStepValue;
        var hasOverride = (datasetStep?.Override ?? false) || !string.IsNullOrWhiteSpace(datasetStep?.OverrideValue);
        if (!hasOverride)
        {
            return stepData;
        }

        var overrideMap = ParseOverrideMapForExport(datasetStep?.OverrideValue);

        var stepDesc = ResolveOverrideExportValue(overrideMap, "description")
            ?? NormalizeOptionalText(step.Description)
            ?? string.Empty;
        var keyword = ResolveOverrideExportValue(overrideMap, "keyword")
            ?? NormalizeOptionalText(step.Keyword?.Name)
            ?? NormalizeOptionalText(step.GlobalKeyword?.Name)
            ?? string.Empty;
        var locator = ResolveOverrideExportValue(overrideMap, "xpath")
            ?? NormalizeOptionalText(step.XPath)
            ?? string.Empty;
        var beforeStep = ResolveOverrideExportValue(overrideMap, "before_step")
            ?? JoinStepArray(step.BeforeStep);
        var afterStep = ResolveOverrideExportValue(overrideMap, "after_step")
            ?? JoinStepArray(step.AfterStep);

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(stepDesc))
        {
            parts.Add($"stepdesc:={stepDesc}");
        }

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            parts.Add($"keyword:={keyword}");
        }

        if (!string.IsNullOrWhiteSpace(locator))
        {
            parts.Add($"locator:={locator}");
        }

        if (!string.IsNullOrWhiteSpace(beforeStep))
        {
            parts.Add($"beforestep:={beforeStep}");
        }

        if (!string.IsNullOrWhiteSpace(afterStep))
        {
            parts.Add($"afterstep:={afterStep}");
        }

        parts.Add($"stepdata:={stepData}");

        return string.Join(";;", parts);
    }

    private static string? JoinStepArray(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
        {
            return null;
        }

        var cleaned = values
            .Select(NormalizeOptionalText)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .ToArray();

        return cleaned.Length == 0 ? null : string.Join("||", cleaned);
    }

    private static Dictionary<string, string> ParseOverrideMapForExport(string? raw)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return map;
        }

        var segments = raw.Contains(";;", StringComparison.Ordinal)
            ? raw.Split(";;", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : raw.Contains("<=>", StringComparison.Ordinal)
                ? raw.Split("<=>", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : raw.Split("||", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var segment in segments)
        {
            var splitIndex = segment.IndexOf(":=", StringComparison.Ordinal);
            var separatorLength = 2;
            if (splitIndex < 0)
            {
                splitIndex = segment.IndexOf('=');
                separatorLength = 1;
            }

            if (splitIndex <= 0)
            {
                continue;
            }

            var key = NormalizeOverrideKeyForExport(segment[..splitIndex]);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            map[key] = segment[(splitIndex + separatorLength)..].Trim();
        }

        return map;
    }

    private static string? ResolveOverrideExportValue(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static string NormalizeOverrideKeyForExport(string key)
    {
        return key.Trim().ToLowerInvariant() switch
        {
            "stepdesc" => "description",
            "description" => "description",
            "keyword" => "keyword",
            "locator" => "xpath",
            "xpath" => "xpath",
            "beforestep" => "before_step",
            "before_step" => "before_step",
            "afterstep" => "after_step",
            "after_step" => "after_step",
            "stepdata" => "value",
            "value" => "value",
            "action" => "value",
            _ => string.Empty
        };
    }

    private static byte[] BuildWorkbookBytes(params WorksheetExport[] sheets)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddZipEntry(archive, "[Content_Types].xml", BuildContentTypesXml(sheets.Length));
            AddZipEntry(archive, "_rels/.rels", RootRelationshipsXml);
            AddZipEntry(archive, "xl/workbook.xml", BuildWorkbookXml(sheets));
            AddZipEntry(archive, "xl/_rels/workbook.xml.rels", BuildWorkbookRelationshipsXml(sheets.Length));
            AddZipEntry(archive, "xl/styles.xml", StylesXml);

            for (var index = 0; index < sheets.Length; index++)
            {
                AddZipEntry(archive, $"xl/worksheets/sheet{index + 1}.xml", BuildWorksheetXml(sheets[index].Rows));
            }
        }

        return stream.ToArray();
    }

    private static void AddZipEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
        using var entryStream = entry.Open();
        using var writer = new StreamWriter(entryStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static string BuildWorkbookXml(IReadOnlyList<WorksheetExport> sheets)
    {
        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        builder.Append("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
        for (var index = 0; index < sheets.Count; index++)
        {
            var escapedName = EscapeXml(sheets[index].Name);
            builder.Append($"<sheet name=\"{escapedName}\" sheetId=\"{index + 1}\" r:id=\"rId{index + 1}\"/>");
        }

        builder.Append("</sheets></workbook>");
        return builder.ToString();
    }

    private static string BuildWorkbookRelationshipsXml(int sheetCount)
    {
        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        builder.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
        for (var index = 0; index < sheetCount; index++)
        {
            builder.Append($"<Relationship Id=\"rId{index + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{index + 1}.xml\"/>");
        }

        builder.Append($"<Relationship Id=\"rId{sheetCount + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
        builder.Append("</Relationships>");
        return builder.ToString();
    }

    private static string BuildContentTypesXml(int sheetCount)
    {
        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        builder.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
        builder.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
        builder.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
        builder.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
        builder.Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
        for (var index = 0; index < sheetCount; index++)
        {
            builder.Append($"<Override PartName=\"/xl/worksheets/sheet{index + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
        }

        builder.Append("</Types>");
        return builder.ToString();
    }

    private static string BuildWorksheetXml(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        builder.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");

        for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            var rowNumber = rowIndex + 1;
            builder.Append($"<row r=\"{rowNumber}\">");
            var row = rows[rowIndex];
            for (var columnIndex = 0; columnIndex < row.Count; columnIndex++)
            {
                var cellRef = GetExcelColumnName(columnIndex + 1) + rowNumber.ToString(CultureInfo.InvariantCulture);
                var value = EscapeXml(row[columnIndex] ?? string.Empty);
                builder.Append($"<c r=\"{cellRef}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{value}</t></is></c>");
            }

            builder.Append("</row>");
        }

        builder.Append("</sheetData></worksheet>");
        return builder.ToString();
    }

    private static string GetExcelColumnName(int columnNumber)
    {
        var number = columnNumber;
        var chars = new Stack<char>();
        while (number > 0)
        {
            var remainder = (number - 1) % 26;
            chars.Push((char)('A' + remainder));
            number = (number - 1) / 26;
        }

        return new string(chars.ToArray());
    }

    private static string EscapeXml(string? value)
    {
        return (value ?? string.Empty)
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);
    }

    private const string RootRelationshipsXml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>";

    private const string StylesXml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><fonts count=\"1\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts><fills count=\"1\"><fill><patternFill patternType=\"none\"/></fill></fills><borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/></cellXfs></styleSheet>";

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);
        return memory.ToArray();
    }

    private static bool IsReferenceRow(TestDataSheetImportRow row)
    {
        return string.Equals(row.E, "N", StringComparison.OrdinalIgnoreCase)
            && string.Equals(NormalizeOptionalText(row.TestCaseDescription), "FN", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<ImportTestSuitesResultDto> ImportTwoSheetWorkbookAsync(ClaimsPrincipal principal, TwoSheetWorkbookImport workbook, CancellationToken cancellationToken)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return new ImportTestSuitesResultDto();
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var defaultStateId = await LoadDefaultTestStateIdAsync(connection, cancellationToken);
        var components = await LoadComponentCatalogAsync(connection, context.ClientId.Value, cancellationToken);
        var suitesPayload = await GetTestSuitesAsync(principal, null, null, null, null, null, null, 1, 0, true, cancellationToken);
        var suites = suitesPayload as IReadOnlyList<TestSuiteListDto> ?? [];

        var suiteById = suites.ToDictionary(item => item.Id, item => item);
        var suiteByTitle = suites
            .Where(item => !string.IsNullOrWhiteSpace(item.Title))
            .ToDictionary(item => item.Title!.Trim(), item => item.Id, StringComparer.OrdinalIgnoreCase);

        var createdTests = 0;
        var updatedTests = 0;
        var createdDatasets = 0;

        foreach (var requirement in workbook.Requirements)
        {
            var title = NormalizeOptionalText(requirement.Requirement);
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var suiteRows = workbook.TestData
                .Where(item => string.Equals(item.TDID, requirement.TCID, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var componentsForSuite = new List<SaveTestSuiteComponentRequest>();
            foreach (var componentGroup in suiteRows
                .Where(item => !string.IsNullOrWhiteSpace(item.TSID))
                .GroupBy(item => item.TSID, StringComparer.OrdinalIgnoreCase))
            {
                var catalog = ResolveComponentCatalog(components, componentGroup.Key);
                if (catalog is null)
                {
                    throw new InvalidOperationException($"Unable to resolve component for TSID '{componentGroup.Key}' in '{requirement.TCID}'.");
                }

                var orderedSteps = catalog.Steps
                    .OrderBy(step => step.DisplayId ?? int.MaxValue)
                    .ThenBy(step => step.StepId)
                    .ToList();

                var referenceRow = componentGroup.FirstOrDefault(IsReferenceRow);
                var referenceLabels = referenceRow?.StepValues ?? [];
                var datasetRows = componentGroup.Where(row => !IsReferenceRow(row)).ToList();

                var datasets = new List<SaveTestSuiteDatasetRequest>();
                foreach (var datasetRow in datasetRows)
                {
                    var rowStepCount = Math.Max(Math.Max(referenceLabels.Count, datasetRow.StepValues.Count), orderedSteps.Count);
                    var mappedSteps = new Dictionary<long, SaveTestSuiteStepRequest>();

                    for (var index = 0; index < rowStepCount; index++)
                    {
                        var cellValue = index < datasetRow.StepValues.Count ? datasetRow.StepValues[index] : string.Empty;
                        var refLabel = index < referenceLabels.Count ? referenceLabels[index] : string.Empty;
                        var payload = ParseCompositeStepPayload(cellValue);

                        var step = ResolveStepCatalog(orderedSteps, refLabel, payload?.StepDesc, index);
                        if (step is null)
                        {
                            continue;
                        }

                        var value = payload is not null
                            ? payload.StepData
                            : cellValue;
                        value = string.IsNullOrWhiteSpace(value) ? SkipStepValue : value;

                        mappedSteps[step.StepId] = new SaveTestSuiteStepRequest
                        {
                            DisplayId = step.DisplayId ?? (index + 1),
                            Id = step.StepId,
                            Value = value,
                            Override = payload is not null,
                            OverrideValue = payload?.CanonicalOverrideValue
                        };
                    }

                    foreach (var step in orderedSteps)
                    {
                        if (!mappedSteps.ContainsKey(step.StepId))
                        {
                            mappedSteps[step.StepId] = new SaveTestSuiteStepRequest
                            {
                                DisplayId = step.DisplayId,
                                Id = step.StepId,
                                Value = SkipStepValue,
                                Override = false,
                                OverrideValue = null
                            };
                        }
                    }

                    datasets.Add(new SaveTestSuiteDatasetRequest
                    {
                        Scenario = NormalizeOptionalText(datasetRow.TestCaseDescription) ?? "Dataset",
                        Status = string.Equals(datasetRow.E, "Y", StringComparison.OrdinalIgnoreCase),
                        Steps = mappedSteps.Values
                            .OrderBy(step => step.DisplayId ?? int.MaxValue)
                            .ThenBy(step => step.Id ?? long.MaxValue)
                            .ToList()
                    });

                    createdDatasets++;
                }

                if (datasets.Count > 0)
                {
                    componentsForSuite.Add(new SaveTestSuiteComponentRequest
                    {
                        ComponentId = catalog.ComponentId,
                        ProjectId = catalog.ProjectId,
                        Status = true,
                        Datasets = datasets
                    });
                }
            }

            var suiteProjectId = componentsForSuite.FirstOrDefault()?.ProjectId;
            var request = new SaveTestSuiteRequest
            {
                Details = new SaveTestSuiteDetailsRequest
                {
                    Title = title,
                    TestStateId = defaultStateId,
                    TestSuiteType = 1,
                    ProjectId = suiteProjectId,
                    Tags = null,
                    Comment = null,
                    KbaReady = false,
                    TrainingReady = false,
                    ReleaseNotesReady = false
                },
                DesignedComponents = componentsForSuite
            };

            long? targetSuiteId = null;
            if (TryParseTcidAsSuiteId(requirement.TCID, out var parsedSuiteId) && suiteById.ContainsKey(parsedSuiteId))
            {
                targetSuiteId = parsedSuiteId;
            }
            else if (suiteByTitle.TryGetValue(title, out var suiteIdByTitle))
            {
                targetSuiteId = suiteIdByTitle;
            }

            if (targetSuiteId.HasValue)
            {
                var updated = await UpdateTestSuiteAsync(principal, targetSuiteId.Value, request, cancellationToken);
                if (updated.Outcome == SaveTestSuiteOutcome.Saved)
                {
                    updatedTests++;
                    continue;
                }

                if (updated.Outcome != SaveTestSuiteOutcome.NotFound)
                {
                    throw new InvalidOperationException(updated.ErrorMessage ?? "Unable to import test suite row due to invalid references.");
                }
            }

            var created = await CreateTestSuiteAsync(principal, request, cancellationToken);
            if (created.Outcome != SaveTestSuiteOutcome.Saved)
            {
                throw new InvalidOperationException(created.ErrorMessage ?? "Unable to import test suite row due to invalid references.");
            }

            createdTests++;
        }

        return new ImportTestSuitesResultDto
        {
            CreatedTests = createdTests,
            UpdatedTests = updatedTests,
            CreatedDatasets = createdDatasets
        };
    }

    private static bool TryParseTcidAsSuiteId(string tcid, out long suiteId)
    {
        suiteId = 0;
        var text = NormalizeOptionalText(tcid);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (!text.StartsWith("TC", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return long.TryParse(text[2..], NumberStyles.Integer, CultureInfo.InvariantCulture, out suiteId) && suiteId > 0;
    }

    private static ComponentCatalog? ResolveComponentCatalog(IReadOnlyList<ComponentCatalog> catalog, string tsid)
    {
        var key = NormalizeOptionalText(tsid);
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var tokens = key
            .Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();

        var candidateKeys = new List<string> { key };
        if (tokens.Length > 0)
        {
            candidateKeys.Add(tokens[^1]);
        }

        var pageToken = tokens.Length > 1 ? tokens[^2] : string.Empty;
        var normalizedCandidates = candidateKeys
            .Select(NormalizeMatchKey)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var ranked = catalog
            .Select(item => new
            {
                Component = item,
                Score = ScoreComponentMatch(item, candidateKeys, normalizedCandidates, pageToken)
            })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Component.ComponentId)
            .FirstOrDefault();

        return ranked?.Component;
    }

    private static int ScoreComponentMatch(ComponentCatalog component, IReadOnlyList<string> candidates, IReadOnlyList<string> normalizedCandidates, string pageToken)
    {
        var score = 0;
        foreach (var candidate in candidates)
        {
            if (string.Equals(component.Feature, candidate, StringComparison.OrdinalIgnoreCase))
            {
                score += 100;
            }

            if (string.Equals(component.Name, candidate, StringComparison.OrdinalIgnoreCase))
            {
                score += 90;
            }

            if (!string.IsNullOrWhiteSpace(component.Feature)
                && component.Feature.Contains(candidate, StringComparison.OrdinalIgnoreCase))
            {
                score += 25;
            }

            if (!string.IsNullOrWhiteSpace(component.Name)
                && component.Name.Contains(candidate, StringComparison.OrdinalIgnoreCase))
            {
                score += 20;
            }
        }

        var featureKey = NormalizeMatchKey(component.Feature);
        var nameKey = NormalizeMatchKey(component.Name);
        foreach (var normalized in normalizedCandidates)
        {
            if (string.Equals(featureKey, normalized, StringComparison.OrdinalIgnoreCase))
            {
                score += 50;
            }

            if (string.Equals(nameKey, normalized, StringComparison.OrdinalIgnoreCase))
            {
                score += 40;
            }

            if (!string.IsNullOrWhiteSpace(featureKey) && featureKey.Contains(normalized, StringComparison.OrdinalIgnoreCase))
            {
                score += 15;
            }

            if (!string.IsNullOrWhiteSpace(nameKey) && nameKey.Contains(normalized, StringComparison.OrdinalIgnoreCase))
            {
                score += 10;
            }
        }

        if (!string.IsNullOrWhiteSpace(pageToken)
            && !string.IsNullOrWhiteSpace(component.Page)
            && component.Page.Contains(pageToken, StringComparison.OrdinalIgnoreCase))
        {
            score += 15;
        }

        return score;
    }

    private static string NormalizeMatchKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var chars = value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray();
        return new string(chars);
    }

    private static ComponentStepCatalog? ResolveStepCatalog(IReadOnlyList<ComponentStepCatalog> steps, string? referenceLabel, string? payloadStepDesc, int index)
    {
        // Preserve the spreadsheet's column-to-step positional mapping first.
        // Label matching is only used as a fallback for malformed/misaligned sheets.
        if (index >= 0 && index < steps.Count)
        {
            return steps[index];
        }

        var label = NormalizeOptionalText(referenceLabel);
        if (!string.IsNullOrWhiteSpace(label))
        {
            var exact = steps.FirstOrDefault(step => string.Equals(NormalizeOptionalText(step.Description), label, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }
        }

        var payloadLabel = NormalizeOptionalText(payloadStepDesc);
        if (!string.IsNullOrWhiteSpace(payloadLabel))
        {
            var exact = steps.FirstOrDefault(step => string.Equals(NormalizeOptionalText(step.Description), payloadLabel, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }
        }

        return steps.FirstOrDefault();
    }

    private static CompositeStepPayload? ParseCompositeStepPayload(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in SplitOverrideSegmentsForImport(raw))
        {
            if (!TrySplitOverridePairForImport(part, out var rawKey, out var rawValue))
            {
                continue;
            }

            var key = NormalizeOverrideKeyForImport(rawKey);
            if (!string.IsNullOrWhiteSpace(key))
            {
                normalized[key] = rawValue;
            }
        }

        if (normalized.Count == 0)
        {
            return null;
        }

        normalized.TryGetValue("description", out var stepDesc);
        normalized.TryGetValue("value", out var stepData);

        var canonicalOrder = new[]
        {
            "description",
            "value",
            "keyword",
            "xpath",
            "before_step",
            "after_step",
            "expected_output"
        };

        var canonicalParts = canonicalOrder
            .Where(key => normalized.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            .Select(key => $"{key}={normalized[key]}")
            .ToList();

        if (canonicalParts.Count == 0)
        {
            return null;
        }

        return new CompositeStepPayload
        {
            StepDesc = stepDesc,
            StepData = stepData,
            CanonicalOverrideValue = string.Join("||", canonicalParts)
        };
    }

    private static IReadOnlyList<string> SplitOverrideSegmentsForImport(string raw)
    {
        if (raw.Contains(";;", StringComparison.Ordinal))
        {
            return raw.Split(";;", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        if (raw.Contains("<=>", StringComparison.Ordinal))
        {
            return raw.Split("<=>", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        if (raw.Contains("||", StringComparison.Ordinal))
        {
            return raw.Split("||", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        return [raw.Trim()];
    }

    private static bool TrySplitOverridePairForImport(string segment, out string key, out string value)
    {
        var splitIndex = segment.IndexOf(":=", StringComparison.Ordinal);
        var separatorLength = 2;
        if (splitIndex < 0)
        {
            splitIndex = segment.IndexOf('=');
            separatorLength = 1;
        }

        if (splitIndex <= 0)
        {
            key = string.Empty;
            value = string.Empty;
            return false;
        }

        key = segment[..splitIndex].Trim();
        value = segment[(splitIndex + separatorLength)..].Trim();
        return !string.IsNullOrWhiteSpace(key);
    }

    private static string NormalizeOverrideKeyForImport(string key)
    {
        return key.Trim().ToLowerInvariant() switch
        {
            "stepdesc" => "description",
            "description" => "description",
            "stepdata" => "value",
            "value" => "value",
            "action" => "value",
            "keyword" => "keyword",
            "locator" => "xpath",
            "xpath" => "xpath",
            "beforestep" => "before_step",
            "before_step" => "before_step",
            "afterstep" => "after_step",
            "after_step" => "after_step",
            "expected" => "expected_output",
            "expected_output" => "expected_output",
            _ => string.Empty
        };
    }

    private async Task<IReadOnlyList<ComponentCatalog>> LoadComponentCatalogAsync(SqlConnection connection, long clientId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                c.id AS component_id,
                c.project_id,
                c.name,
                c.feature,
                c.page,
                cs.id AS step_id,
                cs.display_id,
                cs.description
            FROM components c
            LEFT JOIN component_steps cs ON cs.component_id = c.id AND cs.deleted_at IS NULL
            WHERE c.client_id = @clientId AND c.deleted_at IS NULL
            ORDER BY c.id, ISNULL(cs.display_id, 2147483647), cs.id;
            """;

        var map = new Dictionary<long, ComponentCatalog>();
        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@clientId", clientId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var componentId = GetInt64(reader, "component_id") ?? 0;
            if (componentId <= 0)
            {
                continue;
            }

            if (!map.TryGetValue(componentId, out var catalog))
            {
                catalog = new ComponentCatalog
                {
                    ComponentId = componentId,
                    ProjectId = GetInt64(reader, "project_id"),
                    Name = NormalizeOptionalText(GetString(reader, "name")),
                    Feature = NormalizeOptionalText(GetString(reader, "feature")),
                    Page = NormalizeOptionalText(GetString(reader, "page"))
                };
                map[componentId] = catalog;
            }

            var stepId = GetInt64(reader, "step_id");
            if (stepId.HasValue)
            {
                catalog.Steps.Add(new ComponentStepCatalog
                {
                    StepId = stepId.Value,
                    DisplayId = GetInt32(reader, "display_id"),
                    Description = NormalizeOptionalText(GetString(reader, "description"))
                });
            }
        }

        return map.Values.ToList();
    }

    private static async Task<TwoSheetWorkbookImport?> TryParseTwoSheetWorkbookAsync(Stream stream, CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        try
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var workbookEntry = archive.GetEntry("xl/workbook.xml");
            var relsEntry = archive.GetEntry("xl/_rels/workbook.xml.rels");
            if (workbookEntry is null || relsEntry is null)
            {
                return null;
            }

            var workbookXml = new XmlDocument();
            using (var reader = new StreamReader(workbookEntry.Open()))
            {
                workbookXml.LoadXml(reader.ReadToEnd());
            }

            var relsXml = new XmlDocument();
            using (var reader = new StreamReader(relsEntry.Open()))
            {
                relsXml.LoadXml(reader.ReadToEnd());
            }

            var sharedStrings = LoadSharedStrings(archive);
            var workbookNs = new XmlNamespaceManager(workbookXml.NameTable);
            workbookNs.AddNamespace("x", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
            workbookNs.AddNamespace("r", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");

            var relNs = new XmlNamespaceManager(relsXml.NameTable);
            relNs.AddNamespace("p", "http://schemas.openxmlformats.org/package/2006/relationships");

            var relationMap = relsXml.SelectNodes("//p:Relationship", relNs)?
                .Cast<XmlNode>()
                .Select(node => new
                {
                    Id = node.Attributes?["Id"]?.Value,
                    Target = node.Attributes?["Target"]?.Value
                })
                .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Target))
                .ToDictionary(item => item.Id!, item => item.Target!, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var sheets = workbookXml.SelectNodes("//x:sheets/x:sheet", workbookNs)?.Cast<XmlNode>().ToList() ?? [];
            var requirementsPath = ResolveWorksheetPathByName(sheets, "Requirements", relationMap);
            var testDataPath = ResolveWorksheetPathByName(sheets, "TestData", relationMap);
            if (requirementsPath is null || testDataPath is null)
            {
                return null;
            }

            var requirementsRows = ReadWorksheetRows(archive, requirementsPath, sharedStrings);
            var testDataRows = ReadWorksheetRows(archive, testDataPath, sharedStrings);
            if (requirementsRows.Count == 0 || testDataRows.Count == 0)
            {
                return null;
            }

            var requirements = ParseRequirementsRows(requirementsRows);
            var testData = ParseTestDataRows(testDataRows);
            return new TwoSheetWorkbookImport
            {
                Requirements = requirements,
                TestData = testData
            };
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (XmlException)
        {
            return null;
        }
        finally
        {
            if (stream.CanSeek)
            {
                stream.Position = 0;
            }
        }
    }

    private static string? ResolveWorksheetPathByName(IReadOnlyList<XmlNode> sheets, string sheetName, IReadOnlyDictionary<string, string> relationMap)
    {
        var node = sheets.FirstOrDefault(item => string.Equals(item.Attributes?["name"]?.Value, sheetName, StringComparison.OrdinalIgnoreCase));
        var relationId = node?.Attributes?.Cast<XmlAttribute>().FirstOrDefault(attribute => string.Equals(attribute.LocalName, "id", StringComparison.OrdinalIgnoreCase))?.Value;
        if (string.IsNullOrWhiteSpace(relationId) || !relationMap.TryGetValue(relationId, out var target))
        {
            return null;
        }

        if (target.StartsWith("/", StringComparison.Ordinal))
        {
            return target.TrimStart('/');
        }

        return target.StartsWith("xl/", StringComparison.OrdinalIgnoreCase)
            ? target
            : $"xl/{target.TrimStart('/')}";
    }

    private static List<string> LoadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null)
        {
            return [];
        }

        var xml = new XmlDocument();
        using var reader = new StreamReader(entry.Open());
        xml.LoadXml(reader.ReadToEnd());
        var ns = new XmlNamespaceManager(xml.NameTable);
        ns.AddNamespace("x", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");

        var values = new List<string>();
        var nodes = xml.SelectNodes("//x:si", ns) ?? xml.SelectNodes("//si");
        if (nodes is null)
        {
            return values;
        }

        foreach (XmlNode node in nodes)
        {
            var textNodes = node.SelectNodes(".//x:t", ns) ?? node.SelectNodes(".//t");
            if (textNodes is null || textNodes.Count == 0)
            {
                values.Add(string.Empty);
                continue;
            }

            var combined = string.Concat(textNodes.Cast<XmlNode>().Select(item => item.InnerText));
            values.Add(combined);
        }

        return values;
    }

    private static List<List<string>> ReadWorksheetRows(ZipArchive archive, string entryPath, IReadOnlyList<string> sharedStrings)
    {
        var entry = archive.GetEntry(entryPath);
        if (entry is null)
        {
            throw new InvalidOperationException($"Worksheet entry '{entryPath}' not found.");
        }

        var xml = new XmlDocument();
        using (var reader = new StreamReader(entry.Open()))
        {
            xml.LoadXml(reader.ReadToEnd());
        }

        var ns = new XmlNamespaceManager(xml.NameTable);
        ns.AddNamespace("x", "http://schemas.openxmlformats.org/spreadsheetml/2006/main");
        var rowNodes = xml.SelectNodes("//x:sheetData/x:row", ns) ?? xml.SelectNodes("//sheetData/row");
        var rows = new List<List<string>>();
        if (rowNodes is null)
        {
            return rows;
        }

        foreach (XmlNode rowNode in rowNodes)
        {
            var cells = rowNode.SelectNodes("x:c", ns) ?? rowNode.SelectNodes("c");
            if (cells is null)
            {
                rows.Add([]);
                continue;
            }

            var map = new Dictionary<int, string>();
            var maxIndex = 0;
            var sequential = 1;
            foreach (XmlNode cell in cells)
            {
                var refText = cell.Attributes?["r"]?.Value;
                var columnIndex = !string.IsNullOrWhiteSpace(refText)
                    ? GetColumnIndexFromReference(refText)
                    : sequential++;

                var value = GetCellText(cell, ns, sharedStrings);
                map[columnIndex] = value;
                maxIndex = Math.Max(maxIndex, columnIndex);
            }

            var row = new List<string>(Enumerable.Repeat(string.Empty, maxIndex));
            foreach (var pair in map)
            {
                row[pair.Key - 1] = pair.Value;
            }

            rows.Add(row);
        }

        return rows;
    }

    private static int GetColumnIndexFromReference(string cellReference)
    {
        var letters = new string(cellReference.TakeWhile(char.IsLetter).ToArray());
        if (string.IsNullOrWhiteSpace(letters))
        {
            return 1;
        }

        var index = 0;
        foreach (var ch in letters.ToUpperInvariant())
        {
            index = (index * 26) + (ch - 'A' + 1);
        }

        return Math.Max(index, 1);
    }

    private static string GetCellText(XmlNode cell, XmlNamespaceManager ns, IReadOnlyList<string> sharedStrings)
    {
        var type = cell.Attributes?["t"]?.Value;
        if (string.Equals(type, "s", StringComparison.OrdinalIgnoreCase))
        {
            var valueNode = cell.SelectSingleNode("x:v", ns) ?? cell.SelectSingleNode("v");
            if (valueNode is null)
            {
                return string.Empty;
            }

            return int.TryParse(valueNode.InnerText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                && index >= 0
                && index < sharedStrings.Count
                ? sharedStrings[index]
                : string.Empty;
        }

        if (string.Equals(type, "inlineStr", StringComparison.OrdinalIgnoreCase))
        {
            var textNodes = cell.SelectNodes("x:is/x:t", ns) ?? cell.SelectNodes("is/t");
            if (textNodes is null)
            {
                return string.Empty;
            }

            return string.Concat(textNodes.Cast<XmlNode>().Select(item => item.InnerText));
        }

        var direct = cell.SelectSingleNode("x:v", ns) ?? cell.SelectSingleNode("v");
        return direct?.InnerText ?? string.Empty;
    }

    private static List<RequirementsSheetImportRow> ParseRequirementsRows(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var header = rows[0]
            .Select((value, index) => new { Key = NormalizeOptionalText(value) ?? string.Empty, Index = index })
            .ToDictionary(item => item.Key, item => item.Index, StringComparer.OrdinalIgnoreCase);

        if (!header.TryGetValue("TCID", out var tcidIndex) || !header.TryGetValue("Requirement", out var requirementIndex))
        {
            throw new InvalidOperationException("Requirements sheet is missing required columns (TCID, Requirement).");
        }

        var serIndex = header.TryGetValue("Ser", out var serValue) ? serValue : -1;
        var runFlagIndex = header.TryGetValue("RunFlag", out var runFlagValue) ? runFlagValue : -1;
        var statusIndex = header.TryGetValue("Status", out var statusValue) ? statusValue : -1;

        var result = new List<RequirementsSheetImportRow>();
        foreach (var row in rows.Skip(1))
        {
            var tcid = ReadRowValue(row, tcidIndex);
            if (string.IsNullOrWhiteSpace(tcid))
            {
                continue;
            }

            result.Add(new RequirementsSheetImportRow
            {
                TCID = tcid,
                Ser = serIndex >= 0 ? ReadRowValue(row, serIndex) : string.Empty,
                Requirement = ReadRowValue(row, requirementIndex),
                RunFlag = runFlagIndex >= 0 ? ReadRowValue(row, runFlagIndex) : string.Empty,
                Status = statusIndex >= 0 ? ReadRowValue(row, statusIndex) : string.Empty
            });
        }

        return result;
    }

    private static List<TestDataSheetImportRow> ParseTestDataRows(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var headerRow = rows[0];
        var header = headerRow
            .Select((value, index) => new { Key = NormalizeOptionalText(value) ?? string.Empty, Index = index })
            .ToDictionary(item => item.Key, item => item.Index, StringComparer.OrdinalIgnoreCase);

        if (!header.TryGetValue("TDID", out var tdidIndex)
            || !header.TryGetValue("TSID", out var tsidIndex)
            || !header.TryGetValue("E", out var eIndex)
            || !header.TryGetValue("Test_Case_Description", out var descIndex))
        {
            throw new InvalidOperationException("TestData sheet is missing required columns (TDID, TSID, E, Test_Case_Description).");
        }

        var expectedIndex = header.TryGetValue("Expected", out var expectedValue) ? expectedValue : -1;
        var actualIndex = header.TryGetValue("Actual", out var actualValue) ? actualValue : -1;
        var resultIndex = header.TryGetValue("Result", out var resultValue) ? resultValue : -1;

        var ipIndices = headerRow
            .Select((value, index) => new { Header = NormalizeOptionalText(value) ?? string.Empty, Index = index })
            .Where(item => item.Header.StartsWith("IP", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.Index)
            .Select(item => item.Index)
            .ToList();

        var data = new List<TestDataSheetImportRow>();
        foreach (var row in rows.Skip(1))
        {
            var tdid = ReadRowValue(row, tdidIndex);
            if (string.IsNullOrWhiteSpace(tdid))
            {
                continue;
            }

            var stepValues = ipIndices.Select(index => ReadRowValue(row, index)).ToList();
            data.Add(new TestDataSheetImportRow
            {
                TDID = tdid,
                TSID = ReadRowValue(row, tsidIndex),
                E = ReadRowValue(row, eIndex),
                TestCaseDescription = ReadRowValue(row, descIndex),
                Expected = expectedIndex >= 0 ? ReadRowValue(row, expectedIndex) : string.Empty,
                Actual = actualIndex >= 0 ? ReadRowValue(row, actualIndex) : string.Empty,
                Result = resultIndex >= 0 ? ReadRowValue(row, resultIndex) : string.Empty,
                StepValues = stepValues
            });
        }

        return data;
    }

    private static string ReadRowValue(IReadOnlyList<string> row, int index)
    {
        if (index < 0 || index >= row.Count)
        {
            return string.Empty;
        }

        return row[index].Trim();
    }

    private async Task<IReadOnlyList<TestSuiteMatrixRow>> ParseTestSuiteMatrixRowsAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, true, leaveOpen: true);
        var header = await reader.ReadLineAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(header))
        {
            throw new InvalidOperationException("The uploaded file is empty.");
        }

        var headers = ParseCsvLine(header)
            .Select((name, index) => new { Name = name.Trim().ToLowerInvariant(), Index = index })
            .ToDictionary(item => item.Name, item => item.Index, StringComparer.OrdinalIgnoreCase);

        if (!headers.ContainsKey("title"))
        {
            throw new InvalidOperationException("The matrix file is missing the title column.");
        }

        if (!headers.ContainsKey("project_id"))
        {
            throw new InvalidOperationException("The matrix file is missing the project_id column.");
        }

        var rows = new List<TestSuiteMatrixRow>();
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var columns = ParseCsvLine(line);
            string Read(string name)
            {
                if (!headers.TryGetValue(name, out var index))
                {
                    return string.Empty;
                }

                return index < columns.Count ? columns[index].Trim() : string.Empty;
            }

            var title = Read("title");
            var projectId = ParseNullableLong(Read("project_id"));
            if (string.IsNullOrWhiteSpace(title) || !projectId.HasValue)
            {
                continue;
            }

            rows.Add(new TestSuiteMatrixRow
            {
                TestSuiteId = ParseNullableLong(Read("tdid")),
                Title = title,
                ProjectId = projectId.Value,
                TestStateId = ParseNullableLong(Read("test_state_id")),
                TestSuiteType = ParseNullableInt(Read("test_suite_type")),
                Priority = Read("priority"),
                StoryId = Read("story_id"),
                TestTitle = Read("test_title"),
                Tags = Read("tags"),
                Comment = Read("comment"),
                ComponentOrder = ParseNullableInt(Read("component_order")),
                ComponentId = ParseNullableLong(Read("component_id")),
                ComponentProjectId = ParseNullableLong(Read("component_project_id")),
                ComponentStatus = ParseNullableBool(Read("component_status")),
                DatasetIndex = ParseNullableInt(Read("dataset_index")),
                DatasetScenario = Read("dataset_scenario"),
                DatasetStatus = ParseNullableBool(Read("dataset_status")),
                StepDisplayId = ParseNullableInt(Read("step_display_id")),
                StepId = ParseNullableLong(Read("step_id")),
                StepValue = Read("step_value"),
                StepOverride = ParseNullableBool(Read("step_override")),
                StepOverrideValue = Read("step_override_value")
            });
        }

        if (rows.Count == 0)
        {
            throw new InvalidOperationException("The matrix file does not contain any valid rows.");
        }

        return rows;
    }

    private static List<TestSuiteImportModel> BuildTestSuiteImportModels(IReadOnlyList<TestSuiteMatrixRow> rows)
    {
        var suites = new Dictionary<string, TestSuiteImportModel>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var suiteKey = row.TestSuiteId.HasValue
                ? $"id:{row.TestSuiteId.Value}"
                : $"new:{row.ProjectId}:{row.Title}";

            if (!suites.TryGetValue(suiteKey, out var suite))
            {
                suite = new TestSuiteImportModel
                {
                    TestSuiteId = row.TestSuiteId,
                    Title = row.Title,
                    ProjectId = row.ProjectId,
                    TestStateId = row.TestStateId,
                    TestSuiteType = row.TestSuiteType,
                    Priority = row.Priority,
                    StoryId = row.StoryId,
                    TestTitle = row.TestTitle,
                    Tags = row.Tags,
                    Comment = row.Comment
                };
                suites[suiteKey] = suite;
            }

            if (!row.ComponentId.HasValue)
            {
                continue;
            }

            var componentKey = row.ComponentId.Value.ToString(CultureInfo.InvariantCulture);
            if (!suite.ComponentMap.TryGetValue(componentKey, out var component))
            {
                component = new TestSuiteImportComponent
                {
                    ComponentId = row.ComponentId,
                    ProjectId = row.ComponentProjectId ?? row.ProjectId,
                    Status = row.ComponentStatus ?? true
                };
                suite.ComponentMap[componentKey] = component;
                suite.ComponentOrder.Add(componentKey);
            }

            var datasetKey = (row.DatasetIndex ?? 1).ToString(CultureInfo.InvariantCulture);
            if (!component.DatasetMap.TryGetValue(datasetKey, out var dataset))
            {
                dataset = new TestSuiteImportDataset
                {
                    Scenario = row.DatasetScenario,
                    Status = row.DatasetStatus ?? false
                };
                component.DatasetMap[datasetKey] = dataset;
                component.DatasetOrder.Add(datasetKey);
            }

            if (!row.StepId.HasValue)
            {
                continue;
            }

            dataset.Steps.Add(new TestSuiteImportStep
            {
                DisplayId = row.StepDisplayId ?? dataset.Steps.Count + 1,
                StepId = row.StepId,
                Value = string.IsNullOrWhiteSpace(row.StepValue) ? SkipStepValue : row.StepValue,
                Override = row.StepOverride ?? false,
                OverrideValue = NormalizeOptionalText(row.StepOverrideValue)
            });
        }

        var models = suites.Values.ToList();
        foreach (var suite in models)
        {
            suite.Components = suite.ComponentOrder.Select(key => suite.ComponentMap[key]).ToList();
            foreach (var component in suite.Components)
            {
                component.Datasets = component.DatasetOrder.Select(key => component.DatasetMap[key]).ToList();
            }
        }

        return models;
    }

    private async Task<long> LoadDefaultTestStateIdAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, "SELECT TOP 1 id FROM test_states ORDER BY id;");
        var value = await command.ExecuteScalarAsync(cancellationToken);
        var stateId = value is null || value == DBNull.Value
            ? 0L
            : Convert.ToInt64(value, CultureInfo.InvariantCulture);

        if (stateId <= 0)
        {
            throw new InvalidOperationException("No test states are configured for test suite import.");
        }

        return stateId;
    }

    private static JsonElement? CreateTagsJsonElement(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags))
        {
            return null;
        }

        var parsed = ParseTags(tags);
        return JsonSerializer.SerializeToElement(parsed);
    }

    private static long? ParseNullableLong(string? raw)
    {
        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static int? ParseNullableInt(string? raw)
    {
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static bool? ParseNullableBool(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (bool.TryParse(raw, out var boolValue))
        {
            return boolValue;
        }

        return raw.Trim() switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };
    }

    private async Task<Dictionary<string, long>> LoadProjectNameMapAsync(SqlConnection connection, long clientId, CancellationToken cancellationToken)
    {
        const string sql = "SELECT id, project_name FROM projects WHERE client_id = @clientId AND deleted_at IS NULL;";
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@clientId", clientId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            map[GetString(reader, "project_name") ?? string.Empty] = reader.GetInt64(reader.GetOrdinal("id"));
        }

        return map;
    }

    private async Task<Dictionary<string, string>> LoadKeywordNameMapAsync(SqlConnection connection, long clientId, CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        await using (var globalCommand = CreateCommand(connection, "SELECT id, name FROM global_keywords ORDER BY name;"))
        {
            await using var reader = await globalCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var name = GetString(reader, "name");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    map[name] = $"g:{reader.GetInt64(reader.GetOrdinal("id"))}";
                }
            }
        }

        await using (var customCommand = CreateCommand(connection, "SELECT id, name FROM component_keywords WHERE client_id = @clientId ORDER BY name;"))
        {
            customCommand.Parameters.AddWithValue("@clientId", clientId);
            await using var reader = await customCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var name = GetString(reader, "name");
                if (!string.IsNullOrWhiteSpace(name) && !map.ContainsKey(name))
                {
                    map[name] = $"c:{reader.GetInt64(reader.GetOrdinal("id"))}";
                }
            }
        }

        return map;
    }

    private sealed class WorksheetExport(string name, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        public string Name { get; } = name;

        public IReadOnlyList<IReadOnlyList<string>> Rows { get; } = rows;
    }

    private sealed class RequirementsSheetRow
    {
        public string TCID { get; init; } = string.Empty;

        public string Ser { get; init; } = string.Empty;

        public string Requirement { get; init; } = string.Empty;

        public string RunFlag { get; init; } = string.Empty;

        public string Status { get; init; } = string.Empty;
    }

    private sealed class TestDataSheetRow
    {
        public string TDID { get; init; } = string.Empty;

        public string TSID { get; init; } = string.Empty;

        public string E { get; init; } = string.Empty;

        public string TestCaseDescription { get; init; } = string.Empty;

        public string Expected { get; init; } = string.Empty;

        public string Actual { get; init; } = string.Empty;

        public string Result { get; init; } = string.Empty;

        public IReadOnlyList<string> StepValues { get; init; } = [];
    }

    private sealed class TwoSheetWorkbookImport
    {
        public IReadOnlyList<RequirementsSheetImportRow> Requirements { get; init; } = [];

        public IReadOnlyList<TestDataSheetImportRow> TestData { get; init; } = [];
    }

    private sealed class RequirementsSheetImportRow
    {
        public string TCID { get; init; } = string.Empty;

        public string Ser { get; init; } = string.Empty;

        public string Requirement { get; init; } = string.Empty;

        public string RunFlag { get; init; } = string.Empty;

        public string Status { get; init; } = string.Empty;
    }

    private sealed class TestDataSheetImportRow
    {
        public string TDID { get; init; } = string.Empty;

        public string TSID { get; init; } = string.Empty;

        public string E { get; init; } = string.Empty;

        public string TestCaseDescription { get; init; } = string.Empty;

        public string Expected { get; init; } = string.Empty;

        public string Actual { get; init; } = string.Empty;

        public string Result { get; init; } = string.Empty;

        public IReadOnlyList<string> StepValues { get; init; } = [];
    }

    private sealed class ComponentCatalog
    {
        public long ComponentId { get; init; }

        public long? ProjectId { get; init; }

        public string? Name { get; init; }

        public string? Feature { get; init; }

        public string? Page { get; init; }

        public List<ComponentStepCatalog> Steps { get; } = [];
    }

    private sealed class ComponentStepCatalog
    {
        public long StepId { get; init; }

        public int? DisplayId { get; init; }

        public string? Description { get; init; }
    }

    private sealed class CompositeStepPayload
    {
        public string? StepDesc { get; init; }

        public string? StepData { get; init; }

        public string? CanonicalOverrideValue { get; init; }
    }

    private sealed class TestSuiteMatrixRow
    {
        public long? TestSuiteId { get; init; }

        public string Title { get; init; } = string.Empty;

        public long ProjectId { get; init; }

        public long? TestStateId { get; init; }

        public int? TestSuiteType { get; init; }

        public string? Priority { get; init; }

        public string? StoryId { get; init; }

        public string? TestTitle { get; init; }

        public string? Tags { get; init; }

        public string? Comment { get; init; }

        public int? ComponentOrder { get; init; }

        public long? ComponentId { get; init; }

        public long? ComponentProjectId { get; init; }

        public bool? ComponentStatus { get; init; }

        public int? DatasetIndex { get; init; }

        public string? DatasetScenario { get; init; }

        public bool? DatasetStatus { get; init; }

        public int? StepDisplayId { get; init; }

        public long? StepId { get; init; }

        public string? StepValue { get; init; }

        public bool? StepOverride { get; init; }

        public string? StepOverrideValue { get; init; }
    }

    private sealed class TestSuiteImportModel
    {
        public long? TestSuiteId { get; init; }

        public string Title { get; init; } = string.Empty;

        public long ProjectId { get; init; }

        public long? TestStateId { get; init; }

        public int? TestSuiteType { get; init; }

        public string? Priority { get; init; }

        public string? StoryId { get; init; }

        public string? TestTitle { get; init; }

        public string? Tags { get; init; }

        public string? Comment { get; init; }

        public Dictionary<string, TestSuiteImportComponent> ComponentMap { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> ComponentOrder { get; } = [];

        public List<TestSuiteImportComponent> Components { get; set; } = [];
    }

    private sealed class TestSuiteImportComponent
    {
        public long? ComponentId { get; init; }

        public long? ProjectId { get; init; }

        public bool Status { get; init; }

        public Dictionary<string, TestSuiteImportDataset> DatasetMap { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> DatasetOrder { get; } = [];

        public List<TestSuiteImportDataset> Datasets { get; set; } = [];
    }

    private sealed class TestSuiteImportDataset
    {
        public string? Scenario { get; init; }

        public bool Status { get; init; }

        public List<TestSuiteImportStep> Steps { get; } = [];
    }

    private sealed class TestSuiteImportStep
    {
        public int? DisplayId { get; init; }

        public long? StepId { get; init; }

        public string? Value { get; init; }

        public bool Override { get; init; }

        public string? OverrideValue { get; init; }
    }
}
