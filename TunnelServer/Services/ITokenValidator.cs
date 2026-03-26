namespace TunnelServer.Services;

public interface ITokenValidator
{
    bool IsValid(string? token);
}
