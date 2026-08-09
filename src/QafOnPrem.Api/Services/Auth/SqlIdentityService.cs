using System.Data;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using QafOnPrem.Api.Configuration;
using QafOnPrem.Api.Contracts.Auth;

namespace QafOnPrem.Api.Services.Auth;

public sealed class SqlIdentityService(
    IConfiguration configuration,
    IOptions<SqlIdentitySettings> settings,
    ITokenFactory tokenFactory) : ISqlIdentityService
{
    private readonly string _connectionString = configuration.GetConnectionString("SqlServer") ?? string.Empty;
    private readonly SqlIdentitySettings _settings = settings.Value;
    private readonly ITokenFactory _tokenFactory = tokenFactory;

    public async Task<AuthenticationResult> AuthenticateClientAsync(string email, string password, string? ipAddress, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var user = await LoadUserByEmailAsync(connection, email, cancellationToken);
        if (user is null || string.IsNullOrWhiteSpace(user.PasswordHash))
        {
            return AuthenticationResult.Failure(StatusCodes.Status401Unauthorized, "Invalid Username or password!");
        }

        if (!BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
        {
            return AuthenticationResult.Failure(StatusCodes.Status401Unauthorized, "Invalid Username or password!");
        }

        if (!user.IsActive)
        {
            return AuthenticationResult.Failure(StatusCodes.Status403Forbidden, "User account is inactive.");
        }

        if (!user.ClientId.HasValue)
        {
            return AuthenticationResult.Failure(StatusCodes.Status403Forbidden, "Please use the admin portal to sign in.");
        }

        if (string.Equals(user.ClientStatus, "disabled", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(user.AccountDisableReason, "manual_admin_disable", StringComparison.OrdinalIgnoreCase))
            {
                return AuthenticationResult.Failure(StatusCodes.Status403Forbidden, "Client account is disabled by administrator.");
            }

            if (user.IsClient != 1)
            {
                return AuthenticationResult.Failure(StatusCodes.Status403Forbidden, "Client account is disabled.");
            }
        }

        var securityError = ValidateClientSecurity(user, ipAddress);
        if (securityError is not null)
        {
            return AuthenticationResult.Failure(StatusCodes.Status403Forbidden, securityError);
        }

        var role = await LoadPrimaryRoleAsync(connection, user.Id, cancellationToken);
        var token = _tokenFactory.CreateToken(new TokenSubject(
            user.Id,
            user.Email,
            user.Name,
            user.ClientId,
            user.IsClient,
            user.ClientStatus ?? "active",
            role));

        var currentUser = await BuildCurrentUserAsync(connection, user, token, cancellationToken, role);

        return AuthenticationResult.Success("User Login Successfully", currentUser);
    }

    public async Task<CurrentUserDto?> GetCurrentUserAsync(ClaimsPrincipal principal, string? bearerToken, CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(principal);
        if (!userId.HasValue)
        {
            return null;
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var user = await LoadUserByIdAsync(connection, userId.Value, cancellationToken);
        if (user is null)
        {
            return null;
        }

        return await BuildCurrentUserAsync(connection, user, bearerToken ?? string.Empty, cancellationToken);
    }

    public async Task<IReadOnlyList<PermissionGroupDto>> GetPermissionsAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        var userId = GetUserId(principal);
        if (!userId.HasValue)
        {
            return [];
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await LoadPermissionsAsync(connection, userId.Value, cancellationToken);
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

    private async Task<SqlIdentityUser?> LoadUserByEmailAsync(SqlConnection connection, string email, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP 1
                u.id,
                u.name,
                u.email,
                u.phone,
                u.job_title,
                u.department,
                u.timezone,
                u.avatar_path,
                u.client_id,
                CAST(ISNULL(u.is_client, 0) AS int) AS is_client,
                CAST(ISNULL(u.is_active, 1) AS bit) AS is_active,
                CAST(ISNULL(u.mfa_enabled, 0) AS bit) AS mfa_enabled,
                CAST(ISNULL(u.sso_enabled, 0) AS bit) AS sso_enabled,
                CAST(ISNULL(u.must_reset_password, 0) AS bit) AS must_reset_password,
                u.password,
                u.email_verified_at,
                u.deleted_at,
                u.created_at,
                u.updated_at,
                c.account_status,
                c.max_users,
                CAST(ISNULL(c.mfa_required, 0) AS bit) AS mfa_required,
                CAST(ISNULL(c.sso_required, 0) AS bit) AS sso_required,
                c.ip_allowlist_json,
                c.account_disable_reason
            FROM users u
            LEFT JOIN clients c ON c.id = u.client_id
            WHERE LOWER(u.email) = LOWER(@email) AND u.deleted_at IS NULL
            ORDER BY u.id;
            """;

        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@email", email);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapUser(reader) : null;
    }

    private async Task<SqlIdentityUser?> LoadUserByIdAsync(SqlConnection connection, int userId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP 1
                u.id,
                u.name,
                u.email,
                u.phone,
                u.job_title,
                u.department,
                u.timezone,
                u.avatar_path,
                u.client_id,
                CAST(ISNULL(u.is_client, 0) AS int) AS is_client,
                CAST(ISNULL(u.is_active, 1) AS bit) AS is_active,
                CAST(ISNULL(u.mfa_enabled, 0) AS bit) AS mfa_enabled,
                CAST(ISNULL(u.sso_enabled, 0) AS bit) AS sso_enabled,
                CAST(ISNULL(u.must_reset_password, 0) AS bit) AS must_reset_password,
                u.password,
                u.email_verified_at,
                u.deleted_at,
                u.created_at,
                u.updated_at,
                c.account_status,
                c.max_users,
                CAST(ISNULL(c.mfa_required, 0) AS bit) AS mfa_required,
                CAST(ISNULL(c.sso_required, 0) AS bit) AS sso_required,
                c.ip_allowlist_json,
                c.account_disable_reason
            FROM users u
            LEFT JOIN clients c ON c.id = u.client_id
            WHERE u.id = @userId AND u.deleted_at IS NULL;
            """;

        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@userId", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapUser(reader) : null;
    }

    private async Task<CurrentUserDto> BuildCurrentUserAsync(SqlConnection connection, SqlIdentityUser user, string token, CancellationToken cancellationToken, string? roleOverride = null)
    {
        var role = roleOverride ?? await LoadPrimaryRoleAsync(connection, user.Id, cancellationToken);
        var permissions = await LoadPermissionsAsync(connection, user.Id, cancellationToken);
        var settings = await LoadUserSettingsAsync(connection, user.Id, cancellationToken);
        var ticketingSystem = await LoadTicketingSystemFlagAsync(connection, user.ClientId, cancellationToken);

        return new CurrentUserDto
        {
            Id = user.Id,
            Name = user.Name,
            Email = user.Email,
            Phone = user.Phone,
            JobTitle = user.JobTitle,
            Department = user.Department,
            Timezone = user.Timezone,
            AvatarUrl = ResolveAvatarUrl(user.AvatarPath),
            ClientId = user.ClientId,
            IsClient = user.IsClient,
            IsActive = user.IsActive,
            ClientStatus = user.ClientStatus,
            ClientMaxUsers = user.ClientMaxUsers,
            MfaEnabled = user.MfaEnabled,
            SsoEnabled = user.SsoEnabled,
            MustResetPassword = user.MustResetPassword,
            EmailVerifiedAt = user.EmailVerifiedAt,
            DeletedAt = user.DeletedAt,
            CreatedAt = user.CreatedAt,
            UpdatedAt = user.UpdatedAt,
            Token = token,
            TicketingSystem = ticketingSystem,
            UserPermissions = permissions,
            Settings = settings,
            Role = role
        };
    }

    private async Task<string> LoadPrimaryRoleAsync(SqlConnection connection, int userId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP 1 r.name
            FROM model_has_roles mhr
            INNER JOIN roles r ON r.id = mhr.role_id
                        WHERE REPLACE(mhr.model_type, '\', '') = REPLACE(@modelType, '\', '')
                            AND mhr.model_id = @userId
            ORDER BY r.id;
            """;

        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@modelType", _settings.UserModelType);
        command.Parameters.AddWithValue("@userId", userId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value?.ToString() ?? string.Empty;
    }

    private async Task<IReadOnlyList<PermissionGroupDto>> LoadPermissionsAsync(SqlConnection connection, int userId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT DISTINCT p.category, p.name
            FROM model_has_roles mhr
            INNER JOIN role_has_permissions rhp ON rhp.role_id = mhr.role_id
            INNER JOIN permissions p ON p.id = rhp.permission_id
                        WHERE REPLACE(mhr.model_type, '\', '') = REPLACE(@modelType, '\', '')
                            AND mhr.model_id = @userId
            ORDER BY p.category, p.name;
            """;

        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@modelType", _settings.UserModelType);
        command.Parameters.AddWithValue("@userId", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

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

        return groups
            .Select(pair => new PermissionGroupDto
            {
                Module = pair.Key,
                Permissions = pair.Value
            })
            .ToList();
    }

    private async Task<object> LoadUserSettingsAsync(SqlConnection connection, int userId, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT TOP 1 settings
            FROM user_settings
            WHERE user_id = @userId
            ORDER BY id DESC;
            """;

        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@userId", userId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        var json = value?.ToString();
        if (string.IsNullOrWhiteSpace(json))
        {
            return new { };
        }

        try
        {
            return JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch (JsonException)
        {
            return new { };
        }
    }

    private async Task<bool> LoadTicketingSystemFlagAsync(SqlConnection connection, int? clientId, CancellationToken cancellationToken)
    {
        if (!clientId.HasValue)
        {
            return false;
        }

        const string sql = """
            SELECT TOP 1 1
            FROM ticketing_systems
            WHERE client_id = @clientId
              AND ticketing_token IS NOT NULL
              AND LTRIM(RTRIM(ticketing_token)) <> '';
            """;

        await using var command = CreateCommand(connection, sql);
        command.Parameters.AddWithValue("@clientId", clientId.Value);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is not null && value != DBNull.Value;
    }

    private SqlCommand CreateCommand(SqlConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandType = CommandType.Text;
        command.CommandTimeout = _settings.CommandTimeoutSeconds;
        return command;
    }

    private static SqlIdentityUser MapUser(SqlDataReader reader)
    {
        return new SqlIdentityUser
        {
            Id = GetInt32(reader, "id"),
            Name = GetString(reader, "name") ?? string.Empty,
            Email = GetString(reader, "email") ?? string.Empty,
            Phone = GetString(reader, "phone"),
            JobTitle = GetString(reader, "job_title"),
            Department = GetString(reader, "department"),
            Timezone = GetString(reader, "timezone"),
            AvatarPath = GetString(reader, "avatar_path"),
            ClientId = GetNullableInt32(reader, "client_id"),
            IsClient = GetInt32(reader, "is_client"),
            IsActive = GetBoolean(reader, "is_active"),
            MfaEnabled = GetBoolean(reader, "mfa_enabled"),
            SsoEnabled = GetBoolean(reader, "sso_enabled"),
            MustResetPassword = GetBoolean(reader, "must_reset_password"),
            PasswordHash = GetString(reader, "password") ?? string.Empty,
            EmailVerifiedAt = GetNullableDateTimeOffset(reader, "email_verified_at"),
            DeletedAt = GetNullableDateTimeOffset(reader, "deleted_at"),
            CreatedAt = GetNullableDateTimeOffset(reader, "created_at"),
            UpdatedAt = GetNullableDateTimeOffset(reader, "updated_at"),
            ClientStatus = GetString(reader, "account_status") ?? "active",
            ClientMaxUsers = GetNullableInt32(reader, "max_users"),
            MfaRequired = GetBoolean(reader, "mfa_required"),
            SsoRequired = GetBoolean(reader, "sso_required"),
            IpAllowlistJson = GetString(reader, "ip_allowlist_json"),
            AccountDisableReason = GetString(reader, "account_disable_reason")
        };
    }

    private static int? GetUserId(ClaimsPrincipal principal)
    {
        var raw = principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue("sub");

        return int.TryParse(raw, out var userId) ? userId : null;
    }

    private string? ValidateClientSecurity(SqlIdentityUser user, string? ipAddress)
    {
        if (user.MfaRequired && !user.MfaEnabled)
        {
            return "MFA is required for this client account.";
        }

        if (user.SsoRequired && !user.SsoEnabled)
        {
            return "SSO is required for this client account.";
        }

        var allowlist = ParseAllowlist(user.IpAllowlistJson);
        if (allowlist.Count > 0 && !IpAllowed(ipAddress, allowlist))
        {
            return "Access denied from this IP address.";
        }

        return null;
    }

    private static List<string> ParseAllowlist(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(raw) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static bool IpAllowed(string? ipAddress, IReadOnlyList<string> allowlist)
    {
        if (string.IsNullOrWhiteSpace(ipAddress))
        {
            return false;
        }

        foreach (var rule in allowlist)
        {
            var trimmed = (rule ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (IpMatches(ipAddress, trimmed))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IpMatches(string ipAddress, string rule)
    {
        if (IPAddress.TryParse(rule, out _))
        {
            return string.Equals(ipAddress, rule, StringComparison.OrdinalIgnoreCase);
        }

        if (!rule.Contains('/'))
        {
            return false;
        }

        var parts = rule.Split('/', 2);
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var subnet) || !int.TryParse(parts[1], out var prefixLength))
        {
            return false;
        }

        if (!IPAddress.TryParse(ipAddress, out var address) || address.AddressFamily != subnet.AddressFamily)
        {
            return false;
        }

        var addressBytes = address.GetAddressBytes();
        var subnetBytes = subnet.GetAddressBytes();
        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;

        for (var index = 0; index < fullBytes; index++)
        {
            if (addressBytes[index] != subnetBytes[index])
            {
                return false;
            }
        }

        if (remainingBits == 0)
        {
            return true;
        }

        var mask = (byte)~((1 << (8 - remainingBits)) - 1);
        return (addressBytes[fullBytes] & mask) == (subnetBytes[fullBytes] & mask);
    }

    private static string? ResolveAvatarUrl(string? avatarPath)
    {
        return string.IsNullOrWhiteSpace(avatarPath) ? null : avatarPath;
    }

    private static string? GetString(SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static int GetInt32(SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        var fieldType = reader.GetFieldType(ordinal);
        if (fieldType == typeof(int))
        {
            return reader.GetInt32(ordinal);
        }

        if (fieldType == typeof(long))
        {
            return checked((int)reader.GetInt64(ordinal));
        }

        var value = reader.GetValue(ordinal);
        return checked(Convert.ToInt32(value));
    }

    private static int? GetNullableInt32(SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var fieldType = reader.GetFieldType(ordinal);
        if (fieldType == typeof(int))
        {
            return reader.GetInt32(ordinal);
        }

        if (fieldType == typeof(long))
        {
            return checked((int)reader.GetInt64(ordinal));
        }

        var value = reader.GetValue(ordinal);
        return checked(Convert.ToInt32(value));
    }

    private static bool GetBoolean(SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return !reader.IsDBNull(ordinal) && reader.GetBoolean(ordinal);
    }

    private static DateTimeOffset? GetNullableDateTimeOffset(SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var fieldType = reader.GetFieldType(ordinal);
        if (fieldType == typeof(DateTimeOffset))
        {
            return reader.GetFieldValue<DateTimeOffset>(ordinal);
        }

        if (fieldType == typeof(DateTime))
        {
            return new DateTimeOffset(reader.GetDateTime(ordinal));
        }

        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateTimeOffset dateTimeOffset => dateTimeOffset,
            DateTime dateTime => new DateTimeOffset(dateTime),
            _ => null
        };
    }

    private sealed class SqlIdentityUser
    {
        public int Id { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Email { get; init; } = string.Empty;
        public string? Phone { get; init; }
        public string? JobTitle { get; init; }
        public string? Department { get; init; }
        public string? Timezone { get; init; }
        public string? AvatarPath { get; init; }
        public int? ClientId { get; init; }
        public int IsClient { get; init; }
        public bool IsActive { get; init; }
        public bool MfaEnabled { get; init; }
        public bool SsoEnabled { get; init; }
        public bool MustResetPassword { get; init; }
        public string PasswordHash { get; init; } = string.Empty;
        public DateTimeOffset? EmailVerifiedAt { get; init; }
        public DateTimeOffset? DeletedAt { get; init; }
        public DateTimeOffset? CreatedAt { get; init; }
        public DateTimeOffset? UpdatedAt { get; init; }
        public string? ClientStatus { get; init; }
        public int? ClientMaxUsers { get; init; }
        public bool MfaRequired { get; init; }
        public bool SsoRequired { get; init; }
        public string? IpAllowlistJson { get; init; }
        public string? AccountDisableReason { get; init; }
    }
}
