namespace QafOnPrem.Api.Services.Auth;

public interface ITokenFactory
{
    string CreateToken(TokenSubject user);
}
