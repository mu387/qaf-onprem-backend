namespace QafOnPrem.Api.Contracts.Auth;

public sealed record AuthenticationResult(bool Succeeded, int StatusCode, string Message, CurrentUserDto? User)
{
    public static AuthenticationResult Success(string message, CurrentUserDto user)
        => new(true, StatusCodes.Status200OK, message, user);

    public static AuthenticationResult Failure(int statusCode, string message)
        => new(false, statusCode, message, null);
}
