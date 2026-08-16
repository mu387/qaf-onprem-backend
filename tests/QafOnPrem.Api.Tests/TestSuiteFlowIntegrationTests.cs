using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Xunit;

namespace QafOnPrem.Api.Tests;

public sealed class TestSuiteFlowIntegrationTests : IClassFixture<ProjectApiFactory>
{
    private const long ClientId = 11;
    private readonly ProjectApiFactory _factory;

    public TestSuiteFlowIntegrationTests(ProjectApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task UpdateFlow_AllowsComponentFromDifferentProjectThanTest()
    {
        long testProjectId = 0;
        long componentProjectId = 0;
        long componentId = 0;
        long suiteId = 0;

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await _factory.CreateTokenAsync(ClientId));

        try
        {
            testProjectId = await _factory.InsertProjectAsync(ClientId, $"Cross Project Test {Guid.NewGuid():N}");
            componentProjectId = await _factory.InsertProjectAsync(ClientId, $"Cross Project Component {Guid.NewGuid():N}");
            componentId = await _factory.InsertComponentAsync(ClientId, componentProjectId, $"Reusable Component {Guid.NewGuid():N}");

            var createSuitePayload = new
            {
                details = new
                {
                    title = $"Cross Project Suite {Guid.NewGuid():N}",
                    test_state_id = 1,
                    test_suite_type = 1,
                    project_id = testProjectId,
                    tags = Array.Empty<string>()
                },
                designed_components = Array.Empty<object>(),
                datasets = Array.Empty<object>()
            };

            var createSuiteResponse = await client.PostAsync("/api/test-suite", JsonContent(createSuitePayload));
            var createdSuite = await ReadJsonAsync(createSuiteResponse);
            Assert.Equal(HttpStatusCode.OK, createSuiteResponse.StatusCode);

            suiteId = createdSuite.RootElement.GetProperty("data").GetProperty("id").GetInt64();

            var flowPayload = new
            {
                components = new object[]
                {
                    new
                    {
                        client_key = "cross-project-component",
                        component_id = componentId,
                        project_id = componentProjectId,
                        sort_order = 1
                    }
                }
            };

            var flowResponse = await client.PutAsync($"/api/test-suite/{suiteId}/flow", JsonContent(flowPayload));
            var flowResult = await ReadJsonAsync(flowResponse);

            Assert.Equal(HttpStatusCode.OK, flowResponse.StatusCode);

            var savedComponent = Assert.Single(flowResult.RootElement.GetProperty("data").EnumerateArray());
            Assert.Equal(componentId, savedComponent.GetProperty("component_id").GetInt64());
            Assert.Equal(componentProjectId, savedComponent.GetProperty("project_id").GetInt64());
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

            if (componentProjectId > 0)
            {
                await _factory.HardDeleteProjectAsync(componentProjectId);
            }

            if (testProjectId > 0)
            {
                await _factory.HardDeleteProjectAsync(testProjectId);
            }
        }
    }

    [Fact]
    public async Task UpdateFlow_RemoveCopiedCrossProjectComponent_ClearsSuiteComponents()
    {
        long testProjectId = 0;
        long componentProjectId = 0;
        long componentId = 0;
        long suiteId = 0;

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await _factory.CreateTokenAsync(ClientId));

        try
        {
            testProjectId = await _factory.InsertProjectAsync(ClientId, $"Cross Project Remove Test {Guid.NewGuid():N}");
            componentProjectId = await _factory.InsertProjectAsync(ClientId, $"Cross Project Remove Component {Guid.NewGuid():N}");
            componentId = await _factory.InsertComponentAsync(ClientId, componentProjectId, $"Reusable Remove Component {Guid.NewGuid():N}");

            var createSuitePayload = new
            {
                details = new
                {
                    title = $"Cross Project Remove Suite {Guid.NewGuid():N}",
                    test_state_id = 1,
                    test_suite_type = 1,
                    project_id = testProjectId,
                    tags = Array.Empty<string>()
                },
                designed_components = Array.Empty<object>(),
                datasets = Array.Empty<object>()
            };

            var createSuiteResponse = await client.PostAsync("/api/test-suite", JsonContent(createSuitePayload));
            var createdSuite = await ReadJsonAsync(createSuiteResponse);
            Assert.Equal(HttpStatusCode.OK, createSuiteResponse.StatusCode);

            suiteId = createdSuite.RootElement.GetProperty("data").GetProperty("id").GetInt64();

            var addFlowPayload = new
            {
                components = new object[]
                {
                    new
                    {
                        client_key = "cross-project-component",
                        component_id = componentId,
                        project_id = componentProjectId,
                        sort_order = 1
                    }
                }
            };

            var addFlowResponse = await client.PutAsync($"/api/test-suite/{suiteId}/flow", JsonContent(addFlowPayload));
            var addFlowResult = await ReadJsonAsync(addFlowResponse);

            Assert.Equal(HttpStatusCode.OK, addFlowResponse.StatusCode);
            Assert.Single(addFlowResult.RootElement.GetProperty("data").EnumerateArray());

            var removeFlowPayload = new
            {
                components = Array.Empty<object>()
            };

            var removeFlowResponse = await client.PutAsync($"/api/test-suite/{suiteId}/flow", JsonContent(removeFlowPayload));
            var removeFlowResult = await ReadJsonAsync(removeFlowResponse);

            Assert.Equal(HttpStatusCode.OK, removeFlowResponse.StatusCode);
            Assert.Empty(removeFlowResult.RootElement.GetProperty("data").EnumerateArray());

            var suiteResponse = await client.GetAsync($"/api/test-suite/{suiteId}/full");
            var suitePayload = await ReadJsonAsync(suiteResponse);

            Assert.Equal(HttpStatusCode.OK, suiteResponse.StatusCode);
            Assert.Empty(suitePayload.RootElement.GetProperty("data").GetProperty("components").EnumerateArray());
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

            if (componentProjectId > 0)
            {
                await _factory.HardDeleteProjectAsync(componentProjectId);
            }

            if (testProjectId > 0)
            {
                await _factory.HardDeleteProjectAsync(testProjectId);
            }
        }
    }

    private static StringContent JsonContent(object value)
    {
        return new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(content);
    }
}
