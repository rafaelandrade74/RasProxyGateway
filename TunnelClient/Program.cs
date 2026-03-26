using System.Net;
using TunnelClient.Configuration;
using TunnelClient.Services;

try
{
    var options = ClientOptions.Parse(args);

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    var client = new WebSocketTunnelClient(options, cts.Token);
    await client.RunAsync();
}
catch (Exception ex) when (ex is ArgumentException or UriFormatException)
{
    Console.WriteLine($"❌ Configuração inválida: {ex.Message}");
    Console.WriteLine("Exemplo: dotnet run --project TunnelClient -- --server ws://localhost:5000/ws --token secret-token-1 --target-port 8080");
}
