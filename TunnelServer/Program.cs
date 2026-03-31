using TunnelServer.Services;
using TunnelServer.WebSockets;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddLogging();
builder.Services.AddRouting();
builder.Services.AddControllers();

builder.Services.AddSingleton<ITokenValidator, TokenValidator>();
builder.Services.AddSingleton<TunnelManager>();
builder.Services.AddSingleton<TunnelTcpListenerService>();
builder.Services.AddSingleton<WebSocketHandler>();

var app = builder.Build();

app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});
app.UseMiddleware<WebSocketMiddleware>();

app.MapControllers();
app.MapGet("/", () => Results.Ok(new { status = "ok", message = "Tunnel server running" }));

await app.RunAsync();
