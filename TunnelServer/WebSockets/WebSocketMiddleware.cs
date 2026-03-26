using Microsoft.AspNetCore.Http;

namespace TunnelServer.WebSockets;

/// <summary>
/// Intercepts /ws and hands the socket off to the WebSocket handler.
/// </summary>
public sealed class WebSocketMiddleware
{
    private readonly RequestDelegate _next;

    public WebSocketMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, WebSocketHandler handler)
    {
        if (context.Request.Path.StartsWithSegments("/ws", StringComparison.OrdinalIgnoreCase))
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("WebSocket requests only");
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await handler.HandleAsync(context, socket, context.RequestAborted);
            return;
        }

        await _next(context);
    }
}
