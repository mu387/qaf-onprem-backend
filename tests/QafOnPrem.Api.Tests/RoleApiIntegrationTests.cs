using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Xunit;

namespace QafOnPrem.Api.Tests;

public sealed class RoleApiIntegrationTests : IClassFixture<ProjectApiFactory>
{
    private const long ClientId = 11;
    private readonly ProjectApiFactory _factory;

    public RoleApiIntegrationTests(ProjectApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task RoleLifecycle_CreateGetUpdateDelete_WorksAgainstSqlServer()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await _factory.CreateTokenAsync(ClientId));
        var roleBaseName = $"Integration Role {Guid.NewGuid():N}";

        var createPayload = new
        {
            name = $"{roleBaseName} Create",
            permissions = new[] { "Read Component", "Create Component" }
        };

        var createResponse = await client.PostAsync("/api/roles", JsonContent(createPayload));
        var created = await ReadJsonAsync(createResponse);

        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        Assert.Equal("Role Saved Successfully", created.RootElement.GetProperty("message").GetString());

        var roleId = created.RootElement.GetProperty("data").GetProperty("id").GetInt64();
        try
        {
            var detailResponse = await client.GetAsync($"/api/roles/{roleId}");
            var detail = await ReadJsonAsync(detailResponse);

            Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
            Assert.Equal($"{roleBaseName} Create", detail.RootElement.GetProperty("data").GetProperty("name").GetString());

            var updatePayload = new
            {
                name = $"{roleBaseName} Updated",
                permissions = new[] { "Read Component", "Update Component" }
            };

            var updateResponse = await client.PutAsync($"/api/roles/{roleId}", JsonContent(updatePayload));
            var updated = await ReadJsonAsync(updateResponse);

            Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
            Assert.Equal($"{roleBaseName} Updated", updated.RootElement.GetProperty("data").GetProperty("name").GetString());

            var permissionGroups = updated.RootElement.GetProperty("data").GetProperty("permissions");
            var componentPermissions = permissionGroups.EnumerateArray()
                .First(group => string.Equals(group.GetProperty("module").GetString(), "Components", StringComparison.Ordinal));
            var permissions = componentPermissions.GetProperty("permissions").EnumerateArray().Select(item => item.GetString()).ToArray();
            Assert.Contains("Read Component", permissions);
            Assert.Contains("Update Component", permissions);
            Assert.DoesNotContain("Create Component", permissions);

            var deleteResponse = await client.DeleteAsync($"/api/roles/{roleId}");
            Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
        }
        finally
        {
            await _factory.HardDeleteRoleAsync(roleId);
        }
    }

    [Fact]
    public async Task DeleteRole_ReturnsConflict_WhenRoleIsAssignedToUsers()
    {
        var roleId = await _factory.InsertRoleAsync(ClientId, $"Delete Guard Role {Guid.NewGuid():N}", ["Read Component"]);
        var userId = await _factory.ResolveUserIdAsync(ClientId);
        await _factory.AssignRoleToUserAsync(roleId, userId);

        try
        {
            using var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await _factory.CreateTokenAsync(ClientId));

            var response = await client.DeleteAsync($"/api/roles/{roleId}");
            var payload = await ReadJsonAsync(response);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("Role cannot be deleted because it is assigned to users.", payload.RootElement.GetProperty("message").GetString());
        }
        finally
        {
            await _factory.RemoveRoleAssignmentsAsync(roleId);
            await _factory.HardDeleteRoleAsync(roleId);
        }
    }

    [Fact]
    public async Task Permissions_ExcludesPlatformAndFolderCategories_ForClientScope()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await _factory.CreateTokenAsync(ClientId));

        var response = await client.GetAsync("/api/permissions");
        var payload = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var modules = payload.RootElement.GetProperty("data")
            .EnumerateArray()
            .Select(group => group.GetProperty("module").GetString())
            .Where(value => value is not null)
            .ToArray();

        Assert.Contains("Components", modules);
        Assert.DoesNotContain("Platform", modules);
        Assert.DoesNotContain("Folder", modules);
        Assert.DoesNotContain("Folders", modules);
    }

    [Fact]
    public async Task UpdateRole_AllowsClientRoleNameThatMatchesPlatformRole()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await _factory.CreateTokenAsync(ClientId));

        var response = await client.GetAsync("/api/roles/19");
        var role = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var permissionNames = role.RootElement.GetProperty("data").GetProperty("permissions")
            .EnumerateArray()
            .SelectMany(group => group.GetProperty("permissions").EnumerateArray())
            .Select(value => value.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();

        var updatePayload = new
        {
            name = "QA Analyst",
            permissions = permissionNames
        };

        var updateResponse = await client.PutAsync("/api/roles/19", JsonContent(updatePayload));
        var updated = await ReadJsonAsync(updateResponse);

        Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
        Assert.Equal("QA Analyst", updated.RootElement.GetProperty("data").GetProperty("name").GetString());
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