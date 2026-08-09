using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Xunit;

namespace QafOnPrem.Api.Tests;

public sealed class ComponentDatasetSyncIntegrationTests : IClassFixture<ProjectApiFactory>
{
    private const long ClientId = 11;
    private readonly ProjectApiFactory _factory;

    public ComponentDatasetSyncIntegrationTests(ProjectApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ComponentMetadataCatalog_ReturnsProjectScopedPagesAndFeaturePages()
    {
        long projectId = 0;
        var componentIds = new List<long>();

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await _factory.CreateTokenAsync(ClientId));

        try
        {
            projectId = await _factory.InsertProjectAsync(ClientId, "Component Catalog Project");

            componentIds.Add(await CreateComponentAsync(client, projectId, "Catalog Component 1", "Elements", "Buttons"));
            componentIds.Add(await CreateComponentAsync(client, projectId, "Catalog Component 2", "Elements", "Inputs"));
            componentIds.Add(await CreateComponentAsync(client, projectId, "Catalog Component 3", "Frames", "Buttons"));

            var response = await client.GetAsync($"/api/components/catalog?project_id={projectId}");
            var payload = await ReadJsonAsync(response);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var pages = payload.RootElement
                .GetProperty("data")
                .GetProperty("pages")
                .EnumerateArray()
                .Select(item => item.GetString())
                .Where(item => item is not null)
                .Cast<string>()
                .ToArray();

            Assert.Equal(new[] { "Elements", "Frames" }, pages);

            var features = payload.RootElement
                .GetProperty("data")
                .GetProperty("features")
                .EnumerateArray()
                .Select(item => new
                {
                    Feature = item.GetProperty("feature").GetString(),
                    Pages = item.GetProperty("pages").EnumerateArray().Select(page => page.GetString()).Where(page => page is not null).Cast<string>().ToArray()
                })
                .ToArray();

            var buttons = Assert.Single(features, item => item.Feature == "Buttons");
            Assert.Equal(new[] { "Elements", "Frames" }, buttons.Pages);

            var inputs = Assert.Single(features, item => item.Feature == "Inputs");
            Assert.Equal(new[] { "Elements" }, inputs.Pages);
        }
        finally
        {
            foreach (var componentId in componentIds)
            {
                await _factory.HardDeleteComponentAsync(componentId);
            }

            if (projectId > 0)
            {
                await _factory.HardDeleteProjectAsync(projectId);
            }
        }
    }

    [Fact]
    public async Task ComponentSave_ReturnsConflict_WhenProjectPageFeatureAlreadyExists()
    {
        long projectId = 0;
        var componentIds = new List<long>();

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await _factory.CreateTokenAsync(ClientId));

