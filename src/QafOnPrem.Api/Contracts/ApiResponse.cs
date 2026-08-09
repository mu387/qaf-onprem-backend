namespace QafOnPrem.Api.Contracts;

public sealed record ApiResponse<T>(bool Success, int StatusCode, string Message, T? Data);
