using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Xunit;

namespace QafOnPrem.Api.Tests;

public sealed class UserApiIntegrationTests : IClassFixture<ProjectApiFactory>
{
    private const long ClientId = 11;
    private readonly ProjectApiFactory _factory;

    public UserApiIntegrationTests(ProjectApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task UserLifecycle_CreateGetUpdateDelete_WorksAgainstSqlServer()
    {
        var roleId = await _factory.InsertRoleAsync(ClientId, $"User Lifecycle Role {Guid.NewGuid():N}", ["Read User"]);
        var updatedRoleId = await _factory.InsertRoleAsync(ClientId, $"User Lifecycle Updated Role {Guid.NewGuid():N}", ["Update User"]);
        var emailBase = Guid.NewGuid().ToString("N");

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await _factory.CreateTokenAsync(ClientId));

        var createPayload = new
        {
            name = "Integration User Create",
            email = $"user-{emailBase}@example.com",
            password = "Password123!",
            password_confirmation = "Password123!",
            role_id = roleId
        };

        var createResponse = await client.PostAsync("/api/users", JsonContent(createPayload));
        var created = await ReadJsonAsync(createResponse);

        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);
        var userId = created.RootElement.GetProperty("data").GetProperty("id").GetInt64();

        try
        {
            var detailResponse = await client.GetAsync($"/api/users/{userId}");
            var detail = await ReadJsonAsync(detailResponse);

            Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
            Assert.Equal($"user-{emailBase}@example.com", detail.RootElement.GetProperty("data").GetProperty("email").GetString());

            var updatePayload = new
            {
                name = "Integration User Updated",
                email = $"updated-{emailBase}@example.com",
                role_id = updatedRoleId,
                is_active = false
            };

            var updateResponse = await client.PutAsync($"/api/users/{userId}", JsonContent(updatePayload));
            var updated = await ReadJsonAsync(updateResponse);

            Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
            Assert.Equal("Integration User Updated", updated.RootElement.GetProperty("data").GetProperty("name").GetString());
            Assert.Equal($"updated-{emailBase}@example.com", updated.RootElement.GetProperty("data").GetProperty("email").GetString());
            Assert.False(updated.RootElement.GetProperty("data").GetProperty("is_active").GetBoolean());
            Assert.Equal(updatedRoleId, updated.RootElement.GetProperty("data").GetProperty("roles")[0].GetProperty("id").GetInt64());

            var deleteResponse = await client.DeleteAsync($"/api/users/{userId}");
            Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
        }
        finally
        {
            await _factory.HardDeleteUserAsync(userId);
            await _factory.HardDeleteRoleAsync(roleId);
            await _factory.HardDeleteRoleAsync(updatedRoleId);
        }
    }

    [Fact]
    public async Task DeleteUser_ReturnsConflict_WhenUserIsAssignedToTestPlan()
    {
        var roleId = await _factory.InsertRoleAsync(ClientId, $"User Delete Guard Role {Guid.NewGuid():N}", ["Read User"]);
        var userId = await _factory.InsertUserAsync(ClientId, "Delete Guard User", $"guard-{Guid.NewGuid():N}@example.com", "Password123!", roleId);
        var testPlanId = await _factory.ResolveTestPlanIdAsync(ClientId);
        await _factory.InsertTestPlanUserAssignmentAsync(testPlanId, userId);

        try
        {
            using var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await _factory.CreateTokenAsync(ClientId));

            var response = await client.DeleteAsync($"/api/users/{userId}");
            var payload = await ReadJsonAsync(response);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("User cannot be deleted because it is linked to: test plans (assigned)", payload.RootElement.GetProperty("message").GetString());
        }
        finally
        {
            await _factory.DeleteTestPlanUserAssignmentsAsync(userId);
            await _factory.HardDeleteUserAsync(userId);
            await _factory.HardDeleteRoleAsync(roleId);
        }
    }

    [Fact]
    public async Task BulkDeleteUsers_SoftDeletesAllRequestedUsers()
    {
        var roleId = await _factory.InsertRoleAsync(ClientId, $"Bulk Delete Role {Guid.NewGuid():N}", ["Read User"]);
        var userId1 = await _factory.InsertUserAsync(ClientId, "Bulk Delete User One", $"bulk-one-{Guid.NewGuid():N}@example.com", "Password123!", roleId);
        var userId2 = await _factory.InsertUserAsync(ClientId, "Bulk Delete User Two", $"bulk-two-{Guid.NewGuid():N}@example.com", "Password123!", roleId);

        try
        {
            using var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await _factory.CreateTokenAsync(ClientId));

            var response = await client.PostAsync("/api/users/bulk-delete", JsonContent(new { user_ids = new[] { userId1, userId2 } }));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var userOneResponse = await client.GetAsync($"/api/users/{userId1}");
            var userTwoResponse = await client.GetAsync($"/api/users/{userId2}");

            Assert.Equal(HttpStatusCode.NotFound, userOneResponse.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, userTwoResponse.StatusCode);
        }
        finally
        {
            await _factory.HardDeleteUserAsync(userId1);
            await _factory.HardDeleteUserAsync(userId2);
            await _factory.HardDeleteRoleAsync(roleId);
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