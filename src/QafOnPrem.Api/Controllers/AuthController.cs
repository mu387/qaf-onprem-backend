using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Data.SqlClient;
using QafOnPrem.Api.Contracts;
using QafOnPrem.Api.Contracts.Auth;
using QafOnPrem.Api.Services.AppData;
using QafOnPrem.Api.Services.Auth;

namespace QafOnPrem.Api.Controllers;

[ApiController]
[Route("api")]
[Route("")]
public sealed class AuthController(IAuthService authService, ISqlAppDataService appDataService, IConfiguration configuration) : ControllerBase
{
    private readonly IAuthService _authService = authService;
    private readonly ISqlAppDataService _appDataService = appDataService;
    private readonly string _connectionString = configuration.GetConnectionString("SqlServer") ?? string.Empty;

    [HttpPost("login")]
    [ProducesResponseType(typeof(ApiResponse<CurrentUserDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        return ClientLogin(request, cancellationToken);
    }

    [HttpPost("client/login")]
    [ProducesResponseType(typeof(ApiResponse<CurrentUserDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ClientLogin([FromBody] LoginRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        var result = await _authService.AuthenticateClientAsync(request.Email, request.Password, HttpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken);
        if (!result.Succeeded || result.User is null)
        {
            return StatusCode(result.StatusCode, Failure(result.Message, result.StatusCode));
        }

        return Ok(Success(result.Message, result.User));
    }

    [HttpPost("client/forgot-password")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var userId = await FindUserIdByEmailAsync(connection, request.Email, cancellationToken);
        if (!userId.HasValue)
        {
            return Ok(Success("Password reset link sent successfully.", Array.Empty<object>()));
        }

        var token = Guid.NewGuid().ToString("N");
        await using (var deleteCommand = new SqlCommand("DELETE FROM password_resets WHERE email = @email;", connection))
        {
            deleteCommand.Parameters.AddWithValue("@email", request.Email.Trim());
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insertCommand = new SqlCommand("INSERT INTO password_resets (email, token, created_at) VALUES (@email, @token, SYSUTCDATETIME());", connection))
        {
            insertCommand.Parameters.AddWithValue("@email", request.Email.Trim());
            insertCommand.Parameters.AddWithValue("@token", token);
            await insertCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        return Ok(Success("Password reset link sent successfully.", new { token }));
    }

    [HttpPost("reset-password")]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        if (!string.Equals(request.Password, request.PasswordConfirmation, StringComparison.Ordinal))
        {
            return StatusCode(StatusCodes.Status422UnprocessableEntity, Failure("Password confirmation does not match.", StatusCodes.Status422UnprocessableEntity));
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var validToken = await ValidatePasswordResetTokenAsync(connection, request.Email, request.Token, cancellationToken);
        if (!validToken)
        {
            return StatusCode(StatusCodes.Status422UnprocessableEntity, Failure("Invalid or expired password reset token.", StatusCodes.Status422UnprocessableEntity));
        }

        var hash = BCrypt.Net.BCrypt.HashPassword(request.Password);
        await using (var updateCommand = new SqlCommand("UPDATE users SET password = @password, updated_at = SYSUTCDATETIME() WHERE LOWER(email) = LOWER(@email) AND deleted_at IS NULL;", connection))
        {
            updateCommand.Parameters.AddWithValue("@password", hash);
            updateCommand.Parameters.AddWithValue("@email", request.Email.Trim());
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteCommand = new SqlCommand("DELETE FROM password_resets WHERE email = @email;", connection))
        {
            deleteCommand.Parameters.AddWithValue("@email", request.Email.Trim());
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        return Ok(Success("Password reset successfully.", Array.Empty<object>()));
    }

    [HttpGet("client/machine-user")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status200OK)]
    public IActionResult MachineUser()
    {
        var machineUser = ResolveMachineUserName();
        return Ok(Success("Machine user", new
        {
            machine_user = machineUser,
            available = !string.IsNullOrWhiteSpace(machineUser)
        }));
    }

    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType(typeof(ApiResponse<CurrentUserDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiResponse<object>), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Me(CancellationToken cancellationToken)
    {
        var user = await _authService.GetCurrentUserAsync(User, ExtractBearerToken(), cancellationToken);
        if (user is null)
        {
            return StatusCode(StatusCodes.Status403Forbidden, Failure("Unauthorized", StatusCodes.Status403Forbidden));
        }

        return Ok(Success("Current user", user));
    }

    [HttpGet("permissions")]
    [Authorize]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<PermissionGroupDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Permissions([FromQuery] string? scope, CancellationToken cancellationToken)
    {
        var permissions = await _appDataService.GetPermissionsAsync(User, scope, cancellationToken);
        return Ok(Success("All Permissions", permissions));
    }

    [Authorize]
    [HttpGet("profile")]
    [ProducesResponseType(typeof(ApiResponse<CurrentUserDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Profile(CancellationToken cancellationToken)
    {
        var user = await _authService.GetCurrentUserAsync(User, ExtractBearerToken(), cancellationToken);
        if (user is null)
        {
            return StatusCode(StatusCodes.Status403Forbidden, Failure("Unauthorized", StatusCodes.Status403Forbidden));
        }

        return Ok(Success("Profile", user));
    }

    private ApiResponse<T> Success<T>(string message, T data)
    {
        return new ApiResponse<T>(true, StatusCodes.Status200OK, message, data);
    }

    private ApiResponse<object> Failure(string message, int statusCode)
    {
        return new ApiResponse<object>(false, statusCode, message, null);
    }

    private string? ExtractBearerToken()
    {
        var authorizationHeader = Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (authorizationHeader.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return authorizationHeader[prefix.Length..].Trim();
        }

        return null;
    }

    private async Task<SqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task<long?> FindUserIdByEmailAsync(SqlConnection connection, string email, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT TOP 1 id FROM users WHERE LOWER(email) = LOWER(@email) AND deleted_at IS NULL;", connection);
        command.Parameters.AddWithValue("@email", email.Trim());
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null ? null : Convert.ToInt64(value);
    }

    private static async Task<bool> ValidatePasswordResetTokenAsync(SqlConnection connection, string email, string token, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT COUNT(*) FROM password_resets WHERE LOWER(email) = LOWER(@email) AND token = @token AND created_at >= DATEADD(HOUR, -24, SYSUTCDATETIME());", connection);
        command.Parameters.AddWithValue("@email", email.Trim());
        command.Parameters.AddWithValue("@token", token.Trim());
        var count = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
        return count > 0;
    }

    private string? ResolveMachineUserName()
    {
        var identityName = HttpContext.User?.Identity?.IsAuthenticated == true
            ? HttpContext.User.Identity?.Name
            : null;

        if (!string.IsNullOrWhiteSpace(identityName))
        {
            return NormalizeUserName(identityName!);
        }

        var remoteUser = Request.Headers["X-Remote-User"].ToString();
        if (!string.IsNullOrWhiteSpace(remoteUser))
        {
            return NormalizeUserName(remoteUser);
        }

        remoteUser = Request.Headers["REMOTE_USER"].ToString();
        if (!string.IsNullOrWhiteSpace(remoteUser))
        {
            return NormalizeUserName(remoteUser);
        }

        return null;
    }

    private static string NormalizeUserName(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Contains('\\'))
        {
            return trimmed.Split('\\').LastOrDefault()?.Trim() ?? trimmed;
        }

        return trimmed;
    }
}
