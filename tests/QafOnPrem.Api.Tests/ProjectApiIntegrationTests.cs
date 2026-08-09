using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace QafOnPrem.Api.Tests;

public sealed class ProjectApiIntegrationTests : IClassFixture<ProjectApiFactory>
{
    private const long ClientId = 11;
    private readonly ProjectApiFactory _factory;

    public ProjectApiIntegrationTests(ProjectApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ProjectLifecycle_CreateGetUpdateDelete_WorksAgainstSqlServer()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await _factory.CreateTokenAsync(ClientId));

        var createPayload = new
        {
            project_name = "Integration Project Create",
            description = "Create description",
            area_path = "Area/Integration",
            type_id = 2,
            version = "1.0",
            primary_test_management = "Azure",
            primary_ticketing_system = "Azure"
        };

        var createResponse = await client.PostAsync("/api/projects", JsonContent(createPayload));
        var created = await ReadJsonAsync(createResponse);
        Assert.Equal(HttpStatusCode.OK, createResponse.StatusCode);

        var projectId = created.RootElement.GetProperty("data").GetProperty("id").GetInt64();
        try
        {
            var detailResponse = await client.GetAsync($"/api/projects/{projectId}");
            var detail = await ReadJsonAsync(detailResponse);
            Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
            Assert.Equal("Integration Project Create", detail.RootElement.GetProperty("data").GetProperty("project_name").GetString());

            var updatePayload = new
            {
                project_name = "Integration Project Updated",
                description = "Updated integration description",
                area_path = "Area/Updated",
                type_id = 3,
                version = "2.0",
                primary_test_management = "Azure",
                primary_ticketing_system = (string?)null
            };

            var updateResponse = await client.PatchAsync($"/api/projects/{projectId}", JsonContent(updatePayload));
            var updated = await ReadJsonAsync(updateResponse);
            Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
            Assert.Equal("Integration Project Updated", updated.RootElement.GetProperty("data").GetProperty("project_name").GetString());
            Assert.Equal("Updated integration description", updated.RootElement.GetProperty("data").GetProperty("description").GetString());
            Assert.Equal("Area/Updated", updated.RootElement.GetProperty("data").GetProperty("area_path").GetString());
            Assert.Equal(3, updated.RootElement.GetProperty("data").GetProperty("type_id").GetInt32());
            Assert.Equal("2.0", updated.RootElement.GetProperty("data").GetProperty("version").GetString());

            var deleteResponse = await client.DeleteAsync($"/api/projects/{projectId}");
            Assert.Equal(HttpStatusCode.OK, deleteResponse.StatusCode);
        }
        finally
        {
            await _factory.HardDeleteProjectAsync(projectId);
        }
    }

    [Fact]
    public async Task DeleteProject_ReturnsConflict_WhenProjectHasAttachedComponents()
    {
        var projectId = await _factory.InsertProjectAsync(ClientId, "Delete Guard Project");
        var componentId = await _factory.InsertComponentAsync(ClientId, projectId, "Attached Component");

        try
        {
            using var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await _factory.CreateTokenAsync(ClientId));

            var response = await client.DeleteAsync($"/api/projects/{projectId}");
            var payload = await ReadJsonAsync(response);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("You Can't Delete Project, as it is being associated with Component", payload.RootElement.GetProperty("message").GetString());
        }
        finally
        {
            await _factory.HardDeleteComponentAsync(componentId);
            await _factory.HardDeleteProjectAsync(projectId);
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

public sealed class ProjectApiFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly string _jwtIssuer;
    private readonly string _jwtAudience;
    private readonly string _jwtSigningKey;

    public ProjectApiFactory()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(GetApiProjectPath())
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: false)
            .Build();

        _connectionString = config.GetConnectionString("SqlServer") ?? throw new InvalidOperationException("SqlServer connection string is missing.");
        _jwtIssuer = config["Jwt:Issuer"] ?? throw new InvalidOperationException("Jwt issuer is missing.");
        _jwtAudience = config["Jwt:Audience"] ?? throw new InvalidOperationException("Jwt audience is missing.");
        _jwtSigningKey = config["Jwt:SigningKey"] ?? throw new InvalidOperationException("Jwt signing key is missing.");
    }

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
    }

    public async Task<string> CreateTokenAsync(long clientId)
    {
        var userId = await ResolveUserIdAsync(clientId);
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var now = DateTimeOffset.UtcNow;
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new(JwtRegisteredClaimNames.Email, "integration@example.com"),
            new(ClaimTypes.Name, "Integration User"),
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new("client_id", clientId.ToString()),
            new("is_client", "1"),
            new("client_status", "active"),
            new(ClaimTypes.Role, "Client Owner")
        };

        var token = new JwtSecurityToken(
            issuer: _jwtIssuer,
            audience: _jwtAudience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: now.AddHours(1).UtcDateTime,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public async Task<long> InsertProjectAsync(long clientId, string projectName)
    {
        var userId = await ResolveUserIdAsync(clientId);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        const string sql = """
            INSERT INTO projects
            (
                client_id,
                project_name,
                description,
                status,
                type_id,
                created_by_id,
                updated_by_id,
                created_by,
                updated_by,
                version,
                created_at,
                updated_at
            )
            OUTPUT INSERTED.id
            VALUES
            (
                @clientId,
                @projectName,
                @description,
                1,
                1,
                @userId,
                @userId,
                'Integration User',
                'Integration User',
                '1.0',
                SYSUTCDATETIME(),
                SYSUTCDATETIME()
            );
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@clientId", clientId);
        command.Parameters.AddWithValue("@projectName", projectName);
        command.Parameters.AddWithValue("@description", $"{projectName} description");
        command.Parameters.AddWithValue("@userId", userId);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public async Task<long> InsertComponentAsync(long clientId, long projectId, string componentName)
    {
        var userId = await ResolveUserIdAsync(clientId);
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        const string sql = """
            INSERT INTO components
            (
                project_id,
                client_id,
                name,
                type_id,
                page,
                feature,
                created_by,
                created_by_id,
                updated_by,
                updated_by_id,
                locked,
                status,
                created_at,
                updated_at
            )
            OUTPUT INSERTED.id
            VALUES
            (
                @projectId,
                @clientId,
                @name,
                1,
                'Integration',
                'Delete Guard',
                'Integration User',
                @userId,
                'Integration User',
                @userId,
                0,
                1,
                SYSUTCDATETIME(),
                SYSUTCDATETIME()
            );
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@projectId", projectId);
        command.Parameters.AddWithValue("@clientId", clientId);
        command.Parameters.AddWithValue("@name", componentName);
        command.Parameters.AddWithValue("@userId", userId);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public async Task HardDeleteComponentAsync(long id)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using (var deleteSteps = new SqlCommand("DELETE FROM component_steps WHERE component_id = @id;", connection))
        {
            deleteSteps.Parameters.AddWithValue("@id", id);
            await deleteSteps.ExecuteNonQueryAsync();
        }

        await using var command = new SqlCommand("DELETE FROM components WHERE id = @id;", connection);
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<long> InsertComponentStepAsync(long componentId, string description, int displayId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        const string sql = """
            INSERT INTO component_steps
            (
                component_id,
                description,
                expected_output,
                before_step,
                keyword_id,
                global_keyword_id,
                brpg_obj,
                object_string,
                xpath,
                after_step,
                display_id,
                created_at,
                updated_at
            )
            OUTPUT INSERTED.id
            VALUES
            (
                @componentId,
                @description,
                NULL,
                NULL,
                NULL,
                NULL,
                NULL,
                NULL,
                NULL,
                NULL,
                @displayId,
                SYSUTCDATETIME(),
                SYSUTCDATETIME()
            );
            """;

        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@componentId", componentId);
        command.Parameters.AddWithValue("@description", description);
        command.Parameters.AddWithValue("@displayId", displayId);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public async Task HardDeleteTestSuiteTreeAsync(long testSuiteId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            foreach (var sql in new[]
                     {
                         "DELETE FROM data_set_steps WHERE dataset_id IN (SELECT ds.id FROM data_sets ds INNER JOIN test_components tc ON tc.id = ds.test_component_id WHERE tc.test_design_id = @testSuiteId);",
                         "DELETE FROM data_sets WHERE test_component_id IN (SELECT id FROM test_components WHERE test_design_id = @testSuiteId);",
                         "DELETE FROM test_components WHERE test_design_id = @testSuiteId;",
                         "DELETE FROM test_designs WHERE id = @testSuiteId;"
                     })
            {
                await using var command = new SqlCommand(sql, connection, (SqlTransaction)transaction);
                command.Parameters.AddWithValue("@testSuiteId", testSuiteId);
                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task HardDeleteProjectAsync(long id)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("DELETE FROM projects WHERE id = @id;", connection);
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<long> InsertRoleAsync(long clientId, string roleName, IReadOnlyList<string> permissionNames)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            const string insertRoleSql = """
                INSERT INTO roles
                (
                    name,
                    guard_name,
                    client_id,
                    created_at,
                    updated_at
                )
                OUTPUT INSERTED.id
                VALUES
                (
                    @name,
                    'api',
                    @clientId,
                    SYSUTCDATETIME(),
                    SYSUTCDATETIME()
                );
                """;

            long roleId;
            await using (var insertRole = new SqlCommand(insertRoleSql, connection, (SqlTransaction)transaction))
            {
                insertRole.Parameters.AddWithValue("@name", roleName);
                insertRole.Parameters.AddWithValue("@clientId", clientId);
                roleId = Convert.ToInt64(await insertRole.ExecuteScalarAsync());
            }

            if (permissionNames.Count > 0)
            {
                var permissionIds = await ResolvePermissionIdsAsync(connection, (SqlTransaction)transaction, permissionNames);
                foreach (var permissionId in permissionIds)
                {
                    await using var insertPermission = new SqlCommand("INSERT INTO role_has_permissions (permission_id, role_id) VALUES (@permissionId, @roleId);", connection, (SqlTransaction)transaction);
                    insertPermission.Parameters.AddWithValue("@permissionId", permissionId);
                    insertPermission.Parameters.AddWithValue("@roleId", roleId);
                    await insertPermission.ExecuteNonQueryAsync();
                }
            }

            await transaction.CommitAsync();
            return roleId;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task AssignRoleToUserAsync(long roleId, long userId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("INSERT INTO model_has_roles (role_id, model_type, model_id) VALUES (@roleId, @modelType, @userId);", connection);
        command.Parameters.AddWithValue("@roleId", roleId);
        command.Parameters.AddWithValue("@modelType", "App\\Models\\User");
        command.Parameters.AddWithValue("@userId", userId);
        await command.ExecuteNonQueryAsync();
    }

    public async Task RemoveRoleAssignmentsAsync(long roleId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("DELETE FROM model_has_roles WHERE role_id = @roleId;", connection);
        command.Parameters.AddWithValue("@roleId", roleId);
        await command.ExecuteNonQueryAsync();
    }

    public async Task HardDeleteRoleAsync(long roleId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            foreach (var sql in new[]
                     {
                         "DELETE FROM model_has_roles WHERE role_id = @roleId;",
                         "DELETE FROM role_has_permissions WHERE role_id = @roleId;",
                         "DELETE FROM roles WHERE id = @roleId;"
                     })
            {
                await using var command = new SqlCommand(sql, connection, (SqlTransaction)transaction);
                command.Parameters.AddWithValue("@roleId", roleId);
                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task<long> InsertUserAsync(long clientId, string name, string email, string password, long roleId, bool isActive = true)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            const string insertUserSql = """
                INSERT INTO users
                (
                    name,
                    email,
                    password,
                    client_id,
                    is_client,
                    is_active,
                    mfa_enabled,
                    sso_enabled,
                    must_reset_password,
                    created_at,
                    updated_at
                )
                OUTPUT INSERTED.id
                VALUES
                (
                    @name,
                    @email,
                    @password,
                    @clientId,
                    0,
                    @isActive,
                    0,
                    0,
                    0,
                    SYSUTCDATETIME(),
                    SYSUTCDATETIME()
                );
                """;

            long userId;
            await using (var insertUser = new SqlCommand(insertUserSql, connection, (SqlTransaction)transaction))
            {
                insertUser.Parameters.AddWithValue("@name", name);
                insertUser.Parameters.AddWithValue("@email", email);
                insertUser.Parameters.AddWithValue("@password", BCrypt.Net.BCrypt.HashPassword(password));
                insertUser.Parameters.AddWithValue("@clientId", clientId);
                insertUser.Parameters.AddWithValue("@isActive", isActive);
                userId = Convert.ToInt64(await insertUser.ExecuteScalarAsync());
            }

            await using (var insertRole = new SqlCommand("INSERT INTO model_has_roles (role_id, model_type, model_id) VALUES (@roleId, @modelType, @userId);", connection, (SqlTransaction)transaction))
            {
                insertRole.Parameters.AddWithValue("@roleId", roleId);
                insertRole.Parameters.AddWithValue("@modelType", "App\\Models\\User");
                insertRole.Parameters.AddWithValue("@userId", userId);
                await insertRole.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
            return userId;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task HardDeleteUserAsync(long userId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        try
        {
            foreach (var sql in new[]
                     {
                         "DELETE FROM test_plan_users WHERE user_id = @userId;",
                         "DELETE FROM test_plan_item_suite_users WHERE user_id = @userId;",
                         "DELETE FROM model_has_roles WHERE model_id = @userId;",
                         "DELETE FROM model_has_permissions WHERE model_id = @userId;",
                         "DELETE FROM user_settings WHERE user_id = @userId;",
                         "DELETE FROM users WHERE id = @userId;"
                     })
            {
                await using var command = new SqlCommand(sql, connection, (SqlTransaction)transaction);
                command.Parameters.AddWithValue("@userId", userId);
                await command.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task<long> ResolveTestPlanIdAsync(long clientId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT TOP 1 id FROM test_plans WHERE client_id = @clientId ORDER BY id;", connection);
        command.Parameters.AddWithValue("@clientId", clientId);
        var value = await command.ExecuteScalarAsync();
        if (value is null || value is DBNull)
        {
            throw new InvalidOperationException($"No test plan exists for client_id {clientId}.");
        }

        return Convert.ToInt64(value);
    }

    public async Task InsertTestPlanUserAssignmentAsync(long testPlanId, long userId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("INSERT INTO test_plan_users (test_plan_id, user_id, created_at, updated_at) VALUES (@testPlanId, @userId, SYSUTCDATETIME(), SYSUTCDATETIME());", connection);
        command.Parameters.AddWithValue("@testPlanId", testPlanId);
        command.Parameters.AddWithValue("@userId", userId);
        await command.ExecuteNonQueryAsync();
    }

    public async Task DeleteTestPlanUserAssignmentsAsync(long userId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("DELETE FROM test_plan_users WHERE user_id = @userId;", connection);
        command.Parameters.AddWithValue("@userId", userId);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<long> ResolveUserIdAsync(long clientId)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT TOP 1 id FROM users WHERE client_id = @clientId AND deleted_at IS NULL ORDER BY id;", connection);
        command.Parameters.AddWithValue("@clientId", clientId);
        var value = await command.ExecuteScalarAsync();
        if (value is null || value is DBNull)
        {
            throw new InvalidOperationException($"No user exists for client_id {clientId}.");
        }

        return Convert.ToInt64(value);
    }

    private static async Task<IReadOnlyList<long>> ResolvePermissionIdsAsync(SqlConnection connection, SqlTransaction transaction, IReadOnlyList<string> permissionNames)
    {
        var parameterNames = new List<string>(permissionNames.Count);
        await using var command = new SqlCommand(string.Empty, connection, transaction);
        for (var index = 0; index < permissionNames.Count; index++)
        {
            var parameterName = $"@permission{index}";
            parameterNames.Add(parameterName);
            command.Parameters.AddWithValue(parameterName, permissionNames[index]);
        }

        command.CommandText = $"SELECT id FROM permissions WHERE name IN ({string.Join(", ", parameterNames)});";
        await using var reader = await command.ExecuteReaderAsync();
        var ids = new List<long>();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt64(reader.GetOrdinal("id")));
        }

        return ids;
    }

    private static string GetApiProjectPath()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "src", "QafOnPrem.Api");
            if (File.Exists(Path.Combine(candidate, "appsettings.json")))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate src/QafOnPrem.Api from the test output directory.");
    }
}