        try
        {
            projectId = await _factory.InsertProjectAsync(ClientId, "Component Duplicate Guard Project");

            componentIds.Add(await CreateComponentAsync(client, projectId, "Duplicate Guard 1", "Elements", "Buttons"));
            componentIds.Add(await CreateComponentAsync(client, projectId, "Duplicate Guard 2", "Frames", "Buttons"));

            var duplicateCreatePayload = new
            {
                name = "Duplicate Guard Create",
                project_id = projectId,
                page = "Elements",
                feature = "Buttons",
                type_id = 1,
                steps = BuildSingleStepPayload("Create conflict step")
            };

            var duplicateCreateResponse = await client.PostAsync("/api/components", JsonContent(duplicateCreatePayload));
            var duplicateCreate = await ReadJsonAsync(duplicateCreateResponse);

            Assert.Equal(HttpStatusCode.Conflict, duplicateCreateResponse.StatusCode);
            Assert.Equal("Component with the same Project, Page, and Feature already exists.", duplicateCreate.RootElement.GetProperty("message").GetString());

            var duplicateUpdatePayload = new
            {
                name = "Duplicate Guard 2",
                project_id = projectId,
                page = "Elements",
                feature = "Buttons",
                type_id = 1,
                steps = BuildSingleStepPayload("Update conflict step")
            };

            var duplicateUpdateResponse = await client.PatchAsync($"/api/components/{componentIds[1]}", JsonContent(duplicateUpdatePayload));
            var duplicateUpdate = await ReadJsonAsync(duplicateUpdateResponse);

            Assert.Equal(HttpStatusCode.Conflict, duplicateUpdateResponse.StatusCode);
            Assert.Equal("Component with the same Project, Page, and Feature already exists.", duplicateUpdate.RootElement.GetProperty("message").GetString());
        }
        finally
        {
            foreach (var componentId in componentIds)
            {
                await _factory.HardDeleteComponentAsync(componentId);
            }

            if (projectId > 0)
            {
                await _factory.HardDeleteProjectAsync(projectId);
            }
        }
    }

    [Fact]
    public async Task UpdateComponent_SyncsExistingSuiteDatasets_ForAddMoveAndDelete()
    {
        long projectId = 0;
        long componentId = 0;
        long suiteId = 0;

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await _factory.CreateTokenAsync(ClientId));

        try
        {
            projectId = await _factory.InsertProjectAsync(ClientId, "Component Sync Project");

            var createComponentPayload = new
            {
                name = "Component Sync Flow",
                project_id = projectId,
                page = "Integration",
                feature = "Component Sync",
                type_id = 1,
                steps = new object[]
                {
                    new { description = "Step A", expected_output = "", keyword_ref = "", before_step = Array.Empty<string>(), after_step = Array.Empty<string>(), display_id = 0 },
                    new { description = "Step B", expected_output = "", keyword_ref = "", before_step = Array.Empty<string>(), after_step = Array.Empty<string>(), display_id = 1 },
                    new { description = "Step C", expected_output = "", keyword_ref = "", before_step = Array.Empty<string>(), after_step = Array.Empty<string>(), display_id = 2 }
                }
            };

            var createComponentResponse = await client.PostAsync("/api/components", JsonContent(createComponentPayload));
            var createdComponent = await ReadJsonAsync(createComponentResponse);
            Assert.Equal(HttpStatusCode.OK, createComponentResponse.StatusCode);

            componentId = createdComponent.RootElement.GetProperty("data").GetProperty("id").GetInt64();
            var createdSteps = createdComponent.RootElement.GetProperty("data").GetProperty("steps").EnumerateArray().ToArray();
            var stepA = createdSteps.Single(step => step.GetProperty("description").GetString() == "Step A");
            var stepB = createdSteps.Single(step => step.GetProperty("description").GetString() == "Step B");
            var stepC = createdSteps.Single(step => step.GetProperty("description").GetString() == "Step C");

            var createSuitePayload = new
            {
                details = new
                {
                    title = $"Component Sync Suite {Guid.NewGuid():N}",
                    test_state_id = 1,
                    test_suite_type = 1,
                    project_id = projectId,
                    tags = Array.Empty<string>()
                },
                designed_components = new object[]
                {
                    new
                    {
                        component_id = componentId,
                        project_id = projectId,
                        status = true,
                        datasets = new object[]
                        {
                            new
                            {
                                scenario = "Primary Dataset",
                                status = true,
                                steps = Array.Empty<object>()
                            }
                        }
                    }
                }
            };

            var createSuiteResponse = await client.PostAsync("/api/test-suite", JsonContent(createSuitePayload));
            var createdSuite = await ReadJsonAsync(createSuiteResponse);
            Assert.Equal(HttpStatusCode.OK, createSuiteResponse.StatusCode);

            suiteId = createdSuite.RootElement.GetProperty("data").GetProperty("id").GetInt64();

            var updateComponentPayload = new
            {
                name = "Component Sync Flow",
                project_id = projectId,
                page = "Integration",
                feature = "Component Sync",
                type_id = 1,
                steps = new object[]
                {
                    new
                    {
                        id = stepC.GetProperty("id").GetInt64(),
                        description = "Step C",
                        expected_output = "",
                        keyword_ref = "",
                        before_step = Array.Empty<string>(),
                        after_step = Array.Empty<string>(),
                        display_id = 0
                    },
                    new
                    {
                        id = stepA.GetProperty("id").GetInt64(),
                        description = "Step A",
                        expected_output = "",
                        keyword_ref = "",
                        before_step = Array.Empty<string>(),
                        after_step = Array.Empty<string>(),
                        display_id = 1
                    },
                    new
                    {
                        description = "Step D",
                        expected_output = "",
                        keyword_ref = "",
                        before_step = Array.Empty<string>(),
                        after_step = Array.Empty<string>(),
                        display_id = 2
                    }
                },
                deleted_steps = new[] { stepB.GetProperty("id").GetInt64() }
            };

            var updateComponentResponse = await client.PatchAsync($"/api/components/{componentId}", JsonContent(updateComponentPayload));
            var updatedComponent = await ReadJsonAsync(updateComponentResponse);
            Assert.Equal(HttpStatusCode.OK, updateComponentResponse.StatusCode);

            var newStep = updatedComponent.RootElement
                .GetProperty("data")
                .GetProperty("steps")
                .EnumerateArray()
                .Single(step => step.GetProperty("description").GetString() == "Step D");

            var suiteResponse = await client.GetAsync($"/api/test-suite/{suiteId}/full");
            var suitePayload = await ReadJsonAsync(suiteResponse);
            Assert.Equal(HttpStatusCode.OK, suiteResponse.StatusCode);

            var datasetSteps = suitePayload.RootElement
                .GetProperty("data")
                .GetProperty("components")[0]
                .GetProperty("datasets")[0]
                .GetProperty("steps")
                .EnumerateArray()
                .ToArray();

            Assert.Equal(3, datasetSteps.Length);
            Assert.Collection(
                datasetSteps,
                step =>
                {
                    Assert.Equal(stepC.GetProperty("id").GetInt64(), step.GetProperty("step_id").GetInt64());
                    Assert.Equal(0, step.GetProperty("display_id").GetInt32());
                    Assert.Equal("skip", step.GetProperty("value").GetString());
                },
                step =>
                {
                    Assert.Equal(stepA.GetProperty("id").GetInt64(), step.GetProperty("step_id").GetInt64());
                    Assert.Equal(1, step.GetProperty("display_id").GetInt32());
                    Assert.Equal("skip", step.GetProperty("value").GetString());
                },
                step =>
                {
                    Assert.Equal(newStep.GetProperty("id").GetInt64(), step.GetProperty("step_id").GetInt64());
                    Assert.Equal(2, step.GetProperty("display_id").GetInt32());
                    Assert.Equal("skip", step.GetProperty("value").GetString());
                });
        }
        finally
        {
            if (suiteId > 0)
            {
                await _factory.HardDeleteTestSuiteTreeAsync(suiteId);
            }

            if (componentId > 0)
            {
                await _factory.HardDeleteComponentAsync(componentId);
            }

            if (projectId > 0)
            {
                await _factory.HardDeleteProjectAsync(projectId);
            }
        }
    }

    [Fact]
    public async Task SyncLinkedComponentTests_BackfillsExistingStaleDatasets()
    {
        long projectId = 0;
        long componentId = 0;
        long suiteId = 0;

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await _factory.CreateTokenAsync(ClientId));

        try
        {
            projectId = await _factory.InsertProjectAsync(ClientId, "Component Backfill Project");

            var createComponentPayload = new
            {
                name = "Component Backfill Flow",
                project_id = projectId,
                page = "Integration",
                feature = "Component Backfill",
                type_id = 1,
                steps = new object[]
                {
                    new { description = "Step 1", expected_output = "", keyword_ref = "", before_step = Array.Empty<string>(), after_step = Array.Empty<string>(), display_id = 0 },
                    new { description = "Step 2", expected_output = "", keyword_ref = "", before_step = Array.Empty<string>(), after_step = Array.Empty<string>(), display_id = 1 }
                }
            };

            var createComponentResponse = await client.PostAsync("/api/components", JsonContent(createComponentPayload));
            var createdComponent = await ReadJsonAsync(createComponentResponse);
            Assert.Equal(HttpStatusCode.OK, createComponentResponse.StatusCode);

            componentId = createdComponent.RootElement.GetProperty("data").GetProperty("id").GetInt64();

            var createSuitePayload = new
            {
                details = new
                {
                    title = $"Component Backfill Suite {Guid.NewGuid():N}",
                    test_state_id = 1,
                    test_suite_type = 1,
                    project_id = projectId,
                    tags = Array.Empty<string>()
                },
                designed_components = new object[]
                {
                    new
                    {
                        component_id = componentId,
                        project_id = projectId,
                        status = true,
                        datasets = new object[]
                        {
                            new
                            {
                                scenario = "Primary Dataset",
                                status = true,
                                steps = Array.Empty<object>()
                            }
                        }
                    }
                }
            };

            var createSuiteResponse = await client.PostAsync("/api/test-suite", JsonContent(createSuitePayload));
            var createdSuite = await ReadJsonAsync(createSuiteResponse);
            Assert.Equal(HttpStatusCode.OK, createSuiteResponse.StatusCode);

            suiteId = createdSuite.RootElement.GetProperty("data").GetProperty("id").GetInt64();

            var staleStepId = await _factory.InsertComponentStepAsync(componentId, "Step 3", 2);

            var beforeBackfillResponse = await client.GetAsync($"/api/test-suite/{suiteId}/full");
            var beforeBackfill = await ReadJsonAsync(beforeBackfillResponse);
            Assert.Equal(HttpStatusCode.OK, beforeBackfillResponse.StatusCode);
            Assert.Equal(
                2,
                beforeBackfill.RootElement.GetProperty("data").GetProperty("components")[0].GetProperty("datasets")[0].GetProperty("steps").GetArrayLength());

            var backfillResponse = await client.PostAsync($"/api/components/sync-linked-tests?component_id={componentId}", EmptyJson());
            var backfillPayload = await ReadJsonAsync(backfillResponse);
            Assert.Equal(HttpStatusCode.OK, backfillResponse.StatusCode);
            Assert.Equal(1, backfillPayload.RootElement.GetProperty("data").GetProperty("count").GetInt32());

            var afterBackfillResponse = await client.GetAsync($"/api/test-suite/{suiteId}/full");
            var afterBackfill = await ReadJsonAsync(afterBackfillResponse);
            Assert.Equal(HttpStatusCode.OK, afterBackfillResponse.StatusCode);

            var datasetSteps = afterBackfill.RootElement
                .GetProperty("data")
                .GetProperty("components")[0]
                .GetProperty("datasets")[0]
                .GetProperty("steps")
                .EnumerateArray()
                .ToArray();

            Assert.Equal(3, datasetSteps.Length);
            var backfilledStep = datasetSteps.Single(step => step.GetProperty("step_id").GetInt64() == staleStepId);
            Assert.Equal(2, backfilledStep.GetProperty("display_id").GetInt32());
            Assert.Equal("skip", backfilledStep.GetProperty("value").GetString());
        }
        finally
        {
            if (suiteId > 0)
            {
                await _factory.HardDeleteTestSuiteTreeAsync(suiteId);
            }

            if (componentId > 0)
            {
                await _factory.HardDeleteComponentAsync(componentId);
            }

            if (projectId > 0)
            {
                await _factory.HardDeleteProjectAsync(projectId);
            }
        }
    }

    private static StringContent JsonContent(object value)
    {
        return new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
    }

    private static object[] BuildSingleStepPayload(string description)
    {
        return
        [
            new
            {
                description,
                expected_output = "",
                keyword_ref = "",
                before_step = Array.Empty<string>(),
                after_step = Array.Empty<string>(),
                display_id = 0
            }
        ];
    }

    private static async Task<long> CreateComponentAsync(HttpClient client, long projectId, string name, string page, string feature)
    {
        var payload = new
        {
            name,
            project_id = projectId,
            page,
            feature,
            type_id = 1,
            steps = BuildSingleStepPayload($"{name} step")
        };

        var response = await client.PostAsync("/api/components", JsonContent(payload));
        var created = await ReadJsonAsync(response);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return created.RootElement.GetProperty("data").GetProperty("id").GetInt64();
    }

    private static StringContent EmptyJson()
    {
        return new StringContent("{}", Encoding.UTF8, "application/json");
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(content);
    }
}
