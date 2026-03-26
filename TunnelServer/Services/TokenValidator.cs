namespace TunnelServer.Services;

/// <summary>
/// Simple in-memory token validation for the MVP.
/// </summary>
public sealed class TokenValidator : ITokenValidator
{
    private static readonly HashSet<string> ValidTokens = new()
    {
        "secret-token-1",
        "secret-token-2",
        "secret-token-3"
    };

    public bool IsValid(string? token) => token is not null && ValidTokens.Contains(token);
}
