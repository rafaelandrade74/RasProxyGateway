using System.Globalization;

namespace TunnelClient.Configuration;

internal sealed record ClientOptions(Uri ServerUri, string Token, string TargetHost, int TargetPort)
{
    public static ClientOptions Parse(string[] args)
    {
        static string? GetArg(string[] a, params string[] keys)
        {
            for (var i = 0; i < a.Length - 1; i++)
            {
                if (keys.Any(k => string.Equals(a[i], k, StringComparison.OrdinalIgnoreCase)))
                {
                    return a[i + 1];
                }
            }
            return null;
        }

        var serverUrl = GetArg(args, "--server", "-s")
                        ?? Environment.GetEnvironmentVariable("TUNNEL_SERVER")
                        ?? "ws://localhost:5000/ws";

        var token = GetArg(args, "--token", "-t")
                    ?? Environment.GetEnvironmentVariable("TUNNEL_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException("Token não informado. Use --token ou defina TUNNEL_TOKEN.");
        }

        var targetHost = GetArg(args, "--target-host")
                         ?? Environment.GetEnvironmentVariable("TUNNEL_TARGET_HOST")
                         ?? "localhost";

        var targetPortRaw = GetArg(args, "--target-port")
                            ?? Environment.GetEnvironmentVariable("TUNNEL_TARGET_PORT");
        var targetPort = int.TryParse(targetPortRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPort)
            ? parsedPort
            : 8080;

        return new ClientOptions(new Uri(serverUrl), token!, targetHost, targetPort);
    }
}
