using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Immutable;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using QafOnPrem.Api.Services;
using QafOnPrem.Api.Configuration;
using QafOnPrem.Api.Contracts;
using QafOnPrem.Api.Contracts.Auth;

namespace QafOnPrem.Api.Services.AppData;

public sealed partial class SqlAppDataService(
    IConfiguration configuration,
    IOptions<SqlIdentitySettings> settings,
    ITestSuiteEditSessionService editSessionService,
    ILogger<SqlAppDataService> logger) : ISqlAppDataService
{
    private static readonly (long Id, string Name)[] BuiltInBrowserKeywords =
    [
        (-1001, "launchDebugBrowser"),
        (-1002, "connectBrowser")
    ];

    private readonly string _connectionString = configuration.GetConnectionString("SqlServer") ?? string.Empty;
    private readonly SqlIdentitySettings _settings = settings.Value;
    private readonly ITestSuiteEditSessionService _editSessionService = editSessionService;
    private readonly ILogger<SqlAppDataService> _logger = logger;
    private bool? _hasIntegrationLinksTable;
    private bool? _hasDataSetSortOrderColumn;
    private bool? _hasTestComponentSortOrderColumn;
    private static readonly JsonSerializerOptions AppJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<IReadOnlyList<PermissionGroupDto>> GetPermissionsAsync(ClaimsPrincipal principal, string? scope, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);

        var filters = new List<string>();
        var parameters = new List<SqlParameter>();
        if (string.Equals(scope?.Trim(), "platform", StringComparison.OrdinalIgnoreCase))
        {
            filters.Add("p.category = @platformCategory");
            parameters.Add(new SqlParameter("@platformCategory", "Platform"));
        }
        else
        {
            filters.Add("p.category NOT IN ('Platform', 'Folder', 'Folders')");
        }

        var sql = $"""
            SELECT p.category, p.name
            FROM permissions p
            WHERE {string.Join(" AND ", filters)}
            ORDER BY p.category, p.name;
            """;

        await using var command = CreateCommand(connection, sql);
        AddParameters(command, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        return await ReadPermissionGroupsAsync(reader, cancellationToken);
    }

    public async Task<PagedDataDto<RoleListItemDto>> GetRolesAsync(ClaimsPrincipal principal, string? query, int page, int limit, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        await using var connection = await OpenConnectionAsync(cancellationToken);

        var whereClauses = new List<string> { "r.name <> 'client'" };
        var parameters = new List<SqlParameter>();
        ApplyRoleScope(context, whereClauses, parameters);

        if (!string.IsNullOrWhiteSpace(query))
        {
            whereClauses.Add("r.name LIKE @query");
            parameters.Add(new SqlParameter("@query", $"%{query.Trim()}%"));
        }

        var whereSql = string.Join(" AND ", whereClauses);
        var total = await ExecuteCountAsync(connection, $"SELECT COUNT(*) FROM roles r WHERE {whereSql};", parameters, cancellationToken);

        var sql = $"""
            SELECT r.id, r.name
            FROM roles r
            WHERE {whereSql}
            ORDER BY r.name
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
            """;

        var rows = new List<RoleListItemDto>();
        await using var command = CreateCommand(connection, sql);
        AddParameters(command, parameters);
        command.Parameters.AddWithValue("@offset", (page - 1) * limit);
        command.Parameters.AddWithValue("@limit", limit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new RoleListItemDto
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                Name = reader.GetString(reader.GetOrdinal("name"))
            });
        }

        return CreatePagedData(rows, total, limit);
    }

    public async Task<RoleDetailDto?> GetRoleAsync(ClaimsPrincipal principal, long roleId, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        await using var connection = await OpenConnectionAsync(cancellationToken);

        return await GetRoleDetailByIdAsync(connection, context, roleId, cancellationToken);
    }

    public async Task<SaveRoleResult> CreateRoleAsync(ClaimsPrincipal principal, SaveRoleRequest request, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        var requestedName = NormalizeOptionalText(request.Name);
        if (string.IsNullOrWhiteSpace(requestedName))
        {
            return new SaveRoleResult
            {
                Outcome = SaveRoleOutcome.DuplicateName,
                ErrorMessage = "The role name has already been taken."
            };
        }

        var roleName = RoleRules.NormalizeCreatedRoleName(requestedName, !context.ClientId.HasValue);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        if (await RoleNameExistsAsync(connection, context, roleName, null, cancellationToken))
        {
            return new SaveRoleResult
            {
                Outcome = SaveRoleOutcome.DuplicateName,
                ErrorMessage = "The role name has already been taken."
            };
        }

        var permissionIds = await ResolvePermissionIdsAsync(connection, request.Permissions, cancellationToken);
        if (permissionIds is null)
        {
            return new SaveRoleResult
            {
                Outcome = SaveRoleOutcome.InvalidPermissions,
                ErrorMessage = "One or more selected permissions are invalid."
            };
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
    var committed = false;
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
            await using (var command = CreateCommand(connection, insertRoleSql))
            {
                command.Transaction = (SqlTransaction)transaction;
                command.Parameters.AddWithValue("@name", roleName);
                command.Parameters.AddWithValue("@clientId", (object?)context.ClientId ?? DBNull.Value);
                roleId = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
            }

            await ReplaceRolePermissionsAsync(connection, (SqlTransaction)transaction, roleId, permissionIds, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            committed = true;

            return new SaveRoleResult
            {
                Outcome = SaveRoleOutcome.Saved,
                Role = await GetRoleDetailByIdAsync(connection, context, roleId, cancellationToken)
            };
        }
        catch (SqlException exception) when (IsUniqueConstraintViolation(exception))
        {
            if (!committed)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            return new SaveRoleResult
            {
                Outcome = SaveRoleOutcome.DuplicateName,
                ErrorMessage = "The role name has already been taken."
            };
        }
        catch
        {
            if (!committed)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            throw;
        }
    }

    public async Task<SaveRoleResult> UpdateRoleAsync(ClaimsPrincipal principal, long roleId, SaveRoleRequest request, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        var requestedName = NormalizeOptionalText(request.Name);
        if (string.IsNullOrWhiteSpace(requestedName))
        {
            return new SaveRoleResult
            {
                Outcome = SaveRoleOutcome.DuplicateName,
                ErrorMessage = "The role name has already been taken."
            };
        }

        var roleName = RoleRules.NormalizeCreatedRoleName(requestedName, !context.ClientId.HasValue);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        if (!await RoleBelongsToScopeAsync(connection, context, roleId, cancellationToken))
        {
            return new SaveRoleResult
            {
                Outcome = SaveRoleOutcome.NotFound,
                ErrorMessage = "Role not found"
            };
        }

        if (await RoleNameExistsAsync(connection, context, roleName, roleId, cancellationToken))
        {
            return new SaveRoleResult
            {
                Outcome = SaveRoleOutcome.DuplicateName,
                ErrorMessage = "The role name has already been taken."
            };
        }

        var permissionIds = await ResolvePermissionIdsAsync(connection, request.Permissions, cancellationToken);
        if (permissionIds is null)
        {
            return new SaveRoleResult
            {
                Outcome = SaveRoleOutcome.InvalidPermissions,
                ErrorMessage = "One or more selected permissions are invalid."
            };
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
    var committed = false;
        try
        {
            const string updateRoleSql = """
                UPDATE roles
                SET name = @name,
                    updated_at = SYSUTCDATETIME()
                WHERE id = @roleId;
                """;

            await using (var command = CreateCommand(connection, updateRoleSql))
            {
                command.Transaction = (SqlTransaction)transaction;
                command.Parameters.AddWithValue("@name", roleName);
                command.Parameters.AddWithValue("@roleId", roleId);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await ReplaceRolePermissionsAsync(connection, (SqlTransaction)transaction, roleId, permissionIds, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            committed = true;

            return new SaveRoleResult
            {
                Outcome = SaveRoleOutcome.Saved,
                Role = await GetRoleDetailByIdAsync(connection, context, roleId, cancellationToken)
            };
        }
        catch (SqlException exception) when (IsUniqueConstraintViolation(exception))
        {
            if (!committed)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            return new SaveRoleResult
            {
                Outcome = SaveRoleOutcome.DuplicateName,
                ErrorMessage = "The role name has already been taken."
            };
        }
        catch
        {
            if (!committed)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            throw;
        }
    }

    public async Task<RoleDeletionOutcome> DeleteRoleAsync(ClaimsPrincipal principal, long roleId, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        await using var connection = await OpenConnectionAsync(cancellationToken);

        var exists = await RoleBelongsToScopeAsync(connection, context, roleId, cancellationToken);
        var hasAssignedUsers = exists && await RoleHasAssignedUsersAsync(connection, roleId, cancellationToken);
        var outcome = RoleRules.EvaluateDeletion(exists, hasAssignedUsers);
        if (outcome != RoleDeletionOutcome.Deleted)
        {
            return outcome;
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
    var committed = false;
        try
        {
            await using (var deletePermissions = CreateCommand(connection, "DELETE FROM role_has_permissions WHERE role_id = @roleId;"))
            {
                deletePermissions.Transaction = (SqlTransaction)transaction;
                deletePermissions.Parameters.AddWithValue("@roleId", roleId);
                await deletePermissions.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var deleteRole = CreateCommand(connection, "DELETE FROM roles WHERE id = @roleId;"))
            {
                deleteRole.Transaction = (SqlTransaction)transaction;
                deleteRole.Parameters.AddWithValue("@roleId", roleId);
                await deleteRole.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            committed = true;
            return RoleDeletionOutcome.Deleted;
        }
        catch
        {
            if (!committed)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            throw;
        }
    }

    public async Task<PagedDataDto<UserListItemDto>> GetUsersAsync(ClaimsPrincipal principal, string? query, string? email, long? roleId, int page, int limit, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return CreatePagedData<UserListItemDto>([], 0, limit);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var whereClauses = new List<string>
        {
            "u.deleted_at IS NULL",
            "u.id <> @currentUserId",
            "u.client_id = @clientId",
            "ISNULL(u.is_client, 0) = 0"
        };
        var parameters = new List<SqlParameter>
        {
            new("@currentUserId", context.UserId),
            new("@clientId", context.ClientId.Value),
            new("@modelType", _settings.UserModelType)
        };

        if (!string.IsNullOrWhiteSpace(query))
        {
            whereClauses.Add("(u.name LIKE @query OR u.email LIKE @query OR CAST(u.id AS nvarchar(50)) LIKE @query)");
            parameters.Add(new SqlParameter("@query", $"%{query.Trim()}%"));
        }

        if (!string.IsNullOrWhiteSpace(email))
        {
            whereClauses.Add("u.email LIKE @email");
            parameters.Add(new SqlParameter("@email", $"%{email.Trim()}%"));
        }

        if (roleId.HasValue)
        {
            whereClauses.Add("EXISTS (SELECT 1 FROM model_has_roles mhr WHERE mhr.role_id = @roleId AND REPLACE(mhr.model_type, '\\', '') = REPLACE(@modelType, '\\', '') AND mhr.model_id = u.id)");
            parameters.Add(new SqlParameter("@roleId", roleId.Value));
        }

        var whereSql = string.Join(" AND ", whereClauses);
        var total = await ExecuteCountAsync(connection, $"SELECT COUNT(*) FROM users u WHERE {whereSql};", parameters, cancellationToken);

        var sql = $"""
            SELECT u.id, u.name, u.email, CAST(ISNULL(u.is_active, 1) AS bit) AS is_active
            FROM users u
            WHERE {whereSql}
            ORDER BY u.updated_at DESC, u.id DESC
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
            """;

        var users = new List<UserListItemDto>();
        await using (var command = CreateCommand(connection, sql))
        {
            AddParameters(command, parameters);
            command.Parameters.AddWithValue("@offset", (page - 1) * limit);
            command.Parameters.AddWithValue("@limit", limit);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                users.Add(new UserListItemDto
                {
                    Id = reader.GetInt64(reader.GetOrdinal("id")),
                    Name = GetString(reader, "name") ?? string.Empty,
                    Email = GetString(reader, "email") ?? string.Empty,
                    IsActive = GetBoolean(reader, "is_active") ?? true,
                });
            }
        }

        var userRoles = await LoadUserRolesAsync(connection, users.Select(user => user.Id).ToArray(), cancellationToken);
        var hydrated = users
            .Select(user => new UserListItemDto
            {
                Id = user.Id,
                Name = user.Name,
                Email = user.Email,
                IsActive = user.IsActive,
                Roles = userRoles.TryGetValue(user.Id, out var roles) ? roles : []
            })
            .ToList();

        return CreatePagedData(hydrated, total, limit);
    }

    public async Task<UserDetailDto?> GetUserAsync(ClaimsPrincipal principal, long userId, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT TOP 1 u.id, u.name, u.email, u.phone, u.job_title, u.department, u.timezone, CAST(ISNULL(u.is_active, 1) AS bit) AS is_active
            FROM users u
            WHERE u.id = @userId AND u.client_id = @clientId AND u.deleted_at IS NULL;
            """;

        long detailId;
        string detailName;
        string detailEmail;
        string? detailPhone;
        string? detailJobTitle;
        string? detailDepartment;
        string? detailTimezone;
        bool detailIsActive;
        await using (var command = CreateCommand(connection, sql))
        {
            command.Parameters.AddWithValue("@userId", userId);
            command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            detailId = reader.GetInt64(reader.GetOrdinal("id"));
            detailName = GetString(reader, "name") ?? string.Empty;
            detailEmail = GetString(reader, "email") ?? string.Empty;
            detailPhone = GetString(reader, "phone");
            detailJobTitle = GetString(reader, "job_title");
            detailDepartment = GetString(reader, "department");
            detailTimezone = GetString(reader, "timezone");
            detailIsActive = GetBoolean(reader, "is_active") ?? true;
        }

        var roles = await LoadUserRolesAsync(connection, [userId], cancellationToken);
        return new UserDetailDto
        {
            Id = detailId,
            Name = detailName,
            Email = detailEmail,
            Phone = detailPhone,
            JobTitle = detailJobTitle,
            Department = detailDepartment,
            Timezone = detailTimezone,
            IsActive = detailIsActive,
            Roles = roles.TryGetValue(userId, out var userRoles) ? userRoles : []
        };
    }

    public async Task<SaveUserResult> CreateUserAsync(ClaimsPrincipal principal, SaveUserRequest request, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return new SaveUserResult { Outcome = SaveUserOutcome.NotFound, ErrorMessage = "User not found" };
        }

        var name = NormalizeOptionalText(request.Name);
        var email = NormalizeOptionalText(request.Email);
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email) || !request.RoleId.HasValue)
        {
            return new SaveUserResult { Outcome = SaveUserOutcome.InvalidRole, ErrorMessage = "The selected role is invalid." };
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        if (!await RoleBelongsToClientAsync(connection, context.ClientId.Value, request.RoleId.Value, cancellationToken))
        {
            return new SaveUserResult { Outcome = SaveUserOutcome.InvalidRole, ErrorMessage = "The selected role is invalid." };
        }

        if (await UserEmailExistsAsync(connection, email, null, cancellationToken))
        {
            return new SaveUserResult { Outcome = SaveUserOutcome.DuplicateEmail, ErrorMessage = "The email has already been taken." };
        }

        var activeCount = await CountActiveClientUsersAsync(connection, context.ClientId.Value, cancellationToken);
        var maxUsers = await GetClientMaxUsersAsync(connection, context.ClientId.Value, cancellationToken);
        if (!UserRules.CanAddActiveUser(activeCount, maxUsers))
        {
            return new SaveUserResult { Outcome = SaveUserOutcome.UserLimitReached, ErrorMessage = "User limit reached for this client." };
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var committed = false;
        try
        {
            const string insertSql = """
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
                    1,
                    0,
                    0,
                    0,
                    SYSUTCDATETIME(),
                    SYSUTCDATETIME()
                );
                """;

            long userId;
            await using (var command = CreateCommand(connection, insertSql))
            {
                command.Transaction = (SqlTransaction)transaction;
                command.Parameters.AddWithValue("@name", name);
                command.Parameters.AddWithValue("@email", email);
                command.Parameters.AddWithValue("@password", BCrypt.Net.BCrypt.HashPassword(request.Password));
                command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
                userId = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
            }

            await ReplaceUserRolesAsync(connection, (SqlTransaction)transaction, userId, request.RoleId.Value, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            committed = true;

            return new SaveUserResult
            {
                Outcome = SaveUserOutcome.Saved,
                User = await GetUserDetailByIdAsync(connection, context.ClientId.Value, userId, cancellationToken)
            };
        }
        catch (SqlException exception) when (IsUniqueConstraintViolation(exception))
        {
            if (!committed)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            return new SaveUserResult { Outcome = SaveUserOutcome.DuplicateEmail, ErrorMessage = "The email has already been taken." };
        }
        catch
        {
            if (!committed)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            throw;
        }
    }

    public async Task<SaveUserResult> UpdateUserAsync(ClaimsPrincipal principal, long userId, SaveUserRequest request, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return new SaveUserResult { Outcome = SaveUserOutcome.NotFound, ErrorMessage = "User not found" };
        }

        var name = NormalizeOptionalText(request.Name);
        var email = NormalizeOptionalText(request.Email);
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email) || !request.RoleId.HasValue)
        {
            return new SaveUserResult { Outcome = SaveUserOutcome.InvalidRole, ErrorMessage = "The selected role is invalid." };
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var existingUser = await GetUserRecordAsync(connection, context.ClientId.Value, userId, cancellationToken);
        if (existingUser is null)
        {
            return new SaveUserResult { Outcome = SaveUserOutcome.NotFound, ErrorMessage = "User not found" };
        }

        if (!await RoleBelongsToClientAsync(connection, context.ClientId.Value, request.RoleId.Value, cancellationToken))
        {
            return new SaveUserResult { Outcome = SaveUserOutcome.InvalidRole, ErrorMessage = "The selected role is invalid." };
        }

        if (await UserEmailExistsAsync(connection, email, userId, cancellationToken))
        {
            return new SaveUserResult { Outcome = SaveUserOutcome.DuplicateEmail, ErrorMessage = "The email has already been taken." };
        }

        var currentIsActive = existingUser.Value.IsActive;
        var nextIsActive = request.IsActive ?? currentIsActive;
        if (nextIsActive && !currentIsActive)
        {
            var activeCount = await CountActiveClientUsersAsync(connection, context.ClientId.Value, cancellationToken);
            var maxUsers = await GetClientMaxUsersAsync(connection, context.ClientId.Value, cancellationToken);
            if (!UserRules.CanAddActiveUser(activeCount, maxUsers))
            {
                return new SaveUserResult { Outcome = SaveUserOutcome.UserLimitReached, ErrorMessage = "User limit reached for this client." };
            }
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var committed = false;
        try
        {
            var updateSql = request.Password is { Length: > 0 }
                ? """
                    UPDATE users
                    SET name = @name,
                        email = @email,
                        is_active = @isActive,
                        password = @password,
                        updated_at = SYSUTCDATETIME()
                    WHERE id = @userId AND client_id = @clientId AND deleted_at IS NULL;
                    """
                : """
                    UPDATE users
                    SET name = @name,
                        email = @email,
                        is_active = @isActive,
                        updated_at = SYSUTCDATETIME()
                    WHERE id = @userId AND client_id = @clientId AND deleted_at IS NULL;
                    """;

            await using (var command = CreateCommand(connection, updateSql))
            {
                command.Transaction = (SqlTransaction)transaction;
                command.Parameters.AddWithValue("@name", name);
                command.Parameters.AddWithValue("@email", email);
                command.Parameters.AddWithValue("@isActive", nextIsActive);
                command.Parameters.AddWithValue("@userId", userId);
                command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
                if (request.Password is { Length: > 0 })
                {
                    command.Parameters.AddWithValue("@password", BCrypt.Net.BCrypt.HashPassword(request.Password));
                }

                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await ReplaceUserRolesAsync(connection, (SqlTransaction)transaction, userId, request.RoleId.Value, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            committed = true;

            return new SaveUserResult
            {
                Outcome = SaveUserOutcome.Saved,
                User = await GetUserDetailByIdAsync(connection, context.ClientId.Value, userId, cancellationToken)
            };
        }
        catch (SqlException exception) when (IsUniqueConstraintViolation(exception))
        {
            if (!committed)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            return new SaveUserResult { Outcome = SaveUserOutcome.DuplicateEmail, ErrorMessage = "The email has already been taken." };
        }
        catch
        {
            if (!committed)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            throw;
        }
    }

    public async Task<DeleteUserResult> DeleteUserAsync(ClaimsPrincipal principal, long userId, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return new DeleteUserResult { Outcome = UserDeletionOutcome.NotFound, ErrorMessage = "User not found" };
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var user = await GetUserRecordAsync(connection, context.ClientId.Value, userId, cancellationToken);
        if (user is null)
        {
            return new DeleteUserResult { Outcome = UserDeletionOutcome.NotFound, ErrorMessage = "User not found" };
        }

        var reasons = await GetUserBlockingReasonsAsync(connection, context.ClientId.Value, userId, cancellationToken);
        if (reasons.Count > 0)
        {
            return new DeleteUserResult
            {
                Outcome = UserDeletionOutcome.Blocked,
                ErrorMessage = UserRules.FormatDeleteBlockedMessage(null, reasons, bulkDelete: false)
            };
        }

        await SoftDeleteUserAsync(connection, userId, cancellationToken);
        return new DeleteUserResult { Outcome = UserDeletionOutcome.Deleted };
    }

    public async Task<DeleteUserResult> BulkDeleteUsersAsync(ClaimsPrincipal principal, IReadOnlyList<long> userIds, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return new DeleteUserResult { Outcome = UserDeletionOutcome.NotFound, ErrorMessage = "User not found" };
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var users = await GetUsersByIdsAsync(connection, context.ClientId.Value, userIds, cancellationToken);
        if (users.Count == 0)
        {
            return new DeleteUserResult { Outcome = UserDeletionOutcome.NotFound, ErrorMessage = "User not found" };
        }

        foreach (var user in users)
        {
            var reasons = await GetUserBlockingReasonsAsync(connection, context.ClientId.Value, user.Id, cancellationToken);
            if (reasons.Count > 0)
            {
                return new DeleteUserResult
                {
                    Outcome = UserDeletionOutcome.Blocked,
                    ErrorMessage = UserRules.FormatDeleteBlockedMessage(user.Id, reasons, bulkDelete: true)
                };
            }
        }

        foreach (var user in users)
        {
            await SoftDeleteUserAsync(connection, user.Id, cancellationToken);
        }

        return new DeleteUserResult { Outcome = UserDeletionOutcome.Deleted };
    }

    public async Task<IReadOnlyList<AssignableUserDto>> GetAssignableUsersAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return [];
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT u.id, u.name, u.email
            FROM users u
            WHERE u.client_id = @clientId AND ISNULL(u.is_active, 1) = 1 AND u.deleted_at IS NULL
            ORDER BY u.updated_at DESC, u.id DESC;
            """;

        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var rows = new List<AssignableUserDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new AssignableUserDto
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                Name = GetString(reader, "name") ?? string.Empty,
                Email = GetString(reader, "email") ?? string.Empty,
            });
        }

        return rows;
    }

    public async Task<PagedDataDto<ComponentListItemDto>> GetComponentsAsync(ClaimsPrincipal principal, string? name, string? pageName, string? feature, string? projectIds, string? typeIds, bool? status, int page, int limit, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return CreatePagedData<ComponentListItemDto>([], 0, limit);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);

        var whereClauses = new List<string>
        {
            "c.deleted_at IS NULL",
            "c.client_id = @clientId",
            "EXISTS (SELECT 1 FROM component_steps cs WHERE cs.component_id = c.id AND cs.deleted_at IS NULL)"
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

        var whereSql = string.Join(" AND ", whereClauses);
        var total = await ExecuteCountAsync(connection, $"SELECT COUNT(*) FROM components c WHERE {whereSql};", parameters, cancellationToken);

        var sql = $"""
            SELECT
                c.id,
                c.name,
                c.feature,
                c.page,
                CAST(ISNULL(c.status, 1) AS bit) AS status,
                p.project_name,
                ct.id AS type_id,
                ct.name AS type_name
            FROM components c
            LEFT JOIN projects p ON p.id = c.project_id AND p.deleted_at IS NULL
            LEFT JOIN component_types ct ON ct.id = c.type_id
            WHERE {whereSql}
            ORDER BY c.updated_at DESC, c.id DESC
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
            """;

        var rows = new List<ComponentListItemDto>();
        await using var command = CreateCommand(connection, sql);
        AddParameters(command, parameters);
        command.Parameters.AddWithValue("@offset", (page - 1) * limit);
        command.Parameters.AddWithValue("@limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ComponentListItemDto
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                Name = GetString(reader, "name"),
                Feature = GetString(reader, "feature"),
                Page = GetString(reader, "page"),
                Status = GetBoolean(reader, "status") ?? true,
                Project = new ComponentProjectDto { ProjectName = GetString(reader, "project_name") },
                Type = new ComponentTypeDto { Id = GetInt64(reader, "type_id") ?? 0, Name = GetString(reader, "type_name") }
            });
        }

        return CreatePagedData(rows, total, limit);
    }

    public async Task<ComponentDetailDto?> GetComponentAsync(ClaimsPrincipal principal, long componentId, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT TOP 1 c.id, c.name, c.project_id, c.page, c.feature, c.type_id
            FROM components c
            WHERE c.id = @componentId AND c.client_id = @clientId AND c.deleted_at IS NULL;
            """;

        long detailId;
        string? detailName;
        long? detailProjectId;
        string? detailPage;
        string? detailFeature;
        long? detailTypeId;
        await using (var command = CreateCommand(connection, sql))
        {
            command.Parameters.AddWithValue("@componentId", componentId);
            command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            detailId = reader.GetInt64(reader.GetOrdinal("id"));
            detailName = GetString(reader, "name");
            detailProjectId = GetInt64(reader, "project_id");
            detailPage = GetString(reader, "page");
            detailFeature = GetString(reader, "feature");
            detailTypeId = GetInt64(reader, "type_id");
        }

        return new ComponentDetailDto
        {
            Id = detailId,
            Name = detailName,
            ProjectId = detailProjectId,
            Page = detailPage,
            Feature = detailFeature,
            TypeId = detailTypeId,
            Steps = await LoadComponentStepsAsync(connection, componentId, cancellationToken)
        };
    }

    public async Task<PagedDataDto<ProjectListItemDto>> GetProjectsAsync(ClaimsPrincipal principal, string? query, bool? isActive, int page, int limit, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return CreatePagedData<ProjectListItemDto>([], 0, limit);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var whereClauses = new List<string>
        {
            "p.deleted_at IS NULL",
            "p.client_id = @clientId"
        };
        var parameters = new List<SqlParameter> { new("@clientId", context.ClientId.Value) };

        if (!string.IsNullOrWhiteSpace(query))
        {
            whereClauses.Add("(p.project_name LIKE @query OR p.description LIKE @query)");
            parameters.Add(new SqlParameter("@query", $"%{query.Trim()}%"));
        }

        if (isActive.GetValueOrDefault(true))
        {
            whereClauses.Add("ISNULL(p.status, 1) = 1");
        }

        var whereSql = string.Join(" AND ", whereClauses);
        var total = await ExecuteCountAsync(connection, $"SELECT COUNT(*) FROM projects p WHERE {whereSql};", parameters, cancellationToken);

        var sql = $"""
            SELECT
                p.id,
                p.project_name,
                p.description,
                p.area_path,
                p.primary_test_management,
                p.primary_ticketing_system,
                p.type_id,
                p.version,
                CAST(ISNULL(p.status, 1) AS bit) AS status
            FROM projects p
            WHERE {whereSql}
            ORDER BY p.project_name
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
            """;

        var rows = new List<ProjectListItemDto>();
        await using var command = CreateCommand(connection, sql);
        AddParameters(command, parameters);
        command.Parameters.AddWithValue("@offset", (page - 1) * limit);
        command.Parameters.AddWithValue("@limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ProjectListItemDto
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                ProjectName = GetString(reader, "project_name") ?? string.Empty,
                Description = GetString(reader, "description"),
                AreaPath = GetString(reader, "area_path"),
                PrimaryTestManagement = GetString(reader, "primary_test_management"),
                PrimaryTicketingSystem = GetString(reader, "primary_ticketing_system"),
                TypeId = GetInt64(reader, "type_id"),
                Version = GetString(reader, "version"),
                Status = GetBoolean(reader, "status") ?? true,
            });
        }

        return CreatePagedData(rows, total, limit);
    }

    public async Task<ProjectDetailDto?> GetProjectAsync(ClaimsPrincipal principal, long id, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await GetProjectDetailByIdAsync(connection, context.ClientId.Value, id, cancellationToken);
    }

    public async Task<ProjectListItemDto?> CreateProjectAsync(ClaimsPrincipal principal, SaveProjectRequest request, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var auditUserId = await ResolveAuditUserIdAsync(connection, context.ClientId.Value, context.UserId, cancellationToken);
        const string sql = """
            INSERT INTO projects
            (
                client_id,
                project_name,
                description,
                area_path,
                primary_test_management,
                primary_ticketing_system,
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
                @areaPath,
                @primaryTestManagement,
                @primaryTicketingSystem,
                @status,
                @typeId,
                @userId,
                @userId,
                @createdBy,
                @updatedBy,
                @version,
                SYSUTCDATETIME(),
                SYSUTCDATETIME()
            );
            """;

        var displayName = GetUserDisplayName(principal);
        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
        command.Parameters.AddWithValue("@projectName", request.ProjectName!.Trim());
        command.Parameters.AddWithValue("@description", request.Description!.Trim());
        command.Parameters.AddWithValue("@areaPath", (object?)NormalizeOptionalText(request.AreaPath) ?? DBNull.Value);
        command.Parameters.AddWithValue("@primaryTestManagement", (object?)NormalizeAzureOption(request.PrimaryTestManagement) ?? DBNull.Value);
        command.Parameters.AddWithValue("@primaryTicketingSystem", (object?)NormalizeAzureOption(request.PrimaryTicketingSystem) ?? DBNull.Value);
        command.Parameters.AddWithValue("@status", true);
        command.Parameters.AddWithValue("@typeId", request.TypeId!.Value);
        command.Parameters.AddWithValue("@userId", (object?)auditUserId ?? DBNull.Value);
        command.Parameters.AddWithValue("@createdBy", (object?)displayName ?? DBNull.Value);
        command.Parameters.AddWithValue("@updatedBy", (object?)displayName ?? DBNull.Value);
        command.Parameters.AddWithValue("@version", NormalizeVersion(request.Version));

        var createdId = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        return await GetProjectByIdAsync(connection, context.ClientId.Value, createdId, cancellationToken);
    }

    public async Task<ProjectListItemDto?> UpdateProjectAsync(ClaimsPrincipal principal, long id, SaveProjectRequest request, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        if (!await ProjectBelongsToClientAsync(connection, context.ClientId.Value, id, cancellationToken))
        {
            return null;
        }

        const string sql = """
            UPDATE projects
            SET
                project_name = @projectName,
                description = @description,
                area_path = @areaPath,
                primary_test_management = @primaryTestManagement,
                primary_ticketing_system = @primaryTicketingSystem,
                type_id = @typeId,
                version = @version,
                updated_by_id = @userId,
                updated_by = @updatedBy,
                updated_at = SYSUTCDATETIME()
            WHERE id = @id AND client_id = @clientId AND deleted_at IS NULL;
            """;

        var displayName = GetUserDisplayName(principal);
        var auditUserId = await ResolveAuditUserIdAsync(connection, context.ClientId.Value, context.UserId, cancellationToken);
        await using (var command = CreateCommand(connection, sql))
        {
            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
            command.Parameters.AddWithValue("@projectName", request.ProjectName!.Trim());
            command.Parameters.AddWithValue("@description", request.Description!.Trim());
            command.Parameters.AddWithValue("@areaPath", (object?)NormalizeOptionalText(request.AreaPath) ?? DBNull.Value);
            command.Parameters.AddWithValue("@primaryTestManagement", (object?)NormalizeAzureOption(request.PrimaryTestManagement) ?? DBNull.Value);
            command.Parameters.AddWithValue("@primaryTicketingSystem", (object?)NormalizeAzureOption(request.PrimaryTicketingSystem) ?? DBNull.Value);
            command.Parameters.AddWithValue("@typeId", request.TypeId!.Value);
            command.Parameters.AddWithValue("@version", NormalizeVersion(request.Version));
            command.Parameters.AddWithValue("@userId", (object?)auditUserId ?? DBNull.Value);
            command.Parameters.AddWithValue("@updatedBy", (object?)displayName ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        return await GetProjectByIdAsync(connection, context.ClientId.Value, id, cancellationToken);
    }

    public async Task<ProjectDeletionOutcome> DeleteProjectAsync(ClaimsPrincipal principal, long id, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return ProjectDeletionOutcome.NotFound;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var exists = await ProjectBelongsToClientAsync(connection, context.ClientId.Value, id, cancellationToken);
        var attachedComponentCount = exists
            ? await CountAttachedComponentsAsync(connection, context.ClientId.Value, id, cancellationToken)
            : 0;

        var outcome = ProjectDeletionRules.Evaluate(exists, attachedComponentCount);
        if (outcome != ProjectDeletionOutcome.Deleted)
        {
            return outcome;
        }

        const string sql = """
            UPDATE projects
            SET deleted_at = SYSUTCDATETIME(), updated_at = SYSUTCDATETIME()
            WHERE id = @id AND client_id = @clientId AND deleted_at IS NULL;
            """;

        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return ProjectDeletionOutcome.Deleted;
    }

    public async Task<DashboardSummaryDto> GetDashboardSummaryAsync(ClaimsPrincipal principal, long? projectId, DateOnly? dateFrom, DateOnly? dateTo, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return new DashboardSummaryDto();
        }

        var from = (dateFrom ?? DateOnly.FromDateTime(DateTime.Today.AddDays(-30))).ToDateTime(TimeOnly.MinValue);
        var to = (dateTo ?? DateOnly.FromDateTime(DateTime.Today)).ToDateTime(TimeOnly.MaxValue);

        await using var connection = await OpenConnectionAsync(cancellationToken);

        var totalSuites = await GetDashboardTotalSuitesAsync(connection, context.ClientId.Value, projectId, cancellationToken);
        var executionCounts = await GetDashboardExecutionCountsAsync(connection, context.ClientId.Value, projectId, from, to, cancellationToken);

        var executedSuites = await GetDistinctSuiteCountAsync(connection, context.ClientId.Value, projectId, from, to, [PassedStatusId, FailedStatusId, GlitchStatusId, RetestStatusId, InProgressStatusId], cancellationToken);
        var passedSuites = await GetDistinctSuiteCountAsync(connection, context.ClientId.Value, projectId, from, to, [PassedStatusId], cancellationToken);
        var failedSuites = await GetDistinctSuiteCountAsync(connection, context.ClientId.Value, projectId, from, to, [FailedStatusId, GlitchStatusId], cancellationToken);

        var coverage = totalSuites > 0 ? Math.Round((decimal)executedSuites / totalSuites * 100m, 1) : 0m;
        var passRateDenominator = executionCounts.Passed + executionCounts.Failed;
        var passRate = passRateDenominator > 0 ? Math.Round((decimal)executionCounts.Passed / passRateDenominator * 100m, 1) : 0m;
        var readiness = (int)Math.Round((coverage * 0.6m) + (passRate * 0.4m), 0, MidpointRounding.AwayFromZero);

        var closedStatusIds = await GetClosedDefectStatusIdsAsync(connection, cancellationToken);
        var defectStatuses = await GetDashboardDefectStatusesAsync(connection, context.ClientId.Value, projectId, from, to, cancellationToken);
        var openDefects = await GetOpenDefectCountAsync(connection, context.ClientId.Value, projectId, to, closedStatusIds, cancellationToken);

        return new DashboardSummaryDto
        {
            Kpis = new DashboardKpisDto
            {
                TotalSuites = totalSuites,
                ExecutedSuites = executedSuites,
                ExecutionCoverage = coverage,
                PassRate = passRate,
                OpenDefects = openDefects,
                PassedRuns = executionCounts.Passed,
                FailedRuns = executionCounts.Failed + executionCounts.Glitch,
                FailedSuites = failedSuites,
                PassedSuites = passedSuites,
                NotRunSuites = Math.Max(0, totalSuites - executedSuites),
                ReadinessScore = readiness
            },
            ExecutionCounts = executionCounts,
            ExecutionTrend = await GetDashboardExecutionTrendAsync(connection, context.ClientId.Value, projectId, from, to, cancellationToken),
            DefectTrend = await GetDashboardDefectTrendAsync(connection, context.ClientId.Value, projectId, from, to, closedStatusIds, cancellationToken),
            DefectStatuses = defectStatuses,
            AgingBuckets = await GetDashboardAgingBucketsAsync(connection, context.ClientId.Value, projectId, from, to, cancellationToken)
        };
    }

    public async Task<PagedDataDto<DefectListItemDto>> GetDefectsAsync(ClaimsPrincipal principal, string? query, long? assignedTo, long? statusId, long? createdBy, int page, int limit, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return CreatePagedData<DefectListItemDto>([], 0, limit);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await EnsureDefectSchemaAsync(connection, cancellationToken);
        var whereClauses = new List<string>
        {
            "d.client_id = @clientId",
            "d.deleted_at IS NULL"
        };
        var parameters = new List<SqlParameter> { new("@clientId", context.ClientId.Value) };

        if (!string.IsNullOrWhiteSpace(query))
        {
            whereClauses.Add("""
                (
                    d.title LIKE @query OR
                    tp.name LIKE @query OR
                    tpi.name LIKE @query OR
                    tri.test_suite_name LIKE @query OR
                    assigned_user.name LIKE @query OR
                    assigned_user.email LIKE @query OR
                    created_user.name LIKE @query OR
                    created_user.email LIKE @query OR
                    runner_user.name LIKE @query OR
                    runner_user.email LIKE @query
                )
                """);
            parameters.Add(new SqlParameter("@query", $"%{query.Trim()}%"));
        }

        if (assignedTo.HasValue)
        {
            whereClauses.Add("d.assigned_to = @assignedTo");
            parameters.Add(new SqlParameter("@assignedTo", assignedTo.Value));
        }

        if (statusId.HasValue)
        {
            whereClauses.Add("d.status_id = @statusId");
            parameters.Add(new SqlParameter("@statusId", statusId.Value));
        }

        if (createdBy.HasValue)
        {
            whereClauses.Add("d.created_by = @createdBy");
            parameters.Add(new SqlParameter("@createdBy", createdBy.Value));
        }

        var whereSql = string.Join(" AND ", whereClauses);
        var fromSql = """
            FROM defects d
            LEFT JOIN users assigned_user ON assigned_user.id = d.assigned_to
            LEFT JOIN defect_statuses status_ref ON status_ref.id = d.status_id
            LEFT JOIN users created_user ON created_user.id = d.created_by
            LEFT JOIN test_runner_items tri ON tri.id = d.test_runner_item_id
            LEFT JOIN test_runners tr ON tr.id = tri.test_runner_id
            LEFT JOIN test_plan_items tpi ON tpi.id = tr.test_plan_item_id
            LEFT JOIN test_plans tp ON tp.id = tpi.test_plan_id
            LEFT JOIN users runner_user ON runner_user.id = tri.run_by
            LEFT JOIN test_designs td ON td.id = tri.test_suite_id
            LEFT JOIN configurations cfg ON cfg.id = td.configuration_id
            """;
        var total = await ExecuteCountAsync(connection, $"SELECT COUNT(*) {fromSql} WHERE {whereSql};", parameters, cancellationToken);

        var sql = $"""
            SELECT
                d.id,
                d.title,
                d.description,
                d.expected_result,
                d.actual_result,
                d.test_runner_item_id,
                d.created_at,
                assigned_user.id AS assigned_id,
                assigned_user.name AS assigned_name,
                assigned_user.email AS assigned_email,
                status_ref.id AS status_id,
                status_ref.name AS status_name,
                created_user.id AS created_by_id,
                created_user.name AS created_by_name,
                created_user.email AS created_by_email,
                tp.name AS test_plan_name,
                tpi.id AS test_plan_item_id,
                tpi.name AS test_plan_item_name,
                tri.execution_id,
                tri.test_suite_id AS base_test_suite_id,
                tri.test_suite_name,
                cfg.name AS configuration_name,
                tri.steps,
                tri.created_at AS runner_created_at,
                runner_user.id AS runner_user_id,
                runner_user.name AS runner_user_name,
                runner_user.email AS runner_user_email,
                td.comment AS test_suite_comment,
                COALESCE((
                    SELECT
                        da.id,
                        da.file_name,
                        da.file_path AS url,
                        da.content_type,
                        da.file_size,
                        da.created_at
                    FROM defect_attachments da
                    WHERE da.defect_id = d.id AND da.deleted_at IS NULL
                    ORDER BY da.id
                    FOR JSON PATH
                ), '[]') AS attachments_json
            {fromSql}
            WHERE {whereSql}
            ORDER BY d.id DESC
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
            """;

        var rawRows = new List<(DefectListItemDto Row, long? ExecutionId, long? BaseTestSuiteId)>();
        await using var command = CreateCommand(connection, sql);
        AddParameters(command, parameters);
        command.Parameters.AddWithValue("@offset", (page - 1) * limit);
        command.Parameters.AddWithValue("@limit", limit);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rawRows.Add((
                    BuildDefectListItem(reader),
                    GetInt64(reader, "execution_id"),
                    GetInt64(reader, "base_test_suite_id")));
            }
        }

        var configurationsByRunnerItemId = await LoadRunnerItemConfigurationsAsync(
            connection,
            context.ClientId.Value,
            rawRows
                .Where(row => row.Row.TestRunnerItemId.HasValue)
                .Select(row => (
                    RunnerItemId: row.Row.TestRunnerItemId!.Value,
                    row.ExecutionId,
                    row.BaseTestSuiteId))
                .ToArray(),
            cancellationToken);

        var rows = rawRows
            .Select(row => row.Row.TestRunnerItemId.HasValue && configurationsByRunnerItemId.TryGetValue(row.Row.TestRunnerItemId.Value, out var configuration)
                ? WithConfiguration(row.Row, configuration)
                : row.Row)
            .ToList();

        return CreatePagedData(rows, total, limit);
    }

    public async Task<DefectListItemDto?> GetDefectAsync(ClaimsPrincipal principal, long defectId, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await GetDefectByIdAsync(connection, context.ClientId.Value, defectId, cancellationToken);
    }

    public async Task<IReadOnlyList<DefectStatusDto>> GetDefectStatusesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT id, name
            FROM defect_statuses
            ORDER BY id;
            """;

        await using var command = CreateCommand(connection, sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<DefectStatusDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new DefectStatusDto
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                Name = GetString(reader, "name") ?? string.Empty
            });
        }

        return rows;
    }

    public async Task<IReadOnlyList<ProjectTypeDto>> GetProjectTypesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT id, name, created_at, updated_at
            FROM project_types
            ORDER BY id;
            """;

        await using var command = CreateCommand(connection, sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<ProjectTypeDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ProjectTypeDto
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                Name = GetString(reader, "name"),
                CreatedAt = GetDateTimeOffset(reader, "created_at"),
                UpdatedAt = GetDateTimeOffset(reader, "updated_at")
            });
        }

        return rows;
    }

    public async Task<HealthPollConfigDto> GetHealthPollConfigAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT TOP 1 value
            FROM system_settings
            WHERE [key] = @key;
            """;

        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@key", "execution_device_health_poll_minutes");
        var value = await command.ExecuteScalarAsync(cancellationToken);

        var minutes = 5;
        if (value is not null && int.TryParse(Convert.ToString(value), out var parsed))
        {
            minutes = Math.Clamp(parsed, 1, 60);
        }

        return new HealthPollConfigDto { Minutes = minutes };
    }

    public async Task<SystemSettingDto> SaveSystemSettingAsync(string key, string? value, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        const string selectSql = "SELECT TOP 1 id FROM system_settings WHERE [key] = @key;";
        object? existingId;
        await using (var selectCommand = CreateCommand(connection, selectSql))
        {
            selectCommand.Transaction = transaction;
            selectCommand.Parameters.AddWithValue("@key", key);
            existingId = await selectCommand.ExecuteScalarAsync(cancellationToken);
        }

        if (existingId is null)
        {
            const string insertSql = """
                INSERT INTO system_settings ([key], value, created_at, updated_at)
                VALUES (@key, @value, SYSUTCDATETIME(), SYSUTCDATETIME());
                """;
            await using var insertCommand = CreateCommand(connection, insertSql);
            insertCommand.Transaction = transaction;
            insertCommand.Parameters.AddWithValue("@key", key);
            insertCommand.Parameters.AddWithValue("@value", (object?)value ?? DBNull.Value);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            const string updateSql = "UPDATE system_settings SET value = @value, updated_at = SYSUTCDATETIME() WHERE [key] = @key;";
            await using var updateCommand = CreateCommand(connection, updateSql);
            updateCommand.Transaction = transaction;
            updateCommand.Parameters.AddWithValue("@key", key);
            updateCommand.Parameters.AddWithValue("@value", (object?)value ?? DBNull.Value);
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        const string loadSql = """
            SELECT TOP 1 id, [key], value, created_at, updated_at
            FROM system_settings
            WHERE [key] = @key;
            """;
        await using var loadCommand = CreateCommand(connection, loadSql);
        loadCommand.Parameters.AddWithValue("@key", key);
        await using var reader = await loadCommand.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new SystemSettingDto
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            Key = GetString(reader, "key") ?? key,
            Value = GetString(reader, "value"),
            CreatedAt = GetDateTimeOffset(reader, "created_at"),
            UpdatedAt = GetDateTimeOffset(reader, "updated_at")
        };
    }

    public async Task<DefectListItemDto?> CreateDefectAsync(ClaimsPrincipal principal, CreateDefectRequest request, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        var title = NormalizeOptionalText(request.Title);
        var description = NormalizeOptionalText(request.Description);
        var expected = NormalizeOptionalText(request.Expected);
        var actual = NormalizeOptionalText(request.Actual);
        if (!context.ClientId.HasValue || string.IsNullOrWhiteSpace(title) || !request.AssignedTo.HasValue || request.AssignedTo.Value <= 0 || !request.TestRunnerItemId.HasValue || request.TestRunnerItemId.Value <= 0)
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await EnsureDefectSchemaAsync(connection, cancellationToken);

        const string validateAssigneeSql = "SELECT COUNT(*) FROM users WHERE id = @userId AND client_id = @clientId AND deleted_at IS NULL;";
        var validAssignee = await ExecuteCountAsync(connection, validateAssigneeSql, [new SqlParameter("@userId", request.AssignedTo.Value), new SqlParameter("@clientId", context.ClientId.Value)], cancellationToken);
        if (validAssignee == 0)
        {
            return null;
        }

        const string validateRunnerSql = """
            SELECT TOP 1 tri.id
            FROM test_runner_items tri
            INNER JOIN test_runners tr ON tr.id = tri.test_runner_id
            WHERE tri.id = @testRunnerItemId AND tr.client_id = @clientId;
            """;
        await using (var validateRunnerCommand = CreateCommand(connection, validateRunnerSql))
        {
            validateRunnerCommand.Parameters.AddWithValue("@testRunnerItemId", request.TestRunnerItemId.Value);
            validateRunnerCommand.Parameters.AddWithValue("@clientId", context.ClientId.Value);
            var runnerExists = await validateRunnerCommand.ExecuteScalarAsync(cancellationToken);
            if (runnerExists is null)
            {
                return null;
            }
        }

        const string existingDefectSql = "SELECT TOP 1 id FROM defects WHERE client_id = @clientId AND test_runner_item_id = @testRunnerItemId AND deleted_at IS NULL ORDER BY id DESC;";
        await using (var existingDefectCommand = CreateCommand(connection, existingDefectSql))
        {
            existingDefectCommand.Parameters.AddWithValue("@clientId", context.ClientId.Value);
            existingDefectCommand.Parameters.AddWithValue("@testRunnerItemId", request.TestRunnerItemId.Value);
            var existingDefectId = await existingDefectCommand.ExecuteScalarAsync(cancellationToken);
            if (existingDefectId is not null && existingDefectId != DBNull.Value)
            {
                return await GetDefectByIdAsync(connection, context.ClientId.Value, Convert.ToInt64(existingDefectId), cancellationToken);
            }
        }

        long defaultStatusId = 1;
        const string defaultStatusSql = """
            SELECT TOP 1 id
            FROM defect_statuses
            WHERE LOWER(name) IN ('new', 'open')
            ORDER BY CASE WHEN LOWER(name) = 'new' THEN 0 ELSE 1 END, id;
            """;
        await using (var defaultStatusCommand = CreateCommand(connection, defaultStatusSql))
        {
            var resolvedStatus = await defaultStatusCommand.ExecuteScalarAsync(cancellationToken);
            if (resolvedStatus is not null && resolvedStatus != DBNull.Value)
            {
                defaultStatusId = Convert.ToInt64(resolvedStatus);
            }
        }

        const string insertSql = """
            INSERT INTO defects
            (
                title,
                description,
                expected_result,
                actual_result,
                client_id,
                assigned_to,
                test_runner_item_id,
                status_id,
                created_by,
                created_at,
                updated_at
            )
            OUTPUT INSERTED.id
            VALUES
            (
                @title,
                @description,
                @expected,
                @actual,
                @clientId,
                @assignedTo,
                @testRunnerItemId,
                @statusId,
                @createdBy,
                SYSUTCDATETIME(),
                SYSUTCDATETIME()
            );
            """;

        long defectId;
        await using (var insertCommand = CreateCommand(connection, insertSql))
        {
            insertCommand.Parameters.AddWithValue("@title", title);
            insertCommand.Parameters.AddWithValue("@description", (object?)description ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("@expected", (object?)expected ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("@actual", (object?)actual ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("@clientId", context.ClientId.Value);
            insertCommand.Parameters.AddWithValue("@assignedTo", request.AssignedTo.Value);
            insertCommand.Parameters.AddWithValue("@testRunnerItemId", request.TestRunnerItemId.Value);
            insertCommand.Parameters.AddWithValue("@statusId", defaultStatusId);
            insertCommand.Parameters.AddWithValue("@createdBy", context.UserId);
            defectId = Convert.ToInt64(await insertCommand.ExecuteScalarAsync(cancellationToken));
        }

        return await GetDefectByIdAsync(connection, context.ClientId.Value, defectId, cancellationToken);
    }

    public async Task<DefectListItemDto?> CreateManualDefectAsync(ClaimsPrincipal principal, CreateManualDefectRequest request, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        var title = NormalizeOptionalText(request.Title);
        var description = NormalizeOptionalText(request.Description);
        var expected = NormalizeOptionalText(request.Expected);
        var actual = NormalizeOptionalText(request.Actual);
        if (!context.ClientId.HasValue || string.IsNullOrWhiteSpace(title) || !request.AssignedTo.HasValue || request.AssignedTo.Value <= 0)
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await EnsureDefectSchemaAsync(connection, cancellationToken);

        const string validateAssigneeSql = "SELECT COUNT(*) FROM users WHERE id = @userId AND client_id = @clientId AND deleted_at IS NULL;";
        var validAssignee = await ExecuteCountAsync(connection, validateAssigneeSql, [new SqlParameter("@userId", request.AssignedTo.Value), new SqlParameter("@clientId", context.ClientId.Value)], cancellationToken);
        if (validAssignee == 0)
        {
            return null;
        }

        if (request.TestRunnerItemId.HasValue)
        {
            const string validateRunnerSql = """
                SELECT TOP 1 tri.id
                FROM test_runner_items tri
                INNER JOIN test_runners tr ON tr.id = tri.test_runner_id
                WHERE tri.id = @testRunnerItemId AND tr.client_id = @clientId;
                """;
            await using var validateRunnerCommand = CreateCommand(connection, validateRunnerSql);
            validateRunnerCommand.Parameters.AddWithValue("@testRunnerItemId", request.TestRunnerItemId.Value);
            validateRunnerCommand.Parameters.AddWithValue("@clientId", context.ClientId.Value);
            var runnerExists = await validateRunnerCommand.ExecuteScalarAsync(cancellationToken);
            if (runnerExists is null)
            {
                return null;
            }
        }

        long defaultStatusId = 1;
        const string defaultStatusSql = """
            SELECT TOP 1 id
            FROM defect_statuses
            WHERE LOWER(name) IN ('new', 'open')
            ORDER BY CASE WHEN LOWER(name) = 'new' THEN 0 ELSE 1 END, id;
            """;
        await using (var defaultStatusCommand = CreateCommand(connection, defaultStatusSql))
        {
            var resolvedStatus = await defaultStatusCommand.ExecuteScalarAsync(cancellationToken);
            if (resolvedStatus is not null && resolvedStatus != DBNull.Value)
            {
                defaultStatusId = Convert.ToInt64(resolvedStatus);
            }
        }

        const string insertSql = """
            INSERT INTO defects
            (
                title,
                description,
                expected_result,
                actual_result,
                client_id,
                assigned_to,
                test_runner_item_id,
                status_id,
                created_by,
                created_at,
                updated_at
            )
            OUTPUT INSERTED.id
            VALUES
            (
                @title,
                @description,
                @expected,
                @actual,
                @clientId,
                @assignedTo,
                @testRunnerItemId,
                @statusId,
                @createdBy,
                SYSUTCDATETIME(),
                SYSUTCDATETIME()
            );
            """;

        long defectId;
        await using (var insertCommand = CreateCommand(connection, insertSql))
        {
            insertCommand.Parameters.AddWithValue("@title", title);
            insertCommand.Parameters.AddWithValue("@description", (object?)description ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("@expected", (object?)expected ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("@actual", (object?)actual ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("@clientId", context.ClientId.Value);
            insertCommand.Parameters.AddWithValue("@assignedTo", request.AssignedTo.Value);
            insertCommand.Parameters.AddWithValue("@testRunnerItemId", (object?)request.TestRunnerItemId ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("@statusId", defaultStatusId);
            insertCommand.Parameters.AddWithValue("@createdBy", context.UserId);
            defectId = Convert.ToInt64(await insertCommand.ExecuteScalarAsync(cancellationToken));
        }

        return await GetDefectByIdAsync(connection, context.ClientId.Value, defectId, cancellationToken);
    }

    public async Task<DefectListItemDto?> AddDefectAttachmentsAsync(ClaimsPrincipal principal, long defectId, IReadOnlyList<DefectAttachmentFileInput> attachments, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue || attachments.Count == 0)
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await EnsureDefectSchemaAsync(connection, cancellationToken);

        const string defectExistsSql = "SELECT COUNT(*) FROM defects WHERE id = @defectId AND client_id = @clientId AND deleted_at IS NULL;";
        var defectExists = await ExecuteCountAsync(connection, defectExistsSql, [
            new SqlParameter("@defectId", defectId),
            new SqlParameter("@clientId", context.ClientId.Value)
        ], cancellationToken);
        if (defectExists == 0)
        {
            return null;
        }

        const string insertSql = """
            INSERT INTO defect_attachments
            (
                defect_id,
                client_id,
                file_name,
                file_path,
                content_type,
                file_size,
                created_by,
                created_at,
                updated_at
            )
            VALUES
            (
                @defectId,
                @clientId,
                @fileName,
                @filePath,
                @contentType,
                @fileSize,
                @createdBy,
                SYSUTCDATETIME(),
                SYSUTCDATETIME()
            );
            """;

        foreach (var attachment in attachments)
        {
            await using var insertCommand = CreateCommand(connection, insertSql);
            insertCommand.Parameters.AddWithValue("@defectId", defectId);
            insertCommand.Parameters.AddWithValue("@clientId", context.ClientId.Value);
            insertCommand.Parameters.AddWithValue("@fileName", attachment.FileName);
            insertCommand.Parameters.AddWithValue("@filePath", attachment.Url);
            insertCommand.Parameters.AddWithValue("@contentType", (object?)attachment.ContentType ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("@fileSize", attachment.FileSize);
            insertCommand.Parameters.AddWithValue("@createdBy", context.UserId);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        return await GetDefectByIdAsync(connection, context.ClientId.Value, defectId, cancellationToken);
    }

    public async Task<DefectListItemDto?> DeleteDefectAttachmentAsync(ClaimsPrincipal principal, long defectId, long attachmentId, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await EnsureDefectSchemaAsync(connection, cancellationToken);

        const string deleteSql = """
            UPDATE defect_attachments
            SET deleted_at = SYSUTCDATETIME(),
                updated_at = SYSUTCDATETIME()
            WHERE id = @attachmentId
              AND defect_id = @defectId
              AND client_id = @clientId
              AND deleted_at IS NULL;
            """;

        await using var deleteCommand = CreateCommand(connection, deleteSql);
        deleteCommand.Parameters.AddWithValue("@attachmentId", attachmentId);
        deleteCommand.Parameters.AddWithValue("@defectId", defectId);
        deleteCommand.Parameters.AddWithValue("@clientId", context.ClientId.Value);
        var affected = await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        if (affected == 0)
        {
            return null;
        }

        return await GetDefectByIdAsync(connection, context.ClientId.Value, defectId, cancellationToken);
    }

    public async Task<DefectListItemDto?> UpdateDefectAsync(ClaimsPrincipal principal, long defectId, UpdateDefectRequest request, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        if (request.AssignedTo.HasValue)
        {
            const string validateUserSql = "SELECT COUNT(*) FROM users WHERE id = @userId AND client_id = @clientId AND deleted_at IS NULL;";
            var validAssignee = await ExecuteCountAsync(connection, validateUserSql, [new SqlParameter("@userId", request.AssignedTo.Value), new SqlParameter("@clientId", context.ClientId.Value)], cancellationToken);
            if (validAssignee == 0)
            {
                return null;
            }
        }

        var updates = new List<string>();
        var parameters = new List<SqlParameter>
        {
            new("@defectId", defectId),
            new("@clientId", context.ClientId.Value)
        };

        if (request.Title is not null)
        {
            updates.Add("title = @title");
            parameters.Add(new SqlParameter("@title", NormalizeOptionalText(request.Title) ?? string.Empty));
        }

        if (request.Description is not null)
        {
            updates.Add("description = @description");
            parameters.Add(new SqlParameter("@description", (object?)NormalizeOptionalText(request.Description) ?? DBNull.Value));
        }

        if (request.Expected is not null)
        {
            updates.Add("expected_result = @expected");
            parameters.Add(new SqlParameter("@expected", (object?)NormalizeOptionalText(request.Expected) ?? DBNull.Value));
        }

        if (request.Actual is not null)
        {
            updates.Add("actual_result = @actual");
            parameters.Add(new SqlParameter("@actual", (object?)NormalizeOptionalText(request.Actual) ?? DBNull.Value));
        }

        if (request.AssignedTo.HasValue)
        {
            updates.Add("assigned_to = @assignedTo");
            parameters.Add(new SqlParameter("@assignedTo", request.AssignedTo.Value));
        }

        if (updates.Count == 0)
        {
            return await GetDefectByIdAsync(connection, context.ClientId.Value, defectId, cancellationToken);
        }

        var updateSql = $"UPDATE defects SET {string.Join(", ", updates)}, updated_at = SYSUTCDATETIME() WHERE id = @defectId AND client_id = @clientId AND deleted_at IS NULL;";
        await using (var updateCommand = CreateCommand(connection, updateSql))
        {
            AddParameters(updateCommand, parameters);
            var affected = await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            if (affected == 0)
            {
                return null;
            }
        }

        return await GetDefectByIdAsync(connection, context.ClientId.Value, defectId, cancellationToken);
    }

    public async Task<DefectListItemDto?> UpdateDefectStatusAsync(ClaimsPrincipal principal, long defectId, long statusId, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var statusExists = await ExecuteCountAsync(connection, "SELECT COUNT(*) FROM defect_statuses WHERE id = @statusId;", [new SqlParameter("@statusId", statusId)], cancellationToken);
        if (statusExists == 0)
        {
            return null;
        }

        var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        const string defectSql = """
            SELECT TOP 1 d.test_runner_item_id, tri.test_suite_id, tr.test_plan_item_id
            FROM defects d
            LEFT JOIN test_runner_items tri ON tri.id = d.test_runner_item_id
            LEFT JOIN test_runners tr ON tr.id = tri.test_runner_id
            WHERE d.id = @defectId AND d.client_id = @clientId AND d.deleted_at IS NULL;
            """;

        long? runnerItemId = null;
        long? testSuiteId = null;
        long? testPlanItemId = null;
        await using (var defectCommand = CreateCommand(connection, defectSql))
        {
            defectCommand.Transaction = transaction;
            defectCommand.Parameters.AddWithValue("@defectId", defectId);
            defectCommand.Parameters.AddWithValue("@clientId", context.ClientId.Value);
            await using var defectReader = await defectCommand.ExecuteReaderAsync(cancellationToken);
            if (!await defectReader.ReadAsync(cancellationToken))
            {
                return null;
            }

            runnerItemId = GetInt64(defectReader, "test_runner_item_id");
            testSuiteId = GetInt64(defectReader, "test_suite_id");
            testPlanItemId = GetInt64(defectReader, "test_plan_item_id");
        }

        const string updateDefectSql = "UPDATE defects SET status_id = @statusId, updated_at = SYSUTCDATETIME() WHERE id = @defectId AND client_id = @clientId AND deleted_at IS NULL;";
        await using (var updateDefectCommand = CreateCommand(connection, updateDefectSql))
        {
            updateDefectCommand.Transaction = transaction;
            updateDefectCommand.Parameters.AddWithValue("@statusId", statusId);
            updateDefectCommand.Parameters.AddWithValue("@defectId", defectId);
            updateDefectCommand.Parameters.AddWithValue("@clientId", context.ClientId.Value);
            await updateDefectCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        if (statusId == FixedDefectStatusId && runnerItemId.HasValue)
        {
            const string updateRunnerSql = "UPDATE test_runner_items SET status_id = @retestStatusId, updated_at = SYSUTCDATETIME() WHERE id = @runnerItemId;";
            await using (var updateRunnerCommand = CreateCommand(connection, updateRunnerSql))
            {
                updateRunnerCommand.Transaction = transaction;
                updateRunnerCommand.Parameters.AddWithValue("@retestStatusId", RetestStatusId);
                updateRunnerCommand.Parameters.AddWithValue("@runnerItemId", runnerItemId.Value);
                await updateRunnerCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            if (testPlanItemId.HasValue && testSuiteId.HasValue)
            {
                const string updateSuiteSql = """
                    UPDATE test_plan_item_suites
                    SET status_id = @retestStatusId, updated_at = SYSUTCDATETIME()
                    WHERE test_plan_item_id = @testPlanItemId AND test_design_id = @testSuiteId AND deleted_at IS NULL;
                    """;
                await using var updateSuiteCommand = CreateCommand(connection, updateSuiteSql);
                updateSuiteCommand.Transaction = transaction;
                updateSuiteCommand.Parameters.AddWithValue("@retestStatusId", RetestStatusId);
                updateSuiteCommand.Parameters.AddWithValue("@testPlanItemId", testPlanItemId.Value);
                updateSuiteCommand.Parameters.AddWithValue("@testSuiteId", testSuiteId.Value);
                await updateSuiteCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return await GetDefectByIdAsync(connection, context.ClientId.Value, defectId, cancellationToken);
    }

    public async Task<bool> ToggleFailedStatusAsync(ClaimsPrincipal principal, long testRunnerItemId, string? comment, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return false;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string runnerSql = """
            SELECT TOP 1 tri.status_id, tri.test_suite_id, tr.test_plan_item_id
            FROM test_runner_items tri
            INNER JOIN test_runners tr ON tr.id = tri.test_runner_id
            WHERE tri.id = @testRunnerItemId AND tr.client_id = @clientId;
            """;

        long? currentStatusId = null;
        long? testSuiteId = null;
        long? testPlanItemId = null;
        await using (var runnerCommand = CreateCommand(connection, runnerSql))
        {
            runnerCommand.Parameters.AddWithValue("@testRunnerItemId", testRunnerItemId);
            runnerCommand.Parameters.AddWithValue("@clientId", context.ClientId.Value);
            await using var reader = await runnerCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return false;
            }

            currentStatusId = GetInt64(reader, "status_id");
            testSuiteId = GetInt64(reader, "test_suite_id");
            testPlanItemId = GetInt64(reader, "test_plan_item_id");
        }

        if (currentStatusId is not FailedStatusId and not GlitchStatusId)
        {
            return false;
        }

        var normalizedComment = NormalizeOptionalText(comment);
        var targetStatusId = currentStatusId == FailedStatusId
            ? GlitchStatusId
            : normalizedComment is null ? FailedStatusId : GlitchStatusId;
        var targetComment = targetStatusId == GlitchStatusId ? normalizedComment : null;

        var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        const string updateRunnerSql = """
            UPDATE test_runner_items
            SET status_id = @statusId,
                comment = @comment,
                updated_at = SYSUTCDATETIME()
            WHERE id = @testRunnerItemId;
            """;
        await using (var updateRunnerCommand = CreateCommand(connection, updateRunnerSql))
        {
            updateRunnerCommand.Transaction = transaction;
            updateRunnerCommand.Parameters.AddWithValue("@statusId", targetStatusId);
            updateRunnerCommand.Parameters.AddWithValue("@comment", (object?)targetComment ?? DBNull.Value);
            updateRunnerCommand.Parameters.AddWithValue("@testRunnerItemId", testRunnerItemId);
            await updateRunnerCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        if (testPlanItemId.HasValue && testSuiteId.HasValue)
        {
            const string updateSuiteSql = """
                UPDATE test_plan_item_suites
                SET status_id = @statusId,
                    updated_at = SYSUTCDATETIME()
                WHERE test_plan_item_id = @testPlanItemId AND test_design_id = @testSuiteId AND deleted_at IS NULL;
                """;
            await using var updateSuiteCommand = CreateCommand(connection, updateSuiteSql);
            updateSuiteCommand.Transaction = transaction;
            updateSuiteCommand.Parameters.AddWithValue("@statusId", targetStatusId);
            updateSuiteCommand.Parameters.AddWithValue("@testPlanItemId", testPlanItemId.Value);
            updateSuiteCommand.Parameters.AddWithValue("@testSuiteId", testSuiteId.Value);
            await updateSuiteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> ToggleTestRunnerFavoriteAsync(ClaimsPrincipal principal, long testRunnerItemId, bool isFavorite, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return false;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await EnsureTestRunnerFavoriteColumnAsync(connection, cancellationToken);

        const string runnerSql = """
            SELECT TOP 1 tri.test_suite_id
            FROM test_runner_items tri
            INNER JOIN test_runners tr ON tr.id = tri.test_runner_id
            WHERE tri.id = @testRunnerItemId AND tr.client_id = @clientId;
            """;

        long? testSuiteId = null;
        await using (var runnerCommand = CreateCommand(connection, runnerSql))
        {
            runnerCommand.Parameters.AddWithValue("@testRunnerItemId", testRunnerItemId);
            runnerCommand.Parameters.AddWithValue("@clientId", context.ClientId.Value);
            var result = await runnerCommand.ExecuteScalarAsync(cancellationToken);
            if (result is null || result == DBNull.Value)
            {
                return false;
            }

            testSuiteId = Convert.ToInt64(result);
        }

        var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        if (isFavorite && testSuiteId.HasValue)
        {
            const string clearSql = """
                UPDATE tri
                SET tri.is_favorite = 0,
                    tri.updated_at = SYSUTCDATETIME()
                FROM test_runner_items tri
                INNER JOIN test_runners tr ON tr.id = tri.test_runner_id
                WHERE tr.client_id = @clientId AND tri.test_suite_id = @testSuiteId AND tri.is_favorite = 1;
                """;
            await using var clearCommand = CreateCommand(connection, clearSql);
            clearCommand.Transaction = transaction;
            clearCommand.Parameters.AddWithValue("@clientId", context.ClientId.Value);
            clearCommand.Parameters.AddWithValue("@testSuiteId", testSuiteId.Value);
            await clearCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        const string updateSql = """
            UPDATE tri
            SET tri.is_favorite = @isFavorite,
                tri.updated_at = SYSUTCDATETIME()
            FROM test_runner_items tri
            INNER JOIN test_runners tr ON tr.id = tri.test_runner_id
            WHERE tri.id = @testRunnerItemId AND tr.client_id = @clientId;
            """;
        await using (var updateCommand = CreateCommand(connection, updateSql))
        {
            updateCommand.Transaction = transaction;
            updateCommand.Parameters.AddWithValue("@isFavorite", isFavorite);
            updateCommand.Parameters.AddWithValue("@testRunnerItemId", testRunnerItemId);
            updateCommand.Parameters.AddWithValue("@clientId", context.ClientId.Value);
            var affected = await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            if (affected == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<UserSettingsDto?> GetUserSettingsAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT TOP 1 id, user_id, settings
            FROM user_settings
            WHERE user_id = @userId
            ORDER BY id DESC;
            """;

        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@userId", context.UserId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new UserSettingsDto
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            UserId = reader.GetInt64(reader.GetOrdinal("user_id")),
            Settings = ParseJsonElement(GetString(reader, "settings"))
        };
    }

    public async Task<UserSettingsDto> SaveUserSettingsAsync(ClaimsPrincipal principal, JsonElement settings, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var json = settings.ValueKind == JsonValueKind.Undefined ? "{}" : settings.GetRawText();

        const string selectSql = "SELECT TOP 1 id FROM user_settings WHERE user_id = @userId ORDER BY id DESC;";
        await using var selectCommand = CreateCommand(connection, selectSql);
        selectCommand.Parameters.AddWithValue("@userId", context.UserId);
        var existingId = await selectCommand.ExecuteScalarAsync(cancellationToken);

        long? existingIdValue = existingId is null ? null : Convert.ToInt64(existingId);

        if (existingIdValue.HasValue)
        {
            const string updateSql = "UPDATE user_settings SET settings = @settings, updated_at = SYSUTCDATETIME() WHERE id = @id;";
            await using var updateCommand = CreateCommand(connection, updateSql);
            updateCommand.Parameters.AddWithValue("@settings", json);
            updateCommand.Parameters.AddWithValue("@id", existingIdValue.Value);
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            const string insertSql = "INSERT INTO user_settings (user_id, settings, created_at, updated_at) VALUES (@userId, @settings, SYSUTCDATETIME(), SYSUTCDATETIME());";
            await using var insertCommand = CreateCommand(connection, insertSql);
            insertCommand.Parameters.AddWithValue("@userId", context.UserId);
            insertCommand.Parameters.AddWithValue("@settings", json);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        return await GetUserSettingsAsync(principal, cancellationToken) ?? new UserSettingsDto
        {
            Id = 0,
            UserId = context.UserId,
            Settings = ParseJsonElement(json)
        };
    }

    public async Task<IReadOnlyList<KeywordOptionDto>> GetKeywordsAsync(ClaimsPrincipal principal, bool customOnly, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        await using var connection = await OpenConnectionAsync(cancellationToken);

        var rows = new List<KeywordOptionDto>();
        if (customOnly)
        {
            const string sql = """
                SELECT ck.id, ck.name, 'custom' AS source
                FROM component_keywords ck
                WHERE ck.client_id = @clientId AND ck.global_keyword_id IS NULL
                ORDER BY ck.name;
                """;

            await using var command = CreateCommand(connection, sql);
            command.Parameters.AddWithValue("@clientId", context.ClientId ?? 0);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new KeywordOptionDto
                {
                    Id = reader.GetInt64(reader.GetOrdinal("id")),
                    Name = GetString(reader, "name") ?? string.Empty,
                    Source = GetString(reader, "source") ?? "custom"
                });
            }

            return rows;
        }

        const string unionSql = """
            SELECT gk.id, gk.name, 'global' AS source
            FROM global_keywords gk
            UNION ALL
            SELECT ck.id, ck.name, 'custom' AS source
            FROM component_keywords ck
            WHERE ck.client_id = @clientId AND ck.global_keyword_id IS NULL
            ORDER BY name;
            """;

        await using var unionCommand = CreateCommand(connection, unionSql);
        unionCommand.Parameters.AddWithValue("@clientId", context.ClientId ?? 0);
        await using var unionReader = await unionCommand.ExecuteReaderAsync(cancellationToken);
        while (await unionReader.ReadAsync(cancellationToken))
        {
            rows.Add(new KeywordOptionDto
            {
                Id = unionReader.GetInt64(unionReader.GetOrdinal("id")),
                Name = GetString(unionReader, "name") ?? string.Empty,
                Source = GetString(unionReader, "source") ?? string.Empty
            });
        }

        foreach (var builtInKeyword in BuiltInBrowserKeywords)
        {
            if (rows.Any(row => string.Equals(row.Name, builtInKeyword.Name, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            rows.Add(new KeywordOptionDto
            {
                Id = builtInKeyword.Id,
                Name = builtInKeyword.Name,
                Source = "builtin"
            });
        }

        rows = rows
            .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Source, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return rows;
    }

    public async Task<IReadOnlyList<BeforeAfterStepDto>> GetBeforeAfterStepsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await EnsureBuiltInBeforeAfterStepsAsync(connection, cancellationToken);
        const string sql = """
            SELECT id, name, CAST(ISNULL(field, 0) AS bit) AS field, type, rules
            FROM before_after_steps
            ORDER BY name;
            """;

        await using var command = CreateCommand(connection, sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<BeforeAfterStepDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new BeforeAfterStepDto
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                Name = GetString(reader, "name") ?? string.Empty,
                Field = GetBoolean(reader, "field") ?? false,
                Type = GetString(reader, "type"),
                Rules = ParseNullableJsonElement(GetString(reader, "rules")),
                UsageCount = 0
            });
        }

        return rows;
    }

    public async Task<IReadOnlyList<BeforeAfterStepDto>> GetBeforeAfterStepAdminAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await EnsureBuiltInBeforeAfterStepsAsync(connection, cancellationToken);
        const string sql = """
            SELECT id, name, CAST(ISNULL(field, 0) AS bit) AS field, type, rules
            FROM before_after_steps
            ORDER BY name;
            """;

        var rawRows = new List<(long Id, string Name, bool Field, string? Type, string? RulesJson)>();
        await using (var command = CreateCommand(connection, sql))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rawRows.Add((
                    reader.GetInt64(reader.GetOrdinal("id")),
                    GetString(reader, "name") ?? string.Empty,
                    GetBoolean(reader, "field") ?? false,
                    GetString(reader, "type"),
                    GetString(reader, "rules")));
            }
        }

        var rows = new List<BeforeAfterStepDto>(rawRows.Count);
        foreach (var row in rawRows)
        {
            rows.Add(new BeforeAfterStepDto
            {
                Id = row.Id,
                Name = row.Name,
                Field = row.Field,
                Type = row.Type,
                Rules = ParseNullableJsonElement(row.RulesJson),
                UsageCount = await GetBeforeAfterStepUsageCountAsync(connection, row.Name, cancellationToken)
            });
        }

        return rows;
    }

    public async Task<(BeforeAfterStepDto? Step, bool Duplicate)> CreateBeforeAfterStepAdminAsync(string name, bool field, string? type, JsonElement? rules, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var duplicateCount = await ExecuteCountAsync(connection, "SELECT COUNT(*) FROM before_after_steps WHERE name = @name;", [new SqlParameter("@name", name)], cancellationToken);
        if (duplicateCount > 0)
        {
            return (null, true);
        }

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        long id;
        await using (var insertCommand = CreateCommand(connection, "INSERT INTO before_after_steps (name, field, type, rules, created_at, updated_at) OUTPUT INSERTED.id VALUES (@name, @field, @type, @rules, SYSUTCDATETIME(), SYSUTCDATETIME());"))
        {
            insertCommand.Transaction = transaction;
            insertCommand.Parameters.AddWithValue("@name", name);
            insertCommand.Parameters.AddWithValue("@field", field);
            insertCommand.Parameters.AddWithValue("@type", (object?)type ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("@rules", (object?)SerializeJsonElement(rules) ?? DBNull.Value);
            id = Convert.ToInt64(await insertCommand.ExecuteScalarAsync(cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
        return (await GetBeforeAfterStepByIdAsync(connection, id, cancellationToken), false);
    }

    public async Task<(BeforeAfterStepDto? Step, bool Found, bool Duplicate)> UpdateBeforeAfterStepAdminAsync(long id, string name, bool field, string? type, JsonElement? rules, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var duplicateCount = await ExecuteCountAsync(connection, "SELECT COUNT(*) FROM before_after_steps WHERE name = @name AND id <> @id;", [new SqlParameter("@name", name), new SqlParameter("@id", id)], cancellationToken);
        if (duplicateCount > 0)
        {
            return (null, true, true);
        }

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var updateCommand = CreateCommand(connection, "UPDATE before_after_steps SET name = @name, field = @field, type = @type, rules = @rules, updated_at = SYSUTCDATETIME() WHERE id = @id;"))
        {
            updateCommand.Transaction = transaction;
            updateCommand.Parameters.AddWithValue("@name", name);
            updateCommand.Parameters.AddWithValue("@field", field);
            updateCommand.Parameters.AddWithValue("@type", (object?)type ?? DBNull.Value);
            updateCommand.Parameters.AddWithValue("@rules", (object?)SerializeJsonElement(rules) ?? DBNull.Value);
            updateCommand.Parameters.AddWithValue("@id", id);

            var affected = await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            if (affected == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return (null, false, false);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return (await GetBeforeAfterStepByIdAsync(connection, id, cancellationToken), true, false);
    }

    public async Task<(bool Found, bool InUse)> DeleteBeforeAfterStepAdminAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        string? name = null;
        await using (var command = CreateCommand(connection, "SELECT name FROM before_after_steps WHERE id = @id;"))
        {
            command.Parameters.AddWithValue("@id", id);
            name = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return (false, false);
        }

        var inUse = await GetBeforeAfterStepUsageCountAsync(connection, name, cancellationToken) > 0;
        if (inUse)
        {
            return (true, true);
        }

        await using var deleteCommand = CreateCommand(connection, "DELETE FROM before_after_steps WHERE id = @id;");
        deleteCommand.Parameters.AddWithValue("@id", id);
        await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        return (true, false);
    }

    public async Task<IReadOnlyList<VariableTypeDto>> GetVariableTypesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT id, name, method, executable_method, value, params, CAST(ISNULL(is_encrypted, 0) AS bit) AS is_encrypted
            FROM variable_types
            ORDER BY id;
            """;

        await using var command = CreateCommand(connection, sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<VariableTypeDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new VariableTypeDto
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                Name = GetString(reader, "name"),
                Method = GetString(reader, "method"),
                ExecutableMethod = GetString(reader, "executable_method"),
                Value = GetInt64(reader, "value"),
                Params = GetString(reader, "params"),
                IsEncrypted = GetBoolean(reader, "is_encrypted") ?? false
            });
        }

        return rows;
    }

    public async Task<PagedDataDto<CustomVariableDto>> GetCustomVariablesAsync(ClaimsPrincipal principal, string? scope, long? testCaseId, int page, int limit, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return CreatePagedData<CustomVariableDto>([], 0, limit);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var whereClauses = new List<string> { "cv.client_id = @clientId" };
        var parameters = new List<SqlParameter> { new("@clientId", context.ClientId.Value) };
        var normalizedScope = string.IsNullOrWhiteSpace(scope) ? null : scope.Trim().ToLowerInvariant();

        if (normalizedScope == "global")
        {
            whereClauses.Add("cv.test_case_id IS NULL");
        }
        else if (normalizedScope == "local")
        {
            whereClauses.Add("cv.test_case_id IS NOT NULL");
        }
        else if (string.IsNullOrWhiteSpace(normalizedScope) && testCaseId.HasValue)
        {
            whereClauses.Add("(cv.test_case_id IS NULL OR cv.test_case_id = @testCaseId)");
            parameters.Add(new SqlParameter("@testCaseId", testCaseId.Value));
        }

        var whereSql = string.Join(" AND ", whereClauses);
        var total = await ExecuteCountAsync(connection, $"SELECT COUNT(*) FROM custom_variables cv WHERE {whereSql};", parameters, cancellationToken);

        var sql = $"""
            SELECT
                cv.id,
                cv.name,
                cv.value,
                cv.variable_id,
                cv.test_case_id,
                CAST(ISNULL(cv.is_encrypted, 0) AS bit) AS is_encrypted,
                vt.id AS vt_id,
                vt.name AS vt_name,
                vt.method AS vt_method,
                vt.executable_method AS vt_executable_method,
                vt.value AS vt_value,
                vt.params AS vt_params,
                CAST(ISNULL(vt.is_encrypted, 0) AS bit) AS vt_is_encrypted
            FROM custom_variables cv
            LEFT JOIN variable_types vt ON vt.id = cv.variable_id
            WHERE {whereSql}
            ORDER BY cv.id DESC
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
            """;

        var rows = new List<CustomVariableDto>();
        await using var command = CreateCommand(connection, sql);
        AddParameters(command, parameters);
        command.Parameters.AddWithValue("@offset", (page - 1) * limit);
        command.Parameters.AddWithValue("@limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(MapCustomVariable(reader));
        }

        return CreatePagedData(rows, total, limit);
    }

    public async Task<CustomVariableDto?> CreateCustomVariableAsync(ClaimsPrincipal principal, SaveCustomVariableRequest request, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var variableType = await GetVariableTypeAsync(connection, request.VariableId, cancellationToken);
        if (variableType is null)
        {
            return null;
        }

        const string insertSql = """
            INSERT INTO custom_variables (name, variable_id, client_id, test_case_id, value, is_encrypted, created_at, updated_at)
            OUTPUT INSERTED.id
            VALUES (@name, @variableId, @clientId, @testCaseId, @value, @isEncrypted, SYSUTCDATETIME(), SYSUTCDATETIME());
            """;

        await using var command = CreateCommand(connection, insertSql);
        command.Parameters.AddWithValue("@name", (object?)request.Name?.Trim() ?? DBNull.Value);
        command.Parameters.AddWithValue("@variableId", request.VariableId);
        command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
        command.Parameters.AddWithValue("@testCaseId", (object?)request.TestCaseId ?? DBNull.Value);
        command.Parameters.AddWithValue("@value", (object?)request.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("@isEncrypted", variableType.IsEncrypted);
        var insertedId = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        return await GetCustomVariableByIdAsync(connection, context.ClientId.Value, insertedId, cancellationToken);
    }

    public async Task<CustomVariableDto?> UpdateCustomVariableAsync(ClaimsPrincipal principal, long id, SaveCustomVariableRequest request, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var variableType = await GetVariableTypeAsync(connection, request.VariableId, cancellationToken);
        if (variableType is null)
        {
            return null;
        }

        const string updateSql = """
            UPDATE custom_variables
            SET name = @name,
                variable_id = @variableId,
                test_case_id = @testCaseId,
                value = @value,
                is_encrypted = @isEncrypted,
                updated_at = SYSUTCDATETIME()
            WHERE id = @id AND client_id = @clientId;
            """;

        await using var command = CreateCommand(connection, updateSql);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
        command.Parameters.AddWithValue("@name", (object?)request.Name?.Trim() ?? DBNull.Value);
        command.Parameters.AddWithValue("@variableId", request.VariableId);
        command.Parameters.AddWithValue("@testCaseId", (object?)request.TestCaseId ?? DBNull.Value);
        command.Parameters.AddWithValue("@value", (object?)request.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("@isEncrypted", variableType.IsEncrypted);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected == 0)
        {
            return null;
        }

        return await GetCustomVariableByIdAsync(connection, context.ClientId.Value, id, cancellationToken);
    }

    public async Task<bool> DeleteCustomVariableAsync(ClaimsPrincipal principal, long id, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return false;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = "DELETE FROM custom_variables WHERE id = @id AND client_id = @clientId;";
        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<PagedDataDto<ConfigurationVariableDto>> GetConfigurationVariablesAsync(ClaimsPrincipal principal, int page, int limit, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return CreatePagedData<ConfigurationVariableDto>([], 0, limit);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var parameters = new List<SqlParameter> { new("@clientId", context.ClientId.Value) };
        var total = await ExecuteCountAsync(connection, "SELECT COUNT(*) FROM configuration_variables WHERE client_id = @clientId;", parameters, cancellationToken);

        const string sql = """
            SELECT id, name, description
            FROM configuration_variables
            WHERE client_id = @clientId
            ORDER BY id DESC
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
            """;

        var rows = new List<ConfigurationVariableDto>();
        var ids = new List<long>();
        await using (var command = CreateCommand(connection, sql))
        {
            command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
            command.Parameters.AddWithValue("@offset", (page - 1) * limit);
            command.Parameters.AddWithValue("@limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetInt64(reader.GetOrdinal("id"));
                ids.Add(id);
                rows.Add(new ConfigurationVariableDto
                {
                    Id = id,
                    Name = GetString(reader, "name"),
                    Description = GetString(reader, "description")
                });
            }
        }

        var valuesMap = await LoadConfigurationValuesAsync(connection, ids, cancellationToken);
        var hydrated = rows.Select(row => new ConfigurationVariableDto
        {
            Id = row.Id,
            Name = row.Name,
            Description = row.Description,
            VariableValues = valuesMap.TryGetValue(row.Id, out var values) ? values : []
        }).ToList();

        return CreatePagedData(hydrated, total, limit);
    }

    public async Task<ConfigurationVariableDto?> CreateConfigurationVariableAsync(ClaimsPrincipal principal, SaveConfigurationVariableRequest request, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        long configurationId;
        const string insertSql = """
            INSERT INTO configuration_variables (client_id, name, description, created_at, updated_at)
            OUTPUT INSERTED.id
            VALUES (@clientId, @name, @description, SYSUTCDATETIME(), SYSUTCDATETIME());
            """;

        await using (var command = CreateCommand(connection, insertSql))
        {
            command.Transaction = transaction;
            command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
            command.Parameters.AddWithValue("@name", (object?)request.Name?.Trim() ?? DBNull.Value);
            command.Parameters.AddWithValue("@description", (object?)request.Description?.Trim() ?? DBNull.Value);
            configurationId = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        }

        await InsertConfigurationValuesAsync(connection, transaction, configurationId, request.Values, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await GetConfigurationVariableByIdAsync(connection, context.ClientId.Value, configurationId, cancellationToken);
    }

    public async Task<ConfigurationVariableDto?> UpdateConfigurationVariableAsync(ClaimsPrincipal principal, long id, SaveConfigurationVariableRequest request, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        const string updateSql = """
            UPDATE configuration_variables
            SET name = @name,
                description = @description,
                updated_at = SYSUTCDATETIME()
            WHERE id = @id AND client_id = @clientId;
            """;

        await using (var command = CreateCommand(connection, updateSql))
        {
            command.Transaction = transaction;
            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
            command.Parameters.AddWithValue("@name", (object?)request.Name?.Trim() ?? DBNull.Value);
            command.Parameters.AddWithValue("@description", (object?)request.Description?.Trim() ?? DBNull.Value);
            var affected = await command.ExecuteNonQueryAsync(cancellationToken);
            if (affected == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }
        }

        foreach (var value in request.Values)
        {
            if (value.Id.HasValue)
            {
                const string updateValueSql = """
                    UPDATE configuration_variable_values
                    SET name = @name,
                        updated_at = SYSUTCDATETIME()
                    WHERE id = @id AND variable_id = @variableId;
                    """;

                await using var updateValueCommand = CreateCommand(connection, updateValueSql);
                updateValueCommand.Transaction = transaction;
                updateValueCommand.Parameters.AddWithValue("@id", value.Id.Value);
                updateValueCommand.Parameters.AddWithValue("@variableId", id);
                updateValueCommand.Parameters.AddWithValue("@name", (object?)value.Name?.Trim() ?? DBNull.Value);
                await updateValueCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await InsertConfigurationValuesAsync(connection, transaction, id, request.Values.Where(value => !value.Id.HasValue).ToArray(), cancellationToken);

        if (request.DeletedValues.Count > 0)
        {
            var deleteParameters = AddIdListParameters(new List<SqlParameter>(), "@deleteValueId", request.DeletedValues);
            var deleteSql = $"DELETE FROM configuration_variable_values WHERE variable_id = @variableId AND id IN ({string.Join(", ", deleteParameters)});";
            await using var deleteCommand = CreateCommand(connection, deleteSql);
            deleteCommand.Transaction = transaction;
            deleteCommand.Parameters.AddWithValue("@variableId", id);
            AddParameters(deleteCommand, AddIdListParameterValues(request.DeletedValues, "@deleteValueId"));
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return await GetConfigurationVariableByIdAsync(connection, context.ClientId.Value, id, cancellationToken);
    }

    public async Task<bool> DeleteConfigurationVariableAsync(ClaimsPrincipal principal, long id, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return false;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var deleteValuesCommand = CreateCommand(connection, "DELETE FROM configuration_variable_values WHERE variable_id = @id;"))
        {
            deleteValuesCommand.Transaction = transaction;
            deleteValuesCommand.Parameters.AddWithValue("@id", id);
            await deleteValuesCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var deleteCommand = CreateCommand(connection, "DELETE FROM configuration_variables WHERE id = @id AND client_id = @clientId;");
        deleteCommand.Transaction = transaction;
        deleteCommand.Parameters.AddWithValue("@id", id);
        deleteCommand.Parameters.AddWithValue("@clientId", context.ClientId.Value);
        var affected = await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return affected > 0;
    }

    public async Task<PagedDataDto<ConfigurationDto>> GetConfigurationsAsync(ClaimsPrincipal principal, int page, int limit, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return CreatePagedData<ConfigurationDto>([], 0, limit);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var parameters = new List<SqlParameter> { new("@clientId", context.ClientId.Value) };
        var total = await ExecuteCountAsync(connection, "SELECT COUNT(*) FROM configurations WHERE client_id = @clientId;", parameters, cancellationToken);

        const string sql = """
            SELECT id, name, description, status, created_at
            FROM configurations
            WHERE client_id = @clientId
            ORDER BY id DESC
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
            """;

        var rows = new List<ConfigurationDto>();
        var ids = new List<long>();
        await using (var command = CreateCommand(connection, sql))
        {
            command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
            command.Parameters.AddWithValue("@offset", (page - 1) * limit);
            command.Parameters.AddWithValue("@limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetInt64(reader.GetOrdinal("id"));
                ids.Add(id);
                rows.Add(new ConfigurationDto
                {
                    Id = id,
                    Name = GetString(reader, "name"),
                    Description = GetString(reader, "description"),
                    Status = GetInt32(reader, "status"),
                    CreatedAt = GetDateTimeOffset(reader, "created_at")
                });
            }
        }

        var selections = await LoadConfigurationSelectionsAsync(connection, ids, cancellationToken);
        var hydrated = rows.Select(row => new ConfigurationDto
        {
            Id = row.Id,
            Name = row.Name,
            Description = row.Description,
            Status = row.Status,
            CreatedAt = row.CreatedAt,
            ConfigurationVariables = selections.TryGetValue(row.Id, out var values) ? values : []
        }).ToList();

        return CreatePagedData(hydrated, total, limit);
    }

    public async Task<IReadOnlyList<ExecutionDevicePoolDto>> GetExecutionDevicePoolsAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return [];
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT id, name, status
            FROM execution_device_pools
            WHERE client_id = @clientId
            ORDER BY name;
            """;

        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var rows = new List<ExecutionDevicePoolDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ExecutionDevicePoolDto
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                Name = GetString(reader, "name"),
                Status = GetString(reader, "status")
            });
        }

        return rows;
    }

    public async Task<IReadOnlyList<ExecutionDeviceDto>> GetExecutionDevicesAsync(ClaimsPrincipal principal, long? poolId, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return [];
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var whereClauses = new List<string> { "d.client_id = @clientId" };
        var parameters = new List<SqlParameter> { new("@clientId", context.ClientId.Value) };
        if (poolId.HasValue)
        {
            whereClauses.Add("d.pool_id = @poolId");
            parameters.Add(new SqlParameter("@poolId", poolId.Value));
        }

        var sql = $"""
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
            WHERE {string.Join(" AND ", whereClauses)}
            ORDER BY d.name;
            """;

        await using var command = CreateCommand(connection, sql);
        AddParameters(command, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var rows = new List<ExecutionDeviceDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ExecutionDeviceDto
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                Name = GetString(reader, "name"),
                PoolId = GetInt64(reader, "pool_id"),
                Pool = GetInt64(reader, "pool_ref_id") is long refId ? new BasicRefDto { Id = refId, Name = GetString(reader, "pool_name") } : null,
                Host = GetString(reader, "host"),
                ApiKey = GetString(reader, "api_key"),
                Status = GetString(reader, "status"),
                HealthStatus = GetString(reader, "health_status"),
                RunnerVersion = GetString(reader, "runner_version"),
                MaxConcurrency = GetInt32(reader, "max_concurrency"),
                LastSeenAt = GetDateTimeOffset(reader, "last_seen_at"),
                LastHealthPayload = ParseNullableJsonElement(GetString(reader, "last_health_payload"))
            });
        }

        return rows;
    }

    public async Task<IReadOnlyList<ExecutionScheduleDto>> GetExecutionSchedulesAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return [];
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
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
            WHERE s.client_id = @clientId AND s.deleted_at IS NULL
            ORDER BY s.created_at DESC, s.id DESC;
            """;

        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@clientId", context.ClientId.Value);

        var rows = new List<(long Id, string? Name, string? Cron, string? Timezone, string? RunMode, string? Priority, bool Enabled, DateTimeOffset? LastRunAt, DateTimeOffset? NextRunAt, long? PoolRefId, string? PoolName, string? PayloadJson)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((
                    reader.GetInt64(reader.GetOrdinal("id")),
                    GetString(reader, "name"),
                    GetString(reader, "cron"),
                    GetString(reader, "timezone"),
                    GetString(reader, "run_mode"),
                    GetString(reader, "priority"),
                    GetBoolean(reader, "enabled") ?? true,
                    GetDateTimeOffset(reader, "last_run_at"),
                    GetDateTimeOffset(reader, "next_run_at"),
                    GetInt64(reader, "pool_ref_id"),
                    GetString(reader, "pool_name"),
                    GetString(reader, "payload_json")));
            }
        }

        var result = new List<ExecutionScheduleDto>(rows.Count);
        foreach (var row in rows)
        {
            var payload = await HydrateSchedulePayloadAsync(connection, row.PayloadJson, cancellationToken);
            result.Add(new ExecutionScheduleDto
            {
                Id = row.Id,
                Name = row.Name,
                Cron = row.Cron,
                Timezone = row.Timezone,
                RunMode = row.RunMode,
                Priority = row.Priority,
                Enabled = row.Enabled,
                RunOnce = string.IsNullOrWhiteSpace(row.Cron) && row.NextRunAt.HasValue,
                LastRunAt = row.LastRunAt,
                NextRunAt = row.NextRunAt,
                Pool = row.PoolRefId.HasValue ? new BasicRefDto { Id = row.PoolRefId.Value, Name = row.PoolName } : null,
                PayloadJson = payload.Payload,
                ItemsCount = payload.ItemsCount,
                HasItems = payload.HasItems
            });
        }

        return result;
    }

    public async Task<IReadOnlyList<ExecutionQueueDto>> GetExecutionQueuesAsync(ClaimsPrincipal principal, string? status, string? source, string? priority, long? scheduleId, string? runTarget, long? testPlanId, long? testPlanItemId, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return [];
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var whereClauses = new List<string> { "q.client_id = @clientId" };
        var parameters = new List<SqlParameter> { new("@clientId", context.ClientId.Value) };

        AddOptionalStringFilter(status, "q.status", "@status", whereClauses, parameters);
        AddOptionalStringFilter(source, "q.source", "@source", whereClauses, parameters);
        AddOptionalStringFilter(priority, "q.priority", "@priority", whereClauses, parameters);
        AddOptionalStringFilter(runTarget, "q.run_target", "@runTarget", whereClauses, parameters);
        AddOptionalInt64Filter(scheduleId, "q.schedule_id", "@scheduleId", whereClauses, parameters);
        AddOptionalInt64Filter(testPlanId, "q.test_plan_id", "@testPlanId", whereClauses, parameters);
        AddOptionalInt64Filter(testPlanItemId, "q.test_plan_item_id", "@testPlanItemId", whereClauses, parameters);

        var sql = $"""
            SELECT
                q.id,
                q.queue_code,
                q.status,
                q.priority,
                q.source,
                q.run_mode,
                q.run_target,
                q.created_at,
                p.id AS pool_ref_id,
                p.name AS pool_name,
                s.id AS schedule_ref_id,
                s.name AS schedule_name
            FROM execution_queues q
            LEFT JOIN execution_device_pools p ON p.id = q.pool_id
            LEFT JOIN execution_schedules s ON s.id = q.schedule_id
            WHERE {string.Join(" AND ", whereClauses)}
            ORDER BY q.created_at DESC, q.id DESC;
            """;

        var queueRows = new List<ExecutionQueueDto>();
        var queueIds = new List<long>();
        await using (var command = CreateCommand(connection, sql))
        {
            AddParameters(command, parameters);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetInt64(reader.GetOrdinal("id"));
                queueIds.Add(id);
                queueRows.Add(new ExecutionQueueDto
                {
                    Id = id,
                    QueueCode = GetString(reader, "queue_code"),
                    Status = GetString(reader, "status"),
                    Priority = GetString(reader, "priority"),
                    Source = GetString(reader, "source"),
                    RunMode = GetString(reader, "run_mode"),
                    RunTarget = GetString(reader, "run_target"),
                    CreatedAt = GetDateTimeOffset(reader, "created_at"),
                    Pool = GetInt64(reader, "pool_ref_id") is long poolRefId ? new BasicRefDto { Id = poolRefId, Name = GetString(reader, "pool_name") } : null,
                    Schedule = GetInt64(reader, "schedule_ref_id") is long scheduleRefId ? new BasicRefDto { Id = scheduleRefId, Name = GetString(reader, "schedule_name") } : null
                });
            }
        }

        var itemMap = await LoadExecutionQueueItemsAsync(connection, queueIds, cancellationToken);
        return queueRows.Select(row => new ExecutionQueueDto
        {
            Id = row.Id,
            QueueCode = row.QueueCode,
            Status = row.Status,
            Priority = row.Priority,
            Source = row.Source,
            RunMode = row.RunMode,
            RunTarget = row.RunTarget,
            CreatedAt = row.CreatedAt,
            Pool = row.Pool,
            Schedule = row.Schedule,
            Items = itemMap.TryGetValue(row.Id, out var items) ? items : []
        }).ToList();
    }

    public async Task<ExecutionQueueDto?> GetExecutionQueueAsync(ClaimsPrincipal principal, long id, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT TOP 1
                q.id,
                q.queue_code,
                q.status,
                q.priority,
                q.source,
                q.run_mode,
                q.run_target,
                q.created_at,
                p.id AS pool_ref_id,
                p.name AS pool_name,
                s.id AS schedule_ref_id,
                s.name AS schedule_name
            FROM execution_queues q
            LEFT JOIN execution_device_pools p ON p.id = q.pool_id
            LEFT JOIN execution_schedules s ON s.id = q.schedule_id
            WHERE q.id = @id AND q.client_id = @clientId;
            """;

        ExecutionQueueDto? dto = null;
        await using (var command = CreateCommand(connection, sql))
        {
            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                dto = new ExecutionQueueDto
                {
                    Id = reader.GetInt64(reader.GetOrdinal("id")),
                    QueueCode = GetString(reader, "queue_code"),
                    Status = GetString(reader, "status"),
                    Priority = GetString(reader, "priority"),
                    Source = GetString(reader, "source"),
                    RunMode = GetString(reader, "run_mode"),
                    RunTarget = GetString(reader, "run_target"),
                    CreatedAt = GetDateTimeOffset(reader, "created_at"),
                    Pool = GetInt64(reader, "pool_ref_id") is long poolRefId ? new BasicRefDto { Id = poolRefId, Name = GetString(reader, "pool_name") } : null,
                    Schedule = GetInt64(reader, "schedule_ref_id") is long scheduleRefId ? new BasicRefDto { Id = scheduleRefId, Name = GetString(reader, "schedule_name") } : null
                };
            }
        }

        if (dto is null)
        {
            return null;
        }

        var itemMap = await LoadExecutionQueueItemsAsync(connection, [id], cancellationToken);
        return new ExecutionQueueDto
        {
            Id = dto.Id,
            QueueCode = dto.QueueCode,
            Status = dto.Status,
            Priority = dto.Priority,
            Source = dto.Source,
            RunMode = dto.RunMode,
            RunTarget = dto.RunTarget,
            CreatedAt = dto.CreatedAt,
            Pool = dto.Pool,
            Schedule = dto.Schedule,
            Items = itemMap.TryGetValue(id, out var items) ? items : []
        };
    }

    public async Task<IReadOnlyList<IntegrationConnectionDto>> GetIntegrationConnectionsAsync(ClaimsPrincipal principal, long? projectId, string? provider, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return [];
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var whereClauses = new List<string> { "client_id = @clientId" };
        var parameters = new List<SqlParameter> { new("@clientId", context.ClientId.Value) };

        AddOptionalInt64Filter(projectId, "project_id", "@projectId", whereClauses, parameters);
        AddOptionalStringFilter(provider, "provider", "@provider", whereClauses, parameters);

        var sql = $"""
            SELECT
                id,
                client_id,
                project_id,
                provider,
                name,
                CAST(ISNULL(is_enabled, 0) AS bit) AS is_enabled,
                CAST(ISNULL(sync_test_cases, 0) AS bit) AS sync_test_cases,
                CAST(ISNULL(sync_test_plans, 0) AS bit) AS sync_test_plans,
                CAST(ISNULL(sync_test_runs, 0) AS bit) AS sync_test_runs,
                CAST(ISNULL(sync_defects, 0) AS bit) AS sync_defects,
                CAST(ISNULL(auto_sync_test_cases, 0) AS bit) AS auto_sync_test_cases,
                CAST(ISNULL(auto_sync_test_runs, 0) AS bit) AS auto_sync_test_runs,
                CAST(ISNULL(auto_sync_defects, 0) AS bit) AS auto_sync_defects,
                config_json,
                credentials_encrypted,
                created_by,
                updated_by,
                created_at,
                updated_at
            FROM integration_connections
            WHERE {string.Join(" AND ", whereClauses)}
            ORDER BY id DESC;
            """;

        await using var command = CreateCommand(connection, sql);
        AddParameters(command, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var rows = new List<IntegrationConnectionDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new IntegrationConnectionDto
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                ClientId = reader.GetInt64(reader.GetOrdinal("client_id")),
                ProjectId = GetInt64(reader, "project_id"),
                Provider = GetString(reader, "provider"),
                Name = GetString(reader, "name"),
                IsEnabled = GetBoolean(reader, "is_enabled") ?? false,
                SyncTestCases = GetBoolean(reader, "sync_test_cases") ?? false,
                SyncTestPlans = GetBoolean(reader, "sync_test_plans") ?? false,
                SyncTestRuns = GetBoolean(reader, "sync_test_runs") ?? false,
                SyncDefects = GetBoolean(reader, "sync_defects") ?? false,
                AutoSyncTestCases = GetBoolean(reader, "auto_sync_test_cases") ?? false,
                AutoSyncTestRuns = GetBoolean(reader, "auto_sync_test_runs") ?? false,
                AutoSyncTestDefects = GetBoolean(reader, "auto_sync_defects") ?? false,
                Config = ParseJsonElementOrDefault(GetString(reader, "config_json"), new { }),
                HasCredentials = !string.IsNullOrWhiteSpace(GetString(reader, "credentials_encrypted")),
                CreatedBy = GetInt64(reader, "created_by"),
                UpdatedBy = GetInt64(reader, "updated_by"),
                CreatedAt = GetDateTimeOffset(reader, "created_at"),
                UpdatedAt = GetDateTimeOffset(reader, "updated_at")
            });
        }

        return rows;
    }

    public async Task<IntegrationConnectionDto?> CreateIntegrationConnectionAsync(ClaimsPrincipal principal, SaveIntegrationConnectionRequest request, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue || string.IsNullOrWhiteSpace(request.Provider) || string.IsNullOrWhiteSpace(request.Name))
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        if (request.ProjectId.HasValue && !await ProjectBelongsToClientAsync(connection, context.ClientId.Value, request.ProjectId.Value, cancellationToken))
        {
            return null;
        }

        if (!ValidateIntegrationProviderConfig(request.Provider, request.Config, request.Credentials, isCreate: true))
        {
            return null;
        }

        const string sql = """
            INSERT INTO integration_connections
            (
                client_id,
                project_id,
                provider,
                name,
                is_enabled,
                sync_test_cases,
                sync_test_plans,
                sync_test_runs,
                sync_defects,
                auto_sync_test_cases,
                auto_sync_test_runs,
                auto_sync_defects,
                config_json,
                credentials_encrypted,
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
                @provider,
                @name,
                @isEnabled,
                @syncTestCases,
                @syncTestPlans,
                @syncTestRuns,
                @syncDefects,
                @autoSyncTestCases,
                0,
                0,
                @configJson,
                @credentials,
                @createdBy,
                @updatedBy,
                SYSUTCDATETIME(),
                SYSUTCDATETIME()
            );
            """;

        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
        command.Parameters.AddWithValue("@projectId", (object?)request.ProjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("@provider", request.Provider.Trim());
        command.Parameters.AddWithValue("@name", request.Name.Trim());
        command.Parameters.AddWithValue("@isEnabled", request.IsEnabled ?? false);
        command.Parameters.AddWithValue("@syncTestCases", request.SyncTestCases ?? false);
        command.Parameters.AddWithValue("@syncTestPlans", request.SyncTestPlans ?? false);
        command.Parameters.AddWithValue("@syncTestRuns", request.SyncTestRuns ?? false);
        command.Parameters.AddWithValue("@syncDefects", request.SyncDefects ?? false);
        command.Parameters.AddWithValue("@autoSyncTestCases", request.AutoSyncTestCases ?? false);
        command.Parameters.AddWithValue("@configJson", ToNullableJsonText(request.Config));
        command.Parameters.AddWithValue("@credentials", ToNullableJsonText(request.Credentials));
        command.Parameters.AddWithValue("@createdBy", context.UserId);
        command.Parameters.AddWithValue("@updatedBy", context.UserId);
        var id = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        return await GetIntegrationConnectionByIdAsync(connection, context.ClientId.Value, id, cancellationToken);
    }

    public async Task<IntegrationConnectionDto?> UpdateIntegrationConnectionAsync(ClaimsPrincipal principal, long id, SaveIntegrationConnectionRequest request, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var existing = await GetIntegrationConnectionByIdAsync(connection, context.ClientId.Value, id, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        if (request.ProjectId.HasValue && !await ProjectBelongsToClientAsync(connection, context.ClientId.Value, request.ProjectId.Value, cancellationToken))
        {
            return null;
        }

        var provider = request.Provider?.Trim() ?? existing.Provider ?? string.Empty;
        var configForValidation = request.Config ?? existing.Config;
        var credentialsForValidation = request.Credentials;
        if (!ValidateIntegrationProviderConfig(provider, configForValidation, credentialsForValidation, isCreate: false))
        {
            return null;
        }

        const string sql = """
            UPDATE integration_connections
            SET
                project_id = @projectId,
                name = @name,
                is_enabled = @isEnabled,
                sync_test_cases = @syncTestCases,
                sync_test_plans = @syncTestPlans,
                sync_test_runs = @syncTestRuns,
                sync_defects = @syncDefects,
                auto_sync_test_cases = @autoSyncTestCases,
                auto_sync_test_runs = 0,
                auto_sync_defects = 0,
                config_json = @configJson,
                credentials_encrypted = COALESCE(@credentials, credentials_encrypted),
                updated_by = @updatedBy,
                updated_at = SYSUTCDATETIME()
            WHERE id = @id AND client_id = @clientId;
            """;

        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
        var effectiveProjectId = request.ClearProjectScope == true
            ? DBNull.Value
            : request.ProjectId.HasValue
            ? (object)request.ProjectId.Value
            : existing.ProjectId.HasValue
                ? existing.ProjectId.Value
                : DBNull.Value;
        command.Parameters.AddWithValue("@projectId", effectiveProjectId);
        command.Parameters.AddWithValue("@name", string.IsNullOrWhiteSpace(request.Name) ? (existing.Name ?? string.Empty) : request.Name.Trim());
        command.Parameters.AddWithValue("@isEnabled", request.IsEnabled ?? existing.IsEnabled);
        command.Parameters.AddWithValue("@syncTestCases", request.SyncTestCases ?? existing.SyncTestCases);
        command.Parameters.AddWithValue("@syncTestPlans", request.SyncTestPlans ?? existing.SyncTestPlans);
        command.Parameters.AddWithValue("@syncTestRuns", request.SyncTestRuns ?? existing.SyncTestRuns);
        command.Parameters.AddWithValue("@syncDefects", request.SyncDefects ?? existing.SyncDefects);
        command.Parameters.AddWithValue("@autoSyncTestCases", request.AutoSyncTestCases ?? existing.AutoSyncTestCases);
        command.Parameters.AddWithValue("@configJson", ToNullableJsonText(request.Config) ?? ToNullableJsonText(existing.Config));
        command.Parameters.AddWithValue("@credentials", (object?)ToNullableJsonText(request.Credentials) ?? DBNull.Value);
        command.Parameters.AddWithValue("@updatedBy", context.UserId);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return await GetIntegrationConnectionByIdAsync(connection, context.ClientId.Value, id, cancellationToken);
    }

    public async Task<IReadOnlyList<IntegrationJobDto>> GetIntegrationJobsAsync(ClaimsPrincipal principal, long? connectionId, string? status, int limit, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return [];
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var whereClauses = new List<string> { "j.client_id = @clientId" };
        var parameters = new List<SqlParameter> { new("@clientId", context.ClientId.Value) };

        AddOptionalInt64Filter(connectionId, "j.integration_connection_id", "@connectionId", whereClauses, parameters);
        AddOptionalStringFilter(status, "j.status", "@status", whereClauses, parameters);

        var sql = $"""
            SELECT TOP (@limit)
                j.id,
                j.integration_connection_id,
                j.client_id,
                c.project_id,
                j.entity_type,
                j.internal_id,
                j.status,
                j.attempts,
                j.max_attempts,
                j.last_error,
                j.created_at,
                j.sent_at
            FROM integration_jobs j
            LEFT JOIN integration_connections c ON c.id = j.integration_connection_id
            WHERE {string.Join(" AND ", whereClauses)}
            ORDER BY j.id DESC;
            """;

        await using var command = CreateCommand(connection, sql);
        AddParameters(command, parameters);
        command.Parameters.AddWithValue("@limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var rows = new List<IntegrationJobDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new IntegrationJobDto
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                IntegrationConnectionId = GetInt64(reader, "integration_connection_id"),
                ClientId = reader.GetInt64(reader.GetOrdinal("client_id")),
                ProjectId = GetInt64(reader, "project_id"),
                EntityType = GetString(reader, "entity_type"),
                InternalId = GetInt64(reader, "internal_id"),
                Status = GetString(reader, "status"),
                Attempts = GetInt32(reader, "attempts") ?? 0,
                MaxAttempts = GetInt32(reader, "max_attempts") ?? 0,
                LastError = GetString(reader, "last_error"),
                CreatedAt = GetDateTimeOffset(reader, "created_at"),
                SentAt = GetDateTimeOffset(reader, "sent_at")
            });
        }

        return rows;
    }

    public async Task<IntegrationJobDto?> QueueIntegrationSyncAsync(ClaimsPrincipal principal, QueueIntegrationSyncRequest request, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue || request.ConnectionId <= 0 || request.InternalId <= 0 || string.IsNullOrWhiteSpace(request.EntityType))
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var integrationConnection = await GetIntegrationConnectionByIdAsync(connection, context.ClientId.Value, request.ConnectionId, cancellationToken);
        if (integrationConnection is null || !integrationConnection.IsEnabled || !IsSyncEnabledForEntity(integrationConnection, request.EntityType))
        {
            return null;
        }

        var jobId = await InsertIntegrationJobAsync(connection, context.ClientId.Value, request.ConnectionId, request.EntityType.Trim(), request.InternalId, context.UserId, request.Payload, cancellationToken);
        return await GetIntegrationJobByIdAsync(connection, context.ClientId.Value, jobId, cancellationToken);
    }

    public async Task<IntegrationBulkQueueResultDto> QueueIntegrationBulkSyncAsync(ClaimsPrincipal principal, QueueIntegrationBulkSyncRequest request, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue || string.IsNullOrWhiteSpace(request.EntityType) || request.InternalIds.Count == 0)
        {
            return new IntegrationBulkQueueResultDto { Requested = request.InternalIds.Count };
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var availableConnections = await GetIntegrationConnectionsAsync(principal, request.ProjectId, null, cancellationToken);
        var targets = availableConnections
            .Where(item => item.IsEnabled && IsSyncEnabledForEntity(item, request.EntityType))
            .ToArray();

        var queued = new List<IntegrationJobDto>();
        foreach (var target in targets)
        {
            foreach (var internalId in request.InternalIds.Where(value => value > 0).Distinct())
            {
                var jobId = await InsertIntegrationJobAsync(connection, context.ClientId.Value, target.Id, request.EntityType.Trim(), internalId, context.UserId, null, cancellationToken);
                var job = await GetIntegrationJobByIdAsync(connection, context.ClientId.Value, jobId, cancellationToken);
                if (job is not null)
                {
                    queued.Add(job);
                }
            }
        }

        return new IntegrationBulkQueueResultDto
        {
            Requested = request.InternalIds.Count,
            Queued = queued.Count,
            Jobs = queued
        };
    }

    public async Task<IntegrationJobDto?> RetryIntegrationJobAsync(ClaimsPrincipal principal, long id, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = """
            UPDATE integration_jobs
            SET status = 'pending',
                scheduled_at = SYSUTCDATETIME(),
                last_error = NULL,
                updated_at = SYSUTCDATETIME()
            WHERE id = @id AND client_id = @clientId AND status = 'failed';
            """;
        await using (var command = CreateCommand(connection, sql))
        {
            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
            var affected = await command.ExecuteNonQueryAsync(cancellationToken);
            if (affected == 0)
            {
                return null;
            }
        }

        return await GetIntegrationJobByIdAsync(connection, context.ClientId.Value, id, cancellationToken);
    }

    public async Task<CountResultDto> ReplayFailedIntegrationJobsAsync(ClaimsPrincipal principal, long? connectionId, int limit, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return new CountResultDto();
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var whereClauses = new List<string>
        {
            "client_id = @clientId",
            "status = 'failed'",
            "attempts < max_attempts"
        };
        var parameters = new List<SqlParameter> { new("@clientId", context.ClientId.Value) };
        AddOptionalInt64Filter(connectionId, "integration_connection_id", "@connectionId", whereClauses, parameters);

        var sql = $"""
            WITH targets AS (
                SELECT TOP (@limit) id
                FROM integration_jobs
                WHERE {string.Join(" AND ", whereClauses)}
                ORDER BY id DESC
            )
            UPDATE integration_jobs
            SET status = 'pending',
                last_error = NULL,
                scheduled_at = SYSUTCDATETIME(),
                updated_at = SYSUTCDATETIME()
            WHERE id IN (SELECT id FROM targets);
            """;

        await using var command = CreateCommand(connection, sql);
        AddParameters(command, parameters);
        command.Parameters.AddWithValue("@limit", limit);
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return new CountResultDto { Count = affected };
    }

    public async Task<IntegrationMappingDto?> GetIntegrationMappingAsync(ClaimsPrincipal principal, long connectionId, string entityType, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string connectionSql = "SELECT TOP 1 id FROM integration_connections WHERE id = @id AND client_id = @clientId;";
        await using (var command = CreateCommand(connection, connectionSql))
        {
            command.Parameters.AddWithValue("@id", connectionId);
            command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
            var exists = await command.ExecuteScalarAsync(cancellationToken);
            if (exists is null || exists == DBNull.Value)
            {
                return null;
            }
        }

        const string mappingSql = """
            SELECT TOP 1 field_map_json, status_map_json, priority_map_json, options_json
            FROM integration_mappings
            WHERE integration_connection_id = @connectionId AND entity_type = @entityType;
            """;

        await using var mappingCommand = CreateCommand(connection, mappingSql);
        mappingCommand.Parameters.AddWithValue("@connectionId", connectionId);
        mappingCommand.Parameters.AddWithValue("@entityType", entityType);
        await using var reader = await mappingCommand.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new IntegrationMappingDto
            {
                IntegrationConnectionId = connectionId,
                EntityType = entityType,
                FieldMapJson = JsonSerializer.SerializeToElement(new { }),
                StatusMapJson = JsonSerializer.SerializeToElement(new { }),
                PriorityMapJson = JsonSerializer.SerializeToElement(new { }),
                OptionsJson = JsonSerializer.SerializeToElement(new { })
            };
        }

        return new IntegrationMappingDto
        {
            IntegrationConnectionId = connectionId,
            EntityType = entityType,
            FieldMapJson = ParseJsonElementOrDefault(GetString(reader, "field_map_json"), new { }),
            StatusMapJson = ParseJsonElementOrDefault(GetString(reader, "status_map_json"), new { }),
            PriorityMapJson = ParseJsonElementOrDefault(GetString(reader, "priority_map_json"), new { }),
            OptionsJson = ParseJsonElementOrDefault(GetString(reader, "options_json"), new { })
        };
    }

    public async Task<IntegrationMappingDto?> SaveIntegrationMappingAsync(ClaimsPrincipal principal, long connectionId, string entityType, SaveIntegrationMappingRequest request, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var integrationConnection = await GetIntegrationConnectionByIdAsync(connection, context.ClientId.Value, connectionId, cancellationToken);
        if (integrationConnection is null)
        {
            return null;
        }

        const string sql = """
            MERGE integration_mappings AS target
            USING (SELECT @connectionId AS integration_connection_id, @entityType AS entity_type) AS source
            ON target.integration_connection_id = source.integration_connection_id AND target.entity_type = source.entity_type
            WHEN MATCHED THEN
                UPDATE SET
                    field_map_json = @fieldMapJson,
                    status_map_json = @statusMapJson,
                    priority_map_json = @priorityMapJson,
                    options_json = @optionsJson,
                    updated_by = @updatedBy,
                    updated_at = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
                INSERT (client_id, integration_connection_id, entity_type, field_map_json, status_map_json, priority_map_json, options_json, updated_by, created_at, updated_at)
                VALUES (@clientId, @connectionId, @entityType, @fieldMapJson, @statusMapJson, @priorityMapJson, @optionsJson, @updatedBy, SYSUTCDATETIME(), SYSUTCDATETIME());
            """;

        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
        command.Parameters.AddWithValue("@connectionId", connectionId);
        command.Parameters.AddWithValue("@entityType", entityType);
        command.Parameters.AddWithValue("@fieldMapJson", ToNullableJsonText(request.FieldMapJson) ?? "{}");
        command.Parameters.AddWithValue("@statusMapJson", ToNullableJsonText(request.StatusMapJson) ?? "{}");
        command.Parameters.AddWithValue("@priorityMapJson", ToNullableJsonText(request.PriorityMapJson) ?? "{}");
        command.Parameters.AddWithValue("@optionsJson", ToNullableJsonText(request.OptionsJson) ?? "{}");
        command.Parameters.AddWithValue("@updatedBy", context.UserId);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return await GetIntegrationMappingAsync(principal, connectionId, entityType, cancellationToken);
    }

    public async Task<IntegrationSummaryDto> GetIntegrationOperationsSummaryAsync(ClaimsPrincipal principal, int days, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return new IntegrationSummaryDto { WindowDays = days };
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var from = DateTime.UtcNow.AddDays(-days);

        const string byTypeStatusSql = """
            SELECT entity_type, status, COUNT(*) AS total
            FROM integration_jobs
            WHERE client_id = @clientId AND created_at >= @from
            GROUP BY entity_type, status
            ORDER BY entity_type, status;
            """;

        var byTypeStatus = new List<IntegrationSummaryRowDto>();
        await using (var command = CreateCommand(connection, byTypeStatusSql))
        {
            command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
            command.Parameters.AddWithValue("@from", from);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                byTypeStatus.Add(new IntegrationSummaryRowDto
                {
                    EntityType = GetString(reader, "entity_type"),
                    Status = GetString(reader, "status"),
                    Total = GetInt32(reader, "total") ?? 0
                });
            }
        }

        BasicRefDto? oldestPending = null;
        long? oldestPendingMinutes = null;
        const string oldestPendingSql = "SELECT TOP 1 id, created_at FROM integration_jobs WHERE client_id = @clientId AND status = 'pending' ORDER BY created_at;";
        await using (var command = CreateCommand(connection, oldestPendingSql))
        {
            command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var createdAt = GetDateTimeOffset(reader, "created_at");
                oldestPending = new BasicRefDto { Id = reader.GetInt64(reader.GetOrdinal("id")) };
                if (createdAt.HasValue)
                {
                    oldestPendingMinutes = Math.Max(0L, Convert.ToInt64((DateTimeOffset.UtcNow - createdAt.Value).TotalMinutes));
                }
            }
        }

        const string topErrorsSql = """
            SELECT TOP (10)
                LEFT(last_error, 160) AS reason,
                COUNT(*) AS total
            FROM integration_jobs
            WHERE client_id = @clientId AND status = 'failed' AND last_error IS NOT NULL
            GROUP BY LEFT(last_error, 160)
            ORDER BY COUNT(*) DESC, LEFT(last_error, 160);
            """;

        var topErrors = new List<IntegrationErrorReasonDto>();
        await using (var command = CreateCommand(connection, topErrorsSql))
        {
            command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                topErrors.Add(new IntegrationErrorReasonDto
                {
                    Reason = GetString(reader, "reason"),
                    Total = GetInt32(reader, "total") ?? 0
                });
            }
        }

        return new IntegrationSummaryDto
        {
            WindowDays = days,
            ByTypeStatus = byTypeStatus,
            OldestPending = oldestPending,
            OldestPendingMinutes = oldestPendingMinutes,
            TopErrorReasons = topErrors
        };
    }

    public async Task<IntegrationHealthDto> GetIntegrationHealthAsync(ClaimsPrincipal principal, int pendingSlaMinutes, double failureRateThreshold, int windowMinutes, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return new IntegrationHealthDto
            {
                WindowMinutes = windowMinutes,
                PendingSlaMinutes = pendingSlaMinutes,
                FailureRateThreshold = failureRateThreshold,
                IsHealthy = true
            };
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);

        long oldestPendingMinutes = 0;
        const string oldestPendingSql = "SELECT TOP 1 created_at FROM integration_jobs WHERE client_id = @clientId AND status = 'pending' ORDER BY created_at;";
        await using (var command = CreateCommand(connection, oldestPendingSql))
        {
            command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var createdAt = GetDateTimeOffset(reader, "created_at");
                if (createdAt.HasValue)
                {
                    oldestPendingMinutes = Math.Max(0L, Convert.ToInt64((DateTimeOffset.UtcNow - createdAt.Value).TotalMinutes));
                }
            }
        }

        var from = DateTime.UtcNow.AddMinutes(-windowMinutes);
        const string recentSql = """
            SELECT
                COUNT(*) AS total,
                SUM(CASE WHEN status = 'failed' THEN 1 ELSE 0 END) AS failed
            FROM integration_jobs
            WHERE client_id = @clientId AND created_at >= @from;
            """;

        var total = 0;
        var failed = 0;
        await using (var command = CreateCommand(connection, recentSql))
        {
            command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
            command.Parameters.AddWithValue("@from", from);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                total = GetInt32(reader, "total") ?? 0;
                failed = GetInt32(reader, "failed") ?? 0;
            }
        }

        var failureRate = total > 0 ? Math.Round((double)failed / total, 4) : 0d;
        var alerts = new List<string>();
        if (oldestPendingMinutes > pendingSlaMinutes)
        {
            alerts.Add("pending_age_exceeded");
        }

        if (failureRate > failureRateThreshold)
        {
            alerts.Add("failure_rate_exceeded");
        }

        return new IntegrationHealthDto
        {
            WindowMinutes = windowMinutes,
            PendingSlaMinutes = pendingSlaMinutes,
            FailureRateThreshold = failureRateThreshold,
            OldestPendingMinutes = oldestPendingMinutes,
            RecentTotal = total,
            RecentFailed = failed,
            RecentFailureRate = failureRate,
            Alerts = alerts,
            IsHealthy = alerts.Count == 0
        };
    }

    public async Task<PagedDataDto<TestPlanDto>> GetTestPlansAsync(ClaimsPrincipal principal, string? query, string? planType, string? planStatus, long? projectId, bool? isActive, int page, int limit, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return CreatePagedData<TestPlanDto>([], 0, limit);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var whereClauses = new List<string> { "tp.client_id = @clientId" };
        var parameters = new List<SqlParameter> { new("@clientId", context.ClientId.Value) };

        if (!string.IsNullOrWhiteSpace(query))
        {
            whereClauses.Add("(tp.name LIKE @query OR CAST(tp.id AS nvarchar(50)) LIKE @query)");
            parameters.Add(new SqlParameter("@query", $"%{query.Trim()}%"));
        }

        AddOptionalStringFilter(planType, "tp.plan_type", "@planType", whereClauses, parameters);
        AddOptionalStringFilter(planStatus, "tp.plan_status", "@planStatus", whereClauses, parameters);
        AddOptionalInt64Filter(projectId, "tp.project_id", "@projectId", whereClauses, parameters);

        if (isActive.HasValue)
        {
            whereClauses.Add("CAST(ISNULL(tp.is_active, CASE WHEN ISNULL(tp.status, 1) = 1 THEN 1 ELSE 0 END) AS bit) = @isActive");
            parameters.Add(new SqlParameter("@isActive", isActive.Value));
        }

        var whereSql = string.Join(" AND ", whereClauses);
        var total = await ExecuteCountAsync(connection, $"SELECT COUNT(*) FROM test_plans tp WHERE {whereSql};", parameters, cancellationToken);

        var sql = $"""
            SELECT
                tp.id,
                tp.name,
                tp.area_path,
                tp.iteration_path,
                tp.plan_type,
                tp.plan_status,
                CAST(ISNULL(tp.is_active, CASE WHEN ISNULL(tp.status, 1) = 1 THEN 1 ELSE 0 END) AS bit) AS is_active,
                tp.status,
                tp.start_date,
                tp.end_date,
                tp.last_updated,
                tp.updated_at,
                tp.target_version,
                tp.objective,
                tp.project_id,
                p.id AS project_ref_id,
                p.project_name,
                p.area_path AS project_area_path,
                owner_user.id AS owner_id,
                owner_user.name AS owner_name,
                owner_user.email AS owner_email
            FROM test_plans tp
            LEFT JOIN projects p ON p.id = tp.project_id
            LEFT JOIN users owner_user ON owner_user.id = tp.owner_user_id
            WHERE {whereSql}
            ORDER BY tp.updated_at DESC, tp.id DESC
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
            """;

        var rows = new List<TestPlanDto>();
        var planIds = new List<long>();
        await using (var command = CreateCommand(connection, sql))
        {
            AddParameters(command, parameters);
            command.Parameters.AddWithValue("@offset", (page - 1) * limit);
            command.Parameters.AddWithValue("@limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetInt64(reader.GetOrdinal("id"));
                planIds.Add(id);
                rows.Add(new TestPlanDto
                {
                    Id = id,
                    Name = GetString(reader, "name"),
                    AreaPath = GetString(reader, "area_path"),
                    IterationPath = GetString(reader, "iteration_path"),
                    PlanType = GetString(reader, "plan_type"),
                    PlanStatus = GetString(reader, "plan_status"),
                    IsActive = GetBoolean(reader, "is_active"),
                    Status = GetInt32(reader, "status"),
                    StartDate = GetDateString(reader, "start_date"),
                    EndDate = GetDateString(reader, "end_date"),
                    LastUpdated = GetString(reader, "last_updated"),
                    UpdatedAt = GetDateTimeOffset(reader, "updated_at"),
                    TargetVersion = GetString(reader, "target_version"),
                    Objective = GetString(reader, "objective"),
                    ProjectId = GetInt64(reader, "project_id"),
                    Project = GetInt64(reader, "project_ref_id") is long projectRefId
                        ? new TestPlanProjectDto
                        {
                            Id = projectRefId,
                            ProjectName = GetString(reader, "project_name"),
                            AreaPath = GetString(reader, "project_area_path")
                        }
                        : null,
                    Owner = GetInt64(reader, "owner_id") is long ownerId
                        ? new UserBasicDto
                        {
                            Id = ownerId,
                            Name = GetString(reader, "owner_name"),
                            Email = GetString(reader, "owner_email")
                        }
                        : null
                });
            }
        }

        var usersMap = await LoadTestPlanUsersAsync(connection, planIds, cancellationToken);
        return CreatePagedData(rows.Select(row => new TestPlanDto
        {
            Id = row.Id,
            Name = row.Name,
            AreaPath = row.AreaPath,
            IterationPath = row.IterationPath,
            PlanType = row.PlanType,
            PlanStatus = row.PlanStatus,
            IsActive = row.IsActive,
            Status = row.Status,
            StartDate = row.StartDate,
            EndDate = row.EndDate,
            LastUpdated = row.LastUpdated,
            UpdatedAt = row.UpdatedAt,
            TargetVersion = row.TargetVersion,
            Objective = row.Objective,
            ProjectId = row.ProjectId,
            Project = row.Project,
            Owner = row.Owner,
            Users = usersMap.TryGetValue(row.Id, out var users) ? users : []
        }).ToList(), total, limit);
    }

    public async Task<TestPlanDto?> GetTestPlanAsync(ClaimsPrincipal principal, long id, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT TOP 1
                tp.id,
                tp.name,
                tp.area_path,
                tp.iteration_path,
                tp.plan_type,
                tp.plan_status,
                CAST(ISNULL(tp.is_active, CASE WHEN ISNULL(tp.status, 1) = 1 THEN 1 ELSE 0 END) AS bit) AS is_active,
                tp.status,
                tp.start_date,
                tp.end_date,
                tp.last_updated,
                tp.updated_at,
                tp.target_version,
                tp.objective,
                tp.project_id,
                p.id AS project_ref_id,
                p.project_name,
                p.area_path AS project_area_path,
                owner_user.id AS owner_id,
                owner_user.name AS owner_name,
                owner_user.email AS owner_email
            FROM test_plans tp
            LEFT JOIN projects p ON p.id = tp.project_id
            LEFT JOIN users owner_user ON owner_user.id = tp.owner_user_id
            WHERE tp.id = @id AND tp.client_id = @clientId;
            """;

        TestPlanDto? row = null;
        await using (var command = CreateCommand(connection, sql))
        {
            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                row = new TestPlanDto
                {
                    Id = reader.GetInt64(reader.GetOrdinal("id")),
                    Name = GetString(reader, "name"),
                    AreaPath = GetString(reader, "area_path"),
                    IterationPath = GetString(reader, "iteration_path"),
                    PlanType = GetString(reader, "plan_type"),
                    PlanStatus = GetString(reader, "plan_status"),
                    IsActive = GetBoolean(reader, "is_active"),
                    Status = GetInt32(reader, "status"),
                    StartDate = GetDateString(reader, "start_date"),
                    EndDate = GetDateString(reader, "end_date"),
                    LastUpdated = GetString(reader, "last_updated"),
                    UpdatedAt = GetDateTimeOffset(reader, "updated_at"),
                    TargetVersion = GetString(reader, "target_version"),
                    Objective = GetString(reader, "objective"),
                    ProjectId = GetInt64(reader, "project_id"),
                    Project = GetInt64(reader, "project_ref_id") is long projectRefId
                        ? new TestPlanProjectDto { Id = projectRefId, ProjectName = GetString(reader, "project_name"), AreaPath = GetString(reader, "project_area_path") }
                        : null,
                    Owner = GetInt64(reader, "owner_id") is long ownerId
                        ? new UserBasicDto { Id = ownerId, Name = GetString(reader, "owner_name"), Email = GetString(reader, "owner_email") }
                        : null
                };
            }
        }

        if (row is null)
        {
            return null;
        }

        var usersMap = await LoadTestPlanUsersAsync(connection, [id], cancellationToken);
        return new TestPlanDto
        {
            Id = row.Id,
            Name = row.Name,
            AreaPath = row.AreaPath,
            IterationPath = row.IterationPath,
            PlanType = row.PlanType,
            PlanStatus = row.PlanStatus,
            IsActive = row.IsActive,
            Status = row.Status,
            StartDate = row.StartDate,
            EndDate = row.EndDate,
            LastUpdated = row.LastUpdated,
            UpdatedAt = row.UpdatedAt,
            TargetVersion = row.TargetVersion,
            Objective = row.Objective,
            ProjectId = row.ProjectId,
            Project = row.Project,
            Owner = row.Owner,
            Users = usersMap.TryGetValue(id, out var users) ? users : []
        };
    }

    public async Task<PagedDataDto<TestPlanItemDto>> GetTestPlanItemsAsync(ClaimsPrincipal principal, int page, int limit, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return CreatePagedData<TestPlanItemDto>([], 0, limit);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await EnsureTestPlanItemSortOrderColumnAsync(connection, null, cancellationToken);
        var parameters = new List<SqlParameter> { new("@clientId", context.ClientId.Value) };
        const string countSql = """
            SELECT COUNT(*)
            FROM test_plan_items tpi
            INNER JOIN test_plans tp ON tp.id = tpi.test_plan_id
            WHERE tp.client_id = @clientId;
            """;
        var total = await ExecuteCountAsync(connection, countSql, parameters, cancellationToken);

        const string sql = """
            SELECT tpi.id, tpi.name, tpi.test_plan_id, tpi.sort_order
            FROM test_plan_items tpi
            INNER JOIN test_plans tp ON tp.id = tpi.test_plan_id
            WHERE tp.client_id = @clientId
            ORDER BY ISNULL(tpi.sort_order, 2147483647), tpi.id
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
            """;

        var rows = new List<TestPlanItemDto>();
        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
        command.Parameters.AddWithValue("@offset", (page - 1) * limit);
        command.Parameters.AddWithValue("@limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new TestPlanItemDto
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                Name = GetString(reader, "name"),
                TestPlanId = GetInt64(reader, "test_plan_id"),
                SortOrder = GetInt32(reader, "sort_order")
            });
        }

        return CreatePagedData(rows, total, limit);
    }

    public async Task<IReadOnlyList<TestPlanItemDto>> GetTestPlanItemsForPlanAsync(ClaimsPrincipal principal, long testPlanId, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return [];
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await EnsureTestPlanItemSortOrderColumnAsync(connection, null, cancellationToken);
        const string sql = """
            SELECT tpi.id, tpi.name, tpi.test_plan_id, tpi.sort_order
            FROM test_plan_items tpi
            INNER JOIN test_plans tp ON tp.id = tpi.test_plan_id
            WHERE tpi.test_plan_id = @testPlanId AND tp.client_id = @clientId
            ORDER BY ISNULL(tpi.sort_order, 2147483647), tpi.id;
            """;

        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@testPlanId", testPlanId);
        command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<TestPlanItemDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new TestPlanItemDto
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                Name = GetString(reader, "name"),
                TestPlanId = GetInt64(reader, "test_plan_id"),
                SortOrder = GetInt32(reader, "sort_order")
            });
        }

        return rows;
    }

    public async Task<IReadOnlyList<TestStateDto>> GetTestSuiteStatesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = "SELECT id, name FROM test_states ORDER BY id;";
        await using var command = CreateCommand(connection, sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<TestStateDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new TestStateDto { Id = reader.GetInt64(reader.GetOrdinal("id")), Name = GetString(reader, "name") });
        }

        return rows;
    }

    private async Task<long?> ResolveTestStateIdByNameAsync(string name, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = "SELECT TOP 1 id FROM test_states WHERE LOWER(name) = LOWER(@name) ORDER BY id;";
        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@name", name);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null || result == DBNull.Value)
        {
            return null;
        }

        return Convert.ToInt64(result);
    }

    public async Task<object> GetTestSuitesAsync(ClaimsPrincipal principal, string? query, string? tags, long? projectId, long? testStateId, int? testSuiteType, long? testPlanItemId, int page, int limit, bool light, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return limit > 0 ? CreatePagedData<TestSuiteListDto>([], 0, limit) : Array.Empty<TestSuiteListDto>();
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var whereClauses = new List<string> { "td.client_id = @clientId", "td.parent_id IS NULL", "td.deleted_at IS NULL" };
        var parameters = new List<SqlParameter> { new("@clientId", context.ClientId.Value) };

        if (!string.IsNullOrWhiteSpace(query))
        {
            whereClauses.Add("(td.title LIKE @query OR td.test_title LIKE @query OR CAST(td.id AS nvarchar(50)) LIKE @query)");
            parameters.Add(new SqlParameter("@query", $"%{query.Trim()}%"));
        }

        if (!string.IsNullOrWhiteSpace(tags))
        {
            whereClauses.Add("td.tags LIKE @tags");
            parameters.Add(new SqlParameter("@tags", $"%{tags.Trim()}%"));
        }

        AddOptionalInt64Filter(projectId, "td.project_id", "@projectId", whereClauses, parameters);
        AddOptionalInt64Filter(testStateId, "td.test_state_id", "@testStateId", whereClauses, parameters);
        if (testSuiteType.HasValue)
        {
            whereClauses.Add("td.test_suite_type = @testSuiteType");
            parameters.Add(new SqlParameter("@testSuiteType", testSuiteType.Value));
        }

        if (testPlanItemId.HasValue)
        {
            whereClauses.Add("""
                NOT EXISTS (
                    SELECT 1
                    FROM test_plan_item_suites tpis
                    WHERE tpis.test_plan_item_id = @testPlanItemId
                      AND tpis.test_design_id = td.id
                      AND tpis.deleted_at IS NULL
                )
                """);
            parameters.Add(new SqlParameter("@testPlanItemId", testPlanItemId.Value));
        }

        var whereSql = string.Join(" AND ", whereClauses);
        var total = await ExecuteCountAsync(connection, $"SELECT COUNT(*) FROM test_designs td WHERE {whereSql};", parameters, cancellationToken);

        var sql = $"""
            WITH last_run AS (
                SELECT tri.test_suite_id, MAX(tri.id) AS last_item_id
                FROM test_runner_items tri
                INNER JOIN test_runners tr ON tr.id = tri.test_runner_id
                WHERE tr.client_id = @clientId AND tri.status_id IN (3, 4, 5, 6)
                GROUP BY tri.test_suite_id
            )
            SELECT
                td.id,
                td.title,
                td.test_title,
                td.parent_id,
                td.test_suite_type,
                td.test_state_id,
                td.created_by,
                td.updated_by,
                ts.id AS state_ref_id,
                ts.name AS state_name,
                p.project_name,
                td.tags,
                {(light ? "CAST(NULL AS nvarchar(255)) AS priority" : "td.priority")},
                tri_last.updated_at AS last_run,
                COALESCE(css.name,
                    CASE tri_last.status_id
                        WHEN 1 THEN 'Not Started'
                        WHEN 2 THEN 'In Progress'
                        WHEN 3 THEN 'Passed'
                        WHEN 4 THEN 'Failed'
                        WHEN 5 THEN 'Glitch - Fail'
                        WHEN 6 THEN 'Retest'
                        ELSE NULL
                    END
                ) AS last_result
            FROM test_designs td
            LEFT JOIN projects p ON p.id = td.project_id
            LEFT JOIN test_states ts ON ts.id = td.test_state_id
            LEFT JOIN last_run lr ON lr.test_suite_id = td.id
            LEFT JOIN test_runner_items tri_last ON tri_last.id = lr.last_item_id
            LEFT JOIN component_step_statuses css ON css.id = tri_last.status_id
            WHERE {whereSql}
            ORDER BY td.id DESC
            {(limit > 0 ? "OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY" : string.Empty)};
            """;

        var rows = new List<TestSuiteListDto>();
        await using var command = CreateCommand(connection, sql);
        AddParameters(command, parameters);
        if (limit > 0)
        {
            command.Parameters.AddWithValue("@offset", (page - 1) * limit);
            command.Parameters.AddWithValue("@limit", limit);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new TestSuiteListDto
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                Title = GetString(reader, "title"),
                TestTitle = GetString(reader, "test_title"),
                ParentId = GetInt64(reader, "parent_id"),
                TestSuiteType = GetInt32(reader, "test_suite_type"),
                TestStateId = GetInt64(reader, "test_state_id"),
                CreatedBy = GetString(reader, "created_by"),
                UpdatedBy = GetString(reader, "updated_by"),
                State = GetInt64(reader, "state_ref_id") is long stateId ? new BasicRefDto { Id = stateId, Name = GetString(reader, "state_name") } : null,
                ProjectName = GetString(reader, "project_name"),
                Tags = GetString(reader, "tags"),
                Priority = GetString(reader, "priority"),
                LastRun = GetDateTimeOffset(reader, "last_run"),
                LastResult = GetString(reader, "last_result")
            });
        }

        return limit > 0 ? CreatePagedData(rows, total, limit) : rows;
    }

    public async Task<IReadOnlyList<string>> GetSharedTestSuiteTagsAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return Array.Empty<string>();
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT tags
            FROM test_designs
            WHERE client_id = @clientId
              AND parent_id IS NULL
              AND deleted_at IS NULL
              AND tags IS NOT NULL;
            """;

        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            foreach (var tag in ParseTags(GetString(reader, "tags")))
            {
                if (!string.IsNullOrWhiteSpace(tag))
                {
                    results.Add(tag.Trim());
                }
            }
        }

        return results
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<IReadOnlyList<string>> RenameSharedTestSuiteTagAsync(ClaimsPrincipal principal, string oldTag, string newTag, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return Array.Empty<string>();
        }

        var normalizedOldTag = oldTag.Trim();
        var normalizedNewTag = newTag.Trim();
        if (string.IsNullOrWhiteSpace(normalizedOldTag) || string.IsNullOrWhiteSpace(normalizedNewTag))
        {
            return await GetSharedTestSuiteTagsAsync(principal, cancellationToken);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string selectSql = """
            SELECT id, tags
            FROM test_designs
            WHERE client_id = @clientId
              AND parent_id IS NULL
              AND deleted_at IS NULL
              AND tags IS NOT NULL;
            """;

        var updates = new List<(long Id, string Tags)>();
        await using (var selectCommand = CreateCommand(connection, selectSql))
        {
            selectCommand.Parameters.AddWithValue("@clientId", context.ClientId.Value);
            await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var existingTags = ParseTags(GetString(reader, "tags")).ToList();
                if (existingTags.Count == 0)
                {
                    continue;
                }

                var renamed = false;
                for (var i = 0; i < existingTags.Count; i++)
                {
                    if (string.Equals(existingTags[i], normalizedOldTag, StringComparison.OrdinalIgnoreCase))
                    {
                        existingTags[i] = normalizedNewTag;
                        renamed = true;
                    }
                }

                if (!renamed)
                {
                    continue;
                }

                var normalizedJson = NormalizeSuiteTags(JsonSerializer.SerializeToElement(existingTags.ToArray()));
                if (!string.IsNullOrWhiteSpace(normalizedJson))
                {
                    updates.Add((reader.GetInt64(reader.GetOrdinal("id")), normalizedJson));
                }
            }
        }

        if (updates.Count > 0)
        {
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                const string updateSql = """
                    UPDATE test_designs
                    SET tags = @tags,
                        updated_at = SYSUTCDATETIME()
                    WHERE id = @id
                      AND client_id = @clientId
                      AND deleted_at IS NULL;
                    """;

                foreach (var update in updates)
                {
                    await using var updateCommand = CreateCommand(connection, updateSql);
                    updateCommand.Transaction = transaction;
                    updateCommand.Parameters.AddWithValue("@id", update.Id);
                    updateCommand.Parameters.AddWithValue("@clientId", context.ClientId.Value);
                    updateCommand.Parameters.AddWithValue("@tags", update.Tags);
                    await updateCommand.ExecuteNonQueryAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }

        return await GetSharedTestSuiteTagsAsync(principal, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> DeleteSharedTestSuiteTagAsync(ClaimsPrincipal principal, string tag, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return Array.Empty<string>();
        }

        var normalizedTag = tag.Trim();
        if (string.IsNullOrWhiteSpace(normalizedTag))
        {
            return await GetSharedTestSuiteTagsAsync(principal, cancellationToken);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string selectSql = """
            SELECT id, tags
            FROM test_designs
            WHERE client_id = @clientId
              AND parent_id IS NULL
              AND deleted_at IS NULL
              AND tags IS NOT NULL;
            """;

        var updates = new List<(long Id, string? Tags)>();
        await using (var selectCommand = CreateCommand(connection, selectSql))
        {
            selectCommand.Parameters.AddWithValue("@clientId", context.ClientId.Value);
            await using var reader = await selectCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var remaining = ParseTags(GetString(reader, "tags"))
                    .Where(existing => !string.Equals(existing, normalizedTag, StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                var existingTags = ParseTags(GetString(reader, "tags")).ToArray();
                if (remaining.Length == existingTags.Length)
                {
                    continue;
                }

                var normalizedJson = remaining.Length > 0
                    ? NormalizeSuiteTags(JsonSerializer.SerializeToElement(remaining))
                    : null;
                updates.Add((reader.GetInt64(reader.GetOrdinal("id")), normalizedJson));
            }
        }

        if (updates.Count > 0)
        {
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                const string updateSql = """
                    UPDATE test_designs
                    SET tags = @tags,
                        updated_at = SYSUTCDATETIME()
                    WHERE id = @id
                      AND client_id = @clientId
                      AND deleted_at IS NULL;
                    """;

                foreach (var update in updates)
                {
                    await using var updateCommand = CreateCommand(connection, updateSql);
                    updateCommand.Transaction = transaction;
                    updateCommand.Parameters.AddWithValue("@id", update.Id);
                    updateCommand.Parameters.AddWithValue("@clientId", context.ClientId.Value);
                    updateCommand.Parameters.AddWithValue("@tags", (object?)update.Tags ?? DBNull.Value);
                    await updateCommand.ExecuteNonQueryAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }

        return await GetSharedTestSuiteTagsAsync(principal, cancellationToken);
    }

    public async Task<TestSuiteFullDto?> GetTestSuiteFullAsync(ClaimsPrincipal principal, long testSuiteId, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
    await EnsureTestLevelDatasetsSchemaAsync(connection, cancellationToken);
        const string suiteSql = """
            SELECT TOP 1
                td.id,
                td.title,
                td.test_state_id,
                td.test_suite_type,
                td.folder_path_id,
                td.comment,
                td.project_id,
                td.azure_iteration_path,
                td.priority,
                td.story_id,
                td.test_title,
                td.tags,
                td.parent_id,
                td.configuration_id,
                CAST(ISNULL(td.kba_ready, 0) AS bit) AS kba_ready,
                CAST(ISNULL(td.training_ready, 0) AS bit) AS training_ready,
                CAST(ISNULL(td.release_notes_ready, 0) AS bit) AS release_notes_ready
            FROM test_designs td
            WHERE td.id = @testSuiteId AND td.client_id = @clientId AND td.deleted_at IS NULL;
            """;

        TestSuiteFullDto? suite;
        await using (var command = CreateCommand(connection, suiteSql))
        {
            command.Parameters.AddWithValue("@testSuiteId", testSuiteId);
            command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            suite = new TestSuiteFullDto
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                Title = GetString(reader, "title"),
                TestStateId = GetInt64(reader, "test_state_id"),
                TestSuiteType = GetInt32(reader, "test_suite_type"),
                FolderPathId = GetInt64(reader, "folder_path_id"),
                Comment = GetString(reader, "comment"),
                ProjectId = GetInt64(reader, "project_id"),
                IterationPath = GetString(reader, "azure_iteration_path"),
                Priority = GetString(reader, "priority"),
                StoryId = GetString(reader, "story_id"),
                TestTitle = GetString(reader, "test_title"),
                Tags = GetString(reader, "tags"),
                ParentId = GetInt64(reader, "parent_id"),
                ConfigurationId = GetInt64(reader, "configuration_id"),
                KbaReady = GetBoolean(reader, "kba_ready") ?? false,
                TrainingReady = GetBoolean(reader, "training_ready") ?? false,
                ReleaseNotesReady = GetBoolean(reader, "release_notes_ready") ?? false,
            };
        }

        var components = await LoadFullTestSuiteComponentsAsync(connection, context.ClientId.Value, testSuiteId, cancellationToken);
    var datasets = await LoadTestDesignDatasetsAsync(connection, testSuiteId, cancellationToken);
        return new TestSuiteFullDto
        {
            Id = suite.Id,
            Title = suite.Title,
            TestStateId = suite.TestStateId,
            TestSuiteType = suite.TestSuiteType,
            FolderPathId = suite.FolderPathId,
            Comment = suite.Comment,
            ProjectId = suite.ProjectId,
            IterationPath = suite.IterationPath,
            Priority = suite.Priority,
            StoryId = suite.StoryId,
            TestTitle = suite.TestTitle,
            Tags = suite.Tags,
            ParentId = suite.ParentId,
            ConfigurationId = suite.ConfigurationId,
            KbaReady = suite.KbaReady,
            TrainingReady = suite.TrainingReady,
            ReleaseNotesReady = suite.ReleaseNotesReady,
            Components = components,
            Datasets = datasets
        };
    }

    public async Task<IReadOnlyList<TestSuiteFullDatasetDto>?> GetTestComponentDatasetsAsync(ClaimsPrincipal principal, long testSuiteId, long testComponentId, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string testComponentSql = """
            SELECT TOP 1 tc.id
            FROM test_components tc
            INNER JOIN test_designs td ON td.id = tc.test_design_id
            WHERE tc.id = @testComponentId
              AND tc.test_design_id = @testSuiteId
              AND td.client_id = @clientId
              AND td.deleted_at IS NULL
              AND tc.deleted_at IS NULL;
            """;

        await using (var command = CreateCommand(connection, testComponentSql))
        {
            command.Parameters.AddWithValue("@testComponentId", testComponentId);
            command.Parameters.AddWithValue("@testSuiteId", testSuiteId);
            command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
            var exists = await command.ExecuteScalarAsync(cancellationToken);
            if (exists is null)
            {
                return null;
            }
        }

        var datasetMap = await LoadTestSuiteDatasetMapAsync(connection, [testComponentId], cancellationToken);
        return datasetMap.TryGetValue(testComponentId, out var datasets) ? datasets : [];
    }

    private async Task<(bool NotFound, List<long> AffectedSuiteIds, long? ProjectId)> PrepareDatasetMutationScopeAsync(
        SqlConnection connection,
        long clientId,
        long testSuiteId,
        CancellationToken cancellationToken)
    {
        var suiteProjectId = await GetScopedTestSuiteProjectIdAsync(connection, clientId, testSuiteId, cancellationToken);
        if (suiteProjectId.NotFound)
        {
            return (true, [], null);
        }

        var affectedSuiteIds = new List<long> { testSuiteId };
        affectedSuiteIds.AddRange(await LoadChildSuiteIdsAsync(connection, clientId, testSuiteId, cancellationToken));
        return (false, affectedSuiteIds, suiteProjectId.ProjectId);
    }

    public async Task<TestSuiteFullDatasetDto?> UpdateTestComponentDatasetAsync(ClaimsPrincipal principal, long testSuiteId, long testComponentId, long datasetId, SaveTestSuiteDatasetRequest request, CancellationToken cancellationToken = default)
    {
        _editSessionService.EnsureCanEdit(principal, testSuiteId);
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var mutationScope = await PrepareDatasetMutationScopeAsync(connection, context.ClientId.Value, testSuiteId, cancellationToken);
        if (mutationScope.NotFound)
        {
            return null;
        }

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        if (!await DatasetBelongsToSuiteAsync(connection, transaction, context.ClientId.Value, testSuiteId, testComponentId, datasetId, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var hasSortOrder = await HasDataSetSortOrderColumnAsync(connection, transaction, cancellationToken);
        var updateSql = hasSortOrder
            ? "UPDATE data_sets SET scenario=@scenario,status=@status,sort_order=COALESCE(@sortOrder,sort_order),updated_at=SYSUTCDATETIME() WHERE id=@datasetId;"
            : "UPDATE data_sets SET scenario=@scenario,status=@status,updated_at=SYSUTCDATETIME() WHERE id=@datasetId;";
        await using (var command = CreateCommand(connection, updateSql))
        {
            command.Transaction = transaction;
            command.Parameters.AddWithValue("@datasetId", datasetId);
            command.Parameters.AddWithValue("@scenario", (object?)NormalizeOptionalText(request.Scenario) ?? DBNull.Value);
            command.Parameters.AddWithValue("@status", request.Status ?? false);
            if (hasSortOrder)
            {
                command.Parameters.AddWithValue("@sortOrder", (object?)request.SortOrder ?? DBNull.Value);
            }
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await PersistRequestedDatasetStepsAsync(connection, transaction, datasetId, request.Steps, cancellationToken);
        await ResetLinkedPlanStatusesAsync(connection, transaction, mutationScope.AffectedSuiteIds, cancellationToken);
        await TouchTestSuitesAsync(connection, transaction, context, principal, mutationScope.AffectedSuiteIds, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await QueueAutoSyncTestCaseJobsAsync(connection, context, mutationScope.AffectedSuiteIds, mutationScope.ProjectId, cancellationToken);
        var map = await LoadTestSuiteDatasetMapAsync(connection, [testComponentId], cancellationToken);
        return map.TryGetValue(testComponentId, out var datasets) ? datasets.FirstOrDefault(item => item.Id == datasetId) : null;
    }

    public async Task<IReadOnlyList<TestSuiteComponentDatasetSummaryDto>?> UpdateTestComponentDatasetsAsync(ClaimsPrincipal principal, long testSuiteId, long testComponentId, SaveTestSuiteComponentDatasetsRequest request, CancellationToken cancellationToken = default)
    {
        _editSessionService.EnsureCanEdit(principal, testSuiteId);
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var mutationScope = await PrepareDatasetMutationScopeAsync(connection, context.ClientId.Value, testSuiteId, cancellationToken);
        if (mutationScope.NotFound)
        {
            return null;
        }

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        if (!await TestComponentBelongsToSuiteAsync(connection, transaction, context.ClientId.Value, testSuiteId, testComponentId, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var hasSortOrder = await HasDataSetSortOrderColumnAsync(connection, transaction, cancellationToken);
        var existingIds = new List<long>();
        await using (var command = CreateCommand(connection, "SELECT id FROM data_sets WHERE test_component_id=@testComponentId AND deleted_at IS NULL;"))
        {
            command.Transaction = transaction;
            command.Parameters.AddWithValue("@testComponentId", testComponentId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) existingIds.Add(reader.GetInt64(0));
        }

        var existingSet = existingIds.ToHashSet();
        var retainedIds = request.Datasets.Where(item => item.DatasetId.HasValue && existingSet.Contains(item.DatasetId.Value)).Select(item => item.DatasetId!.Value).ToHashSet();
        foreach (var removedId in existingIds.Where(id => !retainedIds.Contains(id)))
        {
            await using var deleteSteps = CreateCommand(connection, "DELETE FROM data_set_steps WHERE dataset_id=@datasetId;");
            deleteSteps.Transaction = transaction;
            deleteSteps.Parameters.AddWithValue("@datasetId", removedId);
            await deleteSteps.ExecuteNonQueryAsync(cancellationToken);
            await using var deleteDataset = CreateCommand(connection, "DELETE FROM data_sets WHERE id=@datasetId;");
            deleteDataset.Transaction = transaction;
            deleteDataset.Parameters.AddWithValue("@datasetId", removedId);
            await deleteDataset.ExecuteNonQueryAsync(cancellationToken);
        }

        var summaries = new List<TestSuiteComponentDatasetSummaryDto>();
        for (var index = 0; index < request.Datasets.Count; index++)
        {
            var dataset = request.Datasets[index];
            var sortOrder = dataset.SortOrder ?? index + 1;
            var scenario = NormalizeOptionalText(dataset.Scenario);
            var status = dataset.Status ?? false;
            long persistedId;
            if (dataset.DatasetId.HasValue && existingSet.Contains(dataset.DatasetId.Value))
            {
                persistedId = dataset.DatasetId.Value;
                var sql = hasSortOrder
                    ? "UPDATE data_sets SET scenario=@scenario,status=@status,sort_order=@sortOrder,updated_at=SYSUTCDATETIME() WHERE id=@datasetId;"
                    : "UPDATE data_sets SET scenario=@scenario,status=@status,updated_at=SYSUTCDATETIME() WHERE id=@datasetId;";
                await using var update = CreateCommand(connection, sql);
                update.Transaction = transaction;
                update.Parameters.AddWithValue("@datasetId", persistedId);
                update.Parameters.AddWithValue("@scenario", (object?)scenario ?? DBNull.Value);
                update.Parameters.AddWithValue("@status", status);
                if (hasSortOrder) update.Parameters.AddWithValue("@sortOrder", sortOrder);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
            else
            {
                var sql = hasSortOrder
                    ? "INSERT INTO data_sets(status,scenario,sort_order,test_component_id,created_at,updated_at) OUTPUT INSERTED.id VALUES(@status,@scenario,@sortOrder,@testComponentId,SYSUTCDATETIME(),SYSUTCDATETIME());"
                    : "INSERT INTO data_sets(status,scenario,test_component_id,created_at,updated_at) OUTPUT INSERTED.id VALUES(@status,@scenario,@testComponentId,SYSUTCDATETIME(),SYSUTCDATETIME());";
                await using var insert = CreateCommand(connection, sql);
                insert.Transaction = transaction;
                insert.Parameters.AddWithValue("@status", status);
                insert.Parameters.AddWithValue("@scenario", (object?)scenario ?? DBNull.Value);
                insert.Parameters.AddWithValue("@testComponentId", testComponentId);
                if (hasSortOrder) insert.Parameters.AddWithValue("@sortOrder", sortOrder);
                persistedId = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken));
            }

            await PersistRequestedDatasetStepsAsync(connection, transaction, persistedId, dataset.Steps, cancellationToken);
            summaries.Add(new TestSuiteComponentDatasetSummaryDto { Id = persistedId, SortOrder = sortOrder, Scenario = scenario, Status = status });
        }

        await ResetLinkedPlanStatusesAsync(connection, transaction, mutationScope.AffectedSuiteIds, cancellationToken);
        await TouchTestSuitesAsync(connection, transaction, context, principal, mutationScope.AffectedSuiteIds, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await QueueAutoSyncTestCaseJobsAsync(connection, context, mutationScope.AffectedSuiteIds, mutationScope.ProjectId, cancellationToken);
        return summaries;
    }

    public async Task<EnsureTestComponentDatasetResponse?> EnsureTestComponentDatasetAsync(ClaimsPrincipal principal, long testSuiteId, long testComponentId, EnsureTestComponentDatasetRequest request, CancellationToken cancellationToken = default)
    {
        _editSessionService.EnsureCanEdit(principal, testSuiteId);
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue) return null;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var mutationScope = await PrepareDatasetMutationScopeAsync(connection, context.ClientId.Value, testSuiteId, cancellationToken);
        if (mutationScope.NotFound) return null;
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        if (!await TestComponentBelongsToSuiteAsync(connection, transaction, context.ClientId.Value, testSuiteId, testComponentId, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var componentId = await GetTestComponentSourceIdAsync(connection, transaction, testComponentId, cancellationToken);
        if (!componentId.HasValue) return null;
        var dataset = await InsertInitializedDatasetAsync(connection, transaction, testComponentId, componentId.Value, request, cancellationToken);
        await ResetLinkedPlanStatusesAsync(connection, transaction, mutationScope.AffectedSuiteIds, cancellationToken);
        await TouchTestSuitesAsync(connection, transaction, context, principal, mutationScope.AffectedSuiteIds, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await QueueAutoSyncTestCaseJobsAsync(connection, context, mutationScope.AffectedSuiteIds, mutationScope.ProjectId, cancellationToken);
        return new EnsureTestComponentDatasetResponse { TestComponentId = testComponentId, Dataset = dataset };
    }

    public async Task<EnsureTestComponentDatasetResponse?> EnsureTestComponentDatasetForSuiteAsync(ClaimsPrincipal principal, long testSuiteId, EnsureTestComponentDatasetRequest request, CancellationToken cancellationToken = default)
    {
        _editSessionService.EnsureCanEdit(principal, testSuiteId);
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue) return null;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var mutationScope = await PrepareDatasetMutationScopeAsync(connection, context.ClientId.Value, testSuiteId, cancellationToken);
        if (mutationScope.NotFound) return null;
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        long testComponentId;
        long componentId;
        if (request.TestComponentId.HasValue && await TestComponentBelongsToSuiteAsync(connection, transaction, context.ClientId.Value, testSuiteId, request.TestComponentId.Value, cancellationToken))
        {
            testComponentId = request.TestComponentId.Value;
            componentId = (await GetTestComponentSourceIdAsync(connection, transaction, testComponentId, cancellationToken))!.Value;
        }
        else
        {
            if (!request.ComponentId.HasValue || !request.ProjectId.HasValue) return null;
            const string validateSql = "SELECT TOP 1 c.id FROM components c INNER JOIN projects p ON p.id=c.project_id WHERE c.id=@componentId AND c.project_id=@projectId AND c.deleted_at IS NULL AND p.deleted_at IS NULL AND p.client_id=@clientId;";
            await using (var validate = CreateCommand(connection, validateSql))
            {
                validate.Transaction = transaction;
                validate.Parameters.AddWithValue("@componentId", request.ComponentId.Value);
                validate.Parameters.AddWithValue("@projectId", request.ProjectId.Value);
                validate.Parameters.AddWithValue("@clientId", context.ClientId.Value);
                if (await validate.ExecuteScalarAsync(cancellationToken) is null) return null;
            }

            var hasSortOrder = await HasTestComponentSortOrderColumnAsync(connection, transaction, cancellationToken);
            var insertSql = hasSortOrder
                ? "INSERT INTO test_components(component_id,project_id,status,test_design_id,sort_order,created_at,updated_at) OUTPUT INSERTED.id VALUES(@componentId,@projectId,1,@suiteId,(SELECT COUNT(*) FROM test_components WHERE test_design_id=@suiteId AND deleted_at IS NULL),SYSUTCDATETIME(),SYSUTCDATETIME());"
                : "INSERT INTO test_components(component_id,project_id,status,test_design_id,created_at,updated_at) OUTPUT INSERTED.id VALUES(@componentId,@projectId,1,@suiteId,SYSUTCDATETIME(),SYSUTCDATETIME());";
            await using var insert = CreateCommand(connection, insertSql);
            insert.Transaction = transaction;
            insert.Parameters.AddWithValue("@componentId", request.ComponentId.Value);
            insert.Parameters.AddWithValue("@projectId", request.ProjectId.Value);
            insert.Parameters.AddWithValue("@suiteId", testSuiteId);
            testComponentId = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken));
            componentId = request.ComponentId.Value;
        }

        var dataset = await InsertInitializedDatasetAsync(connection, transaction, testComponentId, componentId, request, cancellationToken);
        await ResetLinkedPlanStatusesAsync(connection, transaction, mutationScope.AffectedSuiteIds, cancellationToken);
        await TouchTestSuitesAsync(connection, transaction, context, principal, mutationScope.AffectedSuiteIds, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await QueueAutoSyncTestCaseJobsAsync(connection, context, mutationScope.AffectedSuiteIds, mutationScope.ProjectId, cancellationToken);
        return new EnsureTestComponentDatasetResponse { TestComponentId = testComponentId, Dataset = dataset };
    }

    public async Task<SaveTestSuiteResult> CreateTestSuiteAsync(ClaimsPrincipal principal, SaveTestSuiteRequest request, CancellationToken cancellationToken = default)
    {
        return await SaveTestSuiteInternalAsync(principal, null, request, cancellationToken);
    }
    public async Task<SaveTestSuiteResult> CloneTestSuiteAsync(ClaimsPrincipal principal, long testSuiteId, CloneTestSuiteRequest request, CancellationToken cancellationToken = default)
    {
        var source = await GetTestSuiteFullAsync(principal, testSuiteId, cancellationToken);
        if (source is null)
        {
            return new SaveTestSuiteResult { Outcome = SaveTestSuiteOutcome.NotFound };
        }

        var designStateId = await ResolveTestStateIdByNameAsync("Design", cancellationToken);
        if (!designStateId.HasValue)
        {
            return new SaveTestSuiteResult
            {
                Outcome = SaveTestSuiteOutcome.InvalidReference,
                ErrorField = "details.test_state_id",
                ErrorMessage = "The Design test state is required to clone a test."
            };
        }

        var cloneRequest = BuildCloneRequest(source, source.ConfigurationId, request.Title?.Trim(), designStateId.Value);
        var result = await SaveTestSuiteInternalAsync(principal, null, cloneRequest, cancellationToken);
        if (result.Outcome == SaveTestSuiteOutcome.Saved && result.TestSuite?.Id > 0)
        {
            var context = GetRequestContext(principal);
            if (context.ClientId.HasValue)
            {
                await CloneLocalVariablesAsync(source.Id, result.TestSuite.Id, context.ClientId.Value, cancellationToken);
            }
        }

        return result;
    }

    public async Task<SaveTestSuiteResult> UpdateTestSuiteAsync(ClaimsPrincipal principal, long testSuiteId, SaveTestSuiteRequest request, CancellationToken cancellationToken = default)
    {
        _editSessionService.EnsureCanEdit(principal, testSuiteId);
        return await SaveTestSuiteInternalAsync(principal, testSuiteId, request, cancellationToken);
    }

    public async Task<SaveTestSuiteDetailsResult> UpdateTestSuiteDetailsAsync(ClaimsPrincipal principal, long testSuiteId, SaveTestSuiteDetailsRequest request, CancellationToken cancellationToken = default)
    {
        _editSessionService.EnsureCanEdit(principal, testSuiteId);
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return new SaveTestSuiteDetailsResult { Outcome = SaveTestSuiteOutcome.NotFound };
        }

        var normalizedTags = NormalizeSuiteTags(request.Tags);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var validation = await ValidateTestSuiteReferencesAsync(connection, context.ClientId.Value, new SaveTestSuiteRequest
        {
            Details = request,
            DesignedComponents = []
        }, cancellationToken);
        if (validation is not null)
        {
            return new SaveTestSuiteDetailsResult
            {
                Outcome = validation.Outcome,
                ErrorField = validation.ErrorField,
                ErrorMessage = validation.ErrorMessage
            };
        }

        var exists = await ExecuteCountAsync(connection, "SELECT COUNT(*) FROM test_designs WHERE id = @id AND client_id = @clientId AND deleted_at IS NULL;", [
            new SqlParameter("@id", testSuiteId),
            new SqlParameter("@clientId", context.ClientId.Value)
        ], cancellationToken);
        if (exists == 0)
        {
            return new SaveTestSuiteDetailsResult { Outcome = SaveTestSuiteOutcome.NotFound };
        }

        var suiteIds = new List<long> { testSuiteId };
        suiteIds.AddRange(await LoadChildSuiteIdsAsync(connection, context.ClientId.Value, testSuiteId, cancellationToken));
        var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var suiteId in suiteIds)
            {
                await UpdateTestSuiteDetailsRowAsync(connection, transaction, context, principal, suiteId, request, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        await QueueAutoSyncTestCaseJobsAsync(connection, context, suiteIds, request.ProjectId, cancellationToken);
        return new SaveTestSuiteDetailsResult
        {
            Outcome = SaveTestSuiteOutcome.Saved,
            Details = new SaveTestSuiteDetailsRequest
            {
                Title = request.Title?.Trim(),
                TestStateId = request.TestStateId,
                TestSuiteType = request.TestSuiteType,
                FolderPathId = request.FolderPathId,
                Comment = NormalizeOptionalText(request.Comment),
                ProjectId = request.ProjectId,
                IterationPath = NormalizeOptionalText(request.IterationPath),
                Priority = NormalizeOptionalText(request.Priority),
                StoryId = NormalizeOptionalText(request.StoryId),
                TestTitle = NormalizeOptionalText(request.TestTitle),
                Tags = CreateTagsJsonElement(normalizedTags),
                ConfigurationId = request.ConfigurationId,
                KbaReady = request.KbaReady,
                TrainingReady = request.TrainingReady,
                ReleaseNotesReady = request.ReleaseNotesReady
            }
        };
    }

    public async Task<SaveTestSuiteFlowResult> UpdateTestSuiteFlowAsync(ClaimsPrincipal principal, long testSuiteId, SaveTestSuiteFlowRequest request, CancellationToken cancellationToken = default)
    {
        _editSessionService.EnsureCanEdit(principal, testSuiteId);
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return new SaveTestSuiteFlowResult { Outcome = SaveTestSuiteOutcome.NotFound };
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var suiteProjectId = await GetScopedTestSuiteProjectIdAsync(connection, context.ClientId.Value, testSuiteId, cancellationToken);
        if (suiteProjectId.NotFound)
        {
            return new SaveTestSuiteFlowResult { Outcome = SaveTestSuiteOutcome.NotFound };
        }

        var validation = await ValidateTestSuiteFlowReferencesAsync(connection, context.ClientId.Value, testSuiteId, request, cancellationToken);
        if (validation is not null)
        {
            return validation;
        }

        await EnsureTestComponentSortOrderColumnAsync(connection, cancellationToken);
        var childSuiteIds = await LoadChildSuiteIdsAsync(connection, context.ClientId.Value, testSuiteId, cancellationToken);
        var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        IReadOnlyList<TestSuiteFlowComponentSummaryDto> summaries;
        try
        {
            summaries = await ApplyTestSuiteFlowAsync(connection, transaction, testSuiteId, request.Components, true, cancellationToken);
            foreach (var childSuiteId in childSuiteIds)
            {
                await ApplyTestSuiteFlowAsync(connection, transaction, childSuiteId, request.Components, false, cancellationToken);
            }

            var affectedSuiteIds = new List<long> { testSuiteId };
            affectedSuiteIds.AddRange(childSuiteIds);
            await ResetLinkedPlanStatusesAsync(connection, transaction, affectedSuiteIds, cancellationToken);
            await TouchTestSuitesAsync(connection, transaction, context, principal, affectedSuiteIds, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        var suitesToSync = new List<long> { testSuiteId };
        suitesToSync.AddRange(childSuiteIds);
        await QueueAutoSyncTestCaseJobsAsync(connection, context, suitesToSync, suiteProjectId.ProjectId, cancellationToken);
        return new SaveTestSuiteFlowResult
        {
            Outcome = SaveTestSuiteOutcome.Saved,
            Components = summaries
        };
    }

    public async Task<DeleteTestSuiteResult> DeleteTestSuiteAsync(ClaimsPrincipal principal, long testSuiteId, CancellationToken cancellationToken = default)
    {
        _editSessionService.EnsureCanEdit(principal, testSuiteId);
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return new DeleteTestSuiteResult { Outcome = DeleteTestSuiteOutcome.NotFound };
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string suiteSql = """
            SELECT TOP 1 title
            FROM test_designs
            WHERE id = @testSuiteId AND client_id = @clientId AND deleted_at IS NULL;
            """;

        string? title;
        await using (var suiteCommand = CreateCommand(connection, suiteSql))
        {
            suiteCommand.Parameters.AddWithValue("@testSuiteId", testSuiteId);
            suiteCommand.Parameters.AddWithValue("@clientId", context.ClientId.Value);
            title = await suiteCommand.ExecuteScalarAsync(cancellationToken) as string;
        }

        if (title is null)
        {
            return new DeleteTestSuiteResult { Outcome = DeleteTestSuiteOutcome.NotFound };
        }

        const string activePlanSql = """
            SELECT COUNT(DISTINCT tp.id)
            FROM test_plan_item_suites tpis
            INNER JOIN test_plan_items tpi ON tpi.id = tpis.test_plan_item_id
            INNER JOIN test_plans tp ON tp.id = tpi.test_plan_id
            WHERE tpis.test_design_id = @testSuiteId
              AND tpis.deleted_at IS NULL
              AND tp.client_id = @clientId
              AND ISNULL(tp.status, 0) = 1;
            """;

        var activePlans = await ExecuteCountAsync(connection, activePlanSql, [
            new SqlParameter("@testSuiteId", testSuiteId),
            new SqlParameter("@clientId", context.ClientId.Value)
        ], cancellationToken);

        if (activePlans > 0)
        {
            return new DeleteTestSuiteResult
            {
                Outcome = DeleteTestSuiteOutcome.ActivePlansBlocked,
                ErrorMessage = $"Can not delete Test Suite {title} Becuase {activePlans} Active Test Plan Found"
            };
        }

        const string deleteSql = """
            UPDATE test_designs
            SET deleted_at = SYSUTCDATETIME(),
                updated_at = SYSUTCDATETIME()
            WHERE id = @testSuiteId AND client_id = @clientId AND deleted_at IS NULL;
            """;

        await using var deleteCommand = CreateCommand(connection, deleteSql);
        deleteCommand.Parameters.AddWithValue("@testSuiteId", testSuiteId);
        deleteCommand.Parameters.AddWithValue("@clientId", context.ClientId.Value);
        var affected = await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        return new DeleteTestSuiteResult
        {
            Outcome = affected > 0 ? DeleteTestSuiteOutcome.Deleted : DeleteTestSuiteOutcome.NotFound
        };
    }

    public async Task<SaveOverrideValueResult> SaveOverrideValueAsync(ClaimsPrincipal principal, SaveOverrideValueRequest request, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return new SaveOverrideValueResult
            {
                Outcome = SaveOverrideValueOutcome.NotFound,
                ErrorMessage = "DataSet Step No Found"
            };
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        if (request.Reset != true)
        {
            var validationError = await ValidateOverrideValueAsync(connection, request.Value ?? string.Empty, cancellationToken);
            if (validationError is not null)
            {
                return new SaveOverrideValueResult
                {
                    Outcome = SaveOverrideValueOutcome.ValidationFailed,
                    ErrorMessage = validationError
                };
            }
        }

        const string datasetStepSql = """
            SELECT TOP 1 dss.step_info, td.id AS test_suite_id
            FROM data_set_steps dss
            INNER JOIN data_sets ds ON ds.id = dss.dataset_id
            INNER JOIN test_components tc ON tc.id = ds.test_component_id
            INNER JOIN test_designs td ON td.id = tc.test_design_id
            WHERE dss.dataset_id = @datasetId
              AND (dss.step_id = @stepId OR dss.display = @stepId)
              AND td.client_id = @clientId;
            """;

        string? stepInfoJson;
        long? testSuiteId = null;
        await using (var datasetStepCommand = CreateCommand(connection, datasetStepSql))
        {
            datasetStepCommand.Parameters.AddWithValue("@datasetId", request.DatasetId);
            datasetStepCommand.Parameters.AddWithValue("@stepId", request.StepId);
            datasetStepCommand.Parameters.AddWithValue("@clientId", context.ClientId.Value);
            await using var reader = await datasetStepCommand.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                stepInfoJson = GetString(reader, "step_info");
                testSuiteId = GetInt64(reader, "test_suite_id");
            }
            else
            {
                stepInfoJson = null;
            }
        }

        if (stepInfoJson is null)
        {
            return new SaveOverrideValueResult
            {
                Outcome = SaveOverrideValueOutcome.NotFound,
                ErrorMessage = "DataSet Step No Found"
            };
        }

        if (testSuiteId.HasValue)
        {
            _editSessionService.EnsureCanEdit(principal, testSuiteId.Value);
        }

        JsonObject stepInfo;
        try
        {
            stepInfo = JsonNode.Parse(stepInfoJson) as JsonObject ?? [];
        }
        catch
        {
            stepInfo = [];
        }

        bool isOverride;
        string? overrideValue;
        if (request.Reset == true)
        {
            stepInfo.Remove("override");
            stepInfo.Remove("override_value");
            isOverride = false;
            overrideValue = null;
        }
        else
        {
            stepInfo["override"] = true;
            stepInfo["override_value"] = request.Value;
            isOverride = true;
            overrideValue = request.Value;
        }

        const string updateSql = """
            UPDATE dss
            SET dss.step_info = @stepInfo,
                dss.[override] = @override,
                dss.override_value = @overrideValue,
                dss.updated_at = SYSUTCDATETIME()
            FROM data_set_steps dss
            INNER JOIN data_sets ds ON ds.id = dss.dataset_id
            INNER JOIN test_components tc ON tc.id = ds.test_component_id
            INNER JOIN test_designs td ON td.id = tc.test_design_id
            WHERE dss.dataset_id = @datasetId
              AND (dss.step_id = @stepId OR dss.display = @stepId)
              AND td.client_id = @clientId;
            """;

        await using var updateCommand = CreateCommand(connection, updateSql);
        updateCommand.Parameters.AddWithValue("@stepInfo", stepInfo.ToJsonString());
        updateCommand.Parameters.AddWithValue("@override", isOverride);
        updateCommand.Parameters.AddWithValue("@overrideValue", (object?)overrideValue ?? DBNull.Value);
        updateCommand.Parameters.AddWithValue("@datasetId", request.DatasetId);
        updateCommand.Parameters.AddWithValue("@stepId", request.StepId);
        updateCommand.Parameters.AddWithValue("@clientId", context.ClientId.Value);
        await updateCommand.ExecuteNonQueryAsync(cancellationToken);

        return new SaveOverrideValueResult { Outcome = SaveOverrideValueOutcome.Saved };
    }

    public async Task<IReadOnlyList<TestPlanSuitesForItemDto>> GetSuitesForPlanItemLightAsync(ClaimsPrincipal principal, long testPlanItemId, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return [];
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await EnsurePointBasedConfigurationStateAsync(connection, testPlanItemId, cancellationToken);
        await EnsureTestLevelDatasetsSchemaAsync(connection, cancellationToken);
        const string planItemSql = """
            SELECT TOP 1 tpi.id, tpi.name
            FROM test_plan_items tpi
            INNER JOIN test_plans tp ON tp.id = tpi.test_plan_id
            WHERE tpi.id = @id AND tp.client_id = @clientId;
            """;

        long? planItemId = null;
        string? planItemName = null;
        await using (var command = CreateCommand(connection, planItemSql))
        {
            command.Parameters.AddWithValue("@id", testPlanItemId);
            command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                planItemId = reader.GetInt64(reader.GetOrdinal("id"));
                planItemName = GetString(reader, "name");
            }
        }

        if (!planItemId.HasValue)
        {
            return [];
        }

        const string suitesSql = """
            SELECT
                tpis.id,
                tpis.test_design_id,
                status_ref.id AS status_ref_id,
                status_ref.name AS status_name,
                td.title,
                ts.id AS state_ref_id,
                ts.name AS state_name,
                cfg.id AS config_id,
                cfg.name AS config_name
            FROM test_plan_item_suites tpis
            LEFT JOIN test_plan_item_suite_statuses status_ref ON status_ref.id = tpis.status_id
            LEFT JOIN test_designs td ON td.id = tpis.test_design_id
            LEFT JOIN test_states ts ON ts.id = td.test_state_id
            LEFT JOIN configurations cfg ON cfg.id = td.configuration_id
            WHERE tpis.test_plan_item_id = @testPlanItemId
              AND tpis.deleted_at IS NULL
              AND NOT (tpis.parent_id IS NOT NULL AND td.configuration_id IS NOT NULL)
            ORDER BY ISNULL(tpis.sort_order, 2147483647), tpis.id;
            """;

        var rows = new List<(TestPlanItemSuiteLightDto Row, long? ConfigurationId, long UserSourceId)>();
        var suiteLinkIds = new List<long>();
        var configurationIds = new List<long>();
        await using (var command = CreateCommand(connection, suitesSql))
        {
            command.Parameters.AddWithValue("@testPlanItemId", testPlanItemId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var linkId = reader.GetInt64(reader.GetOrdinal("id"));
                suiteLinkIds.Add(linkId);
                var configurationId = GetInt64(reader, "config_id");
                if (configurationId.HasValue)
                {
                    configurationIds.Add(configurationId.Value);
                }

                rows.Add((
                    new TestPlanItemSuiteLightDto
                    {
                        Id = linkId,
                        ExecutionId = GetInt64(reader, "test_design_id") ?? linkId,
                        TestDesignId = GetInt64(reader, "test_design_id"),
                        ParentId = null,
                        Status = GetInt64(reader, "status_ref_id") is long statusId ? new BasicRefDto { Id = statusId, Name = GetString(reader, "status_name") } : null,
                        Suite = new SuiteLightDto
                        {
                            Title = GetString(reader, "title"),
                            State = GetInt64(reader, "state_ref_id") is long stateId ? new BasicRefDto { Id = stateId, Name = GetString(reader, "state_name") } : null,
                            Configuration = configurationId.HasValue ? new SuiteConfigurationDto { Id = configurationId.Value, Name = GetString(reader, "config_name") } : null
                        }
                    },
                    configurationId,
                    linkId));
            }
        }

        var assignmentRows = await LoadConfigurationAssignmentsForPlanItemAsync(connection, context.ClientId.Value, testPlanItemId, cancellationToken);
        foreach (var assignmentRow in assignmentRows)
        {
            configurationIds.Add(assignmentRow.ConfigurationId);
            rows.Add((
                new TestPlanItemSuiteLightDto
                {
                    Id = ToConfigurationExecutionId(assignmentRow.AssignmentId),
                    ExecutionId = ToConfigurationExecutionId(assignmentRow.AssignmentId),
                    TestDesignId = assignmentRow.BaseTestDesignId,
                    ParentId = assignmentRow.ParentSuiteLinkId,
                    Status = assignmentRow.StatusId.HasValue ? new BasicRefDto { Id = assignmentRow.StatusId.Value, Name = assignmentRow.StatusName } : null,
                    Suite = new SuiteLightDto
                    {
                        Title = assignmentRow.SuiteTitle,
                        State = assignmentRow.StateId.HasValue ? new BasicRefDto { Id = assignmentRow.StateId.Value, Name = assignmentRow.StateName } : null,
                        Configuration = new SuiteConfigurationDto
                        {
                            Id = assignmentRow.ConfigurationId,
                            Name = assignmentRow.ConfigurationName
                        }
                    }
                },
                assignmentRow.ConfigurationId,
                assignmentRow.ParentSuiteLinkId));
        }

        var datasetRows = await LoadTestDesignDatasetPlanRowsAsync(connection, context.ClientId.Value, testPlanItemId, cancellationToken);
        foreach (var datasetRow in datasetRows)
        {
            rows.Add((
                new TestPlanItemSuiteLightDto
                {
                    Id = ToDatasetRowId(datasetRow.DatasetPlanRowId),
                    ExecutionId = ToDatasetExecutionId(datasetRow.DatasetPlanRowId),
                    TestDesignId = datasetRow.BaseTestDesignId,
                    ParentId = datasetRow.ParentSuiteLinkId,
                    Status = datasetRow.StatusId.HasValue ? new BasicRefDto { Id = datasetRow.StatusId.Value, Name = datasetRow.StatusName } : null,
                    Suite = new SuiteLightDto
                    {
                        Title = string.IsNullOrWhiteSpace(datasetRow.Scenario)
                            ? datasetRow.SuiteTitle
                            : $"{datasetRow.SuiteTitle} [{datasetRow.Scenario}]",
                        State = datasetRow.StateId.HasValue ? new BasicRefDto { Id = datasetRow.StateId.Value, Name = datasetRow.StateName } : null,
                        Configuration = datasetRow.ConfigurationId.HasValue
                            ? new SuiteConfigurationDto
                            {
                                Id = datasetRow.ConfigurationId.Value,
                                Name = datasetRow.ConfigurationName
                            }
                            : null
                    }
                },
                datasetRow.ConfigurationId,
                datasetRow.ParentSuiteLinkId));
        }

        var userMap = await LoadPlanItemSuiteUsersAsync(connection, suiteLinkIds, cancellationToken);
        var configurationMap = await LoadSuiteConfigurationsAsync(connection, configurationIds.Distinct().ToArray(), cancellationToken);
        var pausedSuiteMap = await LoadPausedSuiteStateMapAsync(connection, planItemId.Value, cancellationToken);

        return
        [
            new TestPlanSuitesForItemDto
            {
                Id = planItemId.Value,
                Name = planItemName,
                AddedSuites = rows.Select(entry => new TestPlanItemSuiteLightDto
                {
                    Id = entry.Row.Id,
                    ExecutionId = entry.Row.ExecutionId,
                    TestDesignId = entry.Row.TestDesignId,
                    ParentId = entry.Row.ParentId,
                    Status = entry.Row.Status,
                    IsPaused = pausedSuiteMap.TryGetValue(entry.Row.ExecutionId, out var isPaused)
                        && isPaused,
                    Suite = entry.Row.Suite is null
                        ? null
                        : new SuiteLightDto
                        {
                            Title = entry.Row.Suite.Title,
                            State = entry.Row.Suite.State,
                            Configuration = entry.ConfigurationId.HasValue && configurationMap.TryGetValue(entry.ConfigurationId.Value, out var configuration)
                                ? configuration
                                : entry.Row.Suite.Configuration
                        },
                    Users = userMap.TryGetValue(entry.UserSourceId, out var users) ? users : []
                }).ToList()
            }
        ];
    }

    public async Task<TestRunnerPayloadDto?> GetTestSuiteStepsAsync(ClaimsPrincipal principal, GetTestSuiteStepsRequest request, bool invokedViaAutomation, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return null;
        }

        var requestedSuiteIds = request.TestSuites
            .Where(id => id != 0)
            .Distinct()
            .ToArray();

        if (requestedSuiteIds.Length == 0)
        {
            return new TestRunnerPayloadDto();
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await EnsurePointBasedConfigurationStateAsync(connection, request.TestPlanItemId, cancellationToken);
        await EnsureTestLevelDatasetsSchemaAsync(connection, cancellationToken);
        var suites = await LoadRunnerSuitesAsync(connection, context.ClientId.Value, requestedSuiteIds, cancellationToken);
        if (suites.Count == 0)
        {
            return new TestRunnerPayloadDto();
        }

        if (requestedSuiteIds.Length > 0)
        {
            var requestedOrder = requestedSuiteIds
                .Select((suiteId, index) => new { suiteId, index })
                .ToDictionary(row => row.suiteId, row => row.index);
            suites = suites
                .OrderBy(suite => requestedOrder.TryGetValue(suite.RuntimeSuiteId == 0 ? suite.Id : suite.RuntimeSuiteId, out var order) ? order : int.MaxValue)
                .ThenBy(suite => suite.RuntimeSuiteId == 0 ? suite.Id : suite.RuntimeSuiteId)
                .ToList();
        }

        var variableMap = await LoadRunnerVariableMapAsync(connection, context.ClientId.Value, suites.Select(suite => suite.Id).ToArray(), cancellationToken);
        var configurationIds = suites
            .Where(suite => suite.ConfigurationId.HasValue)
            .Select(suite => suite.ConfigurationId!.Value)
            .Distinct()
            .ToArray();
        var configurationMap = await LoadSuiteConfigurationsAsync(connection, configurationIds, cancellationToken);

        if (!invokedViaAutomation && request.TestPlanItemId.HasValue && requestedSuiteIds.Length == 1)
        {
            var pausedRunner = await LoadLatestPausedRunnerItemAsync(connection, context.ClientId.Value, request.TestPlanItemId.Value, requestedSuiteIds[0], cancellationToken);
            if (pausedRunner is RunnerItemRecord pausedRunnerItem)
            {
                var resumedStepsJson = ClearPausedRunnerStepJson(pausedRunnerItem.StepsJson);
                if (!string.Equals(resumedStepsJson, pausedRunnerItem.StepsJson, StringComparison.Ordinal))
                {
                    var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
                    try
                    {
                        await SaveRunnerItemStateAsync(connection, transaction, pausedRunnerItem, resumedStepsJson, InProgressStatusId, cancellationToken);
                        await transaction.CommitAsync(cancellationToken);
                    }
                    catch
                    {
                        await transaction.RollbackAsync(cancellationToken);
                        throw;
                    }
                }

                return await LoadExistingRunnerPayloadForSuiteAsync(connection, context.ClientId.Value, pausedRunnerItem.TestRunnerId, pausedRunnerItem.TestSuiteId, cancellationToken);
            }
        }

        var globalCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var suiteSteps = new List<TestRunnerSuiteDto>();
        foreach (var suite in suites)
        {
            var runtimeSuiteId = suite.RuntimeSuiteId == 0 ? suite.Id : suite.RuntimeSuiteId;
            if (!invokedViaAutomation && request.TestPlanItemId.HasValue)
            {
                var pausedRunner = await LoadLatestPausedRunnerItemAsync(connection, context.ClientId.Value, request.TestPlanItemId.Value, runtimeSuiteId, cancellationToken);
                if (pausedRunner is RunnerItemRecord pausedRunnerItem)
                {
                    var resumedStepsJson = ClearPausedRunnerStepJson(pausedRunnerItem.StepsJson);
                    if (!string.Equals(resumedStepsJson, pausedRunnerItem.StepsJson, StringComparison.Ordinal))
                    {
                        var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
                        try
                        {
                            await SaveRunnerItemStateAsync(connection, transaction, pausedRunnerItem, resumedStepsJson, InProgressStatusId, cancellationToken);
                            await transaction.CommitAsync(cancellationToken);
                        }
                        catch
                        {
                            await transaction.RollbackAsync(cancellationToken);
                            throw;
                        }
                    }

                    suiteSteps.Add(BuildRunnerSuiteDtoFromRunnerItem(suite, configurationMap, pausedRunnerItem));
                    continue;
                }
            }

            var localCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var configuration = suite.ConfigurationId.HasValue && configurationMap.TryGetValue(suite.ConfigurationId.Value, out var loadedConfiguration)
                ? loadedConfiguration
                : null;
            suiteSteps.Add(await BuildRunnerSuiteDtoAsync(connection, suite, configuration, variableMap, globalCache, localCache, cancellationToken));
        }

        RunnerHeaderDto? runner = null;
        var shouldCreateRunner = request.TestPlanItemId.HasValue;
        if (shouldCreateRunner)
        {
            runner = await PersistRunnerPayloadAsync(connection, context, request.TestPlanItemId!.Value, suiteSteps, cancellationToken);
        }

        return new TestRunnerPayloadDto
        {
            TestRunner = runner,
            TestRunnerSteps = suiteSteps
        };
    }

    public async Task<SaveTestRunnerStepStatusResult> SaveTestRunnerStepStatusAsync(ClaimsPrincipal principal, SaveTestRunnerStepStatusRequest request, IReadOnlyList<string> imagePaths, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return new SaveTestRunnerStepStatusResult
            {
                Outcome = SaveTestRunnerStepStatusOutcome.NotFound,
                ErrorMessage = "Test Runner Item Not Found"
            };
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await EnsurePointBasedConfigurationStateAsync(connection, request.TestPlanItemId, cancellationToken);
        var executionId = ResolveExecutionIdentity(request.TestSuiteId, request.ExecutionId);
        var runnerItem = await LoadRunnerItemRecordAsync(connection, context.ClientId.Value, request.TestRunnerId, request.TestPlanItemId, executionId, cancellationToken);
        if (runnerItem is not RunnerItemRecord runnerItemValue)
        {
            return new SaveTestRunnerStepStatusResult
            {
                Outcome = SaveTestRunnerStepStatusOutcome.NotFound,
                ErrorMessage = "Test Runner Item Not Found"
            };
        }

        var updateResult = UpdateRunnerStepsJson(runnerItemValue.StepsJson, request, imagePaths);
        var statusId = DetermineRunnerSuiteStatusId(updateResult.StepsJson);

        var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await SaveRunnerItemStateAsync(connection, transaction, runnerItemValue, updateResult.StepsJson, statusId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        return new SaveTestRunnerStepStatusResult
        {
            Outcome = SaveTestRunnerStepStatusOutcome.Saved,
            Payload = await LoadExistingRunnerPayloadAsync(connection, context.ClientId.Value, runnerItemValue.TestRunnerId, cancellationToken),
            Summary = new TestRunnerStepStatusSummaryDto
            {
                Accepted = updateResult.AcceptedCount,
                Matched = updateResult.MatchedCount,
                Updated = updateResult.UpdatedCount,
                SuiteStatus = GetRunnerSuiteStatusName(statusId)
            }
        };
    }

    public async Task<RunnerItemMutationResult> PauseTestSuiteAsync(ClaimsPrincipal principal, PauseTestSuiteRequest request, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return new RunnerItemMutationResult
            {
                Outcome = RunnerItemMutationOutcome.NotFound,
                ErrorMessage = "Test Runner Item Not Found"
            };
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await EnsurePointBasedConfigurationStateAsync(connection, request.TestPlanItemId, cancellationToken);
        var executionId = ResolveExecutionIdentity(request.TestSuiteId, request.ExecutionId);
        var runnerItem = await LoadRunnerItemRecordAsync(connection, context.ClientId.Value, request.TestRunnerId, request.TestPlanItemId, executionId, cancellationToken);
        if (runnerItem is not RunnerItemRecord runnerItemValue)
        {
            return new RunnerItemMutationResult
            {
                Outcome = RunnerItemMutationOutcome.NotFound,
                ErrorMessage = "Test Runner Item Not Found"
            };
        }

        var pausedStepsJson = MarkPausedRunnerStepJson(runnerItemValue.StepsJson, request.ResumeStepId, request.ResumeStepIndex);
        var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await SaveRunnerItemStateAsync(connection, transaction, runnerItemValue, pausedStepsJson, InProgressStatusId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        return new RunnerItemMutationResult { Outcome = RunnerItemMutationOutcome.Saved };
    }

    public async Task<RunnerItemMutationResult> SaveAndCloseTestSuiteAsync(ClaimsPrincipal principal, SaveAndCloseTestSuiteRequest request, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return new RunnerItemMutationResult
            {
                Outcome = RunnerItemMutationOutcome.NotFound,
                ErrorMessage = "Test Runner Item Not Found"
            };
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await EnsurePointBasedConfigurationStateAsync(connection, request.TestPlanItemId, cancellationToken);
        var executionId = ResolveExecutionIdentity(request.TestSuiteId, request.ExecutionId);
        var runnerItem = await LoadRunnerItemRecordAsync(connection, context.ClientId.Value, request.TestRunnerId, request.TestPlanItemId, executionId, cancellationToken);
        if (runnerItem is not RunnerItemRecord runnerItemValue)
        {
            return new RunnerItemMutationResult
            {
                Outcome = RunnerItemMutationOutcome.NotFound,
                ErrorMessage = "Test Runner Item Not Found"
            };
        }

        var finalizedStepsJson = ClearPausedRunnerStepJson(runnerItemValue.StepsJson);
        var statusId = DetermineRunnerSuiteStatusId(finalizedStepsJson);
        var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await SaveRunnerItemStateAsync(connection, transaction, runnerItemValue, finalizedStepsJson, statusId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        return new RunnerItemMutationResult { Outcome = RunnerItemMutationOutcome.Saved };
    }

    public async Task<RunnerItemMutationResult> UploadTestSuiteVideoAsync(ClaimsPrincipal principal, long testRunnerId, long testSuiteId, IReadOnlyList<string> videoPaths, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return new RunnerItemMutationResult
            {
                Outcome = RunnerItemMutationOutcome.NotFound,
                ErrorMessage = "Test Runner Item Not Found"
            };
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await EnsurePointBasedConfigurationStateAsync(connection, null, cancellationToken);
        var runnerItem = await LoadRunnerItemRecordAsync(connection, context.ClientId.Value, testRunnerId, null, testSuiteId, cancellationToken);
        if (runnerItem is not RunnerItemRecord runnerItemValue)
        {
            return new RunnerItemMutationResult
            {
                Outcome = RunnerItemMutationOutcome.NotFound,
                ErrorMessage = "Test Runner Item Not Found"
            };
        }

        const string sql = """
            UPDATE test_runner_items
            SET videos = @videos,
                updated_at = SYSUTCDATETIME()
            WHERE id = @runnerItemId;
            """;

        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@videos", JsonSerializer.Serialize(videoPaths));
        command.Parameters.AddWithValue("@runnerItemId", runnerItemValue.RunnerItemId);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return new RunnerItemMutationResult { Outcome = RunnerItemMutationOutcome.Saved };
    }

    public async Task<IReadOnlyList<BasicRefDto>> GetTestSuiteChildrenAsync(ClaimsPrincipal principal, long testSuiteId, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return [];
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT id
            FROM test_designs
            WHERE parent_id = @testSuiteId AND client_id = @clientId AND deleted_at IS NULL
            ORDER BY id;
            """;
        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@testSuiteId", testSuiteId);
        command.Parameters.AddWithValue("@clientId", context.ClientId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<BasicRefDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new BasicRefDto { Id = reader.GetInt64(reader.GetOrdinal("id")) });
        }

        return rows;
    }

    public async Task<IReadOnlyList<TestStateDto>> GetTestRunnerStatusesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = "SELECT id, name FROM test_plan_item_suite_statuses ORDER BY id;";
        await using var command = CreateCommand(connection, sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<TestStateDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new TestStateDto { Id = reader.GetInt64(reader.GetOrdinal("id")), Name = GetString(reader, "name") });
        }

        return rows;
    }

    public async Task<PagedDataDto<TestRunnerLogItemDto>> GetTestRunnerLogItemsAsync(ClaimsPrincipal principal, long? testPlanId, long? testPlanItemId, string? testSuite, long? runBy, string? status, string? createdAt, string? testRunnerIds, bool includeInProgress, int page, int limit, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return CreatePagedData<TestRunnerLogItemDto>([], 0, limit);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await EnsureTestRunnerFavoriteColumnAsync(connection, cancellationToken);
        var whereClauses = new List<string> { "tr.client_id = @clientId" };
        var parameters = new List<SqlParameter>
        {
            new("@clientId", context.ClientId.Value),
            new("@currentUserId", context.UserId)
        };

        AddOptionalInt64Filter(testPlanId, "tp.id", "@testPlanId", whereClauses, parameters);
        AddOptionalInt64Filter(testPlanItemId, "tpi.id", "@testPlanItemId", whereClauses, parameters);
        AddOptionalInt64Filter(runBy, "tri.run_by", "@runBy", whereClauses, parameters);

        var suiteIds = ParseLongCsv(testSuite);
        if (suiteIds.Count > 0)
        {
            var suiteParams = AddIdListParameters(parameters, "@testSuiteId", suiteIds);
            whereClauses.Add($"tri.test_suite_id IN ({string.Join(", ", suiteParams)})");
        }

        var statusIds = ParseLongCsv(status);
        if (statusIds.Count > 0)
        {
            var statusParams = AddIdListParameters(parameters, "@statusId", statusIds);
            whereClauses.Add($"tri.status_id IN ({string.Join(", ", statusParams)})");
        }

        var explicitRunnerIds = ParseLongCsv(testRunnerIds);
        if (explicitRunnerIds.Count > 0)
        {
            var runnerItemParams = AddIdListParameters(parameters, "@runnerItemId", explicitRunnerIds);
            var runnerHeaderParams = AddIdListParameters(parameters, "@runnerId", explicitRunnerIds);
            whereClauses.Add($"(tri.id IN ({string.Join(", ", runnerItemParams)}) OR tri.test_runner_id IN ({string.Join(", ", runnerHeaderParams)}))");
        }
        else if (!includeInProgress)
        {
            whereClauses.Add("tri.status_id <> 2");
        }

        if (!string.IsNullOrWhiteSpace(createdAt) && DateOnly.TryParse(createdAt, out var createdDate))
        {
            whereClauses.Add("CAST(tri.created_at AS date) = @createdAt");
            parameters.Add(new SqlParameter("@createdAt", createdDate.ToDateTime(TimeOnly.MinValue)));
        }

        var fromSql = $"""
            FROM test_runner_items tri
            INNER JOIN test_runners tr ON tr.id = tri.test_runner_id
            INNER JOIN users users_ref ON users_ref.id = tri.run_by
            INNER JOIN test_plan_item_suite_statuses status_ref ON status_ref.id = tri.status_id
            INNER JOIN test_plan_items tpi ON tpi.id = tr.test_plan_item_id
            INNER JOIN test_plans tp ON tp.id = tpi.test_plan_id
            LEFT JOIN test_designs td ON td.id = tri.test_suite_id
            LEFT JOIN configurations cfg ON cfg.id = td.configuration_id
            LEFT JOIN defects d ON d.test_runner_item_id = tri.id
            WHERE {string.Join(" AND ", whereClauses)}
            """;

        var total = await ExecuteCountAsync(connection, $"SELECT COUNT(*) {fromSql};", parameters, cancellationToken);

        var sql = $"""
            SELECT
                tri.id,
                d.id AS defect_id,
                tri.test_runner_id,
                tp.id AS test_plan_id,
                tp.name AS test_plan_name,
                tpi.id AS test_plan_item_id,
                tpi.name AS test_plan_item_name,
                tri.test_suite_id,
                tri.execution_id,
                tri.test_suite_name,
                cfg.name AS configuration_name,
                CAST(ISNULL(tri.is_favorite, 0) AS bit) AS is_favorite,
                users_ref.id AS user_id,
                users_ref.name AS username,
                users_ref.email,
                status_ref.id AS status_id,
                status_ref.name AS status_name,
                tri.created_at,
                tri.updated_at,
                tri.comment,
                td.comment AS prereq,
                tri.videos,
                CASE WHEN d.id IS NULL THEN 1 ELSE 0 END AS CAN_CREATE_DEFECT,
                tri.steps
            {fromSql}
            ORDER BY tri.created_at DESC, tri.id DESC
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
            """;

        var rawRows = new List<(TestRunnerLogItemDto Row, long? ExecutionId, long? BaseTestSuiteId)>();
        await using var command = CreateCommand(connection, sql);
        AddParameters(command, parameters);
        command.Parameters.AddWithValue("@offset", (page - 1) * limit);
        command.Parameters.AddWithValue("@limit", limit);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var row = new TestRunnerLogItemDto
                {
                    Id = reader.GetInt64(reader.GetOrdinal("id")),
                    DefectId = GetInt64(reader, "defect_id"),
                    TestRunnerId = GetInt64(reader, "test_runner_id"),
                    TestPlanId = GetInt64(reader, "test_plan_id"),
                    TestPlanName = GetString(reader, "test_plan_name"),
                    TestPlanItemId = GetInt64(reader, "test_plan_item_id"),
                    TestPlanItemName = GetString(reader, "test_plan_item_name"),
                    TestSuiteId = GetInt64(reader, "test_suite_id"),
                    TestSuiteName = GetString(reader, "test_suite_name"),
                    ConfigurationName = GetString(reader, "configuration_name"),
                    IsFavorite = GetBoolean(reader, "is_favorite") ?? false,
                    UserId = GetInt64(reader, "user_id"),
                    Username = GetString(reader, "username"),
                    Email = GetString(reader, "email"),
                    StatusId = GetInt64(reader, "status_id"),
                    StatusName = GetString(reader, "status_name"),
                    CreatedAt = GetDateTimeOffset(reader, "created_at"),
                    UpdatedAt = GetDateTimeOffset(reader, "updated_at"),
                    Comment = GetString(reader, "comment"),
                    Prereq = GetString(reader, "prereq"),
                    Videos = GetString(reader, "videos"),
                    CanCreateDefect = GetInt32(reader, "CAN_CREATE_DEFECT") ?? 0,
                    AddedSteps = ParseJsonElementOrDefault(GetString(reader, "steps"), Array.Empty<object>())
                };

                rawRows.Add((row, GetInt64(reader, "execution_id"), row.TestSuiteId));
            }
        }

        var configurationsByRunnerItemId = await LoadRunnerItemConfigurationsAsync(
            connection,
            context.ClientId.Value,
            rawRows.Select(row => (row.Row.Id, row.ExecutionId, row.BaseTestSuiteId)).ToArray(),
            cancellationToken);

        var rows = rawRows
            .Select(row => configurationsByRunnerItemId.TryGetValue(row.Row.Id, out var configuration)
                ? WithConfiguration(row.Row, configuration)
                : row.Row)
            .ToList();

        return CreatePagedData(rows, total, limit);
    }

    public async Task<IReadOnlyList<GlobalKeywordDto>> GetGlobalKeywordsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string sql = """
            SELECT
                gk.id,
                gk.name,
                (
                    SELECT COUNT(*)
                    FROM component_steps cs
                    WHERE cs.deleted_at IS NULL
                      AND (
                       cs.global_keyword_id = gk.id
                       OR cs.keyword_id IN (
                            SELECT ck.id
                            FROM component_keywords ck
                            WHERE ck.global_keyword_id = gk.id
                       )
                      )
                ) AS usage_count
            FROM global_keywords gk
            ORDER BY gk.name;
            """;

        await using var command = CreateCommand(connection, sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<GlobalKeywordDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new GlobalKeywordDto
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                Name = GetString(reader, "name") ?? string.Empty,
                UsageCount = GetInt32(reader, "usage_count") ?? 0
            });
        }

        return rows;
    }

    public async Task<GlobalKeywordDto?> CreateGlobalKeywordAsync(string name, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var exists = await ExecuteCountAsync(connection, "SELECT COUNT(*) FROM global_keywords WHERE name = @name;", [new SqlParameter("@name", name)], cancellationToken);
        if (exists > 0)
        {
            return null;
        }

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        long id;
        await using (var insertCommand = CreateCommand(connection, "INSERT INTO global_keywords (name, created_at, updated_at) OUTPUT INSERTED.id VALUES (@name, SYSUTCDATETIME(), SYSUTCDATETIME());"))
        {
            insertCommand.Transaction = transaction;
            insertCommand.Parameters.AddWithValue("@name", name);
            id = Convert.ToInt64(await insertCommand.ExecuteScalarAsync(cancellationToken));
        }

        var clientIds = new List<long>();
        await using (var clientsCommand = CreateCommand(connection, "SELECT id FROM clients;"))
        {
            clientsCommand.Transaction = transaction;
            await using var clientsReader = await clientsCommand.ExecuteReaderAsync(cancellationToken);
            while (await clientsReader.ReadAsync(cancellationToken))
            {
                clientIds.Add(clientsReader.GetInt64(clientsReader.GetOrdinal("id")));
            }
        }

        foreach (var clientId in clientIds)
        {
            await using var keywordCommand = CreateCommand(connection, """
                INSERT INTO component_keywords (name, client_id, keyword_combination_ids, replica_id, global_keyword_id, created_at, updated_at)
                VALUES (@name, @clientId, NULL, NULL, @globalKeywordId, SYSUTCDATETIME(), SYSUTCDATETIME());
                """);
            keywordCommand.Transaction = transaction;
            keywordCommand.Parameters.AddWithValue("@name", name);
            keywordCommand.Parameters.AddWithValue("@clientId", clientId);
            keywordCommand.Parameters.AddWithValue("@globalKeywordId", id);
            await keywordCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return await GetGlobalKeywordByIdAsync(connection, id, cancellationToken);
    }

    public async Task<GlobalKeywordDto?> UpdateGlobalKeywordAsync(long id, string name, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var duplicateCount = await ExecuteCountAsync(connection, "SELECT COUNT(*) FROM global_keywords WHERE name = @name AND id <> @id;", [new SqlParameter("@name", name), new SqlParameter("@id", id)], cancellationToken);
        if (duplicateCount > 0)
        {
            return null;
        }

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var updateCommand = CreateCommand(connection, "UPDATE global_keywords SET name = @name, updated_at = SYSUTCDATETIME() WHERE id = @id;"))
        {
            updateCommand.Transaction = transaction;
            updateCommand.Parameters.AddWithValue("@name", name);
            updateCommand.Parameters.AddWithValue("@id", id);
            var affected = await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            if (affected == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }
        }

        await using (var updateKeywordsCommand = CreateCommand(connection, "UPDATE component_keywords SET name = @name, updated_at = SYSUTCDATETIME() WHERE global_keyword_id = @id;"))
        {
            updateKeywordsCommand.Transaction = transaction;
            updateKeywordsCommand.Parameters.AddWithValue("@name", name);
            updateKeywordsCommand.Parameters.AddWithValue("@id", id);
            await updateKeywordsCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return await GetGlobalKeywordByIdAsync(connection, id, cancellationToken);
    }

    public async Task<(bool Found, bool InUse)> DeleteGlobalKeywordAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var found = await ExecuteCountAsync(connection, "SELECT COUNT(*) FROM global_keywords WHERE id = @id;", [new SqlParameter("@id", id)], cancellationToken) > 0;
        if (!found)
        {
            return (false, false);
        }

        var inUseSql = """
            SELECT CASE WHEN EXISTS (
                SELECT 1
                FROM component_steps cs
                WHERE cs.global_keyword_id = @id
                   OR cs.keyword_id IN (
                        SELECT ck.id
                        FROM component_keywords ck
                        WHERE ck.global_keyword_id = @id
                   )
            ) THEN 1 ELSE 0 END;
            """;
        await using (var inUseCommand = CreateCommand(connection, inUseSql))
        {
            inUseCommand.Parameters.AddWithValue("@id", id);
            var inUse = Convert.ToInt32(await inUseCommand.ExecuteScalarAsync(cancellationToken) ?? 0) > 0;
            if (inUse)
            {
                return (true, true);
            }
        }

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var deleteKeywordsCommand = CreateCommand(connection, "DELETE FROM component_keywords WHERE global_keyword_id = @id;"))
        {
            deleteKeywordsCommand.Transaction = transaction;
            deleteKeywordsCommand.Parameters.AddWithValue("@id", id);
            await deleteKeywordsCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteCommand = CreateCommand(connection, "DELETE FROM global_keywords WHERE id = @id;"))
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.Parameters.AddWithValue("@id", id);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return (true, false);
    }

    private async Task<VariableTypeDto?> GetVariableTypeAsync(SqlConnection connection, long id, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, name, method, executable_method, value, params, CAST(ISNULL(is_encrypted, 0) AS bit) AS is_encrypted
            FROM variable_types
            WHERE id = @id;
            """;

        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new VariableTypeDto
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            Name = GetString(reader, "name"),
            Method = GetString(reader, "method"),
            ExecutableMethod = GetString(reader, "executable_method"),
            Value = GetInt64(reader, "value"),
            Params = GetString(reader, "params"),
            IsEncrypted = GetBoolean(reader, "is_encrypted") ?? false
        };
    }

    private CustomVariableDto MapCustomVariable(SqlDataReader reader)
    {
        var variable = GetInt64(reader, "vt_id") is long variableTypeId
            ? new VariableTypeDto
            {
                Id = variableTypeId,
                Name = GetString(reader, "vt_name"),
                Method = GetString(reader, "vt_method"),
                ExecutableMethod = GetString(reader, "vt_executable_method"),
                Value = GetInt64(reader, "vt_value"),
                Params = GetString(reader, "vt_params"),
                IsEncrypted = GetBoolean(reader, "vt_is_encrypted") ?? false
            }
            : null;
        var rawValue = GetString(reader, "value");
        return new CustomVariableDto
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            Name = GetString(reader, "name"),
            Value = rawValue,
            ResolvedValue = VariableValueResolver.Resolve(rawValue, variable?.ExecutableMethod, GetBoolean(reader, "is_encrypted") ?? false),
            VariableId = reader.GetInt64(reader.GetOrdinal("variable_id")),
            TestCaseId = GetInt64(reader, "test_case_id"),
            IsEncrypted = GetBoolean(reader, "is_encrypted") ?? false,
            Variable = variable
        };
    }

    private async Task<CustomVariableDto?> GetCustomVariableByIdAsync(SqlConnection connection, long clientId, long id, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                cv.id,
                cv.name,
                cv.value,
                cv.variable_id,
                cv.test_case_id,
                CAST(ISNULL(cv.is_encrypted, 0) AS bit) AS is_encrypted,
                vt.id AS vt_id,
                vt.name AS vt_name,
                vt.method AS vt_method,
                vt.executable_method AS vt_executable_method,
                vt.value AS vt_value,
                vt.params AS vt_params,
                CAST(ISNULL(vt.is_encrypted, 0) AS bit) AS vt_is_encrypted
            FROM custom_variables cv
            LEFT JOIN variable_types vt ON vt.id = cv.variable_id
            WHERE cv.id = @id AND cv.client_id = @clientId;
            """;

        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@clientId", clientId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return MapCustomVariable(reader);
    }

    private async Task<Dictionary<long, IReadOnlyList<ConfigurationVariableValueDto>>> LoadConfigurationValuesAsync(SqlConnection connection, IReadOnlyList<long> variableIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, IReadOnlyList<ConfigurationVariableValueDto>>();
        if (variableIds.Count == 0)
        {
            return result;
        }

        var parameters = AddIdListParameterValues(variableIds, "@configurationVariableId");
        var sql = $"SELECT id, name, variable_id FROM configuration_variable_values WHERE variable_id IN ({string.Join(", ", parameters.Select(parameter => parameter.ParameterName))}) ORDER BY id;";
        await using var command = CreateCommand(connection, sql);
        AddParameters(command, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var buffer = new Dictionary<long, List<ConfigurationVariableValueDto>>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var variableId = reader.GetInt64(reader.GetOrdinal("variable_id"));
            if (!buffer.TryGetValue(variableId, out var values))
            {
                values = [];
                buffer[variableId] = values;
            }

            values.Add(new ConfigurationVariableValueDto
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                Name = GetString(reader, "name")
            });
        }

        foreach (var pair in buffer)
        {
            result[pair.Key] = pair.Value;
        }

        return result;
    }

    private async Task InsertConfigurationValuesAsync(SqlConnection connection, SqlTransaction transaction, long configurationId, IReadOnlyList<SaveConfigurationVariableValueRequest> values, CancellationToken cancellationToken)
    {
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value.Name))
            {
                continue;
            }

            await using var command = CreateCommand(connection, "INSERT INTO configuration_variable_values (name, variable_id, created_at, updated_at) VALUES (@name, @variableId, SYSUTCDATETIME(), SYSUTCDATETIME());");
            command.Transaction = transaction;
            command.Parameters.AddWithValue("@name", value.Name.Trim());
            command.Parameters.AddWithValue("@variableId", configurationId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task<ConfigurationVariableDto?> GetConfigurationVariableByIdAsync(SqlConnection connection, long clientId, long id, CancellationToken cancellationToken)
    {
        const string sql = "SELECT id, name, description FROM configuration_variables WHERE id = @id AND client_id = @clientId;";
        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@clientId", clientId);

        ConfigurationVariableDto? dto = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            dto = new ConfigurationVariableDto
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                Name = GetString(reader, "name"),
                Description = GetString(reader, "description")
            };
        }

        if (dto is null)
        {
            return null;
        }

        var valuesMap = await LoadConfigurationValuesAsync(connection, [dto.Id], cancellationToken);
        return new ConfigurationVariableDto
        {
            Id = dto.Id,
            Name = dto.Name,
            Description = dto.Description,
            VariableValues = valuesMap.TryGetValue(dto.Id, out var values) ? values : []
        };
    }

    private async Task<Dictionary<long, IReadOnlyList<UserBasicDto>>> LoadTestPlanUsersAsync(SqlConnection connection, IReadOnlyList<long> planIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, IReadOnlyList<UserBasicDto>>();
        if (planIds.Count == 0)
        {
            return result;
        }

        var parameters = AddIdListParameterValues(planIds, "@planId");
        var sql = $"""
            SELECT
                tpu.test_plan_id,
                u.id,
                u.name,
                u.email
            FROM test_plan_users tpu
            INNER JOIN users u ON u.id = tpu.user_id
            WHERE tpu.test_plan_id IN ({string.Join(", ", parameters.Select(parameter => parameter.ParameterName))})
            ORDER BY u.name, u.id;
            """;

        await using var command = CreateCommand(connection, sql);
        AddParameters(command, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var buffer = new Dictionary<long, List<UserBasicDto>>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var planId = reader.GetInt64(reader.GetOrdinal("test_plan_id"));
            if (!buffer.TryGetValue(planId, out var users))
            {
                users = [];
                buffer[planId] = users;
            }

            users.Add(new UserBasicDto
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                Name = GetString(reader, "name"),
                Email = GetString(reader, "email")
            });
        }

        foreach (var pair in buffer)
        {
            result[pair.Key] = pair.Value;
        }

        return result;
    }

    private async Task<Dictionary<long, IReadOnlyList<UserBasicDto>>> LoadPlanItemSuiteUsersAsync(SqlConnection connection, IReadOnlyList<long> suiteLinkIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, IReadOnlyList<UserBasicDto>>();
        if (suiteLinkIds.Count == 0)
        {
            return result;
        }

        var parameters = AddIdListParameterValues(suiteLinkIds, "@suiteLinkId");
        var sql = $"""
            SELECT
                tpisu.test_plan_item_suite_id,
                u.id,
                u.name,
                u.email
            FROM test_plan_item_suite_users tpisu
            INNER JOIN users u ON u.id = tpisu.user_id
            WHERE tpisu.test_plan_item_suite_id IN ({string.Join(", ", parameters.Select(parameter => parameter.ParameterName))})
            ORDER BY u.name, u.id;
            """;

        await using var command = CreateCommand(connection, sql);
        AddParameters(command, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var buffer = new Dictionary<long, List<UserBasicDto>>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var suiteLinkId = reader.GetInt64(reader.GetOrdinal("test_plan_item_suite_id"));
            if (!buffer.TryGetValue(suiteLinkId, out var users))
            {
                users = [];
                buffer[suiteLinkId] = users;
            }

            users.Add(new UserBasicDto
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                Name = GetString(reader, "name"),
                Email = GetString(reader, "email")
            });
        }

        foreach (var pair in buffer)
        {
            result[pair.Key] = pair.Value;
        }

        return result;
    }

    private async Task<Dictionary<long, SuiteConfigurationDto>> LoadSuiteConfigurationsAsync(SqlConnection connection, IReadOnlyList<long> configurationIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, SuiteConfigurationDto>();
        if (configurationIds.Count == 0)
        {
            return result;
        }

        var parameters = AddIdListParameterValues(configurationIds, "@configurationId");
        var sql = $"SELECT id, name FROM configurations WHERE id IN ({string.Join(", ", parameters.Select(parameter => parameter.ParameterName))});";
        await using (var command = CreateCommand(connection, sql))
        {
            AddParameters(command, parameters);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                var configurationId = reader.GetInt64(reader.GetOrdinal("id"));
                result[configurationId] = new SuiteConfigurationDto
                {
                    Id = configurationId,
                    Name = GetString(reader, "name")
                };
            }
        }

        var selectionMap = await LoadConfigurationSelectionsAsync(connection, configurationIds, cancellationToken);
        foreach (var configurationId in configurationIds)
        {
            if (!result.TryGetValue(configurationId, out var configuration))
            {
                continue;
            }

            result[configurationId] = new SuiteConfigurationDto
            {
                Id = configurationId,
                Name = configuration.Name,
                ConfigurationVariables = selectionMap.TryGetValue(configurationId, out var selections) ? selections : []
            };
        }

        return result;
    }

    private async Task<Dictionary<long, SuiteConfigurationDto>> LoadRunnerItemConfigurationsAsync(
        SqlConnection connection,
        long clientId,
        IReadOnlyList<(long RunnerItemId, long? ExecutionId, long? BaseTestSuiteId)> runnerItems,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, SuiteConfigurationDto>();
        if (runnerItems.Count == 0)
        {
            return result;
        }

        var distinctRunnerItems = runnerItems
            .Where(row => row.RunnerItemId != 0)
            .GroupBy(row => row.RunnerItemId)
            .Select(group => group.First())
            .ToArray();
        if (distinctRunnerItems.Length == 0)
        {
            return result;
        }

        var executionIds = distinctRunnerItems
            .Select(row => row.ExecutionId ?? row.BaseTestSuiteId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();
        var contextMap = executionIds.Length == 0
            ? new Dictionary<long, ExecutionSuiteContext>()
            : (await LoadExecutionSuiteContextsAsync(connection, clientId, executionIds, cancellationToken))
                .ToDictionary(row => row.ExecutionId);

        var baseSuiteIds = distinctRunnerItems
            .Select(row => row.BaseTestSuiteId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Concat(contextMap.Values.Select(row => row.BaseTestDesignId))
            .Where(id => id != 0)
            .Distinct()
            .ToArray();

        var baseSuiteConfigurationMap = new Dictionary<long, long?>();
        if (baseSuiteIds.Length > 0)
        {
            var parameters = AddIdListParameterValues(baseSuiteIds, "@suiteId");
            var sql = $"SELECT id, configuration_id FROM test_designs WHERE id IN ({string.Join(", ", parameters.Select(parameter => parameter.ParameterName))});";
            await using (var command = CreateCommand(connection, sql))
            {
                AddParameters(command, parameters);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    baseSuiteConfigurationMap[reader.GetInt64(reader.GetOrdinal("id"))] = GetInt64(reader, "configuration_id");
                }
            }
        }

        static long? ResolveConfigurationId(
            (long RunnerItemId, long? ExecutionId, long? BaseTestSuiteId) runnerItem,
            IReadOnlyDictionary<long, ExecutionSuiteContext> contexts,
            IReadOnlyDictionary<long, long?> baseConfigurations)
        {
            var executionId = runnerItem.ExecutionId ?? runnerItem.BaseTestSuiteId;
            if (executionId.HasValue
                && contexts.TryGetValue(executionId.Value, out var context)
                && context.ConfigurationId.HasValue)
            {
                return context.ConfigurationId.Value;
            }

            return runnerItem.BaseTestSuiteId.HasValue
                && baseConfigurations.TryGetValue(runnerItem.BaseTestSuiteId.Value, out var configurationId)
                ? configurationId
                : null;
        }

        var configurationIds = distinctRunnerItems
            .Select(row => ResolveConfigurationId(row, contextMap, baseSuiteConfigurationMap))
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();
        var configurationMap = await LoadSuiteConfigurationsAsync(connection, configurationIds, cancellationToken);

        foreach (var runnerItem in distinctRunnerItems)
        {
            var configurationId = ResolveConfigurationId(runnerItem, contextMap, baseSuiteConfigurationMap);
            if (!configurationId.HasValue || !configurationMap.TryGetValue(configurationId.Value, out var configuration))
            {
                continue;
            }

            result[runnerItem.RunnerItemId] = configuration;
        }

        return result;
    }

    private static TestRunnerLogItemDto WithConfiguration(TestRunnerLogItemDto row, SuiteConfigurationDto configuration)
    {
        return new TestRunnerLogItemDto
        {
            Id = row.Id,
            DefectId = row.DefectId,
            TestRunnerId = row.TestRunnerId,
            TestPlanId = row.TestPlanId,
            TestPlanName = row.TestPlanName,
            TestPlanItemId = row.TestPlanItemId,
            TestPlanItemName = row.TestPlanItemName,
            TestSuiteId = row.TestSuiteId,
            TestSuiteName = row.TestSuiteName,
            ConfigurationName = configuration.Name ?? row.ConfigurationName,
            ConfigurationVariables = configuration.ConfigurationVariables,
            IsFavorite = row.IsFavorite,
            UserId = row.UserId,
            Username = row.Username,
            Email = row.Email,
            StatusId = row.StatusId,
            StatusName = row.StatusName,
            CreatedAt = row.CreatedAt,
            UpdatedAt = row.UpdatedAt,
            AddedSteps = row.AddedSteps,
            Comment = row.Comment,
            Prereq = row.Prereq,
            Videos = row.Videos,
            CanCreateDefect = row.CanCreateDefect
        };
    }

    private static DefectListItemDto WithConfiguration(DefectListItemDto row, SuiteConfigurationDto configuration)
    {
        return new DefectListItemDto
        {
            Id = row.Id,
            Title = row.Title,
            Description = row.Description,
            Expected = row.Expected,
            Actual = row.Actual,
            TestRunnerItemId = row.TestRunnerItemId,
            Assigned = row.Assigned,
            Status = row.Status,
            CreatedBy = row.CreatedBy,
            CreatedAt = row.CreatedAt,
            Attachments = row.Attachments,
            TestPlanItem = row.TestPlanItem is null
                ? null
                : new DefectRunnerItemDto
                {
                    TestPlanName = row.TestPlanItem.TestPlanName,
                    TestPlanItemId = row.TestPlanItem.TestPlanItemId,
                    TestPlanItemName = row.TestPlanItem.TestPlanItemName,
                    TestSuiteName = row.TestPlanItem.TestSuiteName,
                    ConfigurationName = configuration.Name ?? row.TestPlanItem.ConfigurationName,
                    ConfigurationVariables = configuration.ConfigurationVariables,
                    AddedSteps = row.TestPlanItem.AddedSteps,
                    CreatedAt = row.TestPlanItem.CreatedAt,
                    User = row.TestPlanItem.User,
                    TestSuite = row.TestPlanItem.TestSuite
                }
        };
    }

    private async Task<Dictionary<long, IReadOnlyList<ConfigurationSelectedVariableDto>>> LoadConfigurationSelectionsAsync(SqlConnection connection, IReadOnlyList<long> configurationIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, IReadOnlyList<ConfigurationSelectedVariableDto>>();
        if (configurationIds.Count == 0)
        {
            return result;
        }

        var parameters = AddIdListParameterValues(configurationIds, "@configurationId");
        var sql = $"""
            SELECT
                csv.configuration_id,
                cv.id AS variable_id,
                cv.name AS variable_name,
                cvv.id AS value_id,
                cvv.name AS value_name
            FROM configurations_selected_variables csv
            LEFT JOIN configuration_variables cv ON cv.id = csv.variable_id
            LEFT JOIN configuration_variable_values cvv ON cvv.id = csv.variable_value_id
            WHERE csv.configuration_id IN ({string.Join(", ", parameters.Select(parameter => parameter.ParameterName))})
            ORDER BY csv.id;
            """;

        await using var command = CreateCommand(connection, sql);
        AddParameters(command, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var buffer = new Dictionary<long, List<ConfigurationSelectedVariableDto>>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var configurationId = reader.GetInt64(reader.GetOrdinal("configuration_id"));
            if (!buffer.TryGetValue(configurationId, out var selections))
            {
                selections = [];
                buffer[configurationId] = selections;
            }

            selections.Add(new ConfigurationSelectedVariableDto
            {
                Variable = GetInt64(reader, "variable_id") is long variableId ? new BasicRefDto { Id = variableId, Name = GetString(reader, "variable_name") } : null,
                Value = GetInt64(reader, "value_id") is long valueId ? new BasicRefDto { Id = valueId, Name = GetString(reader, "value_name") } : null
            });
        }

        foreach (var pair in buffer)
        {
            result[pair.Key] = pair.Value;
        }

        return result;
    }

    private async Task<(JsonElement Payload, int ItemsCount, bool HasItems)> HydrateSchedulePayloadAsync(SqlConnection connection, string? payloadJson, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return (JsonSerializer.SerializeToElement(new { }), 0, false);
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(payloadJson);
        }
        catch
        {
            return (ParseJsonElementOrDefault(payloadJson, new { }), 0, false);
        }

        if (root is not JsonObject obj)
        {
            return (JsonSerializer.SerializeToElement(new { }), 0, false);
        }

        var items = obj["items"] as JsonArray;
        if (items is null)
        {
            return (JsonSerializer.SerializeToElement(obj), 0, false);
        }

        var suiteIds = new List<long>();
        foreach (var itemNode in items)
        {
            if (itemNode is not JsonObject itemObject)
            {
                continue;
            }

            if (itemObject["test_suite_ids"] is not JsonArray suiteArray)
            {
                continue;
            }

            foreach (var suiteNode in suiteArray)
            {
                if (suiteNode is null)
                {
                    continue;
                }

                if (long.TryParse(suiteNode.ToJsonString().Trim('"'), out var suiteId))
                {
                    suiteIds.Add(suiteId);
                }
            }
        }

        var suiteNames = await LoadSuiteNamesAsync(connection, suiteIds.Distinct().ToArray(), cancellationToken);
        foreach (var itemNode in items)
        {
            if (itemNode is not JsonObject itemObject)
            {
                continue;
            }

            var suiteNameArray = new JsonArray();
            if (itemObject["test_suite_ids"] is JsonArray suiteArray)
            {
                foreach (var suiteNode in suiteArray)
                {
                    if (suiteNode is null)
                    {
                        continue;
                    }

                    if (long.TryParse(suiteNode.ToJsonString().Trim('"'), out var suiteId) && suiteNames.TryGetValue(suiteId, out var suiteName) && !string.IsNullOrWhiteSpace(suiteName))
                    {
                        suiteNameArray.Add(suiteName);
                    }
                }
            }

            itemObject["test_suite_names"] = suiteNameArray;
        }

        return (JsonSerializer.SerializeToElement(obj), items.Count, items.Count > 0);
    }

    private async Task<Dictionary<long, string>> LoadSuiteNamesAsync(SqlConnection connection, IReadOnlyList<long> suiteIds, CancellationToken cancellationToken, SqlTransaction? transaction = null)
    {
        var result = new Dictionary<long, string>();
        if (suiteIds.Count == 0)
        {
            return result;
        }

        var parameters = AddIdListParameterValues(suiteIds, "@suiteId");
        var sql = $"SELECT id, title FROM test_designs WHERE id IN ({string.Join(", ", parameters.Select(parameter => parameter.ParameterName))});";
        await using var command = CreateCommand(connection, sql);
        command.Transaction = transaction;
        AddParameters(command, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result[reader.GetInt64(reader.GetOrdinal("id"))] = GetString(reader, "title") ?? string.Empty;
        }

        return result;
    }

    private async Task<Dictionary<long, IReadOnlyList<ExecutionQueueItemDto>>> LoadExecutionQueueItemsAsync(SqlConnection connection, IReadOnlyList<long> queueIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, IReadOnlyList<ExecutionQueueItemDto>>();
        if (queueIds.Count == 0)
        {
            return result;
        }

        var parameters = AddIdListParameterValues(queueIds, "@queueId");
        var sql = $"""
            SELECT
                qi.execution_queue_id,
                qi.id,
                qi.status,
                COALESCE(qi.last_status, previous_item.previous_status) AS last_status,
                qi.last_run_at,
                qi.queue_run_id,
                qi.attempts,
                qi.test_suite_id,
                qi.test_suite_name,
                qi.test_plan_id,
                qi.test_plan_item_id
            FROM execution_queue_items qi
            OUTER APPLY (
                SELECT TOP 1 previous_qi.status AS previous_status
                FROM execution_queue_items previous_qi
                INNER JOIN execution_queues previous_q ON previous_q.id = previous_qi.execution_queue_id
                WHERE previous_qi.client_id = qi.client_id
                  AND previous_qi.test_suite_id = qi.test_suite_id
                  AND ((previous_qi.test_plan_item_id IS NULL AND qi.test_plan_item_id IS NULL) OR previous_qi.test_plan_item_id = qi.test_plan_item_id)
                  AND previous_qi.id < qi.id
                  AND previous_qi.status IN ('passed', 'failed', 'glitch', 'canceled')
                ORDER BY previous_qi.id DESC
            ) previous_item
            WHERE qi.execution_queue_id IN ({string.Join(", ", parameters.Select(parameter => parameter.ParameterName))})
            ORDER BY qi.id;
            """;

        await using var command = CreateCommand(connection, sql);
        AddParameters(command, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var buffer = new Dictionary<long, List<ExecutionQueueItemDto>>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var queueId = reader.GetInt64(reader.GetOrdinal("execution_queue_id"));
            if (!buffer.TryGetValue(queueId, out var items))
            {
                items = [];
                buffer[queueId] = items;
            }

            items.Add(new ExecutionQueueItemDto
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                Status = GetString(reader, "status"),
                LastStatus = GetString(reader, "last_status"),
                LastRunAt = GetDateTimeOffset(reader, "last_run_at"),
                QueueRunId = GetInt64(reader, "queue_run_id"),
                Attempts = GetInt32(reader, "attempts"),
                TestSuiteId = GetInt64(reader, "test_suite_id"),
                ExecutionId = GetInt64(reader, "test_suite_id"),
                TestSuiteName = GetString(reader, "test_suite_name"),
                TestPlanId = GetInt64(reader, "test_plan_id"),
                TestPlanItemId = GetInt64(reader, "test_plan_item_id")
            });
        }

        foreach (var pair in buffer)
        {
            result[pair.Key] = pair.Value;
        }

        return result;
    }

    private async Task<GlobalKeywordDto?> GetGlobalKeywordByIdAsync(SqlConnection connection, long id, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                gk.id,
                gk.name,
                (
                    SELECT COUNT(*)
                    FROM component_steps cs
                    WHERE cs.deleted_at IS NULL
                      AND (
                       cs.global_keyword_id = gk.id
                       OR cs.keyword_id IN (
                            SELECT ck.id
                            FROM component_keywords ck
                            WHERE ck.global_keyword_id = gk.id
                       )
                      )
                ) AS usage_count
            FROM global_keywords gk
            WHERE gk.id = @id;
            """;

        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new GlobalKeywordDto
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            Name = GetString(reader, "name") ?? string.Empty,
            UsageCount = GetInt32(reader, "usage_count") ?? 0
        };
    }

    private async Task<IntegrationConnectionDto?> GetIntegrationConnectionByIdAsync(SqlConnection connection, long clientId, long id, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                id,
                client_id,
                project_id,
                provider,
                name,
                CAST(ISNULL(is_enabled, 0) AS bit) AS is_enabled,
                CAST(ISNULL(sync_test_cases, 0) AS bit) AS sync_test_cases,
                CAST(ISNULL(sync_test_plans, 0) AS bit) AS sync_test_plans,
                CAST(ISNULL(sync_test_runs, 0) AS bit) AS sync_test_runs,
                CAST(ISNULL(sync_defects, 0) AS bit) AS sync_defects,
                CAST(ISNULL(auto_sync_test_cases, 0) AS bit) AS auto_sync_test_cases,
                CAST(ISNULL(auto_sync_test_runs, 0) AS bit) AS auto_sync_test_runs,
                CAST(ISNULL(auto_sync_defects, 0) AS bit) AS auto_sync_defects,
                config_json,
                credentials_encrypted,
                created_by,
                updated_by,
                created_at,
                updated_at
            FROM integration_connections
            WHERE id = @id AND client_id = @clientId;
            """;

        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@clientId", clientId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new IntegrationConnectionDto
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            ClientId = reader.GetInt64(reader.GetOrdinal("client_id")),
            ProjectId = GetInt64(reader, "project_id"),
            Provider = GetString(reader, "provider"),
            Name = GetString(reader, "name"),
            IsEnabled = GetBoolean(reader, "is_enabled") ?? false,
            SyncTestCases = GetBoolean(reader, "sync_test_cases") ?? false,
            SyncTestPlans = GetBoolean(reader, "sync_test_plans") ?? false,
            SyncTestRuns = GetBoolean(reader, "sync_test_runs") ?? false,
            SyncDefects = GetBoolean(reader, "sync_defects") ?? false,
            AutoSyncTestCases = GetBoolean(reader, "auto_sync_test_cases") ?? false,
            AutoSyncTestRuns = GetBoolean(reader, "auto_sync_test_runs") ?? false,
            AutoSyncTestDefects = GetBoolean(reader, "auto_sync_defects") ?? false,
            Config = ParseJsonElementOrDefault(GetString(reader, "config_json"), new { }),
            HasCredentials = !string.IsNullOrWhiteSpace(GetString(reader, "credentials_encrypted")),
            CreatedBy = GetInt64(reader, "created_by"),
            UpdatedBy = GetInt64(reader, "updated_by"),
            CreatedAt = GetDateTimeOffset(reader, "created_at"),
            UpdatedAt = GetDateTimeOffset(reader, "updated_at")
        };
    }

    private async Task<IntegrationJobDto?> GetIntegrationJobByIdAsync(SqlConnection connection, long clientId, long id, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP 1
                j.id,
                j.integration_connection_id,
                j.client_id,
                c.project_id,
                j.entity_type,
                j.internal_id,
                j.status,
                j.attempts,
                j.max_attempts,
                j.last_error,
                j.created_at,
                j.sent_at
            FROM integration_jobs j
            LEFT JOIN integration_connections c ON c.id = j.integration_connection_id
            WHERE j.id = @id AND j.client_id = @clientId;
            """;

        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@clientId", clientId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new IntegrationJobDto
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            IntegrationConnectionId = GetInt64(reader, "integration_connection_id"),
            ClientId = reader.GetInt64(reader.GetOrdinal("client_id")),
            ProjectId = GetInt64(reader, "project_id"),
            EntityType = GetString(reader, "entity_type"),
            InternalId = GetInt64(reader, "internal_id"),
            Status = GetString(reader, "status"),
            Attempts = GetInt32(reader, "attempts") ?? 0,
            MaxAttempts = GetInt32(reader, "max_attempts") ?? 0,
            LastError = GetString(reader, "last_error"),
            CreatedAt = GetDateTimeOffset(reader, "created_at"),
            SentAt = GetDateTimeOffset(reader, "sent_at")
        };
    }

    private async Task<bool> ProjectBelongsToClientAsync(SqlConnection connection, long clientId, long projectId, CancellationToken cancellationToken)
    {
        const string sql = "SELECT COUNT(*) FROM projects WHERE id = @projectId AND client_id = @clientId AND deleted_at IS NULL;";
        var count = await ExecuteCountAsync(connection, sql, [new SqlParameter("@projectId", projectId), new SqlParameter("@clientId", clientId)], cancellationToken);
        return count > 0;
    }

    private async Task<int> CountAttachedComponentsAsync(SqlConnection connection, long clientId, long projectId, CancellationToken cancellationToken)
    {
        const string sql = "SELECT COUNT(*) FROM components WHERE project_id = @projectId AND client_id = @clientId AND deleted_at IS NULL;";
        return await ExecuteCountAsync(connection, sql, [new SqlParameter("@projectId", projectId), new SqlParameter("@clientId", clientId)], cancellationToken);
    }

    private async Task<UserRecord?> GetUserRecordAsync(SqlConnection connection, long clientId, long userId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP 1 u.id, CAST(ISNULL(u.is_active, 1) AS bit) AS is_active
            FROM users u
            WHERE u.id = @userId AND u.client_id = @clientId AND u.deleted_at IS NULL;
            """;

        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@userId", userId);
        command.Parameters.AddWithValue("@clientId", clientId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new UserRecord(
            reader.GetInt64(reader.GetOrdinal("id")),
            GetBoolean(reader, "is_active") ?? true);
    }

    private async Task<IReadOnlyList<UserRecord>> GetUsersByIdsAsync(SqlConnection connection, long clientId, IReadOnlyList<long> userIds, CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
        {
            return [];
        }

        var parameters = new List<SqlParameter> { new("@clientId", clientId) };
        var placeholders = AddIdListParameters(parameters, "@userId", userIds);
        var sql = $"""
            SELECT u.id, CAST(ISNULL(u.is_active, 1) AS bit) AS is_active
            FROM users u
            WHERE u.client_id = @clientId AND u.deleted_at IS NULL AND u.id IN ({string.Join(", ", placeholders)});
            """;

        await using var command = CreateCommand(connection, sql);
        AddParameters(command, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var users = new List<UserRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            users.Add(new UserRecord(
                reader.GetInt64(reader.GetOrdinal("id")),
                GetBoolean(reader, "is_active") ?? true));
        }

        return users;
    }

    private async Task<UserDetailDto?> GetUserDetailByIdAsync(SqlConnection connection, long clientId, long userId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP 1 u.id, u.name, u.email, u.phone, u.job_title, u.department, u.timezone, CAST(ISNULL(u.is_active, 1) AS bit) AS is_active
            FROM users u
            WHERE u.id = @userId AND u.client_id = @clientId AND u.deleted_at IS NULL;
            """;

        long detailId;
        string detailName;
        string detailEmail;
        string? detailPhone;
        string? detailJobTitle;
        string? detailDepartment;
        string? detailTimezone;
        bool detailIsActive;
        await using (var command = CreateCommand(connection, sql))
        {
            command.Parameters.AddWithValue("@userId", userId);
            command.Parameters.AddWithValue("@clientId", clientId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            detailId = reader.GetInt64(reader.GetOrdinal("id"));
            detailName = GetString(reader, "name") ?? string.Empty;
            detailEmail = GetString(reader, "email") ?? string.Empty;
            detailPhone = GetString(reader, "phone");
            detailJobTitle = GetString(reader, "job_title");
            detailDepartment = GetString(reader, "department");
            detailTimezone = GetString(reader, "timezone");
            detailIsActive = GetBoolean(reader, "is_active") ?? true;
        }

        var roles = await LoadUserRolesAsync(connection, [userId], cancellationToken);
        return new UserDetailDto
        {
            Id = detailId,
            Name = detailName,
            Email = detailEmail,
            Phone = detailPhone,
            JobTitle = detailJobTitle,
            Department = detailDepartment,
            Timezone = detailTimezone,
            IsActive = detailIsActive,
            Roles = roles.TryGetValue(userId, out var userRoles) ? userRoles : []
        };
    }

    private async Task<bool> RoleBelongsToClientAsync(SqlConnection connection, long clientId, long roleId, CancellationToken cancellationToken)
    {
        const string sql = "SELECT COUNT(*) FROM roles WHERE id = @roleId AND client_id = @clientId AND name <> 'client';";
        return await ExecuteCountAsync(connection, sql, [new SqlParameter("@roleId", roleId), new SqlParameter("@clientId", clientId)], cancellationToken) > 0;
    }

    private async Task<bool> UserEmailExistsAsync(SqlConnection connection, string email, long? excludeUserId, CancellationToken cancellationToken)
    {
        var parameters = new List<SqlParameter> { new("@email", email.Trim().ToLowerInvariant()) };
        var whereClauses = new List<string> { "deleted_at IS NULL", "LOWER(LTRIM(RTRIM(email))) = @email" };
        if (excludeUserId.HasValue)
        {
            whereClauses.Add("id <> @excludeUserId");
            parameters.Add(new SqlParameter("@excludeUserId", excludeUserId.Value));
        }

        var sql = $"SELECT COUNT(*) FROM users WHERE {string.Join(" AND ", whereClauses)};";
        return await ExecuteCountAsync(connection, sql, parameters, cancellationToken) > 0;
    }

    private async Task<long?> GetClientMaxUsersAsync(SqlConnection connection, long clientId, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, "SELECT TOP 1 max_users FROM clients WHERE id = @clientId AND deleted_at IS NULL;");
        command.Parameters.AddWithValue("@clientId", clientId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null || value is DBNull)
        {
            return null;
        }

        return Convert.ToInt64(value);
    }

    private async Task<int> CountActiveClientUsersAsync(SqlConnection connection, long clientId, CancellationToken cancellationToken)
    {
        const string sql = "SELECT COUNT(*) FROM users WHERE client_id = @clientId AND deleted_at IS NULL AND ISNULL(is_client, 0) = 0 AND ISNULL(is_active, 1) = 1;";
        return await ExecuteCountAsync(connection, sql, [new SqlParameter("@clientId", clientId)], cancellationToken);
    }

    private async Task ReplaceUserRolesAsync(SqlConnection connection, SqlTransaction transaction, long userId, long roleId, CancellationToken cancellationToken)
    {
        await using (var deleteRoles = CreateCommand(connection, "DELETE FROM model_has_roles WHERE model_id = @userId AND REPLACE(model_type, '\\', '') = REPLACE(@modelType, '\\', '');"))
        {
            deleteRoles.Transaction = transaction;
            deleteRoles.Parameters.AddWithValue("@userId", userId);
            deleteRoles.Parameters.AddWithValue("@modelType", _settings.UserModelType);
            await deleteRoles.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var insertRole = CreateCommand(connection, "INSERT INTO model_has_roles (role_id, model_type, model_id) VALUES (@roleId, @modelType, @userId);");
        insertRole.Transaction = transaction;
        insertRole.Parameters.AddWithValue("@roleId", roleId);
        insertRole.Parameters.AddWithValue("@modelType", _settings.UserModelType);
        insertRole.Parameters.AddWithValue("@userId", userId);
        await insertRole.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<string>> GetUserBlockingReasonsAsync(SqlConnection connection, long clientId, long userId, CancellationToken cancellationToken)
    {
        var reasons = new List<string>();
        if (await HasUserBlockerAsync(connection, "SELECT COUNT(*) FROM components WHERE client_id = @clientId AND deleted_at IS NULL AND (created_by_id = @userId OR updated_by_id = @userId);", clientId, userId, cancellationToken))
        {
            reasons.Add("components");
        }

        if (await HasUserBlockerAsync(connection, "SELECT COUNT(*) FROM projects WHERE client_id = @clientId AND deleted_at IS NULL AND (created_by_id = @userId OR updated_by_id = @userId);", clientId, userId, cancellationToken))
        {
            reasons.Add("projects");
        }

        if (await HasUserBlockerAsync(connection, "SELECT COUNT(*) FROM test_designs WHERE client_id = @clientId AND deleted_at IS NULL AND (created_by_id = @userId OR updated_by_id = @userId);", clientId, userId, cancellationToken))
        {
            reasons.Add("tests");
        }

        if (await HasUserBlockerAsync(connection, "SELECT COUNT(*) FROM test_plan_users tpu INNER JOIN test_plans tp ON tp.id = tpu.test_plan_id WHERE tp.client_id = @clientId AND tpu.user_id = @userId;", clientId, userId, cancellationToken))
        {
            reasons.Add("test plans (assigned)");
        }

        if (await HasUserBlockerAsync(connection, "SELECT COUNT(*) FROM test_plan_item_suite_users tpisu INNER JOIN test_plan_item_suites tpis ON tpis.id = tpisu.test_plan_item_suite_id INNER JOIN test_plan_items tpi ON tpi.id = tpis.test_plan_item_id INNER JOIN test_plans tp ON tp.id = tpi.test_plan_id WHERE tp.client_id = @clientId AND tpisu.user_id = @userId;", clientId, userId, cancellationToken))
        {
            reasons.Add("test plan suites (assigned)");
        }

        if (await HasUserBlockerAsync(connection, "SELECT COUNT(*) FROM test_runner_items tri INNER JOIN test_runners tr ON tr.id = tri.test_runner_id WHERE tr.client_id = @clientId AND tri.run_by = @userId;", clientId, userId, cancellationToken))
        {
            reasons.Add("test runner items");
        }

        if (await HasUserBlockerAsync(connection, "SELECT COUNT(*) FROM defects WHERE client_id = @clientId AND deleted_at IS NULL AND (assigned_to = @userId OR created_by = @userId);", clientId, userId, cancellationToken))
        {
            reasons.Add("defects");
        }

        return reasons;
    }

    private async Task<bool> HasUserBlockerAsync(SqlConnection connection, string sql, long clientId, long userId, CancellationToken cancellationToken)
    {
        return await ExecuteCountAsync(connection, sql, [new SqlParameter("@clientId", clientId), new SqlParameter("@userId", userId)], cancellationToken) > 0;
    }

    private async Task SoftDeleteUserAsync(SqlConnection connection, long userId, CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var committed = false;
        try
        {
            foreach (var sql in new[]
                     {
                         "DELETE FROM model_has_roles WHERE model_id = @userId AND REPLACE(model_type, '\\', '') = REPLACE(@modelType, '\\', '');",
                         "DELETE FROM model_has_permissions WHERE model_id = @userId AND REPLACE(model_type, '\\', '') = REPLACE(@modelType, '\\', '');",
                         "UPDATE users SET deleted_at = SYSUTCDATETIME(), updated_at = SYSUTCDATETIME() WHERE id = @userId AND deleted_at IS NULL;"
                     })
            {
                await using var command = CreateCommand(connection, sql);
                command.Transaction = (SqlTransaction)transaction;
                command.Parameters.AddWithValue("@userId", userId);
                if (sql.Contains("@modelType", StringComparison.Ordinal))
                {
                    command.Parameters.AddWithValue("@modelType", _settings.UserModelType);
                }

                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            committed = true;
        }
        catch
        {
            if (!committed)
            {
                await transaction.RollbackAsync(cancellationToken);
            }

            throw;
        }
    }

    private async Task<bool> RoleBelongsToScopeAsync(SqlConnection connection, RequestContext context, long roleId, CancellationToken cancellationToken)
    {
        var whereClauses = new List<string> { "r.id = @roleId", "r.name <> 'client'" };
        var parameters = new List<SqlParameter> { new("@roleId", roleId) };
        ApplyRoleScope(context, whereClauses, parameters);
        var sql = $"SELECT COUNT(*) FROM roles r WHERE {string.Join(" AND ", whereClauses)};";
        return await ExecuteCountAsync(connection, sql, parameters, cancellationToken) > 0;
    }

    private async Task<RoleDetailDto?> GetRoleDetailByIdAsync(SqlConnection connection, RequestContext context, long roleId, CancellationToken cancellationToken)
    {
        var whereClauses = new List<string> { "r.id = @roleId", "r.name <> 'client'" };
        var parameters = new List<SqlParameter> { new("@roleId", roleId) };
        ApplyRoleScope(context, whereClauses, parameters);

        var sql = $"""
            SELECT TOP 1 r.id, r.name, r.guard_name, r.created_at, r.updated_at
            FROM roles r
            WHERE {string.Join(" AND ", whereClauses)};
            """;

        long detailId;
        string detailName;
        string detailGuardName;
        DateTimeOffset? detailCreatedAt;
        DateTimeOffset? detailUpdatedAt;
        await using (var command = CreateCommand(connection, sql))
        {
            AddParameters(command, parameters);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            detailId = reader.GetInt64(reader.GetOrdinal("id"));
            detailName = GetString(reader, "name") ?? string.Empty;
            detailGuardName = GetString(reader, "guard_name") ?? string.Empty;
            detailCreatedAt = GetDateTimeOffset(reader, "created_at");
            detailUpdatedAt = GetDateTimeOffset(reader, "updated_at");
        }

        return new RoleDetailDto
        {
            Id = detailId,
            Name = detailName,
            GuardName = detailGuardName,
            CreatedAt = detailCreatedAt,
            UpdatedAt = detailUpdatedAt,
            Permissions = await LoadRolePermissionsAsync(connection, roleId, cancellationToken)
        };
    }

    private async Task<bool> RoleNameExistsAsync(SqlConnection connection, RequestContext context, string roleName, long? excludeRoleId, CancellationToken cancellationToken)
    {
        var parameters = new List<SqlParameter> { new("@roleName", roleName.Trim().ToLowerInvariant()) };
        var whereClauses = new List<string> { "LOWER(LTRIM(RTRIM(r.name))) = @roleName" };
        ApplyRoleScope(context, whereClauses, parameters);
        if (excludeRoleId.HasValue)
        {
            whereClauses.Add("id <> @excludeRoleId");
            parameters.Add(new SqlParameter("@excludeRoleId", excludeRoleId.Value));
        }

        var sql = $"SELECT COUNT(*) FROM roles r WHERE {string.Join(" AND ", whereClauses)};";
        return await ExecuteCountAsync(connection, sql, parameters, cancellationToken) > 0;
    }

    private async Task<IReadOnlyList<long>?> ResolvePermissionIdsAsync(SqlConnection connection, IReadOnlyList<string> requestedPermissions, CancellationToken cancellationToken)
    {
        var normalizedPermissions = requestedPermissions
            .Select(value => NormalizeOptionalText(value))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalizedPermissions.Length == 0)
        {
            return null;
        }

        var parameters = new List<SqlParameter>();
        var placeholders = new List<string>(normalizedPermissions.Length);
        for (var index = 0; index < normalizedPermissions.Length; index++)
        {
            var parameterName = $"@permission{index}";
            placeholders.Add(parameterName);
            parameters.Add(new SqlParameter(parameterName, normalizedPermissions[index]!));
        }

        var sql = $"SELECT id, name FROM permissions WHERE name IN ({string.Join(", ", placeholders)});";
        await using var command = CreateCommand(connection, sql);
        AddParameters(command, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var permissionIds = new List<long>();
        var foundPermissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
        {
            permissionIds.Add(reader.GetInt64(reader.GetOrdinal("id")));
            var name = GetString(reader, "name");
            if (!string.IsNullOrWhiteSpace(name))
            {
                foundPermissions.Add(name);
            }
        }

        return foundPermissions.Count == normalizedPermissions.Length
            ? permissionIds
            : null;
    }

    private async Task ReplaceRolePermissionsAsync(SqlConnection connection, SqlTransaction transaction, long roleId, IReadOnlyList<long> permissionIds, CancellationToken cancellationToken)
    {
        await using (var deleteCommand = CreateCommand(connection, "DELETE FROM role_has_permissions WHERE role_id = @roleId;"))
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.Parameters.AddWithValue("@roleId", roleId);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var permissionId in permissionIds)
        {
            await using var insertCommand = CreateCommand(connection, "INSERT INTO role_has_permissions (permission_id, role_id) VALUES (@permissionId, @roleId);");
            insertCommand.Transaction = transaction;
            insertCommand.Parameters.AddWithValue("@permissionId", permissionId);
            insertCommand.Parameters.AddWithValue("@roleId", roleId);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task<bool> RoleHasAssignedUsersAsync(SqlConnection connection, long roleId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT(*)
            FROM model_has_roles
            WHERE role_id = @roleId
              AND REPLACE(model_type, '\\', '') = REPLACE(@modelType, '\\', '');
            """;

        return await ExecuteCountAsync(connection, sql, [new SqlParameter("@roleId", roleId), new SqlParameter("@modelType", _settings.UserModelType)], cancellationToken) > 0;
    }

    private async Task<long?> ResolveAuditUserIdAsync(SqlConnection connection, long clientId, long userId, CancellationToken cancellationToken)
    {
        const string sql = "SELECT COUNT(*) FROM users WHERE id = @userId AND client_id = @clientId AND deleted_at IS NULL;";
        var count = await ExecuteCountAsync(connection, sql, [new SqlParameter("@userId", userId), new SqlParameter("@clientId", clientId)], cancellationToken);
        return count > 0 ? userId : null;
    }

    private async Task<ProjectListItemDto?> GetProjectByIdAsync(SqlConnection connection, long clientId, long id, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP 1
                p.id,
                p.project_name,
                p.description,
                p.area_path,
                p.primary_test_management,
                p.primary_ticketing_system,
                p.type_id,
                p.version,
                CAST(ISNULL(p.status, 1) AS bit) AS status
            FROM projects p
            WHERE p.id = @id AND p.client_id = @clientId AND p.deleted_at IS NULL;
            """;

        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@clientId", clientId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ProjectListItemDto
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            ProjectName = GetString(reader, "project_name") ?? string.Empty,
            Description = GetString(reader, "description"),
            AreaPath = GetString(reader, "area_path"),
            PrimaryTestManagement = GetString(reader, "primary_test_management"),
            PrimaryTicketingSystem = GetString(reader, "primary_ticketing_system"),
            TypeId = GetInt64(reader, "type_id"),
            Version = GetString(reader, "version"),
            Status = GetBoolean(reader, "status") ?? true,
        };
    }

    private async Task<ProjectDetailDto?> GetProjectDetailByIdAsync(SqlConnection connection, long clientId, long id, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP 1
                p.id,
                p.project_name,
                p.description,
                p.area_path,
                p.primary_test_management,
                p.primary_ticketing_system,
                p.type_id,
                p.version,
                CAST(ISNULL(p.status, 1) AS bit) AS status
            FROM projects p
            WHERE p.id = @id AND p.client_id = @clientId AND p.deleted_at IS NULL;
            """;

        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@clientId", clientId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ProjectDetailDto
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            ProjectName = GetString(reader, "project_name") ?? string.Empty,
            Description = GetString(reader, "description"),
            AreaPath = GetString(reader, "area_path"),
            PrimaryTestManagement = GetString(reader, "primary_test_management"),
            PrimaryTicketingSystem = GetString(reader, "primary_ticketing_system"),
            TypeId = GetInt64(reader, "type_id"),
            Version = GetString(reader, "version"),
            Status = GetBoolean(reader, "status") ?? true,
        };
    }

    private async Task<long> InsertIntegrationJobAsync(SqlConnection connection, long clientId, long connectionId, string entityType, long internalId, long userId, JsonElement? payload, CancellationToken cancellationToken, string? idempotencyKey = null)
    {
        const string sql = """
            INSERT INTO integration_jobs
            (
                client_id,
                integration_connection_id,
                entity_type,
                internal_id,
                action,
                idempotency_key,
                status,
                attempts,
                max_attempts,
                scheduled_at,
                payload_json,
                created_by,
                created_at,
                updated_at
            )
            OUTPUT INSERTED.id
            VALUES
            (
                @clientId,
                @connectionId,
                @entityType,
                @internalId,
                'upsert',
                @idempotencyKey,
                'pending',
                0,
                5,
                SYSUTCDATETIME(),
                @payloadJson,
                @createdBy,
                SYSUTCDATETIME(),
                SYSUTCDATETIME()
            );
            """;

        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@clientId", clientId);
        command.Parameters.AddWithValue("@connectionId", connectionId);
        command.Parameters.AddWithValue("@entityType", entityType);
        command.Parameters.AddWithValue("@internalId", internalId);
        command.Parameters.AddWithValue("@idempotencyKey", idempotencyKey ?? $"{connectionId}:{entityType}:{internalId}:{Guid.NewGuid():N}");
        command.Parameters.AddWithValue("@payloadJson", (object?)ToNullableJsonText(payload) ?? DBNull.Value);
        command.Parameters.AddWithValue("@createdBy", userId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private async Task QueueAutoSyncTestCaseJobsAsync(
        SqlConnection connection,
        RequestContext context,
        IReadOnlyCollection<long> suiteIds,
        long? projectId,
        CancellationToken cancellationToken)
    {
        if (!context.ClientId.HasValue || suiteIds.Count == 0)
        {
            return;
        }

        var connectionIds = await GetAutoSyncTestCaseConnectionIdsAsync(connection, context.ClientId.Value, projectId, cancellationToken);
        if (connectionIds.Count == 0)
        {
            return;
        }

        foreach (var suiteId in suiteIds.Where(value => value > 0).Distinct())
        {
            var isReadyForSync = await IsTestSuiteReadyForSyncAsync(connection, context.ClientId.Value, suiteId, cancellationToken);

            var versionKey = await GetTestSuiteVersionKeyAsync(connection, context.ClientId.Value, suiteId, cancellationToken);
            if (versionKey is null)
            {
                continue;
            }

            foreach (var integrationConnectionId in connectionIds)
            {
                if (!isReadyForSync
                    && !await HasExistingSyncedTestCaseAsync(connection, context.ClientId.Value, integrationConnectionId, suiteId, cancellationToken))
                {
                    continue;
                }

                if (await IsAzureDevOpsIntegrationConnectionAsync(connection, context.ClientId.Value, integrationConnectionId, cancellationToken)
                    && !await HasRequiredAzureTestCaseRoutingAsync(connection, context.ClientId.Value, suiteId, cancellationToken))
                {
                    continue;
                }

                var idempotencyKey = BuildIntegrationIdempotencyKey(integrationConnectionId, "test_case", suiteId, "auto", versionKey);
                if (await IntegrationJobExistsByIdempotencyKeyAsync(connection, context.ClientId.Value, idempotencyKey, cancellationToken))
                {
                    continue;
                }

                await InsertIntegrationJobAsync(
                    connection,
                    context.ClientId.Value,
                    integrationConnectionId,
                    "test_case",
                    suiteId,
                    context.UserId,
                    payload: null,
                    cancellationToken,
                    idempotencyKey);
            }
        }
    }

    private async Task<bool> IsTestSuiteReadyForSyncAsync(SqlConnection connection, long clientId, long suiteId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1) ts.name
            FROM test_designs td
            LEFT JOIN test_states ts ON ts.id = td.test_state_id
            WHERE td.id = @suiteId
              AND td.client_id = @clientId
              AND td.deleted_at IS NULL;
            """;

        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@suiteId", suiteId);
        command.Parameters.AddWithValue("@clientId", clientId);

        var stateName = (await command.ExecuteScalarAsync(cancellationToken))?.ToString();
        return string.Equals(stateName?.Trim(), "Ready", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> HasExistingSyncedTestCaseAsync(SqlConnection connection, long clientId, long connectionId, long suiteId, CancellationToken cancellationToken)
    {
        if (await HasIntegrationLinksTableAsync(connection, cancellationToken))
        {
            const string linksSql = """
                SELECT CASE WHEN EXISTS (
                    SELECT 1
                    FROM integration_links
                    WHERE integration_connection_id = @connectionId
                      AND entity_type = 'test_case'
                      AND internal_id = @suiteId
                      AND external_id IS NOT NULL
                ) THEN 1 ELSE 0 END;
                """;

            await using var linksCommand = CreateCommand(connection, linksSql);
            linksCommand.Parameters.AddWithValue("@connectionId", connectionId);
            linksCommand.Parameters.AddWithValue("@suiteId", suiteId);
            if (Convert.ToInt32(await linksCommand.ExecuteScalarAsync(cancellationToken)) == 1)
            {
                return true;
            }
        }

        const string jobsSql = """
            SELECT CASE WHEN EXISTS (
                SELECT 1
                FROM integration_jobs
                WHERE client_id = @clientId
                  AND integration_connection_id = @connectionId
                  AND entity_type = 'test_case'
                  AND internal_id = @suiteId
                  AND status = 'sent'
                  AND external_id IS NOT NULL
            ) THEN 1 ELSE 0 END;
            """;

        await using var jobsCommand = CreateCommand(connection, jobsSql);
        jobsCommand.Parameters.AddWithValue("@clientId", clientId);
        jobsCommand.Parameters.AddWithValue("@connectionId", connectionId);
        jobsCommand.Parameters.AddWithValue("@suiteId", suiteId);
        return Convert.ToInt32(await jobsCommand.ExecuteScalarAsync(cancellationToken)) == 1;
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

        await using var command = CreateCommand(connection, sql);
        var exists = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
        _hasIntegrationLinksTable = exists;
        return exists;
    }

    private async Task<bool> IsAzureDevOpsIntegrationConnectionAsync(SqlConnection connection, long clientId, long connectionId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1) provider
            FROM integration_connections
            WHERE id = @connectionId AND client_id = @clientId;
            """;

        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@connectionId", connectionId);
        command.Parameters.AddWithValue("@clientId", clientId);
        var provider = (await command.ExecuteScalarAsync(cancellationToken))?.ToString();
        return string.Equals(provider?.Trim(), "azure_devops", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> HasRequiredAzureTestCaseRoutingAsync(SqlConnection connection, long clientId, long suiteId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP (1)
                p.primary_test_management,
                p.area_path,
                td.azure_iteration_path
            FROM test_designs td
            LEFT JOIN projects p ON p.id = td.project_id AND p.deleted_at IS NULL
            WHERE td.id = @suiteId
              AND td.client_id = @clientId
              AND td.deleted_at IS NULL;
            """;

        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@suiteId", suiteId);
        command.Parameters.AddWithValue("@clientId", clientId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return false;
        }

        var primaryTestManagement = GetString(reader, "primary_test_management")?.Trim();
        if (!string.Equals(primaryTestManagement, "Azure", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var areaPath = GetString(reader, "area_path")?.Trim();
        var iterationPath = GetString(reader, "azure_iteration_path")?.Trim();
        return !string.IsNullOrWhiteSpace(areaPath) && !string.IsNullOrWhiteSpace(iterationPath);
    }

    private async Task<IReadOnlyList<long>> GetAutoSyncTestCaseConnectionIdsAsync(SqlConnection connection, long clientId, long? projectId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id
            FROM integration_connections
            WHERE client_id = @clientId
              AND CAST(ISNULL(is_enabled, 0) AS bit) = 1
              AND CAST(ISNULL(sync_test_cases, 0) AS bit) = 1
              AND CAST(ISNULL(auto_sync_test_cases, 0) AS bit) = 1
              AND (
                    (@projectId IS NULL AND project_id IS NULL)
                    OR (@projectId IS NOT NULL AND (project_id IS NULL OR project_id = @projectId))
                  );
            """;

        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@clientId", clientId);
        command.Parameters.AddWithValue("@projectId", (object?)projectId ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var connectionIds = new List<long>();
        while (await reader.ReadAsync(cancellationToken))
        {
            connectionIds.Add(reader.GetInt64(reader.GetOrdinal("id")));
        }

        return connectionIds;
    }

    private async Task<string?> GetTestSuiteVersionKeyAsync(SqlConnection connection, long clientId, long suiteId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                td.updated_at AS suite_updated_at,
                COUNT(tpis.id) AS active_membership_count,
                MAX(ISNULL(tpis.updated_at, tpis.created_at)) AS latest_membership_at
            FROM test_designs td
            LEFT JOIN test_plan_item_suites tpis
                ON tpis.test_design_id = td.id
               AND tpis.deleted_at IS NULL
            WHERE td.id = @suiteId
              AND td.client_id = @clientId
              AND td.deleted_at IS NULL
            GROUP BY td.updated_at;
            """;

        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@suiteId", suiteId);
        command.Parameters.AddWithValue("@clientId", clientId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var suiteUpdatedAtRaw = reader["suite_updated_at"];
        if (suiteUpdatedAtRaw is null || suiteUpdatedAtRaw is DBNull)
        {
            return null;
        }

        var suiteUpdatedAt = suiteUpdatedAtRaw is DateTimeOffset suiteDto
            ? suiteDto
            : DateTimeOffset.Parse(suiteUpdatedAtRaw.ToString() ?? string.Empty);

        var membershipCount = reader["active_membership_count"] is DBNull
            ? 0
            : Convert.ToInt64(reader["active_membership_count"]);

        string latestMembershipToken = "none";
        var latestMembershipRaw = reader["latest_membership_at"];
        if (latestMembershipRaw is not DBNull && latestMembershipRaw is not null)
        {
            var latestMembershipAt = latestMembershipRaw is DateTimeOffset membershipDto
                ? membershipDto
                : DateTimeOffset.Parse(latestMembershipRaw.ToString() ?? string.Empty);
            latestMembershipToken = latestMembershipAt.ToUniversalTime().ToString("O");
        }

        return $"{suiteUpdatedAt.ToUniversalTime():O}|m:{membershipCount}|lm:{latestMembershipToken}";
    }

    private async Task<bool> IntegrationJobExistsByIdempotencyKeyAsync(SqlConnection connection, long clientId, string idempotencyKey, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT CASE WHEN EXISTS (
                SELECT 1
                FROM integration_jobs
                WHERE client_id = @clientId AND idempotency_key = @idempotencyKey
            ) THEN 1 ELSE 0 END;
            """;

        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@clientId", clientId);
        command.Parameters.AddWithValue("@idempotencyKey", idempotencyKey);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private static string BuildIntegrationIdempotencyKey(long connectionId, string entityType, long internalId, string source, string versionKey)
    {
        var raw = $"{connectionId}|{entityType}|{internalId}|{source}|{versionKey}";
        using var sha1 = SHA1.Create();
        var hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
    private static bool IsSyncEnabledForEntity(IntegrationConnectionDto connection, string? entityType)
    {
        return entityType?.Trim().ToLowerInvariant() switch
        {
            "test_case" => connection.SyncTestCases,
            "test_plan" => connection.SyncTestPlans,
            "test_run" => connection.SyncTestRuns,
            "defect" => connection.SyncDefects,
            _ => false
        };
    }

    private static bool ValidateIntegrationProviderConfig(string? provider, JsonElement? config, JsonElement? credentials, bool isCreate)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return false;
        }

        if (provider.Equals("azure_devops", StringComparison.OrdinalIgnoreCase))
        {
            var organization = GetJsonStringProperty(config, "organization");
            var project = GetJsonStringProperty(config, "project");
            var pat = GetJsonStringProperty(credentials, "pat");
            if (string.IsNullOrWhiteSpace(organization) || string.IsNullOrWhiteSpace(project))
            {
                return false;
            }

            if (isCreate && string.IsNullOrWhiteSpace(pat))
            {
                return false;
            }
        }

        if (provider.Equals("jira", StringComparison.OrdinalIgnoreCase))
        {
            var baseUrl = GetJsonStringProperty(config, "base_url");
            var projectKey = GetJsonStringProperty(config, "project_key");
            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(projectKey))
            {
                return false;
            }
        }

        return true;
    }

    private static string? GetJsonStringProperty(JsonElement? element, string propertyName)
    {
        if (!element.HasValue || element.Value.ValueKind != JsonValueKind.Object || !element.Value.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString();
    }

    private static string? ToNullableJsonText(JsonElement? element)
    {
        if (!element.HasValue || element.Value.ValueKind == JsonValueKind.Null || element.Value.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        return element.Value.GetRawText();
    }

    private static List<SqlParameter> AddIdListParameterValues(IReadOnlyList<long> values, string baseName)
    {
        var parameters = new List<SqlParameter>(values.Count);
        for (var index = 0; index < values.Count; index++)
        {
            parameters.Add(new SqlParameter($"{baseName}{index}", values[index]));
        }

        return parameters;
    }

    private async Task<int> GetDashboardTotalSuitesAsync(SqlConnection connection, long clientId, long? projectId, CancellationToken cancellationToken)
    {
        var filters = new List<string>
        {
            "tp.client_id = @clientId",
            "tpi.client_id = @clientId"
        };
        var parameters = new List<SqlParameter> { new("@clientId", clientId) };

        if (projectId.HasValue)
        {
            filters.Add("tp.project_id = @projectId");
            parameters.Add(new SqlParameter("@projectId", projectId.Value));
        }

        var sql = $"""
            SELECT COUNT(*)
            FROM test_plan_item_suites tpis
            INNER JOIN test_plan_items tpi ON tpi.id = tpis.test_plan_item_id
            INNER JOIN test_plans tp ON tp.id = tpi.test_plan_id
            WHERE {string.Join(" AND ", filters)};
            """;

        return await ExecuteCountAsync(connection, sql, parameters, cancellationToken);
    }

    private async Task<DashboardExecutionCountsDto> GetDashboardExecutionCountsAsync(SqlConnection connection, long clientId, long? projectId, DateTime from, DateTime to, CancellationToken cancellationToken)
    {
        var (sql, parameters) = BuildRunnerBaseQuery(
            clientId,
            projectId,
            from,
            to,
            """
            SELECT
                SUM(CASE WHEN tri.status_id = 3 THEN 1 ELSE 0 END) AS passed,
                SUM(CASE WHEN tri.status_id = 4 THEN 1 ELSE 0 END) AS failed,
                SUM(CASE WHEN tri.status_id = 5 THEN 1 ELSE 0 END) AS glitch,
                SUM(CASE WHEN tri.status_id = 6 THEN 1 ELSE 0 END) AS retest,
                SUM(CASE WHEN tri.status_id = 2 THEN 1 ELSE 0 END) AS in_progress,
                SUM(CASE WHEN tri.status_id = 1 THEN 1 ELSE 0 END) AS not_started
            {base}
            """);

        await using var command = CreateCommand(connection, sql);
        AddParameters(command, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new DashboardExecutionCountsDto();
        }

        return new DashboardExecutionCountsDto
        {
            Passed = GetInt32(reader, "passed") ?? 0,
            Failed = GetInt32(reader, "failed") ?? 0,
            Glitch = GetInt32(reader, "glitch") ?? 0,
            Retest = GetInt32(reader, "retest") ?? 0,
            InProgress = GetInt32(reader, "in_progress") ?? 0,
            NotStarted = GetInt32(reader, "not_started") ?? 0
        };
    }

    private async Task<int> GetDistinctSuiteCountAsync(SqlConnection connection, long clientId, long? projectId, DateTime from, DateTime to, IReadOnlyList<int> statusIds, CancellationToken cancellationToken)
    {
        var parameters = new List<SqlParameter>();
        var statusParameters = AddIntListParameters(parameters, "@statusId", statusIds);
        var (sql, baseParameters) = BuildRunnerBaseQuery(
            clientId,
            projectId,
            from,
            to,
            $"SELECT COUNT(DISTINCT tri.test_suite_id) {{base}} AND tri.status_id IN ({string.Join(", ", statusParameters)})",
            parameters);

        return await ExecuteCountAsync(connection, sql, baseParameters, cancellationToken);
    }

    private async Task<IReadOnlyList<long>> GetClosedDefectStatusIdsAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id
            FROM defect_statuses
            WHERE LOWER(name) IN ('closed', 'fixed', 'resolved', 'done', 'rejected');
            """;

        await using var command = CreateCommand(connection, sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ids = new List<long>();
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetInt64(reader.GetOrdinal("id")));
        }

        return ids;
    }

    private async Task<int> GetClosedDefectCountAsync(SqlConnection connection, long clientId, long? projectId, DateTime from, DateTime to, IReadOnlyList<long> statusIds, CancellationToken cancellationToken)
    {
        var extraParameters = new List<SqlParameter>();
        var statusParameters = AddIdListParameters(extraParameters, "@closedStatusId", statusIds);
        var (sql, parameters) = BuildDefectBaseQuery(
            clientId,
            projectId,
            from,
            to,
            $"SELECT COUNT(*) FROM {{base}} AND d.status_id IN ({string.Join(", ", statusParameters)})",
            extraParameters,
            useUpdatedAt: true);

        return await ExecuteCountAsync(connection, sql, parameters, cancellationToken);
    }

    private async Task<IReadOnlyList<DashboardDefectStatusDto>> GetDashboardDefectStatusesAsync(SqlConnection connection, long clientId, long? projectId, DateTime from, DateTime to, CancellationToken cancellationToken)
    {
        var (sql, parameters) = BuildDefectBaseQuery(
            clientId,
            projectId,
            from,
            to,
            """
            SELECT COALESCE(ds.name, 'Unknown') AS status, COUNT(*) AS count
            FROM {base}
            GROUP BY COALESCE(ds.name, 'Unknown')
            ORDER BY COUNT(*) DESC, COALESCE(ds.name, 'Unknown')
            """);

        await using var command = CreateCommand(connection, sql);
        AddParameters(command, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<DashboardDefectStatusDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new DashboardDefectStatusDto
            {
                Status = GetString(reader, "status") ?? "Unknown",
                Count = GetInt32(reader, "count") ?? 0
            });
        }

        return rows;
    }

    private async Task<DashboardAgingBucketsDto> GetDashboardAgingBucketsAsync(SqlConnection connection, long clientId, long? projectId, DateTime from, DateTime to, CancellationToken cancellationToken)
    {
        var today = DateTime.Today;
        var extraParameters = new List<SqlParameter> { new("@today", today) };
        var (sql, parameters) = BuildDefectBaseQuery(
            clientId,
            projectId,
            from,
            to,
            """
            SELECT
                SUM(CASE WHEN DATEDIFF(day, CAST(d.created_at AS date), @today) <= 2 THEN 1 ELSE 0 END) AS d0_2,
                SUM(CASE WHEN DATEDIFF(day, CAST(d.created_at AS date), @today) BETWEEN 3 AND 7 THEN 1 ELSE 0 END) AS d3_7,
                SUM(CASE WHEN DATEDIFF(day, CAST(d.created_at AS date), @today) BETWEEN 8 AND 14 THEN 1 ELSE 0 END) AS d8_14,
                SUM(CASE WHEN DATEDIFF(day, CAST(d.created_at AS date), @today) >= 15 THEN 1 ELSE 0 END) AS d15_plus
            FROM {base}
            """,
            extraParameters);

        await using var command = CreateCommand(connection, sql);
        AddParameters(command, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new DashboardAgingBucketsDto();
        }

        return new DashboardAgingBucketsDto
        {
            ZeroToTwo = GetInt32(reader, "d0_2") ?? 0,
            ThreeToSeven = GetInt32(reader, "d3_7") ?? 0,
            EightToFourteen = GetInt32(reader, "d8_14") ?? 0,
            FifteenPlus = GetInt32(reader, "d15_plus") ?? 0
        };
    }

    private async Task<IReadOnlyList<DashboardExecutionTrendRowDto>> GetDashboardExecutionTrendAsync(SqlConnection connection, long clientId, long? projectId, DateTime from, DateTime to, CancellationToken cancellationToken)
    {
        var (sql, parameters) = BuildRunnerBaseQuery(
            clientId,
            projectId,
            from,
            to,
            """
            SELECT
                CONVERT(varchar(10), CAST(tri.created_at AS date), 23) AS [date],
                SUM(CASE WHEN tri.status_id = 3 THEN 1 ELSE 0 END) AS passed,
                SUM(CASE WHEN tri.status_id IN (4, 5) THEN 1 ELSE 0 END) AS failed,
                SUM(CASE WHEN tri.status_id = 6 THEN 1 ELSE 0 END) AS retest,
                SUM(CASE WHEN tri.status_id = 2 THEN 1 ELSE 0 END) AS in_progress
            {base}
            GROUP BY CAST(tri.created_at AS date)
            ORDER BY CAST(tri.created_at AS date)
            """);

        await using var command = CreateCommand(connection, sql);
        AddParameters(command, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<DashboardExecutionTrendRowDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new DashboardExecutionTrendRowDto
            {
                Date = GetString(reader, "date") ?? string.Empty,
                Passed = GetInt32(reader, "passed") ?? 0,
                Failed = GetInt32(reader, "failed") ?? 0,
                Retest = GetInt32(reader, "retest") ?? 0,
                InProgress = GetInt32(reader, "in_progress") ?? 0
            });
        }

        return rows;
    }

    private async Task<IReadOnlyList<DashboardDefectTrendRowDto>> GetDashboardDefectTrendAsync(SqlConnection connection, long clientId, long? projectId, DateTime from, DateTime to, IReadOnlyList<long> closedStatusIds, CancellationToken cancellationToken)
    {
        var createdRows = await GetDashboardDefectDailyCountsAsync(connection, clientId, projectId, from, to, cancellationToken, useUpdatedAt: false, statusIds: null);
        var closedRows = closedStatusIds.Count == 0
            ? new Dictionary<string, int>(StringComparer.Ordinal)
            : await GetDashboardDefectDailyCountsAsync(connection, clientId, projectId, from, to, cancellationToken, useUpdatedAt: true, statusIds: closedStatusIds);

        var dates = createdRows.Keys
            .Concat(closedRows.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        return dates.Select(date => new DashboardDefectTrendRowDto
        {
            Date = date,
            Created = createdRows.TryGetValue(date, out var created) ? created : 0,
            Closed = closedRows.TryGetValue(date, out var closed) ? closed : 0
        }).ToList();
    }

    private async Task<Dictionary<string, int>> GetDashboardDefectDailyCountsAsync(SqlConnection connection, long clientId, long? projectId, DateTime from, DateTime to, CancellationToken cancellationToken, bool useUpdatedAt, IReadOnlyList<long>? statusIds)
    {
        var extraParameters = new List<SqlParameter>();
        var dateColumn = useUpdatedAt ? "updated_at" : "created_at";
        string query;
        if (statusIds is { Count: > 0 })
        {
            var statusParameters = AddIdListParameters(extraParameters, "@defectStatusId", statusIds);
            query = string.Join(Environment.NewLine,
                $"SELECT CONVERT(varchar(10), CAST(d.{dateColumn} AS date), 23) AS [date], COUNT(*) AS count",
                $"FROM {{base}} AND d.status_id IN ({string.Join(", ", statusParameters)})",
                $"GROUP BY CAST(d.{dateColumn} AS date)",
                $"ORDER BY CAST(d.{dateColumn} AS date)");
        }
        else
        {
            query = string.Join(Environment.NewLine,
                $"SELECT CONVERT(varchar(10), CAST(d.{dateColumn} AS date), 23) AS [date], COUNT(*) AS count",
                "FROM {base}",
                $"GROUP BY CAST(d.{dateColumn} AS date)",
                $"ORDER BY CAST(d.{dateColumn} AS date)");
        }

        var (sql, parameters) = BuildDefectBaseQuery(clientId, projectId, from, to, query, extraParameters, useUpdatedAt);
        await using var command = CreateCommand(connection, sql);
        AddParameters(command, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new Dictionary<string, int>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            var date = GetString(reader, "date");
            if (string.IsNullOrWhiteSpace(date))
            {
                continue;
            }

            rows[date] = GetInt32(reader, "count") ?? 0;
        }

        return rows;
    }

    private async Task<IReadOnlyList<DashboardTopFailingSuiteDto>> GetDashboardTopFailingSuitesAsync(SqlConnection connection, long clientId, long? projectId, DateTime from, DateTime to, CancellationToken cancellationToken)
    {
        var parameters = new List<SqlParameter>
        {
            new("@clientId", clientId),
            new("@from", from),
            new("@to", to)
        };
        var filters = new List<string>
        {
            "tri.created_at BETWEEN @from AND @to",
            "tr.client_id = @clientId",
            "tp.client_id = @clientId",
            "td.client_id = @clientId",
            "tri.status_id IN (4, 5)"
        };

        if (projectId.HasValue)
        {
            filters.Add("tp.project_id = @projectId");
            parameters.Add(new SqlParameter("@projectId", projectId.Value));
        }

        var sql = $"""
            SELECT TOP 8 tri.test_suite_id, td.title AS name, COUNT(*) AS failures
            FROM test_runner_items tri
            INNER JOIN test_designs td ON td.id = tri.test_suite_id
            INNER JOIN test_runners tr ON tr.id = tri.test_runner_id
            INNER JOIN test_plan_items tpi ON tpi.id = tr.test_plan_item_id
            INNER JOIN test_plans tp ON tp.id = tpi.test_plan_id
            WHERE {string.Join(" AND ", filters)}
            GROUP BY tri.test_suite_id, td.title
            ORDER BY COUNT(*) DESC, tri.test_suite_id DESC;
            """;

        await using var command = CreateCommand(connection, sql);
        AddParameters(command, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<DashboardTopFailingSuiteDto>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new DashboardTopFailingSuiteDto
            {
                TestSuiteId = reader.GetInt64(reader.GetOrdinal("test_suite_id")),
                Name = GetString(reader, "name") ?? string.Empty,
                Failures = GetInt32(reader, "failures") ?? 0
            });
        }

        return rows;
    }

    private async Task<DefectListItemDto?> GetDefectByIdAsync(SqlConnection connection, long clientId, long defectId, CancellationToken cancellationToken)
    {
        await EnsureDefectSchemaAsync(connection, cancellationToken);

        const string sql = """
            SELECT
                d.id,
                d.title,
                d.description,
                d.expected_result,
                d.actual_result,
                d.test_runner_item_id,
                d.created_at,
                assigned_user.id AS assigned_id,
                assigned_user.name AS assigned_name,
                assigned_user.email AS assigned_email,
                status_ref.id AS status_id,
                status_ref.name AS status_name,
                created_user.id AS created_by_id,
                created_user.name AS created_by_name,
                created_user.email AS created_by_email,
                tp.name AS test_plan_name,
                tpi.id AS test_plan_item_id,
                tpi.name AS test_plan_item_name,
                tri.execution_id,
                tri.test_suite_id AS base_test_suite_id,
                tri.test_suite_name,
                cfg.name AS configuration_name,
                tri.steps,
                tri.created_at AS runner_created_at,
                runner_user.id AS runner_user_id,
                runner_user.name AS runner_user_name,
                runner_user.email AS runner_user_email,
                td.comment AS test_suite_comment,
                COALESCE((
                    SELECT
                        da.id,
                        da.file_name,
                        da.file_path AS url,
                        da.content_type,
                        da.file_size,
                        da.created_at
                    FROM defect_attachments da
                    WHERE da.defect_id = d.id AND da.deleted_at IS NULL
                    ORDER BY da.id
                    FOR JSON PATH
                ), '[]') AS attachments_json
            FROM defects d
            LEFT JOIN users assigned_user ON assigned_user.id = d.assigned_to
            LEFT JOIN defect_statuses status_ref ON status_ref.id = d.status_id
            LEFT JOIN users created_user ON created_user.id = d.created_by
            LEFT JOIN test_runner_items tri ON tri.id = d.test_runner_item_id
            LEFT JOIN test_runners tr ON tr.id = tri.test_runner_id
            LEFT JOIN test_plan_items tpi ON tpi.id = tr.test_plan_item_id
            LEFT JOIN test_plans tp ON tp.id = tpi.test_plan_id
            LEFT JOIN users runner_user ON runner_user.id = tri.run_by
            LEFT JOIN test_designs td ON td.id = tri.test_suite_id
            LEFT JOIN configurations cfg ON cfg.id = td.configuration_id
            WHERE d.id = @defectId AND d.client_id = @clientId AND d.deleted_at IS NULL;
            """;

        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@defectId", defectId);
        command.Parameters.AddWithValue("@clientId", clientId);
        DefectListItemDto? row = null;
        long? executionId = null;
        long? baseTestSuiteId = null;

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            row = BuildDefectListItem(reader);
            executionId = GetInt64(reader, "execution_id");
            baseTestSuiteId = GetInt64(reader, "base_test_suite_id");
        }

        if (row is null)
        {
            return null;
        }

        if (!row.TestRunnerItemId.HasValue)
        {
            return row;
        }

        var configurationsByRunnerItemId = await LoadRunnerItemConfigurationsAsync(
            connection,
            clientId,
            [(row.TestRunnerItemId.Value, executionId, baseTestSuiteId)],
            cancellationToken);

        return configurationsByRunnerItemId.TryGetValue(row.TestRunnerItemId.Value, out var configuration)
            ? WithConfiguration(row, configuration)
            : row;
    }

    private DefectListItemDto BuildDefectListItem(SqlDataReader reader)
    {
        return new DefectListItemDto
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            Title = GetString(reader, "title"),
            Description = GetString(reader, "description"),
            Expected = GetString(reader, "expected_result"),
            Actual = GetString(reader, "actual_result"),
            TestRunnerItemId = GetInt64(reader, "test_runner_item_id"),
            CreatedAt = GetDateTimeOffset(reader, "created_at"),
            Assigned = BuildDefectUser(reader, "assigned_id", "assigned_name", "assigned_email"),
            Status = BuildDefectStatus(reader, "status_id", "status_name"),
            CreatedBy = BuildDefectUser(reader, "created_by_id", "created_by_name", "created_by_email"),
            TestPlanItem = new DefectRunnerItemDto
            {
                TestPlanName = GetString(reader, "test_plan_name"),
                TestPlanItemId = GetInt64(reader, "test_plan_item_id"),
                TestPlanItemName = GetString(reader, "test_plan_item_name"),
                TestSuiteName = GetString(reader, "test_suite_name"),
                ConfigurationName = GetString(reader, "configuration_name"),
                ConfigurationVariables = [],
                AddedSteps = ParseJsonElementOrDefault(GetString(reader, "steps"), Array.Empty<object>()),
                CreatedAt = GetDateTimeOffset(reader, "runner_created_at"),
                User = BuildDefectUser(reader, "runner_user_id", "runner_user_name", "runner_user_email"),
                TestSuite = new DefectTestSuiteDto
                {
                    Comment = GetString(reader, "test_suite_comment")
                }
            },
            Attachments = ParseDefectAttachments(GetString(reader, "attachments_json"))
        };
    }

    private IReadOnlyList<DefectAttachmentDto> ParseDefectAttachments(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<IReadOnlyList<DefectAttachmentDto>>(raw, AppJsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static (string Sql, List<SqlParameter> Parameters) BuildRunnerBaseQuery(long clientId, long? projectId, DateTime from, DateTime to, string querySuffix, List<SqlParameter>? extraParameters = null)
    {
        var parameters = new List<SqlParameter>
        {
            new("@clientId", clientId),
            new("@from", from),
            new("@to", to)
        };
        if (extraParameters is not null)
        {
            parameters.AddRange(extraParameters);
        }

        var filters = new List<string>
        {
            "tri.created_at BETWEEN @from AND @to",
            "tr.client_id = @clientId",
            "tp.client_id = @clientId"
        };

        if (projectId.HasValue)
        {
            filters.Add("tp.project_id = @projectId");
            parameters.Add(new SqlParameter("@projectId", projectId.Value));
        }

        var baseSql = $"""
            FROM test_runner_items tri
            INNER JOIN test_runners tr ON tr.id = tri.test_runner_id
            INNER JOIN test_plan_items tpi ON tpi.id = tr.test_plan_item_id
            INNER JOIN test_plans tp ON tp.id = tpi.test_plan_id
            WHERE {string.Join(" AND ", filters)}
            """;

        return (querySuffix.Replace("{base}", baseSql), parameters);
    }

    private static (string Sql, List<SqlParameter> Parameters) BuildDefectBaseQuery(long clientId, long? projectId, DateTime from, DateTime to, string queryTemplate, List<SqlParameter>? extraParameters = null, bool useUpdatedAt = false)
    {
        var parameters = new List<SqlParameter>
        {
            new("@clientId", clientId),
            new("@from", from),
            new("@to", to)
        };
        if (extraParameters is not null)
        {
            parameters.AddRange(extraParameters);
        }

        var defectDateColumn = useUpdatedAt ? "d.updated_at" : "d.created_at";
        var filters = new List<string>
        {
            $"{defectDateColumn} BETWEEN @from AND @to",
            "d.deleted_at IS NULL",
            "d.client_id = @clientId",
            "tr.client_id = @clientId",
            "tp.client_id = @clientId"
        };

        if (projectId.HasValue)
        {
            filters.Add("tp.project_id = @projectId");
            parameters.Add(new SqlParameter("@projectId", projectId.Value));
        }

        var baseSql = $"""
            defects d
            LEFT JOIN test_runner_items tri ON tri.id = d.test_runner_item_id
            LEFT JOIN test_runners tr ON tr.id = tri.test_runner_id
            LEFT JOIN test_plan_items tpi ON tpi.id = tr.test_plan_item_id
            LEFT JOIN test_plans tp ON tp.id = tpi.test_plan_id
            LEFT JOIN defect_statuses ds ON ds.id = d.status_id
            WHERE {string.Join(" AND ", filters)}
            """;

        return (queryTemplate.Replace("{base}", baseSql), parameters);
    }

    private async Task<int> GetOpenDefectCountAsync(SqlConnection connection, long clientId, long? projectId, DateTime asOf, IReadOnlyList<long> closedStatusIds, CancellationToken cancellationToken)
    {
        var parameters = new List<SqlParameter>
        {
            new("@clientId", clientId),
            new("@asOf", asOf)
        };

        var filters = new List<string>
        {
            "d.deleted_at IS NULL",
            "d.created_at <= @asOf",
            "d.client_id = @clientId",
            "tr.client_id = @clientId",
            "tp.client_id = @clientId"
        };

        if (projectId.HasValue)
        {
            filters.Add("tp.project_id = @projectId");
            parameters.Add(new SqlParameter("@projectId", projectId.Value));
        }

        if (closedStatusIds.Count > 0)
        {
            var closedStatusParameters = AddIdListParameters(parameters, "@closedStatusId", closedStatusIds);
            filters.Add($"d.status_id NOT IN ({string.Join(", ", closedStatusParameters)})");
        }

        var sql = $"""
            SELECT COUNT(*)
            FROM defects d
            LEFT JOIN test_runner_items tri ON tri.id = d.test_runner_item_id
            LEFT JOIN test_runners tr ON tr.id = tri.test_runner_id
            LEFT JOIN test_plan_items tpi ON tpi.id = tr.test_plan_item_id
            LEFT JOIN test_plans tp ON tp.id = tpi.test_plan_id
            WHERE {string.Join(" AND ", filters)};
            """;

        return await ExecuteCountAsync(connection, sql, parameters, cancellationToken);
    }

    private async Task<IReadOnlyList<ComponentStepDto>> LoadComponentStepsAsync(SqlConnection connection, long componentId, CancellationToken cancellationToken)
    {
        var hasKeywordCombinationIds = await HasColumnAsync(connection, "component_keywords", "keyword_combination_ids", transaction: null, cancellationToken);
        var sql = $"""
            SELECT
                cs.id,
                cs.description,
                cs.expected_output,
                cs.keyword_id,
                cs.global_keyword_id,
                cs.brpg_obj,
                cs.object_string,
                cs.xpath,
                cs.before_step,
                cs.after_step,
                cs.display_id,
                ck.name AS keyword_name,
                {(hasKeywordCombinationIds ? "ck.keyword_combination_ids" : "CAST(NULL AS nvarchar(max))")} AS keyword_combination_ids,
                gk.name AS global_keyword_name
            FROM component_steps cs
            LEFT JOIN component_keywords ck ON ck.id = cs.keyword_id
            LEFT JOIN global_keywords gk ON gk.id = cs.global_keyword_id
            WHERE cs.component_id = @componentId AND cs.deleted_at IS NULL
            ORDER BY ISNULL(display_id, 2147483647), id;
            """;

        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@componentId", componentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<(ComponentStepDto Step, string? KeywordCombinationIds)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add((
                new ComponentStepDto
                {
                    Id = reader.GetInt64(reader.GetOrdinal("id")),
                    Description = GetString(reader, "description"),
                    ExpectedOutput = GetString(reader, "expected_output"),
                    KeywordId = GetInt64(reader, "keyword_id"),
                    GlobalKeywordId = GetInt64(reader, "global_keyword_id"),
                    Keyword = GetInt64(reader, "keyword_id") is long keywordId ? new BasicRefDto { Id = keywordId, Name = GetString(reader, "keyword_name") } : null,
                    GlobalKeyword = GetInt64(reader, "global_keyword_id") is long globalKeywordId ? new BasicRefDto { Id = globalKeywordId, Name = GetString(reader, "global_keyword_name") } : null,
                    BrpgObj = GetString(reader, "brpg_obj"),
                    ObjectString = GetString(reader, "object_string"),
                    XPath = GetString(reader, "xpath"),
                    BeforeStep = ParseStringArray(GetString(reader, "before_step")),
                    AfterStep = ParseStringArray(GetString(reader, "after_step")),
                    DisplayId = GetInt32(reader, "display_id")
                },
                GetString(reader, "keyword_combination_ids")));
        }

        var combinationNameMap = await LoadKeywordCombinationNamesAsync(connection, rows.Select(row => row.KeywordCombinationIds).ToArray(), cancellationToken);
        return rows.Select(row => new ComponentStepDto
        {
            Id = row.Step.Id,
            Description = row.Step.Description,
            ExpectedOutput = row.Step.ExpectedOutput,
            KeywordId = row.Step.KeywordId,
            GlobalKeywordId = row.Step.GlobalKeywordId,
            Keyword = row.Step.Keyword,
            KeywordCombinationNames = combinationNameMap.TryGetValue(row.KeywordCombinationIds ?? string.Empty, out var names) ? names : null,
            GlobalKeyword = row.Step.GlobalKeyword,
            BrpgObj = row.Step.BrpgObj,
            ObjectString = row.Step.ObjectString,
            XPath = row.Step.XPath,
            BeforeStep = row.Step.BeforeStep,
            AfterStep = row.Step.AfterStep,
            DisplayId = row.Step.DisplayId
        }).ToList();
    }

    private async Task<IReadOnlyList<TestSuiteFullComponentEntryDto>> LoadFullTestSuiteComponentsAsync(SqlConnection connection, long clientId, long testSuiteId, CancellationToken cancellationToken)
    {
        var hasSortOrder = await HasTestComponentSortOrderColumnAsync(connection, transaction: null, cancellationToken);
        var sql = $"""
            SELECT id, project_id, test_design_id, component_id, CAST(ISNULL(status, 1) AS bit) AS status
            FROM test_components
            WHERE test_design_id = @testSuiteId AND deleted_at IS NULL AND ISNULL(status, 1) = 1
            ORDER BY {(hasSortOrder ? "ISNULL(sort_order, 2147483647), id" : "id")};
            """;

        var componentRows = new List<(long Id, long? ProjectId, long? TestDesignId, long? ComponentId, bool Status)>();
        await using (var command = CreateCommand(connection, sql))
        {
            command.Parameters.AddWithValue("@testSuiteId", testSuiteId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                componentRows.Add((
                    reader.GetInt64(reader.GetOrdinal("id")),
                    GetInt64(reader, "project_id"),
                    GetInt64(reader, "test_design_id"),
                    GetInt64(reader, "component_id"),
                    GetBoolean(reader, "status") ?? true));
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

        var componentMap = await LoadComponentDetailsMapAsync(connection, clientId, componentIds, cancellationToken);
        var datasetMap = await LoadTestSuiteDatasetMapAsync(connection, componentRows.Select(row => row.Id).ToArray(), cancellationToken);

        return componentRows.Select(row => new TestSuiteFullComponentEntryDto
        {
            Id = row.Id,
            ProjectId = row.ProjectId,
            TestDesignId = row.TestDesignId,
            ComponentId = row.ComponentId,
            Status = row.Status,
            Component = row.ComponentId.HasValue && componentMap.TryGetValue(row.ComponentId.Value, out var component) ? component : null,
            Datasets = datasetMap.TryGetValue(row.Id, out var datasets) ? datasets : []
        }).ToList();
    }

    private async Task<Dictionary<long, ComponentDetailDto>> LoadComponentDetailsMapAsync(SqlConnection connection, long clientId, IReadOnlyList<long> componentIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, ComponentDetailDto>();
        if (componentIds.Count == 0)
        {
            return result;
        }

        var parameters = new List<SqlParameter>();
        var placeholders = AddIdListParameters(parameters, "@componentId", componentIds);
        const string sqlTemplate = """
            SELECT c.id, c.name, c.project_id, c.page, c.feature, c.type_id
            FROM components c
            WHERE c.client_id = @clientId AND c.deleted_at IS NULL AND c.id IN ({ids});
            """;

        await using (var command = CreateCommand(connection, sqlTemplate.Replace("{ids}", string.Join(", ", placeholders))))
        {
            command.Parameters.AddWithValue("@clientId", clientId);
            AddParameters(command, parameters);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetInt64(reader.GetOrdinal("id"));
                result[id] = new ComponentDetailDto
                {
                    Id = id,
                    Name = GetString(reader, "name"),
                    ProjectId = GetInt64(reader, "project_id"),
                    Page = GetString(reader, "page"),
                    Feature = GetString(reader, "feature"),
                    TypeId = GetInt64(reader, "type_id")
                };
            }
        }

        foreach (var componentId in componentIds)
        {
            if (!result.TryGetValue(componentId, out var component))
            {
                continue;
            }

            result[componentId] = new ComponentDetailDto
            {
                Id = component.Id,
                Name = component.Name,
                ProjectId = component.ProjectId,
                Page = component.Page,
                Feature = component.Feature,
                TypeId = component.TypeId,
                Steps = await LoadComponentStepsAsync(connection, componentId, cancellationToken)
            };
        }

        return result;
    }

    private async Task<Dictionary<long, IReadOnlyList<TestSuiteFullDatasetDto>>> LoadTestSuiteDatasetMapAsync(SqlConnection connection, IReadOnlyList<long> testComponentIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, IReadOnlyList<TestSuiteFullDatasetDto>>();
        if (testComponentIds.Count == 0)
        {
            return result;
        }

        var parameters = new List<SqlParameter>();
        var placeholders = AddIdListParameters(parameters, "@testComponentId", testComponentIds);
        var hasSortOrder = await HasDataSetSortOrderColumnAsync(connection, transaction: null, cancellationToken);
        var sql = $"""
            SELECT id, test_component_id, scenario, CAST(ISNULL(status, 0) AS bit) AS status
            FROM data_sets
            WHERE deleted_at IS NULL AND test_component_id IN ({string.Join(", ", placeholders)})
            ORDER BY test_component_id, {(hasSortOrder ? "ISNULL(sort_order, 2147483647), id" : "id")};
            """;

        var datasetRows = new List<(long Id, long TestComponentId, string? Scenario, bool Status)>();
        await using (var command = CreateCommand(connection, sql))
        {
            AddParameters(command, parameters);
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
                    GetString(reader, "scenario"),
                    GetBoolean(reader, "status") ?? false));
            }
        }

        var datasetIds = datasetRows.Select(row => row.Id).ToArray();
        var stepMap = await LoadDatasetStepsMapAsync(connection, datasetIds, cancellationToken);

        foreach (var group in datasetRows.GroupBy(row => row.TestComponentId))
        {
            result[group.Key] = group.Select(row => new TestSuiteFullDatasetDto
            {
                Id = row.Id,
                Scenario = row.Scenario,
                Status = row.Status,
                Steps = stepMap.TryGetValue(row.Id, out var steps) ? steps : []
            }).ToList();
        }

        return result;
    }

    private async Task<Dictionary<long, IReadOnlyList<TestSuiteFullDatasetStepDto>>> LoadDatasetStepsMapAsync(SqlConnection connection, IReadOnlyList<long> datasetIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, IReadOnlyList<TestSuiteFullDatasetStepDto>>();
        if (datasetIds.Count == 0)
        {
            return result;
        }

        var parameters = new List<SqlParameter>();
        var placeholders = AddIdListParameters(parameters, "@datasetId", datasetIds);
        var sql = $"""
            SELECT dataset_id, display, skip_step, step_info, step_id, override, override_value
            FROM data_set_steps
            WHERE dataset_id IN ({string.Join(", ", placeholders)})
            ORDER BY ISNULL(display, 2147483647), step_id;
            """;

        var grouped = new Dictionary<long, List<TestSuiteFullDatasetStepDto>>();
        await using var command = CreateCommand(connection, sql);
        AddParameters(command, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var datasetId = GetInt64(reader, "dataset_id");
            if (!datasetId.HasValue)
            {
                continue;
            }

            var stepInfo = ParseJsonElement(GetString(reader, "step_info"));
            if (!grouped.TryGetValue(datasetId.Value, out var steps))
            {
                steps = [];
                grouped[datasetId.Value] = steps;
            }

            var stepId = GetInt64(reader, "step_id");
            steps.Add(new TestSuiteFullDatasetStepDto
            {
                Id = stepId ?? 0,
                DatasetId = datasetId.Value,
                DisplayId = GetInt32(reader, "display"),
                SkipStep = GetBoolean(reader, "skip_step") ?? false,
                StepId = stepId,
                InternalStepId = stepId,
                Value = GetJsonStringProperty(stepInfo, "value"),
                Override = GetBoolean(reader, "override"),
                OverrideValue = GetString(reader, "override_value"),
                StepInfo = stepInfo
            });
        }

        foreach (var pair in grouped)
        {
            result[pair.Key] = pair.Value;
        }

        return result;
    }

    private async Task<List<TestSuiteFullDto>> LoadRunnerSuitesAsync(SqlConnection connection, long clientId, IReadOnlyList<long> suiteIds, CancellationToken cancellationToken)
    {
        var result = new List<TestSuiteFullDto>();
        if (suiteIds.Count == 0)
        {
            return result;
        }

        var contexts = await LoadExecutionSuiteContextsAsync(connection, clientId, suiteIds, cancellationToken);
        if (contexts.Count == 0)
        {
            return result;
        }

        var baseSuiteIds = contexts.Select(row => row.BaseTestDesignId).Distinct().ToArray();
        var parameters = AddIdListParameterValues(baseSuiteIds, "@suiteId");
        var sql = $"""
            SELECT
                td.id,
                td.title,
                td.test_state_id,
                td.test_suite_type,
                td.folder_path_id,
                td.comment,
                td.project_id,
                td.priority,
                td.story_id,
                td.test_title,
                td.tags,
                td.parent_id,
                td.configuration_id,
                CAST(ISNULL(td.kba_ready, 0) AS bit) AS kba_ready,
                CAST(ISNULL(td.training_ready, 0) AS bit) AS training_ready,
                CAST(ISNULL(td.release_notes_ready, 0) AS bit) AS release_notes_ready
            FROM test_designs td
            WHERE td.client_id = @clientId
              AND td.deleted_at IS NULL
              AND td.id IN ({string.Join(", ", parameters.Select(parameter => parameter.ParameterName))})
            ORDER BY td.id;
            """;

        var baseSuites = new Dictionary<long, TestSuiteFullDto>();
        await using (var command = CreateCommand(connection, sql))
        {
            command.Parameters.AddWithValue("@clientId", clientId);
            AddParameters(command, parameters);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var suite = new TestSuiteFullDto
                {
                    Id = reader.GetInt64(reader.GetOrdinal("id")),
                    Title = GetString(reader, "title"),
                    TestStateId = GetInt64(reader, "test_state_id"),
                    TestSuiteType = GetInt32(reader, "test_suite_type"),
                    FolderPathId = GetInt64(reader, "folder_path_id"),
                    Comment = GetString(reader, "comment"),
                    ProjectId = GetInt64(reader, "project_id"),
                    Priority = GetString(reader, "priority"),
                    StoryId = GetString(reader, "story_id"),
                    TestTitle = GetString(reader, "test_title"),
                    Tags = GetString(reader, "tags"),
                    ParentId = GetInt64(reader, "parent_id"),
                    ConfigurationId = GetInt64(reader, "configuration_id"),
                    KbaReady = GetBoolean(reader, "kba_ready") ?? false,
                    TrainingReady = GetBoolean(reader, "training_ready") ?? false,
                    ReleaseNotesReady = GetBoolean(reader, "release_notes_ready") ?? false
                };
                baseSuites[suite.Id] = suite;
            }
        }

        foreach (var context in contexts)
        {
            if (!baseSuites.TryGetValue(context.BaseTestDesignId, out var suite))
            {
                continue;
            }

            result.Add(new TestSuiteFullDto
            {
                Id = suite.Id,
                RuntimeSuiteId = context.ExecutionId,
                TestPlanItemSuiteId = context.TestPlanItemSuiteId,
                ConfigurationAssignmentId = context.ConfigurationAssignmentId,
                SelectedDatasetPlanRowId = context.DatasetId.HasValue ? ToDatasetId(context.ExecutionId) : null,
                SelectedDatasetId = context.DatasetId,
                Title = suite.Title,
                TestStateId = suite.TestStateId,
                TestSuiteType = suite.TestSuiteType,
                FolderPathId = suite.FolderPathId,
                Comment = suite.Comment,
                ProjectId = suite.ProjectId,
                Priority = suite.Priority,
                StoryId = suite.StoryId,
                TestTitle = suite.TestTitle,
                Tags = suite.Tags,
                ParentId = suite.ParentId,
                ConfigurationId = context.ConfigurationId ?? suite.ConfigurationId,
                KbaReady = suite.KbaReady,
                TrainingReady = suite.TrainingReady,
                ReleaseNotesReady = suite.ReleaseNotesReady,
                Components = await LoadFullTestSuiteComponentsAsync(connection, clientId, suite.Id, cancellationToken),
                Datasets = await LoadTestDesignDatasetsAsync(connection, suite.Id, cancellationToken)
            });
        }

        return result;
    }

    private async Task<Dictionary<long, int>> LoadPlanSuiteOrderAsync(SqlConnection connection, long clientId, long testPlanItemId, IReadOnlyList<long> suiteIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, int>();
        if (suiteIds.Count == 0)
        {
            return result;
        }

        var parameters = AddIdListParameterValues(suiteIds, "@suiteId");
        var sql = $"""
            SELECT tpis.test_design_id, ISNULL(tpis.sort_order, 2147483647) AS sort_order
            FROM test_plan_item_suites tpis
            INNER JOIN test_plan_items tpi ON tpi.id = tpis.test_plan_item_id
            INNER JOIN test_plans tp ON tp.id = tpi.test_plan_id
            WHERE tpis.test_plan_item_id = @testPlanItemId
              AND tp.client_id = @clientId
              AND tpis.deleted_at IS NULL
              AND tpis.test_design_id IN ({string.Join(", ", parameters.Select(parameter => parameter.ParameterName))});
            """;

        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@testPlanItemId", testPlanItemId);
        command.Parameters.AddWithValue("@clientId", clientId);
        AddParameters(command, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var suiteId = GetInt64(reader, "test_design_id");
            var sortOrder = GetInt32(reader, "sort_order");
            if (suiteId.HasValue)
            {
                result[suiteId.Value] = sortOrder ?? int.MaxValue;
            }
        }

        return result;
    }

    private async Task<Dictionary<string, string>> LoadKeywordCombinationNamesAsync(SqlConnection connection, IReadOnlyList<string?> rawCombinationIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var combinations = rawCombinationIds
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (combinations.Length == 0)
        {
            return result;
        }

        var ids = combinations
            .SelectMany(ParseLongJsonArray)
            .Distinct()
            .ToArray();
        if (ids.Length == 0)
        {
            return result;
        }

        var parameters = AddIdListParameterValues(ids, "@keywordId");
        var sql = $"SELECT id, name FROM component_keywords WHERE id IN ({string.Join(", ", parameters.Select(parameter => parameter.ParameterName))});";
        var nameMap = new Dictionary<long, string>();
        await using (var command = CreateCommand(connection, sql))
        {
            AddParameters(command, parameters);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                nameMap[reader.GetInt64(reader.GetOrdinal("id"))] = GetString(reader, "name") ?? string.Empty;
            }
        }

        foreach (var combination in combinations)
        {
            var names = ParseLongJsonArray(combination)
                .Where(id => nameMap.TryGetValue(id, out _))
                .Select(id => nameMap[id])
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToArray();
            result[combination] = names.Length > 0 ? string.Join(',', names) : null!;
        }

        return result;
    }

    private async Task<RunnerVariableMaps> LoadRunnerVariableMapAsync(SqlConnection connection, long clientId, IReadOnlyList<long> suiteIds, CancellationToken cancellationToken)
    {
        var global = new Dictionary<string, RunnerVariableValue>(StringComparer.OrdinalIgnoreCase);
        var local = new Dictionary<long, Dictionary<string, RunnerVariableValue>>();
        var parameters = new List<SqlParameter> { new("@clientId", clientId) };
        var localFilter = string.Empty;
        if (suiteIds.Count > 0)
        {
            var suiteParameters = AddIdListParameterValues(suiteIds, "@suiteId");
            parameters.AddRange(suiteParameters);
            localFilter = $" OR cv.test_case_id IN ({string.Join(", ", suiteParameters.Select(parameter => parameter.ParameterName))})";
        }

        var sql = $"""
            SELECT
                cv.name,
                cv.value,
                cv.test_case_id,
                CAST(ISNULL(cv.is_encrypted, 0) AS bit) AS is_encrypted,
                vt.executable_method
            FROM custom_variables cv
            LEFT JOIN variable_types vt ON vt.id = cv.variable_id
            WHERE cv.client_id = @clientId
              AND (cv.test_case_id IS NULL{localFilter})
            ORDER BY cv.id;
            """;

        await using var command = CreateCommand(connection, sql);
        AddParameters(command, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = GetString(reader, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var variable = new RunnerVariableValue(
                GetString(reader, "value"),
                GetString(reader, "executable_method"),
                GetBoolean(reader, "is_encrypted") ?? false);
            var testCaseId = GetInt64(reader, "test_case_id");
            if (testCaseId.HasValue)
            {
                if (!local.TryGetValue(testCaseId.Value, out var localVariables))
                {
                    localVariables = new Dictionary<string, RunnerVariableValue>(StringComparer.OrdinalIgnoreCase);
                    local[testCaseId.Value] = localVariables;
                }

                localVariables[name] = variable;
            }
            else
            {
                global[name] = variable;
            }
        }

        return new RunnerVariableMaps(global, local);
    }

    private async Task<TestRunnerSuiteDto> BuildRunnerSuiteDtoAsync(SqlConnection connection, TestSuiteFullDto suite, SuiteConfigurationDto? configuration, RunnerVariableMaps variableMaps, Dictionary<string, string> globalCache, Dictionary<string, string> localCache, CancellationToken cancellationToken)
    {
        var steps = new List<TestRunnerStepDto>();
        if (suite.SelectedDatasetId.HasValue)
        {
            var dataset = suite.Datasets.FirstOrDefault(row => row.Id == suite.SelectedDatasetId.Value && row.Status);
            if (dataset is not null)
            {
                var orderedComponentSteps = suite.Components
                    .SelectMany(component => component.Component?.Steps ?? [])
                    .Select((step, index) => new { WholeTestDisplayId = index + 1, Step = step })
                    .ToList();
                var componentStepsByWholeDisplayId = orderedComponentSteps
                    .ToDictionary(row => row.WholeTestDisplayId, row => row.Step);
                var componentStepsByStepId = orderedComponentSteps
                    .GroupBy(row => row.Step.Id)
                    .ToDictionary(group => group.Key, group => group.First().Step);

                foreach (var datasetStep in dataset.Steps.Where(step => !step.SkipStep && step.StepId.HasValue))
                {
                    ComponentStepDto? componentStep = null;
                    if (datasetStep.DisplayId.HasValue)
                    {
                        componentStepsByWholeDisplayId.TryGetValue(datasetStep.DisplayId.Value, out componentStep);
                    }

                    if (componentStep is null && datasetStep.StepId.HasValue)
                    {
                        componentStepsByStepId.TryGetValue(datasetStep.StepId.Value, out componentStep);
                    }

                    if (componentStep is null)
                    {
                        continue;
                    }

                    var resolvedValue = ResolveRunnerValue(datasetStep.Value, suite.Id, variableMaps, globalCache, localCache);
                    var overrideParts = string.IsNullOrWhiteSpace(datasetStep.OverrideValue)
                        ? null
                        : await ParseOverridePartsAsync(connection, datasetStep.OverrideValue!, cancellationToken);

                    var keyword = overrideParts?.Keyword ?? BuildRunnerKeyword(componentStep);
                    var keywordName = keyword?.Name ?? string.Empty;
                    if (configuration is not null)
                    {
                        var launchValue = ResolveConfigurationLaunchValue(configuration, keywordName);
                        if (!string.IsNullOrWhiteSpace(launchValue))
                        {
                            resolvedValue = launchValue;
                        }
                    }

                    var beforeSteps = overrideParts?.BeforeStep ?? ParseBeforeAfterSteps(componentStep.BeforeStep);
                    var afterSteps = overrideParts?.AfterStep ?? ParseBeforeAfterSteps(componentStep.AfterStep);
                    var xpathTemplate = overrideParts?.XPath ?? componentStep.XPath;
                    steps.Add(new TestRunnerStepDto
                    {
                        DatasetId = datasetStep.DatasetId,
                        Id = componentStep.Id,
                        Description = overrideParts?.Description ?? componentStep.Description,
                        ExpectedOutput = overrideParts?.ExpectedOutput ?? componentStep.ExpectedOutput,
                        Value = resolvedValue,
                        XPath = ApplyXPathValue(xpathTemplate, resolvedValue),
                        KeywordId = keyword?.Id,
                        Keyword = keyword,
                        BeforeStep = beforeSteps.Count > 0 ? beforeSteps : null,
                        AfterStep = afterSteps.Count > 0 ? afterSteps : null
                    });
                }
            }
        }

        if (steps.Count == 0)
        {
        foreach (var component in suite.Components)
        {
            var componentSteps = component.Component?.Steps?.ToDictionary(step => step.Id) ?? new Dictionary<long, ComponentStepDto>();
            foreach (var dataset in component.Datasets.Where(row => row.Status))
            {
                foreach (var datasetStep in dataset.Steps.Where(step => !step.SkipStep && step.StepId.HasValue))
                {
                    if (!componentSteps.TryGetValue(datasetStep.StepId!.Value, out var componentStep))
                    {
                        continue;
                    }

                    var resolvedValue = ResolveRunnerValue(datasetStep.Value, suite.Id, variableMaps, globalCache, localCache);
                    var overrideParts = string.IsNullOrWhiteSpace(datasetStep.OverrideValue)
                        ? null
                        : await ParseOverridePartsAsync(connection, datasetStep.OverrideValue!, cancellationToken);

                    var keyword = overrideParts?.Keyword ?? BuildRunnerKeyword(componentStep);
                    var keywordName = keyword?.Name ?? string.Empty;
                    if (configuration is not null)
                    {
                        var launchValue = ResolveConfigurationLaunchValue(configuration, keywordName);
                        if (!string.IsNullOrWhiteSpace(launchValue))
                        {
                            resolvedValue = launchValue;
                        }
                    }

                    var beforeSteps = overrideParts?.BeforeStep ?? ParseBeforeAfterSteps(componentStep.BeforeStep);
                    var afterSteps = overrideParts?.AfterStep ?? ParseBeforeAfterSteps(componentStep.AfterStep);
                    var xpathTemplate = overrideParts?.XPath ?? componentStep.XPath;
                    steps.Add(new TestRunnerStepDto
                    {
                        DatasetId = datasetStep.DatasetId,
                        Id = componentStep.Id,
                        Description = overrideParts?.Description ?? componentStep.Description,
                        ExpectedOutput = overrideParts?.ExpectedOutput ?? componentStep.ExpectedOutput,
                        Value = resolvedValue,
                        XPath = ApplyXPathValue(xpathTemplate, resolvedValue),
                        KeywordId = keyword?.Id,
                        Keyword = keyword,
                        BeforeStep = beforeSteps.Count > 0 ? beforeSteps : null,
                        AfterStep = afterSteps.Count > 0 ? afterSteps : null
                    });
                }
            }
        }
        }

        return new TestRunnerSuiteDto
        {
            TestSuite = new RunnerSuiteHeaderDto
            {
                Id = suite.RuntimeSuiteId == 0 ? suite.Id : suite.RuntimeSuiteId,
                BaseTestSuiteId = suite.Id,
                Name = suite.Title,
                Videos = JsonSerializer.SerializeToElement(Array.Empty<string>()),
                Prereq = suite.Comment,
                Configuration = configuration
            },
            Steps = steps
        };
    }

    private async Task<RunnerHeaderDto> PersistRunnerPayloadAsync(SqlConnection connection, RequestContext context, long testPlanItemId, IReadOnlyList<TestRunnerSuiteDto> suiteSteps, CancellationToken cancellationToken)
    {
        const string itemSql = """
            SELECT TOP 1 tpi.name
            FROM test_plan_items tpi
            INNER JOIN test_plans tp ON tp.id = tpi.test_plan_id
            WHERE tpi.id = @testPlanItemId AND tp.client_id = @clientId;
            """;

        string? itemName;
        await using (var itemCommand = CreateCommand(connection, itemSql))
        {
            itemCommand.Parameters.AddWithValue("@testPlanItemId", testPlanItemId);
            itemCommand.Parameters.AddWithValue("@clientId", context.ClientId!.Value);
            itemName = await itemCommand.ExecuteScalarAsync(cancellationToken) as string;
        }

        if (itemName is null)
        {
            return new RunnerHeaderDto { TestPlanItemId = testPlanItemId };
        }

        var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            const string insertRunnerSql = """
                INSERT INTO test_runners (test_plan_item_id, test_plan_item_name, client_id, created_at, updated_at)
                OUTPUT INSERTED.id
                VALUES (@testPlanItemId, @testPlanItemName, @clientId, SYSUTCDATETIME(), SYSUTCDATETIME());
                """;

            long runnerId;
            await using (var insertRunnerCommand = CreateCommand(connection, insertRunnerSql))
            {
                insertRunnerCommand.Transaction = transaction;
                insertRunnerCommand.Parameters.AddWithValue("@testPlanItemId", testPlanItemId);
                insertRunnerCommand.Parameters.AddWithValue("@testPlanItemName", itemName);
                insertRunnerCommand.Parameters.AddWithValue("@clientId", context.ClientId!.Value);
                runnerId = Convert.ToInt64(await insertRunnerCommand.ExecuteScalarAsync(cancellationToken));
            }

            if (suiteSteps.Count > 0)
            {
                var suiteIds = suiteSteps.Select(step => step.TestSuite.Id).ToArray();
                foreach (var suiteId in suiteIds)
                {
                    await UpdateExecutionStatusAsync(connection, transaction, testPlanItemId, suiteId, InProgressStatusId, cancellationToken);
                }

                const string insertItemSql = """
                    INSERT INTO test_runner_items (test_runner_id, test_suite_id, execution_id, test_suite_name, steps, videos, run_by, status_id, created_at, updated_at)
                    VALUES (@runnerId, @testSuiteId, @executionId, @testSuiteName, @steps, @videos, @runBy, @statusId, SYSUTCDATETIME(), SYSUTCDATETIME());
                    """;
                foreach (var suiteStep in suiteSteps)
                {
                    await using var itemCommand = CreateCommand(connection, insertItemSql);
                    itemCommand.Transaction = transaction;
                    itemCommand.Parameters.AddWithValue("@runnerId", runnerId);
                    itemCommand.Parameters.AddWithValue("@testSuiteId", suiteStep.TestSuite.BaseTestSuiteId ?? suiteStep.TestSuite.Id);
                    itemCommand.Parameters.AddWithValue("@executionId", suiteStep.TestSuite.Id);
                    itemCommand.Parameters.AddWithValue("@testSuiteName", suiteStep.TestSuite.Name ?? string.Empty);
                    itemCommand.Parameters.AddWithValue("@steps", JsonSerializer.Serialize(suiteStep.Steps));
                    itemCommand.Parameters.AddWithValue("@videos", JsonSerializer.Serialize(Array.Empty<string>()));
                    itemCommand.Parameters.AddWithValue("@runBy", context.UserId);
                    itemCommand.Parameters.AddWithValue("@statusId", InProgressStatusId);
                    await itemCommand.ExecuteNonQueryAsync(cancellationToken);
                }
            }

            await transaction.CommitAsync(cancellationToken);
            return new RunnerHeaderDto
            {
                Id = runnerId,
                TestPlanItemId = testPlanItemId,
                TestPlanItemName = itemName
            };
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<RunnerItemRecord?> LoadRunnerItemRecordAsync(SqlConnection connection, long clientId, long? testRunnerId, long? testPlanItemId, long testSuiteId, CancellationToken cancellationToken)
    {
        var resolvedRunnerId = testRunnerId;
        if (!resolvedRunnerId.HasValue && testPlanItemId.HasValue)
        {
            const string latestRunnerSql = """
                SELECT TOP 1 tri.test_runner_id
                FROM test_runner_items tri
                INNER JOIN test_runners tr ON tr.id = tri.test_runner_id
                WHERE tr.client_id = @clientId
                  AND tr.test_plan_item_id = @testPlanItemId
                                    AND COALESCE(tri.execution_id, tri.test_suite_id) = @testSuiteId
                ORDER BY tri.id DESC;
                """;
            await using var latestRunnerCommand = CreateCommand(connection, latestRunnerSql);
            latestRunnerCommand.Parameters.AddWithValue("@clientId", clientId);
            latestRunnerCommand.Parameters.AddWithValue("@testPlanItemId", testPlanItemId.Value);
            latestRunnerCommand.Parameters.AddWithValue("@testSuiteId", testSuiteId);
            var latestRunnerValue = await latestRunnerCommand.ExecuteScalarAsync(cancellationToken);
            if (latestRunnerValue is not null && latestRunnerValue is not DBNull)
            {
                resolvedRunnerId = Convert.ToInt64(latestRunnerValue);
            }
        }

        if (!resolvedRunnerId.HasValue)
        {
            return null;
        }

        const string sql = """
            SELECT TOP 1
                tri.id AS runner_item_id,
                tri.test_runner_id,
                                COALESCE(tri.execution_id, tri.test_suite_id) AS runtime_suite_id,
                tri.test_suite_name,
                tri.steps,
                tri.videos,
                tr.test_plan_item_id,
                tr.test_plan_item_name
            FROM test_runner_items tri
            INNER JOIN test_runners tr ON tr.id = tri.test_runner_id
            WHERE tr.client_id = @clientId
              AND tri.test_runner_id = @testRunnerId
                            AND COALESCE(tri.execution_id, tri.test_suite_id) = @testSuiteId
            ORDER BY tri.id DESC;
            """;

        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@clientId", clientId);
        command.Parameters.AddWithValue("@testRunnerId", resolvedRunnerId.Value);
        command.Parameters.AddWithValue("@testSuiteId", testSuiteId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new RunnerItemRecord(
            reader.GetInt64(reader.GetOrdinal("runner_item_id")),
            reader.GetInt64(reader.GetOrdinal("test_runner_id")),
            reader.GetInt64(reader.GetOrdinal("runtime_suite_id")),
            GetInt64(reader, "test_plan_item_id") ?? 0,
            GetString(reader, "test_suite_name"),
            GetString(reader, "test_plan_item_name"),
            GetString(reader, "steps"),
            GetString(reader, "videos"));
    }

    private async Task<RunnerItemRecord?> LoadLatestPausedRunnerItemAsync(SqlConnection connection, long clientId, long testPlanItemId, long testSuiteId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP 20
                tri.id AS runner_item_id,
                tri.test_runner_id,
                                COALESCE(tri.execution_id, tri.test_suite_id) AS runtime_suite_id,
                tri.test_suite_name,
                tri.steps,
                tri.videos,
                tr.test_plan_item_id,
                tr.test_plan_item_name
            FROM test_runner_items tri
            INNER JOIN test_runners tr ON tr.id = tri.test_runner_id
            WHERE tr.client_id = @clientId
              AND tr.test_plan_item_id = @testPlanItemId
                            AND COALESCE(tri.execution_id, tri.test_suite_id) = @testSuiteId
              AND tri.status_id = @statusId
            ORDER BY tri.id DESC;
            """;

        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@clientId", clientId);
        command.Parameters.AddWithValue("@testPlanItemId", testPlanItemId);
        command.Parameters.AddWithValue("@testSuiteId", testSuiteId);
        command.Parameters.AddWithValue("@statusId", InProgressStatusId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var stepsJson = GetString(reader, "steps");
            if (!HasPausedRunnerStep(stepsJson))
            {
                continue;
            }

            return new RunnerItemRecord(
                reader.GetInt64(reader.GetOrdinal("runner_item_id")),
                reader.GetInt64(reader.GetOrdinal("test_runner_id")),
                reader.GetInt64(reader.GetOrdinal("runtime_suite_id")),
                GetInt64(reader, "test_plan_item_id") ?? 0,
                GetString(reader, "test_suite_name"),
                GetString(reader, "test_plan_item_name"),
                stepsJson,
                GetString(reader, "videos"));
        }

        return null;
    }

    private async Task SaveRunnerItemStateAsync(SqlConnection connection, SqlTransaction transaction, RunnerItemRecord runnerItem, string? stepsJson, int statusId, CancellationToken cancellationToken)
    {
        const string updateRunnerItemSql = """
            UPDATE test_runner_items
            SET steps = @steps,
                status_id = @statusId,
                updated_at = SYSUTCDATETIME()
            WHERE id = @runnerItemId;
            """;

        await using (var updateCommand = CreateCommand(connection, updateRunnerItemSql))
        {
            updateCommand.Transaction = transaction;
            updateCommand.Parameters.AddWithValue("@steps", (object?)stepsJson ?? JsonSerializer.Serialize(Array.Empty<object>()));
            updateCommand.Parameters.AddWithValue("@statusId", statusId);
            updateCommand.Parameters.AddWithValue("@runnerItemId", runnerItem.RunnerItemId);
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await UpdateExecutionStatusAsync(connection, transaction, runnerItem.TestPlanItemId, runnerItem.TestSuiteId, statusId, cancellationToken);
    }

    private async Task<TestRunnerPayloadDto> LoadExistingRunnerPayloadAsync(SqlConnection connection, long clientId, long testRunnerId, CancellationToken cancellationToken)
    {
        const string runnerSql = """
            SELECT TOP 1 id, test_plan_item_id, test_plan_item_name
            FROM test_runners
            WHERE id = @testRunnerId AND client_id = @clientId;
            """;

        RunnerHeaderDto? header = null;
        await using (var runnerCommand = CreateCommand(connection, runnerSql))
        {
            runnerCommand.Parameters.AddWithValue("@testRunnerId", testRunnerId);
            runnerCommand.Parameters.AddWithValue("@clientId", clientId);
            await using var reader = await runnerCommand.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                header = new RunnerHeaderDto
                {
                    Id = reader.GetInt64(reader.GetOrdinal("id")),
                    TestPlanItemId = GetInt64(reader, "test_plan_item_id"),
                    TestPlanItemName = GetString(reader, "test_plan_item_name")
                };
            }
        }

        const string itemsSql = """
            SELECT tri.test_suite_id, COALESCE(tri.execution_id, tri.test_suite_id) AS runtime_suite_id, tri.test_suite_name, tri.steps, tri.videos
            FROM test_runner_items tri
            WHERE tri.test_runner_id = @testRunnerId
            ORDER BY tri.id;
            """;

        var rawItems = new List<(long TestSuiteId, long BaseTestSuiteId, string? TestSuiteName, string? StepsJson, string? VideosJson)>();
        var suites = new List<TestRunnerSuiteDto>();
        await using (var itemsCommand = CreateCommand(connection, itemsSql))
        {
            itemsCommand.Parameters.AddWithValue("@testRunnerId", testRunnerId);
            await using var reader = await itemsCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var suiteId = GetInt64(reader, "runtime_suite_id") ?? 0;
                rawItems.Add((suiteId, GetInt64(reader, "test_suite_id") ?? suiteId, GetString(reader, "test_suite_name"), GetString(reader, "steps"), GetString(reader, "videos")));
            }
        }

        var executionIds = rawItems.Select(row => row.TestSuiteId).Distinct().ToArray();
        var contexts = await LoadExecutionSuiteContextsAsync(connection, clientId, executionIds, cancellationToken);
        var contextMap = contexts.ToDictionary(row => row.ExecutionId);
        var baseSuiteIds = contexts.Select(row => row.BaseTestDesignId).Distinct().ToArray();
        var baseSuiteInfo = new Dictionary<long, (string? Title, string? Comment, long? ConfigurationId)>();
        if (baseSuiteIds.Length > 0)
        {
            var parameters = AddIdListParameterValues(baseSuiteIds, "@suiteId");
            var baseSql = $"SELECT id, title, comment, configuration_id FROM test_designs WHERE id IN ({string.Join(", ", parameters.Select(parameter => parameter.ParameterName))});";
            await using var baseCommand = CreateCommand(connection, baseSql);
            AddParameters(baseCommand, parameters);
            await using var baseReader = await baseCommand.ExecuteReaderAsync(cancellationToken);
            while (await baseReader.ReadAsync(cancellationToken))
            {
                baseSuiteInfo[baseReader.GetInt64(baseReader.GetOrdinal("id"))] = (
                    GetString(baseReader, "title"),
                    GetString(baseReader, "comment"),
                    GetInt64(baseReader, "configuration_id"));
            }
        }

        var configurationIds = contexts
            .Select(row => row.ConfigurationId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Concat(baseSuiteInfo.Values.Where(row => row.ConfigurationId.HasValue).Select(row => row.ConfigurationId!.Value))
            .Distinct()
            .ToArray();
        var configurationMap = await LoadSuiteConfigurationsAsync(connection, configurationIds, cancellationToken);

        foreach (var item in rawItems)
        {
            contextMap.TryGetValue(item.TestSuiteId, out var context);
            var baseSuiteId = context.BaseTestDesignId != 0 ? context.BaseTestDesignId : item.BaseTestSuiteId;
            baseSuiteInfo.TryGetValue(baseSuiteId, out var info);
            var configurationId = context.ConfigurationId ?? info.ConfigurationId;
            suites.Add(new TestRunnerSuiteDto
            {
                TestSuite = new RunnerSuiteHeaderDto
                {
                    Id = item.TestSuiteId,
                    BaseTestSuiteId = baseSuiteId,
                    Name = item.TestSuiteName ?? info.Title,
                    Videos = ParseJsonElementOrDefault(item.VideosJson, Array.Empty<string>()),
                    Prereq = info.Comment,
                    Configuration = configurationId.HasValue && configurationMap.TryGetValue(configurationId.Value, out var configuration)
                        ? configuration
                        : null
                },
                Steps = DeserializeRunnerSteps(item.StepsJson)
            });
        }

        return new TestRunnerPayloadDto
        {
            TestRunner = header,
            TestRunnerSteps = suites
        };
    }

    private async Task<TestRunnerPayloadDto> LoadExistingRunnerPayloadForSuiteAsync(SqlConnection connection, long clientId, long testRunnerId, long testSuiteId, CancellationToken cancellationToken)
    {
        var payload = await LoadExistingRunnerPayloadAsync(connection, clientId, testRunnerId, cancellationToken);
        return new TestRunnerPayloadDto
        {
            TestRunner = payload.TestRunner,
            TestRunnerSteps = payload.TestRunnerSteps.Where(row => row.TestSuite.Id == testSuiteId).ToList()
        };
    }

    private TestRunnerSuiteDto BuildRunnerSuiteDtoFromRunnerItem(
        TestSuiteFullDto suite,
        IReadOnlyDictionary<long, SuiteConfigurationDto> configurationMap,
        RunnerItemRecord pausedRunnerItem)
    {
        var configuration = suite.ConfigurationId.HasValue && configurationMap.TryGetValue(suite.ConfigurationId.Value, out var loadedConfiguration)
            ? loadedConfiguration
            : null;

        return new TestRunnerSuiteDto
        {
            TestSuite = new RunnerSuiteHeaderDto
            {
                Id = suite.RuntimeSuiteId == 0 ? suite.Id : suite.RuntimeSuiteId,
                BaseTestSuiteId = suite.Id,
                Name = suite.Title,
                Videos = ParseJsonElementOrDefault(pausedRunnerItem.VideosJson, Array.Empty<string>()),
                Prereq = suite.Comment,
                Configuration = configuration
            },
            Steps = DeserializeRunnerSteps(pausedRunnerItem.StepsJson)
        };
    }

    private static IReadOnlyList<TestRunnerStepDto> DeserializeRunnerSteps(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<TestRunnerStepDto>>(json, AppJsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static bool HasPausedRunnerStep(string? stepsJson)
    {
        if (string.IsNullOrWhiteSpace(stepsJson))
        {
            return false;
        }

        try
        {
            var root = JsonNode.Parse(stepsJson) as JsonArray;
            if (root is null)
            {
                return false;
            }

            return root.OfType<JsonObject>().Any(node => GetNodeBoolean(node, "resume_anchor") == true);
        }
        catch
        {
            return false;
        }
    }

    private async Task<Dictionary<long, bool>> LoadPausedSuiteStateMapAsync(SqlConnection connection, long testPlanItemId, CancellationToken cancellationToken)
    {
        const string sql = """
                        SELECT COALESCE(tri.execution_id, tri.test_suite_id) AS runtime_suite_id, tri.steps
            FROM test_runner_items tri
            INNER JOIN test_runners tr ON tr.id = tri.test_runner_id
            INNER JOIN (
                                SELECT COALESCE(tri_inner.execution_id, tri_inner.test_suite_id) AS runtime_suite_id, MAX(tri_inner.id) AS latest_runner_item_id
                FROM test_runner_items tri_inner
                INNER JOIN test_runners tr_inner ON tr_inner.id = tri_inner.test_runner_id
                WHERE tr_inner.test_plan_item_id = @testPlanItemId
                  AND tri_inner.status_id = @statusId
                                GROUP BY COALESCE(tri_inner.execution_id, tri_inner.test_suite_id)
            ) latest ON latest.latest_runner_item_id = tri.id
            WHERE tr.test_plan_item_id = @testPlanItemId;
            """;

        var result = new Dictionary<long, bool>();
        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@testPlanItemId", testPlanItemId);
        command.Parameters.AddWithValue("@statusId", InProgressStatusId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var suiteId = GetInt64(reader, "runtime_suite_id");
            if (!suiteId.HasValue)
            {
                continue;
            }

            result[suiteId.Value] = HasPausedRunnerStep(GetString(reader, "steps"));
        }

        return result;
    }

    private static string MarkPausedRunnerStepJson(string? stepsJson, long? resumeStepId, int? resumeStepIndex)
    {
        var root = JsonNode.Parse(string.IsNullOrWhiteSpace(stepsJson) ? "[]" : stepsJson) as JsonArray ?? new JsonArray();
        var nodes = root.OfType<JsonObject>().ToList();
        if (nodes.Count == 0)
        {
            return root.ToJsonString();
        }

        foreach (var node in nodes)
        {
            node.Remove("resume_anchor");
        }

        var resolvedIndex = -1;
        if (resumeStepId.HasValue && resumeStepId.Value > 0)
        {
            resolvedIndex = nodes.FindIndex(node => GetNodeInt64(node, "id") == resumeStepId.Value);
        }

        if (resolvedIndex < 0 && resumeStepIndex.HasValue && resumeStepIndex.Value >= 0 && resumeStepIndex.Value < nodes.Count)
        {
            resolvedIndex = resumeStepIndex.Value;
        }

        if (resolvedIndex < 0)
        {
            resolvedIndex = nodes.FindIndex(node => GetNodeBoolean(node, "is_passed") is null);
        }

        if (resolvedIndex < 0)
        {
            resolvedIndex = Math.Max(nodes.Count - 1, 0);
        }

        nodes[resolvedIndex]["resume_anchor"] = true;
        return root.ToJsonString();
    }

    private static string ClearPausedRunnerStepJson(string? stepsJson)
    {
        var root = JsonNode.Parse(string.IsNullOrWhiteSpace(stepsJson) ? "[]" : stepsJson) as JsonArray ?? new JsonArray();
        foreach (var node in root.OfType<JsonObject>())
        {
            node.Remove("resume_anchor");
        }

        return root.ToJsonString();
    }

    private static RunnerStepUpdateResult UpdateRunnerStepsJson(string? stepsJson, SaveTestRunnerStepStatusRequest request, IReadOnlyList<string> imagePaths)
    {
        var root = JsonNode.Parse(string.IsNullOrWhiteSpace(stepsJson) ? "[]" : stepsJson) as JsonArray ?? new JsonArray();
        if (request.BulkUpdate == true)
        {
            foreach (var node in root.OfType<JsonObject>())
            {
                node["is_passed"] = request.IsPassed;
            }

            return new RunnerStepUpdateResult
            {
                StepsJson = root.ToJsonString(),
                AcceptedCount = root.Count,
                MatchedCount = root.Count,
                UpdatedCount = root.Count
            };
        }

        var matchedCount = 0;

        foreach (var update in request.Steps)
        {
            foreach (var node in root.OfType<JsonObject>())
            {
                if (!RunnerStepMatches(node, update))
                {
                    continue;
                }

                node["is_passed"] = update.IsPassed;
                if (update.Comment is not null)
                {
                    node["comment"] = update.Comment;
                }

                if (imagePaths.Count > 0)
                {
                    AppendImages(node, imagePaths);
                }

                matchedCount += 1;

                break;
            }
        }

        return new RunnerStepUpdateResult
        {
            StepsJson = root.ToJsonString(),
            AcceptedCount = request.Steps.Count,
            MatchedCount = matchedCount,
            UpdatedCount = matchedCount
        };
    }

    private static bool RunnerStepMatches(JsonObject node, SaveTestRunnerStepRequest update)
    {
        var currentId = GetNodeInt64(node, "id");
        if (currentId != update.ResolvedId)
        {
            return false;
        }

        if (!update.DatasetId.HasValue)
        {
            return true;
        }

        return GetNodeInt64(node, "dataset_id") == update.DatasetId.Value;
    }

    private static void AppendImages(JsonObject node, IReadOnlyList<string> imagePaths)
    {
        var images = NormalizeStringArrayNode(node["images"]);
        foreach (var imagePath in imagePaths)
        {
            images.Add(imagePath);
        }

        node["images"] = images;
    }

    private static JsonArray NormalizeStringArrayNode(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            return array;
        }

        if (node is JsonObject obj)
        {
            var result = new JsonArray();
            foreach (var pair in obj)
            {
                var text = pair.Value?.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    result.Add(text);
                }
            }

            return result;
        }

        var textValue = node?.ToString();
        if (string.IsNullOrWhiteSpace(textValue))
        {
            return new JsonArray();
        }

        try
        {
            var parsed = JsonNode.Parse(textValue);
            if (parsed is JsonArray parsedArray)
            {
                return parsedArray;
            }

            if (parsed is JsonObject parsedObject)
            {
                return NormalizeStringArrayNode(parsedObject);
            }
        }
        catch
        {
            // Ignore malformed stored image payloads and fall back to string parsing.
        }

        var resultFromString = new JsonArray();
        foreach (var item in textValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            resultFromString.Add(item);
        }

        return resultFromString;
    }

    private static int DetermineRunnerSuiteStatusId(string? stepsJson)
    {
        var steps = DeserializeRunnerSteps(stepsJson);
        if (steps.Count == 0)
        {
            return InProgressStatusId;
        }

        var passedCount = 0;
        var hasFailure = false;
        foreach (var step in steps)
        {
            if (!step.IsPassed.HasValue)
            {
                continue;
            }

            if (step.IsPassed.Value)
            {
                passedCount += 1;
            }
            else
            {
                hasFailure = true;
            }
        }

        if (steps.Count == passedCount)
        {
            return PassedStatusId;
        }

        if (hasFailure)
        {
            return FailedStatusId;
        }

        return InProgressStatusId;
    }

    private static string GetRunnerSuiteStatusName(int statusId)
    {
        return statusId switch
        {
            PassedStatusId => "Passed",
            FailedStatusId => "Failed",
            _ => "In Progress"
        };
    }

    private static long? GetNodeInt64(JsonObject node, string propertyName)
    {
        if (!node.TryGetPropertyValue(propertyName, out var raw) || raw is null)
        {
            return null;
        }

        if (raw is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<long>(out var longValue))
            {
                return longValue;
            }

            if (jsonValue.TryGetValue<int>(out var intValue))
            {
                return intValue;
            }

            if (jsonValue.TryGetValue<string>(out var stringValue) && long.TryParse(stringValue, out var parsed))
            {
                return parsed;
            }
        }

        return long.TryParse(raw.ToString(), out var fallback) ? fallback : null;
    }

    private RunnerKeywordDto BuildRunnerKeyword(ComponentStepDto componentStep)
    {
        if (componentStep.Keyword is not null)
        {
            return new RunnerKeywordDto
            {
                Id = componentStep.Keyword.Id,
                Name = componentStep.Keyword.Name,
                Source = "component",
                KeywordCombinationNames = componentStep.KeywordCombinationNames
            };
        }

        if (componentStep.GlobalKeyword is not null)
        {
            return new RunnerKeywordDto
            {
                Id = componentStep.GlobalKeyword.Id,
                Name = componentStep.GlobalKeyword.Name,
                Source = "global"
            };
        }

        return new RunnerKeywordDto();
    }

    private string ResolveRunnerValue(string? originalValue, long suiteId, RunnerVariableMaps variableMaps, Dictionary<string, string> globalCache, Dictionary<string, string> localCache)
    {
        if (string.IsNullOrEmpty(originalValue))
        {
            return originalValue ?? string.Empty;
        }

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

    private static string GenerateVariableValue(RunnerVariableValue variable)
    {
        return VariableValueResolver.Resolve(variable.Value, NormalizeOptionalText(variable.ExecutableMethod), variable.IsEncrypted);
    }

    private async Task<OverrideParts> ParseOverridePartsAsync(SqlConnection connection, string overrideValue, CancellationToken cancellationToken)
    {
        var parts = new OverrideParts();
        var segments = SplitOverrideSegmentsRuntime(overrideValue);

        foreach (var rawSegment in segments)
        {
            var segment = rawSegment.Trim();
            if (string.IsNullOrWhiteSpace(segment))
            {
                continue;
            }

            if (!TrySplitOverridePairRuntime(segment, out var rawKey, out var rawValue))
            {
                continue;
            }

            var key = NormalizeOverrideKeyRuntime(rawKey);
            switch (key)
            {
                case "keyword":
                    parts.Keyword = await ResolveOverrideKeywordAsync(connection, rawValue.Trim(), cancellationToken) ?? parts.Keyword;
                    break;
                case "before_step":
                    parts.BeforeStep = ParseBeforeAfterSteps([rawValue]);
                    break;
                case "after_step":
                    parts.AfterStep = ParseBeforeAfterSteps([rawValue]);
                    break;
                case "description":
                    parts.Description = rawValue;
                    break;
                case "expected_output":
                    parts.ExpectedOutput = rawValue;
                    break;
                case "xpath":
                    parts.XPath = rawValue;
                    break;
            }
        }

        return parts;
    }

    private async Task<RunnerKeywordDto?> ResolveOverrideKeywordAsync(SqlConnection connection, string keywordName, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP 1 id, name, CAST(NULL AS nvarchar(2000)) AS keyword_combination_names, 'component' AS source
            FROM component_keywords
            WHERE name = @name
            UNION ALL
            SELECT TOP 1 id, name, CAST(NULL AS nvarchar(2000)) AS keyword_combination_names, 'global' AS source
            FROM global_keywords
            WHERE name = @name;
            """;

        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@name", keywordName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new RunnerKeywordDto
        {
            Id = GetInt64(reader, "id"),
            Name = GetString(reader, "name"),
            Source = GetString(reader, "source"),
            KeywordCombinationNames = GetString(reader, "keyword_combination_names")
        };
    }

    private static IReadOnlyList<Dictionary<string, string>> ParseBeforeAfterSteps(IReadOnlyList<string>? values)
    {
        var result = new List<Dictionary<string, string>>();
        if (values is null || values.Count == 0)
        {
            return result;
        }

        foreach (var entry in values)
        {
            var trimmed = NormalizeOptionalText(entry);
            if (string.IsNullOrWhiteSpace(trimmed) || string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var token in SplitBeforeAfterHelperSegments(trimmed))
            {
                if (!TrySplitBeforeAfterHelperToken(token, out var helperName, out var helperValue))
                {
                    continue;
                }

                result.Add(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [helperName.Trim()] = helperValue
                });
            }
        }

        return result;
    }

    private static IReadOnlyList<string> SplitBeforeAfterHelperSegments(string raw)
    {
        if (raw.Contains(";;", StringComparison.Ordinal))
        {
            return [];
        }

        return SplitTopLevelDelimitedSegments(raw, ";");
    }

    private static bool TrySplitBeforeAfterHelperToken(string token, out string helperName, out string helperValue)
    {
        var trimmed = token.Trim();
        var splitIndex = trimmed.IndexOf("=:", StringComparison.Ordinal);

        if (splitIndex <= 0)
        {
            helperName = string.Empty;
            helperValue = string.Empty;
            return false;
        }

        var rawHelperValue = trimmed[(splitIndex + 2)..];
        if (rawHelperValue.StartsWith(':'))
        {
            helperName = string.Empty;
            helperValue = string.Empty;
            return false;
        }

        helperName = trimmed[..splitIndex].Trim(' ', '"');
        helperValue = rawHelperValue.Trim(' ', '"');
        return !string.IsNullOrWhiteSpace(helperName);
    }

    private static IReadOnlyList<string> SplitTopLevelDelimitedSegments(string? raw, string delimiter)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var segments = new List<string>();
        var current = new StringBuilder();
        char quote = '\0';
        var roundDepth = 0;
        var squareDepth = 0;
        var curlyDepth = 0;

        for (var index = 0; index < raw.Length; index += 1)
        {
            var ch = raw[index];
            var next = index + delimiter.Length <= raw.Length
                ? raw.Substring(index, delimiter.Length)
                : string.Empty;

            if (quote != '\0')
            {
                current.Append(ch);
                if (ch == quote && (index == 0 || raw[index - 1] != '\\'))
                {
                    quote = '\0';
                }
                continue;
            }

            if (ch is '"' or '\'')
            {
                quote = ch;
                current.Append(ch);
                continue;
            }

            switch (ch)
            {
                case '(':
                    roundDepth += 1;
                    break;
                case ')':
                    if (roundDepth > 0) roundDepth -= 1;
                    break;
                case '[':
                    squareDepth += 1;
                    break;
                case ']':
                    if (squareDepth > 0) squareDepth -= 1;
                    break;
                case '{':
                    curlyDepth += 1;
                    break;
                case '}':
                    if (curlyDepth > 0) curlyDepth -= 1;
                    break;
            }

            if (next == delimiter && roundDepth == 0 && squareDepth == 0 && curlyDepth == 0)
            {
                var normalized = current.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    segments.Add(normalized);
                }

                current.Clear();
                index += delimiter.Length - 1;
                continue;
            }

            current.Append(ch);
        }

        var tail = current.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(tail))
        {
            segments.Add(tail);
        }

        return segments;
    }

    private static string ApplyXPathValue(string? xpath, string? value)
    {
        if (string.IsNullOrEmpty(xpath))
        {
            return xpath ?? string.Empty;
        }

        return xpath.Replace(XPathReplaceVariable, value ?? string.Empty, StringComparison.Ordinal);
    }

    private static string? ResolveConfigurationLaunchValue(SuiteConfigurationDto configuration, string keywordName)
    {
        if (configuration.ConfigurationVariables.Count == 0)
        {
            return null;
        }

        var normalizedKeyword = NormalizeOptionalText(keywordName)?.ToLowerInvariant();
        if (normalizedKeyword is "launchbrowser" or "launchdebugbrowser")
        {
            return configuration.ConfigurationVariables
                .FirstOrDefault(item => string.Equals(item.Variable?.Name, "browser", StringComparison.OrdinalIgnoreCase))
                ?.Value?.Name;
        }

        if (normalizedKeyword == "launchmobile")
        {
            return configuration.ConfigurationVariables
                .FirstOrDefault(item => item.Variable?.Name?.Contains("mobile", StringComparison.OrdinalIgnoreCase) == true)
                ?.Value?.Name;
        }

        return null;
    }

    private static bool? GetNodeBoolean(JsonObject node, string propertyName)
    {
        if (!node.TryGetPropertyValue(propertyName, out var raw) || raw is null)
        {
            return null;
        }

        if (raw is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<bool>(out var boolValue))
            {
                return boolValue;
            }

            if (jsonValue.TryGetValue<int>(out var intValue))
            {
                return intValue != 0;
            }

            if (jsonValue.TryGetValue<string>(out var stringValue))
            {
                if (bool.TryParse(stringValue, out var parsedBool))
                {
                    return parsedBool;
                }

                if (int.TryParse(stringValue, out var parsedInt))
                {
                    return parsedInt != 0;
                }
            }
        }

        return null;
    }

    private async Task<bool> HasColumnAsync(SqlConnection connection, string tableName, string columnName, SqlTransaction? transaction, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT CASE WHEN EXISTS (
                SELECT 1
                FROM INFORMATION_SCHEMA.COLUMNS
                WHERE TABLE_NAME = @tableName AND COLUMN_NAME = @columnName
            ) THEN 1 ELSE 0 END;
            """;
        await using var command = CreateCommand(connection, sql);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@tableName", tableName);
        command.Parameters.AddWithValue("@columnName", columnName);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private async Task<bool> TableExistsAsync(SqlConnection connection, string tableName, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT CASE WHEN EXISTS (
                SELECT 1
                FROM INFORMATION_SCHEMA.TABLES
                WHERE TABLE_NAME = @tableName
            ) THEN 1 ELSE 0 END;
            """;
        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@tableName", tableName);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    private async Task EnsureTestRunnerFavoriteColumnAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        if (await HasColumnAsync(connection, "test_runner_items", "is_favorite", transaction: null, cancellationToken))
        {
            return;
        }

        const string sql = "ALTER TABLE test_runner_items ADD is_favorite BIT NOT NULL CONSTRAINT DF_test_runner_items_is_favorite DEFAULT (0);";
        await using var command = CreateCommand(connection, sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureDefectSchemaAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        if (!await HasColumnAsync(connection, "defects", "description", transaction: null, cancellationToken))
        {
            await using var command = CreateCommand(connection, "ALTER TABLE defects ADD description NVARCHAR(MAX) NULL;");
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!await HasColumnAsync(connection, "defects", "expected_result", transaction: null, cancellationToken))
        {
            await using var command = CreateCommand(connection, "ALTER TABLE defects ADD expected_result NVARCHAR(MAX) NULL;");
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!await HasColumnAsync(connection, "defects", "actual_result", transaction: null, cancellationToken))
        {
            await using var command = CreateCommand(connection, "ALTER TABLE defects ADD actual_result NVARCHAR(MAX) NULL;");
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!await TableExistsAsync(connection, "defect_attachments", cancellationToken))
        {
            const string createTableSql = """
                CREATE TABLE defect_attachments (
                    id BIGINT IDENTITY(1,1) PRIMARY KEY,
                    defect_id BIGINT NOT NULL,
                    client_id BIGINT NOT NULL,
                    file_name NVARCHAR(260) NOT NULL,
                    file_path NVARCHAR(1000) NOT NULL,
                    content_type NVARCHAR(255) NULL,
                    file_size BIGINT NOT NULL CONSTRAINT DF_defect_attachments_file_size DEFAULT (0),
                    created_by BIGINT NULL,
                    created_at DATETIMEOFFSET(7) NOT NULL CONSTRAINT DF_defect_attachments_created_at DEFAULT SYSUTCDATETIME(),
                    updated_at DATETIMEOFFSET(7) NOT NULL CONSTRAINT DF_defect_attachments_updated_at DEFAULT SYSUTCDATETIME(),
                    deleted_at DATETIMEOFFSET(7) NULL
                );
                CREATE INDEX IX_defect_attachments_defect_id ON defect_attachments(defect_id);
                CREATE INDEX IX_defect_attachments_client_id ON defect_attachments(client_id);
                """;
            await using var command = CreateCommand(connection, createTableSql);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task UpdateTestSuiteDetailsRowAsync(SqlConnection connection, SqlTransaction transaction, RequestContext context, ClaimsPrincipal principal, long suiteId, SaveTestSuiteDetailsRequest details, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE test_designs
            SET title = @title,
                test_state_id = @testStateId,
                test_suite_type = @testSuiteType,
                folder_path_id = @folderPathId,
                project_id = @projectId,
                azure_iteration_path = @iterationPath,
                priority = @priority,
                story_id = @storyId,
                test_title = @testTitle,
                tags = @tags,
                comment = @comment,
                kba_ready = @kbaReady,
                training_ready = @trainingReady,
                release_notes_ready = @releaseNotesReady,
                updated_by_id = @updatedById,
                updated_by = @updatedBy,
                updated_at = SYSUTCDATETIME()
            WHERE id = @id AND client_id = @clientId AND deleted_at IS NULL;
            """;

        var userDisplayName = NormalizeOptionalText(GetUserDisplayName(principal)) ?? $"User {context.UserId}";
        await using var command = CreateCommand(connection, sql);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@id", suiteId);
        command.Parameters.AddWithValue("@clientId", context.ClientId!.Value);
        command.Parameters.AddWithValue("@title", details.Title!.Trim());
        command.Parameters.AddWithValue("@testStateId", details.TestStateId!.Value);
        command.Parameters.AddWithValue("@testSuiteType", details.TestSuiteType!.Value);
        command.Parameters.AddWithValue("@folderPathId", (object?)details.FolderPathId ?? DBNull.Value);
        command.Parameters.AddWithValue("@projectId", (object?)details.ProjectId ?? DBNull.Value);
        command.Parameters.AddWithValue("@iterationPath", (object?)NormalizeOptionalText(details.IterationPath) ?? DBNull.Value);
        command.Parameters.AddWithValue("@priority", (object?)NormalizeOptionalText(details.Priority) ?? DBNull.Value);
        command.Parameters.AddWithValue("@storyId", (object?)NormalizeOptionalText(details.StoryId) ?? DBNull.Value);
        command.Parameters.AddWithValue("@testTitle", (object?)NormalizeOptionalText(details.TestTitle) ?? DBNull.Value);
        command.Parameters.AddWithValue("@tags", (object?)NormalizeSuiteTags(details.Tags) ?? DBNull.Value);
        command.Parameters.AddWithValue("@comment", (object?)NormalizeOptionalText(details.Comment) ?? DBNull.Value);
        command.Parameters.AddWithValue("@kbaReady", details.KbaReady ?? false);
        command.Parameters.AddWithValue("@trainingReady", details.TrainingReady ?? false);
        command.Parameters.AddWithValue("@releaseNotesReady", details.ReleaseNotesReady ?? false);
        command.Parameters.AddWithValue("@updatedById", context.UserId);
        command.Parameters.AddWithValue("@updatedBy", $"{userDisplayName} ({DateTime.Now:M-d-yy h:mm tt})");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<(bool NotFound, long? ProjectId)> GetScopedTestSuiteProjectIdAsync(SqlConnection connection, long clientId, long suiteId, CancellationToken cancellationToken)
    {
        const string sql = "SELECT project_id FROM test_designs WHERE id = @id AND client_id = @clientId AND deleted_at IS NULL;";
        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@id", suiteId);
        command.Parameters.AddWithValue("@clientId", clientId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return (true, null);
        }

        return (false, GetInt64(reader, "project_id"));
    }

    private async Task<SaveTestSuiteFlowResult?> ValidateTestSuiteFlowReferencesAsync(SqlConnection connection, long clientId, long suiteId, SaveTestSuiteFlowRequest request, CancellationToken cancellationToken)
    {
        if (request.Components.Count == 0)
        {
            return null;
        }

        var componentIds = request.Components.Where(item => item.ComponentId.HasValue).Select(item => item.ComponentId!.Value).Distinct().ToArray();
        var projectIds = request.Components.Where(item => item.ProjectId.HasValue).Select(item => item.ProjectId!.Value).Distinct().ToArray();
        if (request.Components.Any(item => !item.ComponentId.HasValue || !item.ProjectId.HasValue))
        {
            return InvalidFlowReference("components", "The component_id and project_id fields are required.");
        }

        var projectParameters = new List<SqlParameter> { new("@clientId", clientId) };
        var projectPlaceholders = AddIdListParameters(projectParameters, "@projectId", projectIds);
        var projectSql = $"SELECT id FROM projects WHERE client_id = @clientId AND id IN ({string.Join(", ", projectPlaceholders)});";
        var validProjectIds = new HashSet<long>();
        await using (var command = CreateCommand(connection, projectSql))
        {
            AddParameters(command, projectParameters);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var projectId = GetInt64(reader, "id");
                if (projectId.HasValue)
                {
                    validProjectIds.Add(projectId.Value);
                }
            }
        }

        if (request.Components.Any(item => !validProjectIds.Contains(item.ProjectId!.Value)))
        {
            return InvalidFlowReference("components.project_id", "One or more selected project_id values are invalid.");
        }

        var componentParameters = new List<SqlParameter> { new("@clientId", clientId) };
        var componentPlaceholders = AddIdListParameters(componentParameters, "@componentId", componentIds);
        var componentSql = $"SELECT id FROM components WHERE client_id = @clientId AND deleted_at IS NULL AND id IN ({string.Join(", ", componentPlaceholders)});";
        var validComponentIds = new HashSet<long>();
        await using (var command = CreateCommand(connection, componentSql))
        {
            AddParameters(command, componentParameters);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var componentId = GetInt64(reader, "id");
                if (componentId.HasValue)
                {
                    validComponentIds.Add(componentId.Value);
                }
            }
        }

        if (request.Components.Any(item => !validComponentIds.Contains(item.ComponentId!.Value)))
        {
            return InvalidFlowReference("components.component_id", "One or more selected component_id values are invalid.");
        }

        var persistedIds = request.Components.Where(item => item.TestComponentId.HasValue).Select(item => item.TestComponentId!.Value).ToArray();
        if (persistedIds.Length != persistedIds.Distinct().Count())
        {
            return InvalidFlowReference("components.test_component_id", "Duplicate test_component_id values are not allowed.");
        }

        if (persistedIds.Length == 0)
        {
            return null;
        }

        var persistedParameters = new List<SqlParameter> { new("@suiteId", suiteId) };
        var persistedPlaceholders = AddIdListParameters(persistedParameters, "@testComponentId", persistedIds);
        var persistedSql = $"""
            SELECT id
            FROM test_components
            WHERE test_design_id = @suiteId
              AND deleted_at IS NULL
              AND id IN ({string.Join(", ", persistedPlaceholders)});
            """;
        var existing = new HashSet<long>();
        await using (var command = CreateCommand(connection, persistedSql))
        {
            AddParameters(command, persistedParameters);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                existing.Add(reader.GetInt64(reader.GetOrdinal("id")));
            }
        }

        foreach (var requested in request.Components.Where(item => item.TestComponentId.HasValue))
        {
            if (!existing.Contains(requested.TestComponentId!.Value))
            {
                return InvalidFlowReference("components.test_component_id", "A test_component_id does not belong to this test suite.");
            }
        }

        return null;
    }

    private static SaveTestSuiteFlowResult InvalidFlowReference(string field, string message)
    {
        return new SaveTestSuiteFlowResult
        {
            Outcome = SaveTestSuiteOutcome.InvalidReference,
            ErrorField = field,
            ErrorMessage = message
        };
    }

    private async Task<IReadOnlyList<TestSuiteFlowComponentSummaryDto>> ApplyTestSuiteFlowAsync(SqlConnection connection, SqlTransaction transaction, long suiteId, IReadOnlyList<SaveTestSuiteFlowComponentRequest> requestedComponents, bool useRequestedIds, CancellationToken cancellationToken)
    {
        const string loadSql = """
            SELECT id, component_id
            FROM test_components
            WHERE test_design_id = @suiteId AND deleted_at IS NULL
            ORDER BY ISNULL(sort_order, 2147483647), id;
            """;
        var existingRows = new List<(long Id, long ComponentId)>();
        await using (var command = CreateCommand(connection, loadSql))
        {
            command.Transaction = transaction;
            command.Parameters.AddWithValue("@suiteId", suiteId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var componentId = GetInt64(reader, "component_id");
                if (componentId.HasValue)
                {
                    existingRows.Add((reader.GetInt64(reader.GetOrdinal("id")), componentId.Value));
                }
            }
        }

        var remainingIds = existingRows.Select(row => row.Id).ToHashSet();
        var rowsByComponent = existingRows
            .GroupBy(row => row.ComponentId)
            .ToDictionary(group => group.Key, group => new Queue<long>(group.Select(row => row.Id)));
        var summaries = new List<TestSuiteFlowComponentSummaryDto>();

        for (var index = 0; index < requestedComponents.Count; index++)
        {
            var requested = requestedComponents[index];
            var sortOrder = requested.SortOrder ?? index;
            long? testComponentId = useRequestedIds ? requested.TestComponentId : null;
            if (testComponentId.HasValue && rowsByComponent.TryGetValue(requested.ComponentId!.Value, out var explicitMatches))
            {
                rowsByComponent[requested.ComponentId.Value] = new Queue<long>(explicitMatches.Where(id => id != testComponentId.Value));
            }

            if (!useRequestedIds && !testComponentId.HasValue && rowsByComponent.TryGetValue(requested.ComponentId!.Value, out var matches) && matches.Count > 0)
            {
                testComponentId = matches.Dequeue();
            }

            if (testComponentId.HasValue)
            {
                remainingIds.Remove(testComponentId.Value);
                const string updateSql = """
                    UPDATE test_components
                    SET component_id = @componentId,
                        project_id = @projectId,
                        status = @status,
                        sort_order = @sortOrder,
                        updated_at = SYSUTCDATETIME()
                    WHERE id = @id AND test_design_id = @suiteId AND deleted_at IS NULL;
                    """;
                await using var updateCommand = CreateCommand(connection, updateSql);
                updateCommand.Transaction = transaction;
                updateCommand.Parameters.AddWithValue("@id", testComponentId.Value);
                updateCommand.Parameters.AddWithValue("@suiteId", suiteId);
                updateCommand.Parameters.AddWithValue("@componentId", requested.ComponentId!.Value);
                updateCommand.Parameters.AddWithValue("@projectId", requested.ProjectId!.Value);
                updateCommand.Parameters.AddWithValue("@status", requested.Status ?? true);
                updateCommand.Parameters.AddWithValue("@sortOrder", sortOrder);
                await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            }
            else
            {
                const string insertSql = """
                    INSERT INTO test_components (component_id, project_id, status, test_design_id, sort_order, created_at, updated_at)
                    OUTPUT INSERTED.id
                    VALUES (@componentId, @projectId, @status, @suiteId, @sortOrder, SYSUTCDATETIME(), SYSUTCDATETIME());
                    """;
                await using var insertCommand = CreateCommand(connection, insertSql);
                insertCommand.Transaction = transaction;
                insertCommand.Parameters.AddWithValue("@componentId", requested.ComponentId!.Value);
                insertCommand.Parameters.AddWithValue("@projectId", requested.ProjectId!.Value);
                insertCommand.Parameters.AddWithValue("@status", requested.Status ?? true);
                insertCommand.Parameters.AddWithValue("@suiteId", suiteId);
                insertCommand.Parameters.AddWithValue("@sortOrder", sortOrder);
                testComponentId = Convert.ToInt64(await insertCommand.ExecuteScalarAsync(cancellationToken));
            }

            summaries.Add(new TestSuiteFlowComponentSummaryDto
            {
                ClientKey = requested.ClientKey,
                TestComponentId = testComponentId.Value,
                ComponentId = requested.ComponentId!.Value,
                ProjectId = requested.ProjectId!.Value,
                Status = requested.Status ?? true,
                SortOrder = sortOrder
            });
        }

        await DeleteTestComponentInstancesAsync(connection, transaction, remainingIds, cancellationToken);
        return summaries;
    }

    private async Task DeleteTestComponentInstancesAsync(SqlConnection connection, SqlTransaction transaction, IReadOnlyCollection<long> testComponentIds, CancellationToken cancellationToken)
    {
        foreach (var testComponentId in testComponentIds)
        {
            const string sql = """
                DELETE FROM data_set_steps WHERE dataset_id IN (SELECT id FROM data_sets WHERE test_component_id = @id);
                DELETE FROM data_sets WHERE test_component_id = @id;
                DELETE FROM test_components WHERE id = @id;
                """;
            await using var command = CreateCommand(connection, sql);
            command.Transaction = transaction;
            command.Parameters.AddWithValue("@id", testComponentId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task TouchTestSuitesAsync(SqlConnection connection, SqlTransaction transaction, RequestContext context, ClaimsPrincipal principal, IReadOnlyList<long> suiteIds, CancellationToken cancellationToken)
    {
        var parameters = new List<SqlParameter>();
        var placeholders = AddIdListParameters(parameters, "@suiteId", suiteIds);
        var sql = $"""
            UPDATE test_designs
            SET updated_by_id = @updatedById,
                updated_by = @updatedBy,
                updated_at = SYSUTCDATETIME()
            WHERE id IN ({string.Join(", ", placeholders)});
            """;
        var userDisplayName = NormalizeOptionalText(GetUserDisplayName(principal)) ?? $"User {context.UserId}";
        await using var command = CreateCommand(connection, sql);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@updatedById", context.UserId);
        command.Parameters.AddWithValue("@updatedBy", $"{userDisplayName} ({DateTime.Now:M-d-yy h:mm tt})");
        AddParameters(command, parameters);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<bool> HasDataSetSortOrderColumnAsync(SqlConnection connection, SqlTransaction? transaction, CancellationToken cancellationToken)
    {
        if (_hasDataSetSortOrderColumn.HasValue)
        {
            return _hasDataSetSortOrderColumn.Value;
        }

        _hasDataSetSortOrderColumn = await HasColumnAsync(connection, "data_sets", "sort_order", transaction, cancellationToken);
        return _hasDataSetSortOrderColumn.Value;
    }

    private async Task<bool> TestComponentBelongsToSuiteAsync(SqlConnection connection, SqlTransaction transaction, long clientId, long suiteId, long testComponentId, CancellationToken cancellationToken)
    {
        const string sql = "SELECT TOP 1 tc.id FROM test_components tc INNER JOIN test_designs td ON td.id=tc.test_design_id WHERE tc.id=@testComponentId AND tc.test_design_id=@suiteId AND td.client_id=@clientId AND td.deleted_at IS NULL AND tc.deleted_at IS NULL;";
        await using var command = CreateCommand(connection, sql);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@testComponentId", testComponentId);
        command.Parameters.AddWithValue("@suiteId", suiteId);
        command.Parameters.AddWithValue("@clientId", clientId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private async Task<bool> DatasetBelongsToSuiteAsync(SqlConnection connection, SqlTransaction transaction, long clientId, long suiteId, long testComponentId, long datasetId, CancellationToken cancellationToken)
    {
        const string sql = "SELECT TOP 1 ds.id FROM data_sets ds INNER JOIN test_components tc ON tc.id=ds.test_component_id INNER JOIN test_designs td ON td.id=tc.test_design_id WHERE ds.id=@datasetId AND ds.test_component_id=@testComponentId AND tc.test_design_id=@suiteId AND td.client_id=@clientId AND ds.deleted_at IS NULL AND tc.deleted_at IS NULL AND td.deleted_at IS NULL;";
        await using var command = CreateCommand(connection, sql);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@datasetId", datasetId);
        command.Parameters.AddWithValue("@testComponentId", testComponentId);
        command.Parameters.AddWithValue("@suiteId", suiteId);
        command.Parameters.AddWithValue("@clientId", clientId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private async Task<long?> GetTestComponentSourceIdAsync(SqlConnection connection, SqlTransaction transaction, long testComponentId, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, "SELECT component_id FROM test_components WHERE id=@id AND deleted_at IS NULL;");
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@id", testComponentId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null || value == DBNull.Value ? null : Convert.ToInt64(value);
    }

    private async Task PersistRequestedDatasetStepsAsync(SqlConnection connection, SqlTransaction transaction, long datasetId, IReadOnlyList<SaveTestSuiteStepRequest> requestedSteps, CancellationToken cancellationToken)
    {
        var map = await LoadDatasetStepsForSyncAsync(connection, transaction, [datasetId], cancellationToken);
        var existing = map.TryGetValue(datasetId, out var rows) ? rows : [];
        var existingByStep = existing.Where(row => row.StepId.HasValue).GroupBy(row => row.StepId!.Value).ToDictionary(group => group.Key, group => group.First());
        var requested = requestedSteps.Where(step => step.Id.HasValue).ToList();
        var requestedIds = requested.Select(step => step.Id!.Value).ToHashSet();
        foreach (var row in existing.Where(row => !row.StepId.HasValue || !requestedIds.Contains(row.StepId.Value) || existingByStep[row.StepId.Value].Id != row.Id))
        {
            await DeleteDatasetStepAsync(connection, transaction, row.Id, cancellationToken);
        }

        foreach (var step in requested)
        {
            var stepId = step.Id!.Value;
            var value = step.Value ?? SkipStepValue;
            var skip = string.Equals(value, SkipStepValue, StringComparison.OrdinalIgnoreCase);
            if (existingByStep.TryGetValue(stepId, out var row))
            {
                var hasOverride = step.Override ?? row.Override;
                var overrideValue = step.Override.HasValue || step.OverrideValue is not null ? step.OverrideValue : row.OverrideValue;
                await UpdateDatasetStepAsync(connection, transaction, row.Id, stepId, step.DisplayId, value, skip, hasOverride, overrideValue, cancellationToken);
            }
            else
            {
                await InsertDatasetStepAsync(connection, transaction, datasetId, stepId, step.DisplayId, value, skip, step.Override == true, step.OverrideValue, cancellationToken);
            }
        }
    }

    private async Task<TestSuiteFullDatasetDto> InsertInitializedDatasetAsync(SqlConnection connection, SqlTransaction transaction, long testComponentId, long componentId, EnsureTestComponentDatasetRequest request, CancellationToken cancellationToken)
    {
        var hasSortOrder = await HasDataSetSortOrderColumnAsync(connection, transaction, cancellationToken);
        var sql = hasSortOrder
            ? "INSERT INTO data_sets(status,scenario,sort_order,test_component_id,created_at,updated_at) OUTPUT INSERTED.id VALUES(@status,@scenario,(SELECT COUNT(*)+1 FROM data_sets WHERE test_component_id=@testComponentId AND deleted_at IS NULL),@testComponentId,SYSUTCDATETIME(),SYSUTCDATETIME());"
            : "INSERT INTO data_sets(status,scenario,test_component_id,created_at,updated_at) OUTPUT INSERTED.id VALUES(@status,@scenario,@testComponentId,SYSUTCDATETIME(),SYSUTCDATETIME());";
        await using var command = CreateCommand(connection, sql);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@status", request.Status ?? false);
        command.Parameters.AddWithValue("@scenario", (object?)NormalizeOptionalText(request.Scenario) ?? DBNull.Value);
        command.Parameters.AddWithValue("@testComponentId", testComponentId);
        var datasetId = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        var steps = request.Steps.Count > 0 ? request.Steps : await LoadDefaultSuiteStepsAsync(connection, transaction, componentId, cancellationToken);
        foreach (var step in steps.Where(step => step.Id.HasValue))
        {
            var value = step.Value ?? SkipStepValue;
            await InsertDatasetStepAsync(connection, transaction, datasetId, step.Id!.Value, step.DisplayId, value, string.Equals(value, SkipStepValue, StringComparison.OrdinalIgnoreCase), step.Override == true, step.OverrideValue, cancellationToken);
        }

        return new TestSuiteFullDatasetDto { Id = datasetId, Scenario = request.Scenario, Status = request.Status ?? false, Steps = [] };
    }

    private async Task<bool> HasTestComponentSortOrderColumnAsync(SqlConnection connection, SqlTransaction? transaction, CancellationToken cancellationToken)
    {
        if (_hasTestComponentSortOrderColumn.HasValue)
        {
            return _hasTestComponentSortOrderColumn.Value;
        }

        _hasTestComponentSortOrderColumn = await HasColumnAsync(connection, "test_components", "sort_order", transaction, cancellationToken);
        return _hasTestComponentSortOrderColumn.Value;
    }

    private async Task EnsureTestComponentSortOrderColumnAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        if (await HasTestComponentSortOrderColumnAsync(connection, transaction: null, cancellationToken))
        {
            return;
        }

        const string sql = "IF COL_LENGTH('test_components', 'sort_order') IS NULL ALTER TABLE test_components ADD sort_order int NULL;";
        await using var command = CreateCommand(connection, sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
        _hasTestComponentSortOrderColumn = true;
    }

    private async Task<SaveTestSuiteResult> SaveTestSuiteInternalAsync(ClaimsPrincipal principal, long? testSuiteId, SaveTestSuiteRequest request, CancellationToken cancellationToken)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue || request.Details is null)
        {
            return new SaveTestSuiteResult
            {
                Outcome = SaveTestSuiteOutcome.InvalidReference,
                ErrorField = "details",
                ErrorMessage = "The details field is required."
            };
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
    await EnsureTestLevelDatasetsSchemaAsync(connection, cancellationToken);
        var validation = await ValidateTestSuiteReferencesAsync(connection, context.ClientId.Value, request, cancellationToken);
        if (validation is not null)
        {
            return validation;
        }

        ComparableSuiteDefinition existingComparable = new([], []);
        List<long> childSuiteIds = [];
        if (testSuiteId.HasValue)
        {
            const string suiteExistsSql = """
                SELECT COUNT(*)
                FROM test_designs
                WHERE id = @testSuiteId AND client_id = @clientId AND deleted_at IS NULL;
                """;
            var exists = await ExecuteCountAsync(connection, suiteExistsSql, [
                new SqlParameter("@testSuiteId", testSuiteId.Value),
                new SqlParameter("@clientId", context.ClientId.Value)
            ], cancellationToken);
            if (exists == 0)
            {
                return new SaveTestSuiteResult { Outcome = SaveTestSuiteOutcome.NotFound };
            }

            existingComparable = await LoadComparableSuiteDefinitionAsync(connection, context.ClientId.Value, testSuiteId.Value, cancellationToken);
            childSuiteIds = await LoadChildSuiteIdsAsync(connection, context.ClientId.Value, testSuiteId.Value, cancellationToken);
        }

        var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            var suiteId = await UpsertTestSuiteAsync(connection, transaction, context, principal, testSuiteId, request, cancellationToken);
            await EnsureDefaultCaptureVariableAsync(connection, transaction, context.ClientId.Value, suiteId, cancellationToken);

            foreach (var childSuiteId in childSuiteIds)
            {
                await UpsertTestSuiteAsync(connection, transaction, context, principal, childSuiteId, request, cancellationToken);
                await EnsureDefaultCaptureVariableAsync(connection, transaction, context.ClientId.Value, childSuiteId, cancellationToken);
            }

            if (testSuiteId.HasValue)
            {
                var requestedComparable = MapComparableDefinition(request.DesignedComponents, request.Datasets);
                if (!AreComparableDefinitionsEqual(existingComparable, requestedComparable))
                {
                    var affectedSuiteIds = new List<long> { suiteId };
                    affectedSuiteIds.AddRange(childSuiteIds);
                    await ResetLinkedPlanStatusesAsync(connection, transaction, affectedSuiteIds, cancellationToken);
                }
            }

            await transaction.CommitAsync(cancellationToken);

            var suitesToSync = new List<long> { suiteId };
            suitesToSync.AddRange(childSuiteIds);
            await QueueAutoSyncTestCaseJobsAsync(connection, context, suitesToSync, request.Details.ProjectId, cancellationToken);

            var saved = await GetTestSuiteFullAsync(principal, suiteId, cancellationToken);
            return new SaveTestSuiteResult
            {
                Outcome = SaveTestSuiteOutcome.Saved,
                TestSuite = saved
            };
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task<long> UpsertTestSuiteAsync(SqlConnection connection, SqlTransaction transaction, RequestContext context, ClaimsPrincipal principal, long? testSuiteId, SaveTestSuiteRequest request, CancellationToken cancellationToken)
    {
        var details = request.Details!;
        var normalizedTags = NormalizeSuiteTags(details.Tags);
        var userDisplayName = NormalizeOptionalText(GetUserDisplayName(principal)) ?? $"User {context.UserId}";
        var updatedBy = $"{userDisplayName} ({DateTime.Now:M-d-yy h:mm tt})";
        long suiteId;

        if (testSuiteId.HasValue)
        {
            const string updateSql = """
                UPDATE test_designs
                SET title = @title,
                    test_state_id = @testStateId,
                    test_suite_type = @testSuiteType,
                    folder_path_id = @folderPathId,
                    project_id = @projectId,
                    azure_iteration_path = @iterationPath,
                    priority = @priority,
                    story_id = @storyId,
                    test_title = @testTitle,
                    tags = @tags,
                    comment = @comment,
                    kba_ready = @kbaReady,
                    training_ready = @trainingReady,
                    release_notes_ready = @releaseNotesReady,
                    updated_by_id = @updatedById,
                    updated_by = @updatedBy,
                    updated_at = SYSUTCDATETIME()
                WHERE id = @id AND client_id = @clientId AND deleted_at IS NULL;
                """;

            await using (var updateCommand = CreateCommand(connection, updateSql))
            {
                updateCommand.Transaction = transaction;
                updateCommand.Parameters.AddWithValue("@id", testSuiteId.Value);
                updateCommand.Parameters.AddWithValue("@clientId", context.ClientId!.Value);
                updateCommand.Parameters.AddWithValue("@title", details.Title!.Trim());
                updateCommand.Parameters.AddWithValue("@testStateId", details.TestStateId!.Value);
                updateCommand.Parameters.AddWithValue("@testSuiteType", details.TestSuiteType!.Value);
                updateCommand.Parameters.AddWithValue("@folderPathId", (object?)details.FolderPathId ?? DBNull.Value);
                updateCommand.Parameters.AddWithValue("@projectId", (object?)details.ProjectId ?? DBNull.Value);
                updateCommand.Parameters.AddWithValue("@iterationPath", (object?)NormalizeOptionalText(details.IterationPath) ?? DBNull.Value);
                updateCommand.Parameters.AddWithValue("@priority", (object?)NormalizeOptionalText(details.Priority) ?? DBNull.Value);
                updateCommand.Parameters.AddWithValue("@storyId", (object?)NormalizeOptionalText(details.StoryId) ?? DBNull.Value);
                updateCommand.Parameters.AddWithValue("@testTitle", (object?)NormalizeOptionalText(details.TestTitle) ?? DBNull.Value);
                updateCommand.Parameters.AddWithValue("@tags", (object?)normalizedTags ?? DBNull.Value);
                updateCommand.Parameters.AddWithValue("@comment", (object?)NormalizeOptionalText(details.Comment) ?? DBNull.Value);
                updateCommand.Parameters.AddWithValue("@kbaReady", details.KbaReady ?? false);
                updateCommand.Parameters.AddWithValue("@trainingReady", details.TrainingReady ?? false);
                updateCommand.Parameters.AddWithValue("@releaseNotesReady", details.ReleaseNotesReady ?? false);
                updateCommand.Parameters.AddWithValue("@updatedById", context.UserId);
                updateCommand.Parameters.AddWithValue("@updatedBy", updatedBy);
                await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            suiteId = testSuiteId.Value;
        }
        else
        {
            const string insertSql = """
                INSERT INTO test_designs (
                    client_id,
                    title,
                    test_state_id,
                    test_suite_type,
                    folder_path_id,
                    project_id,
                    azure_iteration_path,
                    priority,
                    story_id,
                    test_title,
                    tags,
                    comment,
                    kba_ready,
                    training_ready,
                    release_notes_ready,
                    created_by_id,
                    updated_by_id,
                    created_by,
                    updated_by,
                    created_at,
                    updated_at
                )
                OUTPUT INSERTED.id
                VALUES (
                    @clientId,
                    @title,
                    @testStateId,
                    @testSuiteType,
                    @folderPathId,
                    @projectId,
                    @iterationPath,
                    @priority,
                    @storyId,
                    @testTitle,
                    @tags,
                    @comment,
                    @kbaReady,
                    @trainingReady,
                    @releaseNotesReady,
                    @createdById,
                    @updatedById,
                    @createdBy,
                    @updatedBy,
                    SYSUTCDATETIME(),
                    SYSUTCDATETIME()
                );
                """;

            await using var insertCommand = CreateCommand(connection, insertSql);
            insertCommand.Transaction = transaction;
            insertCommand.Parameters.AddWithValue("@clientId", context.ClientId!.Value);
            insertCommand.Parameters.AddWithValue("@title", details.Title!.Trim());
            insertCommand.Parameters.AddWithValue("@testStateId", details.TestStateId!.Value);
            insertCommand.Parameters.AddWithValue("@testSuiteType", details.TestSuiteType!.Value);
            insertCommand.Parameters.AddWithValue("@folderPathId", (object?)details.FolderPathId ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("@projectId", (object?)details.ProjectId ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("@iterationPath", (object?)NormalizeOptionalText(details.IterationPath) ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("@priority", (object?)NormalizeOptionalText(details.Priority) ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("@storyId", (object?)NormalizeOptionalText(details.StoryId) ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("@testTitle", (object?)NormalizeOptionalText(details.TestTitle) ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("@tags", (object?)normalizedTags ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("@comment", (object?)NormalizeOptionalText(details.Comment) ?? DBNull.Value);
            insertCommand.Parameters.AddWithValue("@kbaReady", details.KbaReady ?? false);
            insertCommand.Parameters.AddWithValue("@trainingReady", details.TrainingReady ?? false);
            insertCommand.Parameters.AddWithValue("@releaseNotesReady", details.ReleaseNotesReady ?? false);
            insertCommand.Parameters.AddWithValue("@createdById", context.UserId);
            insertCommand.Parameters.AddWithValue("@updatedById", context.UserId);
            insertCommand.Parameters.AddWithValue("@createdBy", userDisplayName);
            insertCommand.Parameters.AddWithValue("@updatedBy", updatedBy);
            suiteId = Convert.ToInt64(await insertCommand.ExecuteScalarAsync(cancellationToken));
        }

        await ReplaceTestSuiteComponentsAsync(connection, transaction, suiteId, request.DesignedComponents, cancellationToken);
        await ReplaceTestSuiteDatasetsAsync(connection, transaction, suiteId, request.Datasets, cancellationToken);
        return suiteId;
    }

    private async Task EnsureTestLevelDatasetsSchemaAsync(SqlConnection connection, CancellationToken cancellationToken, SqlTransaction? transaction = null)
    {
        const string sql = """
            IF OBJECT_ID(N'dbo.test_design_datasets', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.test_design_datasets
                (
                    id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    test_design_id BIGINT NOT NULL,
                    sort_order INT NULL,
                    scenario NVARCHAR(1000) NULL,
                    status BIT NOT NULL CONSTRAINT DF_test_design_datasets_status DEFAULT (0),
                    created_at DATETIME2(7) NOT NULL CONSTRAINT DF_test_design_datasets_created_at DEFAULT SYSUTCDATETIME(),
                    updated_at DATETIME2(7) NOT NULL CONSTRAINT DF_test_design_datasets_updated_at DEFAULT SYSUTCDATETIME(),
                    deleted_at DATETIME2(7) NULL
                );
            END;

            IF OBJECT_ID(N'dbo.test_design_dataset_steps', N'U') IS NULL
            BEGIN
                CREATE TABLE dbo.test_design_dataset_steps
                (
                    id BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    dataset_id BIGINT NOT NULL,
                    step_id BIGINT NULL,
                    display INT NULL,
                    skip_step BIT NOT NULL CONSTRAINT DF_test_design_dataset_steps_skip_step DEFAULT (0),
                    step_info NVARCHAR(MAX) NULL,
                    [override] BIT NOT NULL CONSTRAINT DF_test_design_dataset_steps_override DEFAULT (0),
                    override_value NVARCHAR(MAX) NULL,
                    created_at DATETIME2(7) NOT NULL CONSTRAINT DF_test_design_dataset_steps_created_at DEFAULT SYSUTCDATETIME(),
                    updated_at DATETIME2(7) NOT NULL CONSTRAINT DF_test_design_dataset_steps_updated_at DEFAULT SYSUTCDATETIME()
                );
            END;

            IF NOT EXISTS (
                SELECT 1
                FROM sys.indexes
                WHERE name = N'IX_test_design_datasets_test_design_id'
                  AND object_id = OBJECT_ID(N'dbo.test_design_datasets')
            )
            BEGIN
                CREATE INDEX IX_test_design_datasets_test_design_id
                    ON dbo.test_design_datasets (test_design_id, sort_order, id)
                    WHERE deleted_at IS NULL;
            END;

            IF NOT EXISTS (
                SELECT 1
                FROM sys.indexes
                WHERE name = N'IX_test_design_dataset_steps_dataset_id'
                  AND object_id = OBJECT_ID(N'dbo.test_design_dataset_steps')
            )
            BEGIN
                CREATE INDEX IX_test_design_dataset_steps_dataset_id
                    ON dbo.test_design_dataset_steps (dataset_id, display, id);
            END;
            """;

        await using var command = CreateCommand(connection, sql);
        command.Transaction = transaction;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ReplaceTestSuiteComponentsAsync(SqlConnection connection, SqlTransaction transaction, long suiteId, IReadOnlyList<SaveTestSuiteComponentRequest> components, CancellationToken cancellationToken)
    {
        var hasComponentSortOrder = await HasTestComponentSortOrderColumnAsync(connection, transaction, cancellationToken);
        var hasDatasetSortOrder = await HasDataSetSortOrderColumnAsync(connection, transaction, cancellationToken);
        const string deleteStepsSql = """
            DELETE FROM data_set_steps
            WHERE dataset_id IN (
                SELECT ds.id
                FROM data_sets ds
                INNER JOIN test_components tc ON tc.id = ds.test_component_id
                WHERE tc.test_design_id = @testSuiteId
            );
            """;
        const string deleteDatasetsSql = """
            DELETE FROM data_sets
            WHERE test_component_id IN (
                SELECT id
                FROM test_components
                WHERE test_design_id = @testSuiteId
            );
            """;
        const string deleteComponentsSql = "DELETE FROM test_components WHERE test_design_id = @testSuiteId;";

        foreach (var sql in new[] { deleteStepsSql, deleteDatasetsSql, deleteComponentsSql })
        {
            await using var deleteCommand = CreateCommand(connection, sql);
            deleteCommand.Transaction = transaction;
            deleteCommand.Parameters.AddWithValue("@testSuiteId", suiteId);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        for (var componentIndex = 0; componentIndex < components.Count; componentIndex++)
        {
            var component = components[componentIndex];
            var insertComponentSql = $"""
                INSERT INTO test_components (component_id, project_id, status, test_design_id{(hasComponentSortOrder ? ", sort_order" : string.Empty)}, created_at, updated_at)
                OUTPUT INSERTED.id
                VALUES (@componentId, @projectId, @status, @testDesignId{(hasComponentSortOrder ? ", @sortOrder" : string.Empty)}, SYSUTCDATETIME(), SYSUTCDATETIME());
                """;

            long testComponentId;
            await using (var componentCommand = CreateCommand(connection, insertComponentSql))
            {
                componentCommand.Transaction = transaction;
                componentCommand.Parameters.AddWithValue("@componentId", component.ComponentId!.Value);
                componentCommand.Parameters.AddWithValue("@projectId", component.ProjectId!.Value);
                componentCommand.Parameters.AddWithValue("@status", component.Status ?? true);
                componentCommand.Parameters.AddWithValue("@testDesignId", suiteId);
                if (hasComponentSortOrder)
                {
                    componentCommand.Parameters.AddWithValue("@sortOrder", componentIndex);
                }
                testComponentId = Convert.ToInt64(await componentCommand.ExecuteScalarAsync(cancellationToken));
            }

            for (var datasetIndex = 0; datasetIndex < component.Datasets.Count; datasetIndex++)
            {
                var dataset = component.Datasets[datasetIndex];
                var insertDatasetSql = $"""
                    INSERT INTO data_sets (status, scenario, test_component_id{(hasDatasetSortOrder ? ", sort_order" : string.Empty)}, created_at, updated_at)
                    OUTPUT INSERTED.id
                    VALUES (@status, @scenario, @testComponentId{(hasDatasetSortOrder ? ", @sortOrder" : string.Empty)}, SYSUTCDATETIME(), SYSUTCDATETIME());
                    """;

                long datasetId;
                await using (var datasetCommand = CreateCommand(connection, insertDatasetSql))
                {
                    datasetCommand.Transaction = transaction;
                    datasetCommand.Parameters.AddWithValue("@status", dataset.Status ?? false);
                    datasetCommand.Parameters.AddWithValue("@scenario", (object?)NormalizeOptionalText(dataset.Scenario) ?? DBNull.Value);
                    datasetCommand.Parameters.AddWithValue("@testComponentId", testComponentId);
                    if (hasDatasetSortOrder)
                    {
                        datasetCommand.Parameters.AddWithValue("@sortOrder", datasetIndex);
                    }
                    datasetId = Convert.ToInt64(await datasetCommand.ExecuteScalarAsync(cancellationToken));
                }

                var datasetSteps = dataset.Steps.Count > 0
                    ? dataset.Steps
                    : await LoadDefaultSuiteStepsAsync(connection, transaction, component.ComponentId!.Value, cancellationToken);

                foreach (var step in datasetSteps)
                {
                    var stepInfo = new JsonObject
                    {
                        ["display_id"] = step.DisplayId,
                        ["id"] = step.Id,
                        ["value"] = step.Value
                    };

                    var hasOverride = step.Override == true;
                    if (hasOverride)
                    {
                        stepInfo["override"] = true;
                        stepInfo["override_value"] = step.OverrideValue;
                    }

                    const string insertStepSql = """
                        INSERT INTO data_set_steps (dataset_id, step_id, display, skip_step, step_info, [override], override_value, created_at, updated_at)
                        VALUES (@datasetId, @stepId, @display, @skipStep, @stepInfo, @override, @overrideValue, SYSUTCDATETIME(), SYSUTCDATETIME());
                        """;

                    await using var stepCommand = CreateCommand(connection, insertStepSql);
                    stepCommand.Transaction = transaction;
                    stepCommand.Parameters.AddWithValue("@datasetId", datasetId);
                    stepCommand.Parameters.AddWithValue("@stepId", step.Id!.Value);
                    stepCommand.Parameters.AddWithValue("@display", step.DisplayId!.Value);
                    stepCommand.Parameters.AddWithValue("@skipStep", string.Equals(step.Value, SkipStepValue, StringComparison.OrdinalIgnoreCase));
                    stepCommand.Parameters.AddWithValue("@stepInfo", stepInfo.ToJsonString());
                    stepCommand.Parameters.AddWithValue("@override", hasOverride);
                    stepCommand.Parameters.AddWithValue("@overrideValue", hasOverride && step.OverrideValue is not null ? step.OverrideValue : DBNull.Value);
                    await stepCommand.ExecuteNonQueryAsync(cancellationToken);
                }
            }
        }
    }

    private async Task ReplaceTestSuiteDatasetsAsync(SqlConnection connection, SqlTransaction transaction, long suiteId, IReadOnlyList<SaveTestSuiteDatasetRequest> datasets, CancellationToken cancellationToken)
    {
        const string deleteStepsSql = """
            DELETE FROM test_design_dataset_steps
            WHERE dataset_id IN (
                SELECT id
                FROM test_design_datasets
                WHERE test_design_id = @testSuiteId
            );
            """;
        const string deleteDatasetsSql = "DELETE FROM test_design_datasets WHERE test_design_id = @testSuiteId;";

        foreach (var sql in new[] { deleteStepsSql, deleteDatasetsSql })
        {
            await using var deleteCommand = CreateCommand(connection, sql);
            deleteCommand.Transaction = transaction;
            deleteCommand.Parameters.AddWithValue("@testSuiteId", suiteId);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        for (var datasetIndex = 0; datasetIndex < datasets.Count; datasetIndex++)
        {
            var dataset = datasets[datasetIndex];
            const string insertDatasetSql = """
                INSERT INTO test_design_datasets (test_design_id, sort_order, scenario, status, created_at, updated_at)
                OUTPUT INSERTED.id
                VALUES (@testDesignId, @sortOrder, @scenario, @status, SYSUTCDATETIME(), SYSUTCDATETIME());
                """;

            long datasetId;
            await using (var datasetCommand = CreateCommand(connection, insertDatasetSql))
            {
                datasetCommand.Transaction = transaction;
                datasetCommand.Parameters.AddWithValue("@testDesignId", suiteId);
                datasetCommand.Parameters.AddWithValue("@sortOrder", dataset.SortOrder ?? datasetIndex + 1);
                datasetCommand.Parameters.AddWithValue("@scenario", (object?)NormalizeOptionalText(dataset.Scenario) ?? DBNull.Value);
                datasetCommand.Parameters.AddWithValue("@status", dataset.Status ?? false);
                datasetId = Convert.ToInt64(await datasetCommand.ExecuteScalarAsync(cancellationToken));
            }

            var datasetSteps = dataset.Steps.Count > 0
                ? dataset.Steps
                : await LoadDefaultTestSuiteStepsAsync(connection, transaction, suiteId, cancellationToken);

            foreach (var step in datasetSteps.Where(step => step.Id.HasValue))
            {
                var stepInfo = new JsonObject
                {
                    ["display_id"] = step.DisplayId,
                    ["id"] = step.Id,
                    ["value"] = step.Value
                };

                var hasOverride = step.Override == true;
                if (hasOverride)
                {
                    stepInfo["override"] = true;
                    stepInfo["override_value"] = step.OverrideValue;
                }

                const string insertStepSql = """
                    INSERT INTO test_design_dataset_steps (dataset_id, step_id, display, skip_step, step_info, [override], override_value, created_at, updated_at)
                    VALUES (@datasetId, @stepId, @display, @skipStep, @stepInfo, @override, @overrideValue, SYSUTCDATETIME(), SYSUTCDATETIME());
                    """;

                await using var stepCommand = CreateCommand(connection, insertStepSql);
                stepCommand.Transaction = transaction;
                stepCommand.Parameters.AddWithValue("@datasetId", datasetId);
                stepCommand.Parameters.AddWithValue("@stepId", step.Id!.Value);
                stepCommand.Parameters.AddWithValue("@display", (object?)step.DisplayId ?? DBNull.Value);
                stepCommand.Parameters.AddWithValue("@skipStep", string.Equals(step.Value, SkipStepValue, StringComparison.OrdinalIgnoreCase));
                stepCommand.Parameters.AddWithValue("@stepInfo", stepInfo.ToJsonString());
                stepCommand.Parameters.AddWithValue("@override", hasOverride);
                stepCommand.Parameters.AddWithValue("@overrideValue", hasOverride && step.OverrideValue is not null ? step.OverrideValue : DBNull.Value);
                await stepCommand.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }

    private async Task<IReadOnlyList<SaveTestSuiteStepRequest>> LoadDefaultTestSuiteStepsAsync(SqlConnection connection, SqlTransaction transaction, long suiteId, CancellationToken cancellationToken)
    {
        var hasComponentSortOrder = await HasTestComponentSortOrderColumnAsync(connection, transaction, cancellationToken);
        var sql = $"""
            SELECT cs.id, cs.display_id
            FROM test_components tc
            INNER JOIN component_steps cs ON cs.component_id = tc.component_id
            WHERE tc.test_design_id = @suiteId
              AND tc.deleted_at IS NULL
              AND cs.deleted_at IS NULL
            ORDER BY {(hasComponentSortOrder ? "ISNULL(tc.sort_order, 2147483647), " : string.Empty)}ISNULL(cs.display_id, 2147483647), cs.id;
            """;

        var rows = new List<SaveTestSuiteStepRequest>();
        await using var command = CreateCommand(connection, sql);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@suiteId", suiteId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new SaveTestSuiteStepRequest
            {
                DisplayId = GetInt32(reader, "display_id"),
                Id = GetInt64(reader, "id"),
                Value = SkipStepValue,
                Override = false,
                OverrideValue = null
            });
        }

        return rows;
    }

    private async Task<IReadOnlyList<TestSuiteFullDatasetDto>> LoadTestDesignDatasetsAsync(SqlConnection connection, long testSuiteId, CancellationToken cancellationToken)
    {
        var sql = """
            SELECT id, sort_order, scenario, CAST(ISNULL(status, 0) AS bit) AS status
            FROM test_design_datasets
            WHERE test_design_id = @testSuiteId
              AND deleted_at IS NULL
            ORDER BY ISNULL(sort_order, 2147483647), id;
            """;

        var datasetRows = new List<(long Id, int? SortOrder, string? Scenario, bool Status)>();
        await using (var command = CreateCommand(connection, sql))
        {
            command.Parameters.AddWithValue("@testSuiteId", testSuiteId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                datasetRows.Add((
                    reader.GetInt64(reader.GetOrdinal("id")),
                    GetInt32(reader, "sort_order"),
                    GetString(reader, "scenario"),
                    GetBoolean(reader, "status") ?? false));
            }
        }

        if (datasetRows.Count == 0)
        {
            return [];
        }

        var stepMap = await LoadTestDesignDatasetStepsMapAsync(connection, datasetRows.Select(row => row.Id).ToArray(), cancellationToken);
        return datasetRows.Select(row => new TestSuiteFullDatasetDto
        {
            Id = row.Id,
            SortOrder = row.SortOrder,
            Scenario = row.Scenario,
            Status = row.Status,
            Steps = stepMap.TryGetValue(row.Id, out var steps) ? steps : []
        }).ToList();
    }

    private async Task<Dictionary<long, IReadOnlyList<TestSuiteFullDatasetStepDto>>> LoadTestDesignDatasetStepsMapAsync(SqlConnection connection, IReadOnlyList<long> datasetIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, IReadOnlyList<TestSuiteFullDatasetStepDto>>();
        if (datasetIds.Count == 0)
        {
            return result;
        }

        var parameters = new List<SqlParameter>();
        var placeholders = AddIdListParameters(parameters, "@datasetId", datasetIds);
        var sql = $"""
            SELECT id, dataset_id, display, skip_step, step_info, step_id, [override], override_value
            FROM test_design_dataset_steps
            WHERE dataset_id IN ({string.Join(", ", placeholders)})
            ORDER BY dataset_id, ISNULL(display, 2147483647), id;
            """;

        var grouped = new Dictionary<long, List<TestSuiteFullDatasetStepDto>>();
        await using var command = CreateCommand(connection, sql);
        AddParameters(command, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var datasetId = reader.GetInt64(reader.GetOrdinal("dataset_id"));
            if (!grouped.TryGetValue(datasetId, out var rows))
            {
                rows = [];
                grouped[datasetId] = rows;
            }

            var stepInfoJson = GetString(reader, "step_info");
            rows.Add(new TestSuiteFullDatasetStepDto
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                DatasetId = datasetId,
                DisplayId = GetInt32(reader, "display"),
                SkipStep = GetBoolean(reader, "skip_step") ?? false,
                StepId = GetInt64(reader, "step_id"),
                InternalStepId = GetInt64(reader, "step_id"),
                Value = ExtractValueFromStepInfo(stepInfoJson),
                Override = GetBoolean(reader, "override"),
                OverrideValue = GetString(reader, "override_value"),
                StepInfo = ParseJsonElementOrDefault(stepInfoJson, new { })
            });
        }

        foreach (var pair in grouped)
        {
            result[pair.Key] = pair.Value;
        }

        return result;
    }

    private static string? ExtractValueFromStepInfo(string? stepInfoJson)
    {
        if (string.IsNullOrWhiteSpace(stepInfoJson))
        {
            return null;
        }

        try
        {
            var node = JsonNode.Parse(stepInfoJson) as JsonObject;
            if (node is null)
            {
                return null;
            }

            return node["value"]?.GetValue<string?>();
        }
        catch
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<SaveTestSuiteStepRequest>> LoadDefaultSuiteStepsAsync(SqlConnection connection, SqlTransaction transaction, long componentId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, display_id
            FROM component_steps
            WHERE component_id = @componentId AND deleted_at IS NULL
            ORDER BY ISNULL(display_id, 2147483647), id;
            """;

        var rows = new List<SaveTestSuiteStepRequest>();
        await using var command = CreateCommand(connection, sql);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@componentId", componentId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new SaveTestSuiteStepRequest
            {
                DisplayId = GetInt32(reader, "display_id"),
                Id = GetInt64(reader, "id"),
                Value = SkipStepValue,
                Override = false,
                OverrideValue = null
            });
        }

        return rows;
    }

    private async Task EnsureDefaultCaptureVariableAsync(SqlConnection connection, SqlTransaction transaction, long clientId, long testSuiteId, CancellationToken cancellationToken)
    {
        const string upsertSql = """
            IF EXISTS (
                SELECT 1
                FROM custom_variables
                WHERE name = @name AND test_case_id = @testCaseId AND client_id = @clientId
            )
            BEGIN
                UPDATE custom_variables
                SET variable_id = @variableId,
                    value = NULL,
                    is_encrypted = 0,
                    updated_at = SYSUTCDATETIME()
                WHERE name = @name AND test_case_id = @testCaseId AND client_id = @clientId;
            END
            ELSE
            BEGIN
                INSERT INTO custom_variables (name, variable_id, client_id, test_case_id, value, is_encrypted, created_at, updated_at)
                VALUES (@name, @variableId, @clientId, @testCaseId, NULL, 0, SYSUTCDATETIME(), SYSUTCDATETIME());
            END
            """;

        await using var command = CreateCommand(connection, upsertSql);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@name", DefaultCaptureVariableName);
        command.Parameters.AddWithValue("@variableId", DefaultCaptureVariableTypeId);
        command.Parameters.AddWithValue("@clientId", clientId);
        command.Parameters.AddWithValue("@testCaseId", testSuiteId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task CloneLocalVariablesAsync(long sourceTestSuiteId, long clonedTestSuiteId, long clientId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            const string upsertSql = """
                MERGE custom_variables AS target
                USING (
                    SELECT
                        name,
                        variable_id,
                        value,
                        is_encrypted
                    FROM custom_variables
                    WHERE client_id = @clientId
                      AND test_case_id = @sourceTestCaseId
                ) AS source
                ON target.client_id = @clientId
                   AND target.test_case_id = @clonedTestCaseId
                   AND target.name = source.name
                WHEN MATCHED THEN
                    UPDATE SET
                        variable_id = source.variable_id,
                        value = source.value,
                        is_encrypted = source.is_encrypted,
                        updated_at = SYSUTCDATETIME()
                WHEN NOT MATCHED BY TARGET THEN
                    INSERT (name, variable_id, client_id, test_case_id, value, is_encrypted, created_at, updated_at)
                    VALUES (source.name, source.variable_id, @clientId, @clonedTestCaseId, source.value, source.is_encrypted, SYSUTCDATETIME(), SYSUTCDATETIME());
                """;

            await using var command = CreateCommand(connection, upsertSql);
            command.Transaction = transaction;
            command.Parameters.AddWithValue("@clientId", clientId);
            command.Parameters.AddWithValue("@sourceTestCaseId", sourceTestSuiteId);
            command.Parameters.AddWithValue("@clonedTestCaseId", clonedTestSuiteId);
            await command.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task ResetLinkedPlanStatusesAsync(SqlConnection connection, SqlTransaction transaction, IReadOnlyList<long> suiteIds, CancellationToken cancellationToken)
    {
        if (suiteIds.Count == 0)
        {
            return;
        }

        var parameters = new List<SqlParameter>();
        var placeholders = AddIdListParameters(parameters, "@suiteId", suiteIds);
        var updateSql = $"""
            UPDATE test_plan_item_suites
            SET status_id = @statusId,
                updated_at = SYSUTCDATETIME()
            WHERE deleted_at IS NULL AND test_design_id IN ({string.Join(", ", placeholders)});
            """;

        await using (var updateCommand = CreateCommand(connection, updateSql))
        {
            updateCommand.Transaction = transaction;
            updateCommand.Parameters.AddWithValue("@statusId", NotStartedStatusId);
            AddParameters(updateCommand, parameters);
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        var deleteSql = $"""
            DELETE FROM test_runner_items
            WHERE status_id = @statusId AND test_suite_id IN ({string.Join(", ", placeholders)});
            """;

        await using var deleteCommand = CreateCommand(connection, deleteSql);
        deleteCommand.Transaction = transaction;
        deleteCommand.Parameters.AddWithValue("@statusId", InProgressStatusId);
        AddParameters(deleteCommand, parameters.Select(parameter => new SqlParameter(parameter.ParameterName, parameter.Value)).ToList());
        await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<List<long>> LoadChildSuiteIdsAsync(SqlConnection connection, long clientId, long testSuiteId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id
            FROM test_designs
            WHERE parent_id = @testSuiteId AND client_id = @clientId AND deleted_at IS NULL
            ORDER BY id;
            """;

        var rows = new List<long>();
        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@testSuiteId", testSuiteId);
        command.Parameters.AddWithValue("@clientId", clientId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(reader.GetInt64(reader.GetOrdinal("id")));
        }

        return rows;
    }

    private async Task<SaveTestSuiteResult?> ValidateTestSuiteReferencesAsync(SqlConnection connection, long clientId, SaveTestSuiteRequest request, CancellationToken cancellationToken)
    {
        if (request.Details?.TestStateId is not long testStateId)
        {
            return new SaveTestSuiteResult
            {
                Outcome = SaveTestSuiteOutcome.InvalidReference,
                ErrorField = "details.test_state_id",
                ErrorMessage = "The test_state_id field is required."
            };
        }

        var testStateCount = await ExecuteCountAsync(connection, "SELECT COUNT(*) FROM test_states WHERE id = @id;", [new SqlParameter("@id", testStateId)], cancellationToken);
        if (testStateCount == 0)
        {
            return new SaveTestSuiteResult
            {
                Outcome = SaveTestSuiteOutcome.InvalidReference,
                ErrorField = "details.test_state_id",
                ErrorMessage = "The selected test_state_id is invalid."
            };
        }

        if (request.Details.FolderPathId.HasValue)
        {
            var folderCount = await ExecuteCountAsync(connection, "SELECT COUNT(*) FROM folder_paths WHERE id = @id AND client_id = @clientId;", [
                new SqlParameter("@id", request.Details.FolderPathId.Value),
                new SqlParameter("@clientId", clientId)
            ], cancellationToken);
            if (folderCount == 0)
            {
                return new SaveTestSuiteResult
                {
                    Outcome = SaveTestSuiteOutcome.InvalidReference,
                    ErrorField = "details.folder_path_id",
                    ErrorMessage = "The selected folder_path_id is invalid."
                };
            }
        }

        var projectIds = new List<long>();
        if (request.Details.ProjectId.HasValue)
        {
            projectIds.Add(request.Details.ProjectId.Value);
        }

        foreach (var component in request.DesignedComponents)
        {
            if (!component.ComponentId.HasValue)
            {
                return new SaveTestSuiteResult
                {
                    Outcome = SaveTestSuiteOutcome.InvalidReference,
                    ErrorField = "designed_components.component_id",
                    ErrorMessage = "The component_id field is required."
                };
            }

            if (!component.ProjectId.HasValue)
            {
                return new SaveTestSuiteResult
                {
                    Outcome = SaveTestSuiteOutcome.InvalidReference,
                    ErrorField = "designed_components.project_id",
                    ErrorMessage = "The project_id field is required."
                };
            }

            projectIds.Add(component.ProjectId.Value);

            foreach (var dataset in component.Datasets)
            {
                foreach (var step in dataset.Steps)
                {
                    if (!step.DisplayId.HasValue)
                    {
                        return new SaveTestSuiteResult
                        {
                            Outcome = SaveTestSuiteOutcome.InvalidReference,
                            ErrorField = "designed_components.steps.display_id",
                            ErrorMessage = "The display_id field is required."
                        };
                    }

                    if (!step.Id.HasValue)
                    {
                        return new SaveTestSuiteResult
                        {
                            Outcome = SaveTestSuiteOutcome.InvalidReference,
                            ErrorField = "designed_components.steps.id",
                            ErrorMessage = "The id field is required."
                        };
                    }
                }
            }
        }

        foreach (var dataset in request.Datasets)
        {
            foreach (var step in dataset.Steps)
            {
                if (!step.DisplayId.HasValue)
                {
                    return new SaveTestSuiteResult
                    {
                        Outcome = SaveTestSuiteOutcome.InvalidReference,
                        ErrorField = "datasets.steps.display_id",
                        ErrorMessage = "The display_id field is required."
                    };
                }

                if (!step.Id.HasValue)
                {
                    return new SaveTestSuiteResult
                    {
                        Outcome = SaveTestSuiteOutcome.InvalidReference,
                        ErrorField = "datasets.steps.id",
                        ErrorMessage = "The id field is required."
                    };
                }
            }
        }

        var distinctProjectIds = projectIds.Distinct().ToArray();
        if (distinctProjectIds.Length > 0)
        {
            var parameters = new List<SqlParameter> { new("@clientId", clientId) };
            var placeholders = AddIdListParameters(parameters, "@projectId", distinctProjectIds);
            var projectSql = $"SELECT COUNT(*) FROM projects WHERE client_id = @clientId AND id IN ({string.Join(", ", placeholders)});";
            var validProjects = await ExecuteCountAsync(connection, projectSql, parameters, cancellationToken);
            if (validProjects != distinctProjectIds.Length)
            {
                return new SaveTestSuiteResult
                {
                    Outcome = SaveTestSuiteOutcome.InvalidReference,
                    ErrorField = "designed_components.project_id",
                    ErrorMessage = "The selected project_id is invalid."
                };
            }
        }

        var componentIds = request.DesignedComponents
            .Select(component => component.ComponentId!.Value)
            .Distinct()
            .ToArray();
        if (componentIds.Length > 0)
        {
            var parameters = new List<SqlParameter> { new("@clientId", clientId) };
            var placeholders = AddIdListParameters(parameters, "@componentId", componentIds);
            var componentSql = $"SELECT COUNT(*) FROM components WHERE client_id = @clientId AND deleted_at IS NULL AND id IN ({string.Join(", ", placeholders)});";
            var validComponents = await ExecuteCountAsync(connection, componentSql, parameters, cancellationToken);
            if (validComponents != componentIds.Length)
            {
                return new SaveTestSuiteResult
                {
                    Outcome = SaveTestSuiteOutcome.InvalidReference,
                    ErrorField = "designed_components.component_id",
                    ErrorMessage = "The selected component_id is invalid."
                };
            }
        }

        var requestedStepPairs = request.DesignedComponents
            .SelectMany(component => component.Datasets.SelectMany(dataset => dataset.Steps.Select(step => (ComponentId: component.ComponentId!.Value, StepId: step.Id!.Value))))
            .Distinct()
            .ToArray();
        if (requestedStepPairs.Length > 0)
        {
            var stepIds = requestedStepPairs.Select(pair => pair.StepId).Distinct().ToArray();
            var stepParameters = new List<SqlParameter>();
            var componentPlaceholders = AddIdListParameters(stepParameters, "@componentId", componentIds);
            var stepPlaceholders = AddIdListParameters(stepParameters, "@stepId", stepIds);
            var stepSql = $"""
                SELECT component_id, id
                FROM component_steps
                WHERE deleted_at IS NULL
                  AND component_id IN ({string.Join(", ", componentPlaceholders)})
                  AND id IN ({string.Join(", ", stepPlaceholders)});
                """;

            var validPairs = new HashSet<string>(StringComparer.Ordinal);
            await using var stepCommand = CreateCommand(connection, stepSql);
            AddParameters(stepCommand, stepParameters);
            await using var reader = await stepCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var componentId = GetInt64(reader, "component_id");
                var stepId = GetInt64(reader, "id");
                if (componentId.HasValue && stepId.HasValue)
                {
                    validPairs.Add($"{componentId.Value}:{stepId.Value}");
                }
            }

            foreach (var pair in requestedStepPairs)
            {
                if (!validPairs.Contains($"{pair.ComponentId}:{pair.StepId}"))
                {
                    return new SaveTestSuiteResult
                    {
                        Outcome = SaveTestSuiteOutcome.InvalidReference,
                        ErrorField = "designed_components.steps.id",
                        ErrorMessage = "The selected step id is invalid."
                    };
                }
            }
        }

        return null;
    }

    private async Task<string?> ValidateOverrideValueAsync(SqlConnection connection, string value, CancellationToken cancellationToken)
    {
        var overrideValues = SplitOverrideSegmentsRuntime(value);

        var beforeAfterNames = await LoadBeforeAfterStepNamesAsync(connection, cancellationToken);
        var allowedNonHelperKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "xpath",
            "description",
            "expected_output",
            "keyword",
            "value",
            "action"
        };

        foreach (var overrideSegment in overrideValues)
        {
            if (!TrySplitOverridePairRuntime(overrideSegment, out var rawKey, out var rawValue))
            {
                continue;
            }

            var normalizedKey = NormalizeOverrideKeyRuntime(rawKey);
            if (!IsSupportedOverrideKey(normalizedKey))
            {
                return $"{rawKey.Trim().Replace('_', ' ')} Not Found";
            }

            if (normalizedKey == "keyword")
            {
                var keywordName = rawValue.Trim();
                if (!string.IsNullOrWhiteSpace(keywordName))
                {
                    const string keywordSql = """
                        SELECT CASE WHEN EXISTS (SELECT 1 FROM component_keywords WHERE name = @name)
                                     OR EXISTS (SELECT 1 FROM global_keywords WHERE name = @name)
                            THEN 1 ELSE 0 END;
                        """;
                    await using var keywordCommand = CreateCommand(connection, keywordSql);
                    keywordCommand.Parameters.AddWithValue("@name", keywordName);
                    var exists = Convert.ToInt32(await keywordCommand.ExecuteScalarAsync(cancellationToken)) == 1;
                    if (!exists)
                    {
                        return $"Keyword {keywordName} Not Found";
                    }
                }
            }
            else if (normalizedKey is "before_step" or "after_step")
            {
                foreach (var token in SplitBeforeAfterHelperSegments(rawValue))
                {
                    if (!TrySplitBeforeAfterHelperToken(token, out var helperName, out _))
                    {
                        return $"Invalid {normalizedKey} helper token: {token}";
                    }

                    var normalizedHelperName = helperName.Trim();
                    if (!beforeAfterNames.Contains(normalizedHelperName) && !allowedNonHelperKeys.Contains(normalizedHelperName))
                    {
                        return $"{normalizedHelperName.Replace('_', ' ')} Not Found";
                    }
                }
            }
        }

        return null;
    }

    public async Task<string?> ValidateComponentHelperSyntaxAsync(ClaimsPrincipal principal, IReadOnlyList<SaveComponentStepRequest> steps, CancellationToken cancellationToken = default)
    {
        var context = GetRequestContext(principal);
        if (!context.ClientId.HasValue)
        {
            return "Unable to validate component helpers.";
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var beforeAfterNames = await LoadBeforeAfterStepNamesAsync(connection, cancellationToken);
        var allowedNonHelperKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "xpath",
            "description",
            "expected_output",
            "keyword",
            "value",
            "action"
        };

        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            var beforeError = ValidateBeforeAfterValueSet(step.BeforeStep, "before_step", beforeAfterNames, allowedNonHelperKeys);
            if (beforeError is not null)
            {
                return $"Step {index + 1} before_step: {beforeError}";
            }

            var afterError = ValidateBeforeAfterValueSet(step.AfterStep, "after_step", beforeAfterNames, allowedNonHelperKeys);
            if (afterError is not null)
            {
                return $"Step {index + 1} after_step: {afterError}";
            }
        }

        return null;
    }

    private static string? ValidateBeforeAfterValueSet(
        IReadOnlyList<string>? values,
        string fieldName,
        HashSet<string> beforeAfterNames,
        HashSet<string> allowedNonHelperKeys)
    {
        if (values is null || values.Count == 0)
        {
            return null;
        }

        foreach (var rawValue in values)
        {
            var trimmed = NormalizeOptionalText(rawValue);
            if (string.IsNullOrWhiteSpace(trimmed) || string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (trimmed.Contains(";;", StringComparison.Ordinal))
            {
                return $"Invalid {fieldName} separator: use ';' between helper functions.";
            }

            foreach (var token in SplitBeforeAfterHelperSegments(trimmed))
            {
                if (!TrySplitBeforeAfterHelperToken(token, out var helperName, out var helperValue))
                {
                    return $"Invalid {fieldName} helper token: {token}";
                }

                var normalizedHelperName = helperName.Trim();
                if (!beforeAfterNames.Contains(normalizedHelperName) &&
                    !allowedNonHelperKeys.Contains(normalizedHelperName) &&
                    !BuiltInBeforeAfterHelperNames.Contains(normalizedHelperName))
                {
                    return $"{normalizedHelperName.Replace('_', ' ')} Not Found";
                }

                if (string.Equals(normalizedHelperName, "sendkey", StringComparison.OrdinalIgnoreCase) &&
                    !IsSupportedSendKeyAction(helperValue))
                {
                    return $"Invalid {fieldName} sendkey value: {helperValue}.";
                }

                if (string.Equals(normalizedHelperName, "waitforelement", StringComparison.OrdinalIgnoreCase) &&
                    !IsSupportedWaitForElementValue(helperValue))
                {
                    return $"Invalid {fieldName} waitForElement value: {helperValue}.";
                }

                if (string.Equals(normalizedHelperName, "waitfortext", StringComparison.OrdinalIgnoreCase) &&
                    !IsSupportedWaitForTextValue(helperValue))
                {
                    return $"Invalid {fieldName} waitForText value: {helperValue}.";
                }
            }
        }

        return null;
    }

    private static IReadOnlyList<string> SplitOverrideSegmentsRuntime(string raw)
    {
        if (raw.Contains(";;", StringComparison.Ordinal))
        {
            return raw.Split(";;", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        if (raw.Contains("<=>", StringComparison.Ordinal))
        {
            return raw.Split("<=>", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        return [raw.Trim()];
    }

    private static bool IsSupportedOverrideKey(string normalizedKey)
    {
        return normalizedKey is
            "xpath" or
            "description" or
            "expected_output" or
            "keyword" or
            "value" or
            "before_step" or
            "after_step";
    }

    private static readonly ImmutableHashSet<string> BuiltInBeforeAfterHelperNames =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "sendkey", "waitforelement", "waitfortext", "visible");

    private static readonly ImmutableHashSet<string> SupportedSendKeyActions =
        ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "selectall",
            "tab",
            "clear",
            "escape",
            "esc",
            "home",
            "backspace",
            "enter",
            "keyup",
            "keydown",
            "arrowdown",
            "arrowup",
            "click",
            "focusout",
            "dismissalert",
            "acceptalert",
            "hover");

    private static bool IsSupportedSendKeyAction(string? helperValue)
    {
        var entries = ParseNamedHelperValue(helperValue);
        if (entries.Count == 0)
        {
            return false;
        }

        string? action = null;
        foreach (var (key, value) in entries)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                action = value.Trim().TrimStart(':');
                continue;
            }

            if (key is not ("locator" or "target" or "scope" or "xpath"))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }
        }

        return !string.IsNullOrWhiteSpace(action) && SupportedSendKeyActions.Contains(action);
    }

    private static readonly ImmutableHashSet<string> SupportedWaitForElementStates =
        ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "exist",
            "notexist",
            "visible",
            "hidden",
            "enabled",
            "disabled",
            "selected",
            "notselected");

    private static readonly ImmutableHashSet<string> SupportedWaitForTextMatches =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "contains", "exact");

    private static IReadOnlyList<(string Key, string Value)> ParseNamedHelperValue(string? helperValue)
    {
        var normalized = NormalizeOptionalText(helperValue);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return [];
        }

        return SplitTopLevelDelimitedSegments(normalized, ">>")
            .Select(part =>
            {
                var splitIndex = part.IndexOf('=');
                if (splitIndex <= 0)
                {
                    return (string.Empty, part.Trim());
                }

                return (part[..splitIndex].Trim().ToLowerInvariant(), part[(splitIndex + 1)..].Trim());
            })
            .ToArray();
    }

    private static bool IsSupportedWaitForElementValue(string? helperValue)
    {
        var entries = ParseNamedHelperValue(helperValue);
        if (entries.Count == 0)
        {
            return false;
        }

        string? state = null;
        foreach (var (key, value) in entries)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                state = value.Trim();
                continue;
            }

            if (key is not ("state" or "timeout" or "target" or "scope" or "xpath"))
            {
                return false;
            }

            if (key is "target" or "scope" or "xpath")
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return false;
                }

                continue;
            }

            if (key == "timeout")
            {
                if (!int.TryParse(value, out var timeout) || timeout <= 0)
                {
                    return false;
                }

                continue;
            }

            if (key == "state")
            {
                state = value.Trim();
            }
        }

        return !string.IsNullOrWhiteSpace(state) && SupportedWaitForElementStates.Contains(state);
    }

    private static bool IsSupportedWaitForTextValue(string? helperValue)
    {
        var entries = ParseNamedHelperValue(helperValue);
        if (entries.Count == 0)
        {
            return false;
        }

        string? text = null;
        var match = "contains";
        foreach (var (key, value) in entries)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                text = value.Trim();
                continue;
            }

            if (key is not ("text" or "scope" or "target" or "xpath" or "match" or "timeout"))
            {
                return false;
            }

            if (key is "text" or "scope" or "target" or "xpath")
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    return false;
                }

                if (key == "text")
                {
                    text = value.Trim();
                }

                continue;
            }

            if (key == "match")
            {
                match = value.Trim();
                continue;
            }

            if (key == "timeout" && (!int.TryParse(value, out var timeout) || timeout <= 0))
            {
                return false;
            }
        }

        return !string.IsNullOrWhiteSpace(text) && SupportedWaitForTextMatches.Contains(match);
    }

    private static bool TrySplitOverridePairRuntime(string segment, out string key, out string value)
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

    private static string NormalizeOverrideKeyRuntime(string key)
    {
        return key.Trim().ToLowerInvariant() switch
        {
            "stepdesc" => "description",
            "description" => "description",
            "expected" => "expected_output",
            "expected_output" => "expected_output",
            "locator" => "xpath",
            "xpath" => "xpath",
            "beforestep" => "before_step",
            "before_step" => "before_step",
            "afterstep" => "after_step",
            "after_step" => "after_step",
            "keyword" => "keyword",
            "stepdata" => "value",
            "value" => "value",
            "action" => "value",
            _ => key.Trim().ToLowerInvariant()
        };
    }

    private async Task<HashSet<string>> LoadBeforeAfterStepNamesAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await EnsureBuiltInBeforeAfterStepsAsync(connection, cancellationToken);
        const string sql = "SELECT name FROM before_after_steps ORDER BY id;";
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = CreateCommand(connection, sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = GetString(reader, "name");
            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name.Trim());
            }
        }

        return names;
    }

    private async Task EnsureBuiltInBeforeAfterStepsAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        foreach (var helperName in BuiltInBeforeAfterHelperNames)
        {
            const string sql = """
                IF NOT EXISTS (SELECT 1 FROM before_after_steps WHERE name = @name)
                BEGIN
                    INSERT INTO before_after_steps (name, field, type, rules, created_at, updated_at)
                    VALUES (@name, 0, NULL, NULL, SYSUTCDATETIME(), SYSUTCDATETIME())
                END
                """;

            await using var command = CreateCommand(connection, sql);
            command.Parameters.AddWithValue("@name", helperName);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task<ComparableSuiteDefinition> LoadComparableSuiteDefinitionAsync(SqlConnection connection, long clientId, long testSuiteId, CancellationToken cancellationToken)
    {
        var components = await LoadFullTestSuiteComponentsAsync(connection, clientId, testSuiteId, cancellationToken);
        var datasets = await LoadTestDesignDatasetsAsync(connection, testSuiteId, cancellationToken);
        return new ComparableSuiteDefinition(
            components.Select(component => new ComparableSuiteComponent(
            component.ComponentId,
            component.ProjectId,
            component.Datasets.Select(dataset => new ComparableSuiteDataset(
                NormalizeOptionalText(dataset.Scenario),
                dataset.Status,
                dataset.Steps.Select(step => new ComparableSuiteStep(
                    step.DisplayId,
                    step.StepId,
                    step.Value,
                    step.Override ?? false,
                    step.OverrideValue)).ToArray())).ToArray())).ToArray(),
            datasets.Select(dataset => new ComparableSuiteDataset(
                NormalizeOptionalText(dataset.Scenario),
                dataset.Status,
                dataset.Steps.Select(step => new ComparableSuiteStep(
                    step.DisplayId,
                    step.StepId,
                    step.Value,
                    step.Override ?? false,
                    step.OverrideValue)).ToArray())).ToArray());
    }

    private static ComparableSuiteDefinition MapComparableDefinition(IReadOnlyList<SaveTestSuiteComponentRequest> components, IReadOnlyList<SaveTestSuiteDatasetRequest> datasets)
    {
        return new ComparableSuiteDefinition(
            components.Select(component => new ComparableSuiteComponent(
                component.ComponentId,
                component.ProjectId,
                component.Datasets.Select(dataset => new ComparableSuiteDataset(
                    NormalizeOptionalText(dataset.Scenario),
                    dataset.Status ?? false,
                    dataset.Steps.Select(step => new ComparableSuiteStep(
                        step.DisplayId,
                        step.Id,
                        step.Value,
                        step.Override ?? false,
                        step.OverrideValue)).ToArray())).ToArray())).ToArray(),
            datasets.Select(dataset => new ComparableSuiteDataset(
                NormalizeOptionalText(dataset.Scenario),
                dataset.Status ?? false,
                dataset.Steps.Select(step => new ComparableSuiteStep(
                    step.DisplayId,
                    step.Id,
                    step.Value,
                    step.Override ?? false,
                    step.OverrideValue)).ToArray())).ToArray());
    }

    private static bool AreComparableDefinitionsEqual(ComparableSuiteDefinition left, ComparableSuiteDefinition right)
    {
        return AreComparableComponentsEqual(left.Components, right.Components)
            && AreComparableDatasetsEqual(left.Datasets, right.Datasets);
    }

    private static bool AreComparableComponentsEqual(IReadOnlyList<ComparableSuiteComponent> left, IReadOnlyList<ComparableSuiteComponent> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var componentIndex = 0; componentIndex < left.Count; componentIndex++)
        {
            var leftComponent = left[componentIndex];
            var rightComponent = right[componentIndex];
            if (leftComponent.ComponentId != rightComponent.ComponentId || leftComponent.ProjectId != rightComponent.ProjectId)
            {
                return false;
            }

            if (leftComponent.Datasets.Count != rightComponent.Datasets.Count)
            {
                return false;
            }

            for (var datasetIndex = 0; datasetIndex < leftComponent.Datasets.Count; datasetIndex++)
            {
                var leftDataset = leftComponent.Datasets[datasetIndex];
                var rightDataset = rightComponent.Datasets[datasetIndex];
                if (!string.Equals(leftDataset.Scenario, rightDataset.Scenario, StringComparison.Ordinal) || leftDataset.Status != rightDataset.Status)
                {
                    return false;
                }

                if (leftDataset.Steps.Count != rightDataset.Steps.Count)
                {
                    return false;
                }

                for (var stepIndex = 0; stepIndex < leftDataset.Steps.Count; stepIndex++)
                {
                    var leftStep = leftDataset.Steps[stepIndex];
                    var rightStep = rightDataset.Steps[stepIndex];
                    if (leftStep.DisplayId != rightStep.DisplayId
                        || leftStep.StepId != rightStep.StepId
                        || !string.Equals(leftStep.Value, rightStep.Value, StringComparison.Ordinal)
                        || leftStep.Override != rightStep.Override
                        || !string.Equals(leftStep.OverrideValue, rightStep.OverrideValue, StringComparison.Ordinal))
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    private static bool AreComparableDatasetsEqual(IReadOnlyList<ComparableSuiteDataset> left, IReadOnlyList<ComparableSuiteDataset> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var datasetIndex = 0; datasetIndex < left.Count; datasetIndex++)
        {
            var leftDataset = left[datasetIndex];
            var rightDataset = right[datasetIndex];
            if (!string.Equals(leftDataset.Scenario, rightDataset.Scenario, StringComparison.Ordinal) || leftDataset.Status != rightDataset.Status)
            {
                return false;
            }

            if (leftDataset.Steps.Count != rightDataset.Steps.Count)
            {
                return false;
            }

            for (var stepIndex = 0; stepIndex < leftDataset.Steps.Count; stepIndex++)
            {
                var leftStep = leftDataset.Steps[stepIndex];
                var rightStep = rightDataset.Steps[stepIndex];
                if (leftStep.DisplayId != rightStep.DisplayId
                    || leftStep.StepId != rightStep.StepId
                    || !string.Equals(leftStep.Value, rightStep.Value, StringComparison.Ordinal)
                    || leftStep.Override != rightStep.Override
                    || !string.Equals(leftStep.OverrideValue, rightStep.OverrideValue, StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static string? NormalizeSuiteTags(JsonElement? tagsElement)
    {
        if (!tagsElement.HasValue || tagsElement.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        static IEnumerable<string> Clean(IEnumerable<string> tags)
        {
            var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "kba", "training", "release" };
            return tags
                .Select(tag => tag.Trim())
                .Where(tag => !string.IsNullOrWhiteSpace(tag) && !blocked.Contains(tag))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        if (tagsElement.Value.ValueKind == JsonValueKind.Array)
        {
            var tags = Clean(tagsElement.Value.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : item.ToString())).ToArray();
            return tags.Any() ? JsonSerializer.Serialize(tags) : null;
        }

        if (tagsElement.Value.ValueKind == JsonValueKind.String)
        {
            var raw = tagsElement.Value.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            try
            {
                if (raw.TrimStart().StartsWith("[", StringComparison.Ordinal))
                {
                    var parsed = JsonSerializer.Deserialize<string[]>(raw);
                    var tags = Clean(parsed ?? Array.Empty<string>()).ToArray();
                    return tags.Length > 0 ? JsonSerializer.Serialize(tags) : null;
                }
            }
            catch
            {
            }

            var split = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var cleaned = Clean(split).ToArray();
            return cleaned.Length > 0 ? JsonSerializer.Serialize(cleaned) : null;
        }

        return null;
    }

    private static IReadOnlyList<long> ParseLongJsonArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<long>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private async Task<IReadOnlyList<PermissionGroupDto>> LoadRolePermissionsAsync(SqlConnection connection, long roleId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT p.category, p.name
            FROM role_has_permissions rhp
            INNER JOIN permissions p ON p.id = rhp.permission_id
            WHERE rhp.role_id = @roleId
            ORDER BY p.category, p.name;
            """;

        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@roleId", roleId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        return await ReadPermissionGroupsAsync(reader, cancellationToken);
    }

    private static async Task<IReadOnlyList<PermissionGroupDto>> ReadPermissionGroupsAsync(SqlDataReader reader, CancellationToken cancellationToken)
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
        {
            var module = GetString(reader, "category") ?? "General";
            var permission = GetString(reader, "name");
            if (string.IsNullOrWhiteSpace(permission))
            {
                continue;
            }

            if (!groups.TryGetValue(module, out var permissions))
            {
                permissions = [];
                groups[module] = permissions;
            }

            permissions.Add(permission);
        }

        return groups.Select(pair => new PermissionGroupDto
        {
            Module = pair.Key,
            Permissions = pair.Value
        }).ToList();
    }

    private static bool IsUniqueConstraintViolation(SqlException exception)
    {
        return exception.Number is 2601 or 2627;
    }

    private async Task<Dictionary<long, IReadOnlyList<UserRoleDto>>> LoadUserRolesAsync(SqlConnection connection, IReadOnlyList<long> userIds, CancellationToken cancellationToken)
    {
        var result = new Dictionary<long, IReadOnlyList<UserRoleDto>>();
        if (userIds.Count == 0)
        {
            return result;
        }

        var parameters = new List<SqlParameter> { new("@modelType", _settings.UserModelType) };
        var userIdPlaceholders = AddIdListParameters(parameters, "@userId", userIds);
        var sql = $"""
            SELECT mhr.model_id, r.id, r.name
            FROM model_has_roles mhr
            INNER JOIN roles r ON r.id = mhr.role_id
            WHERE REPLACE(mhr.model_type, '\\', '') = REPLACE(@modelType, '\\', '')
              AND mhr.model_id IN ({string.Join(", ", userIdPlaceholders)})
            ORDER BY r.id;
            """;

        await using var command = CreateCommand(connection, sql);
        AddParameters(command, parameters);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var buffer = new Dictionary<long, List<UserRoleDto>>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var userId = reader.GetInt64(reader.GetOrdinal("model_id"));
            if (!buffer.TryGetValue(userId, out var roles))
            {
                roles = [];
                buffer[userId] = roles;
            }

            roles.Add(new UserRoleDto
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                Name = GetString(reader, "name") ?? string.Empty
            });
        }

        foreach (var pair in buffer)
        {
            result[pair.Key] = pair.Value;
        }

        return result;
    }

    private static PagedDataDto<T> CreatePagedData<T>(IReadOnlyList<T> rows, int total, int limit)
    {
        return new PagedDataDto<T>
        {
            Data = rows,
            Meta = new PaginationMetaDto
            {
                Total = total,
                Count = rows.Count,
                PerPage = limit
            }
        };
    }

    private static IReadOnlyList<string> ParseStringArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            if (json.TrimStart().StartsWith("[", StringComparison.Ordinal))
            {
                return JsonSerializer.Deserialize<List<string>>(json) ?? [];
            }
        }
        catch
        {
            return [json];
        }

        return [json];
    }

    private static JsonElement ParseJsonElement(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return JsonSerializer.SerializeToElement(new { });
        }

        try
        {
            return JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch
        {
            return JsonSerializer.SerializeToElement(new { });
        }
    }

    private static string? GetJsonStringProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => property.GetString(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            JsonValueKind.Number => property.GetRawText(),
            _ => property.GetRawText()
        };
    }

    private static JsonElement ParseJsonElementOrDefault<T>(string? json, T fallback)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return JsonSerializer.SerializeToElement(fallback);
        }

        try
        {
            return JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch
        {
            return JsonSerializer.SerializeToElement(fallback);
        }
    }

    private static JsonElement? ParseNullableJsonElement(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch
        {
            return null;
        }
    }

    private async Task<BeforeAfterStepDto?> GetBeforeAfterStepByIdAsync(SqlConnection connection, long id, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT id, name, CAST(ISNULL(field, 0) AS bit) AS field, type, rules
            FROM before_after_steps
            WHERE id = @id;
            """;

        long? stepId = null;
        string? name = null;
        bool field = false;
        string? type = null;
        string? rulesJson = null;

        await using (var command = CreateCommand(connection, sql))
        {
            command.Parameters.AddWithValue("@id", id);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            stepId = reader.GetInt64(reader.GetOrdinal("id"));
            name = GetString(reader, "name") ?? string.Empty;
            field = GetBoolean(reader, "field") ?? false;
            type = GetString(reader, "type");
            rulesJson = GetString(reader, "rules");
        }

        return new BeforeAfterStepDto
        {
            Id = stepId.Value,
            Name = name ?? string.Empty,
            Field = field,
            Type = type,
            Rules = ParseNullableJsonElement(rulesJson),
            UsageCount = await GetBeforeAfterStepUsageCountAsync(connection, name, cancellationToken)
        };
    }

    private async Task<int> GetBeforeAfterStepUsageCountAsync(SqlConnection connection, string? name, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return 0;
        }

        const string sql = """
            SELECT before_step, after_step, CAST(NULL AS nvarchar(max)) AS override_value
            FROM component_steps
            WHERE deleted_at IS NULL

            UNION ALL

            SELECT CAST(NULL AS nvarchar(max)) AS before_step, CAST(NULL AS nvarchar(max)) AS after_step, dss.override_value
            FROM data_set_steps dss
            INNER JOIN data_sets ds ON ds.id = dss.dataset_id AND ds.deleted_at IS NULL
            INNER JOIN test_components tc ON tc.id = ds.test_component_id AND tc.deleted_at IS NULL
            INNER JOIN test_designs td ON td.id = tc.test_design_id AND td.deleted_at IS NULL
            WHERE ISNULL(dss.[override], 0) = 1
              AND dss.override_value IS NOT NULL

            UNION ALL

            SELECT CAST(NULL AS nvarchar(max)) AS before_step, CAST(NULL AS nvarchar(max)) AS after_step, tdds.override_value
            FROM test_design_dataset_steps tdds
            INNER JOIN test_design_datasets tdd ON tdd.id = tdds.dataset_id
            INNER JOIN test_designs td ON td.id = tdd.test_design_id AND td.deleted_at IS NULL
            WHERE ISNULL(tdds.[override], 0) = 1
              AND tdds.override_value IS NOT NULL;
            """;

        await using var command = CreateCommand(connection, sql);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var normalizedName = name.Trim();
        var usageCount = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            var beforeStep = GetString(reader, "before_step");
            var afterStep = GetString(reader, "after_step");
            var overrideValue = GetString(reader, "override_value");

            if (ContainsHelperNameInBaseValue(beforeStep, normalizedName)
                || ContainsHelperNameInBaseValue(afterStep, normalizedName)
                || ContainsHelperNameInOverrideValue(overrideValue, normalizedName))
            {
                usageCount += 1;
            }
        }

        return usageCount;
    }

    private static string EscapeSqlLikeValue(string value)
    {
        return value
            .Replace("[", "[[]", StringComparison.Ordinal)
            .Replace("%", "[%]", StringComparison.Ordinal)
            .Replace("_", "[_]", StringComparison.Ordinal);
    }

    private static bool ContainsHelperNameInBaseValue(string? value, string helperName)
    {
        var normalizedHelperName = NormalizeOptionalText(helperName);
        if (string.IsNullOrWhiteSpace(normalizedHelperName))
        {
            return false;
        }

        foreach (var token in SplitBeforeAfterHelperSegments(value ?? string.Empty))
        {
            if (!TrySplitBeforeAfterHelperToken(token, out var parsedHelperName, out _))
            {
                continue;
            }

            if (string.Equals(NormalizeOptionalText(parsedHelperName), normalizedHelperName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsHelperNameInOverrideValue(string? overrideValue, string helperName)
    {
        var normalizedHelperName = NormalizeOptionalText(helperName);
        if (string.IsNullOrWhiteSpace(normalizedHelperName) || string.IsNullOrWhiteSpace(overrideValue))
        {
            return false;
        }

        foreach (var segment in SplitOverrideSegmentsRuntime(overrideValue))
        {
            if (!TrySplitOverridePairRuntime(segment, out var rawKey, out var rawValue))
            {
                continue;
            }

            var normalizedKey = NormalizeOverrideKeyRuntime(rawKey);
            if (normalizedKey is not ("before_step" or "after_step"))
            {
                continue;
            }

            if (ContainsHelperNameInBaseValue(rawValue, normalizedHelperName))
            {
                return true;
            }
        }

        return false;
    }

    private static string? GetDateString(SqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateOnly dateOnly => dateOnly.ToString("yyyy-MM-dd"),
            DateTime dateTime => dateTime.ToString("yyyy-MM-dd"),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("yyyy-MM-dd"),
            _ => Convert.ToString(value)
        };
    }

    private static IReadOnlyList<long> ParseLongCsv(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            try
            {
                var items = JsonSerializer.Deserialize<List<long>>(trimmed);
                return items?.Distinct().ToArray() ?? [];
            }
            catch
            {
                return [];
            }
        }

        return trimmed
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => long.TryParse(item, out var parsed) ? parsed : (long?)null)
            .Where(item => item.HasValue)
            .Select(item => item!.Value)
            .Distinct()
            .ToArray();
    }

    private async Task<SqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            throw new InvalidOperationException("ConnectionStrings:SqlServer is not configured.");
        }

        var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private SqlCommand CreateCommand(SqlConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = _settings.CommandTimeoutSeconds;
        return command;
    }

    private static void AddParameters(SqlCommand command, IEnumerable<SqlParameter> parameters)
    {
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.ParameterName, parameter.Value ?? DBNull.Value);
        }
    }

    private async Task<int> ExecuteCountAsync(SqlConnection connection, string sql, IEnumerable<SqlParameter> parameters, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, sql);
        AddParameters(command, parameters);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(value);
    }

    private static void ApplyRoleScope(RequestContext context, List<string> whereClauses, List<SqlParameter> parameters)
    {
        if (context.ClientId.HasValue)
        {
            whereClauses.Add("r.client_id = @clientId");
            parameters.Add(new SqlParameter("@clientId", context.ClientId.Value));
            return;
        }

        whereClauses.Add("r.client_id IS NULL");
        whereClauses.Add("r.name LIKE 'Platform%'");
    }

    private static void AddCsvFilter(string? csv, string columnName, string parameterBaseName, List<string> whereClauses, List<SqlParameter> parameters)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return;
        }

        var values = csv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => long.TryParse(value, out var parsed) ? parsed : (long?)null)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .Distinct()
            .ToArray();

        if (values.Length == 0)
        {
            return;
        }

        var placeholders = AddIdListParameters(parameters, $"@{parameterBaseName}", values);
        whereClauses.Add($"{columnName} IN ({string.Join(", ", placeholders)})");
    }

    private static void AddOptionalStringFilter(string? value, string columnName, string parameterName, List<string> whereClauses, List<SqlParameter> parameters)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        whereClauses.Add($"{columnName} = {parameterName}");
        parameters.Add(new SqlParameter(parameterName, value.Trim()));
    }

    private static void AddOptionalInt64Filter(long? value, string columnName, string parameterName, List<string> whereClauses, List<SqlParameter> parameters)
    {
        if (!value.HasValue)
        {
            return;
        }

        whereClauses.Add($"{columnName} = {parameterName}");
        parameters.Add(new SqlParameter(parameterName, value.Value));
    }

    private static IReadOnlyList<string> AddIdListParameters(List<SqlParameter> parameters, string baseName, IReadOnlyList<long> values)
    {
        var placeholders = new List<string>();
        for (var index = 0; index < values.Count; index++)
        {
            var name = $"{baseName}{index}";
            parameters.Add(new SqlParameter(name, values[index]));
            placeholders.Add(name);
        }

        return placeholders;
    }

    private static IReadOnlyList<string> AddIntListParameters(List<SqlParameter> parameters, string baseName, IReadOnlyList<int> values)
    {
        var placeholders = new List<string>();
        for (var index = 0; index < values.Count; index++)
        {
            var name = $"{baseName}{index}";
            parameters.Add(new SqlParameter(name, values[index]));
            placeholders.Add(name);
        }

        return placeholders;
    }

    private static RequestContext GetRequestContext(ClaimsPrincipal principal)
    {
        var userId = GetClaimInt64(principal, ClaimTypes.NameIdentifier)
            ?? GetClaimInt64(principal, "sub")
            ?? throw new InvalidOperationException("Authenticated user id is missing.");

        var clientId = GetClaimInt64(principal, "client_id");
        return new RequestContext(userId, clientId);
    }

    private static long? GetClaimInt64(ClaimsPrincipal principal, string claimType)
    {
        var value = principal.FindFirstValue(claimType);
        return long.TryParse(value, out var parsed) ? parsed : null;
    }

    private static string? GetString(SqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static string? NormalizeOptionalText(string? value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string? NormalizeAzureOption(string? value)
    {
        var text = NormalizeOptionalText(value);
        return string.Equals(text, "Azure", StringComparison.OrdinalIgnoreCase) ? "Azure" : null;
    }

    private static string NormalizeVersion(string? value)
    {
        return value?.Trim() ?? string.Empty;
    }

    private static string? GetUserDisplayName(ClaimsPrincipal principal)
    {
        return principal.FindFirstValue(ClaimTypes.Name)
            ?? principal.FindFirstValue("unique_name")
            ?? principal.FindFirstValue(ClaimTypes.Email)
            ?? principal.Identity?.Name;
    }

    private static bool? GetBoolean(SqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetBoolean(ordinal);
    }

    private static long? GetInt64(SqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            long longValue => longValue,
            int intValue => intValue,
            decimal decimalValue => Convert.ToInt64(decimalValue),
            _ => Convert.ToInt64(value)
        };
    }

    private static int? GetInt32(SqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return Convert.ToInt32(reader.GetValue(ordinal));
    }

    private static DateTimeOffset? GetDateTimeOffset(SqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateTimeOffset dto => dto,
            DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
            _ => null
        };
    }

    private static DefectUserRefDto? BuildDefectUser(SqlDataReader reader, string idColumn, string nameColumn, string emailColumn)
    {
        var id = GetInt64(reader, idColumn);
        if (!id.HasValue)
        {
            return null;
        }

        return new DefectUserRefDto
        {
            Id = id.Value,
            Name = GetString(reader, nameColumn),
            Email = GetString(reader, emailColumn)
        };
    }

    private static DefectStatusDto? BuildDefectStatus(SqlDataReader reader, string idColumn, string nameColumn)
    {
        var id = GetInt64(reader, idColumn);
        if (!id.HasValue)
        {
            return null;
        }

        return new DefectStatusDto
        {
            Id = id.Value,
            Name = GetString(reader, nameColumn) ?? string.Empty
        };
    }

    private readonly record struct RequestContext(long UserId, long? ClientId);

    private readonly record struct UserRecord(long Id, bool IsActive);

    private readonly record struct ComparableSuiteDefinition(IReadOnlyList<ComparableSuiteComponent> Components, IReadOnlyList<ComparableSuiteDataset> Datasets);

    private readonly record struct ComparableSuiteComponent(long? ComponentId, long? ProjectId, IReadOnlyList<ComparableSuiteDataset> Datasets);

    private readonly record struct ComparableSuiteDataset(string? Scenario, bool Status, IReadOnlyList<ComparableSuiteStep> Steps);

    private readonly record struct ComparableSuiteStep(int? DisplayId, long? StepId, string? Value, bool Override, string? OverrideValue);

    private readonly record struct RunnerVariableValue(string? Value, string? ExecutableMethod, bool IsEncrypted);

    private readonly record struct RunnerVariableMaps(
        Dictionary<string, RunnerVariableValue> Global,
        Dictionary<long, Dictionary<string, RunnerVariableValue>> Local);

    private readonly record struct RunnerItemRecord(
        long RunnerItemId,
        long TestRunnerId,
        long TestSuiteId,
        long TestPlanItemId,
        string? TestSuiteName,
        string? TestPlanItemName,
        string? StepsJson,
        string? VideosJson);

    private sealed class RunnerStepUpdateResult
    {
        public string StepsJson { get; init; } = "[]";

        public int AcceptedCount { get; init; }

        public int MatchedCount { get; init; }

        public int UpdatedCount { get; init; }
    }

    private sealed class OverrideParts
    {
        public RunnerKeywordDto? Keyword { get; set; }

        public string? Description { get; set; }

        public string? ExpectedOutput { get; set; }

        public string? XPath { get; set; }

        public IReadOnlyList<Dictionary<string, string>>? BeforeStep { get; set; }

        public IReadOnlyList<Dictionary<string, string>>? AfterStep { get; set; }
    }

    private const int NotStartedStatusId = 1;
    private const int InProgressStatusId = 2;
    private const int PassedStatusId = 3;
    private const int FailedStatusId = 4;
    private const int GlitchStatusId = 5;
    private const int RetestStatusId = 6;
    private const int FixedDefectStatusId = 3;
    private const string SkipStepValue = "skip";
    private const string DefaultCaptureVariableName = "{{u_capture}}";
    private const int DefaultCaptureVariableTypeId = 1;
    private const string XPathReplaceVariable = "{{var}}";
    private const string LowercaseChars = "abcdefghijklmnopqrstuvwxyz";
}
