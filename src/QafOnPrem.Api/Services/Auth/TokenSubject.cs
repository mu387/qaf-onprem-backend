namespace QafOnPrem.Api.Services.Auth;

public sealed record TokenSubject(
    int Id,
    string Email,
    string Name,
    int? ClientId,
    int IsClient,
    string ClientStatus,
    string Role);
