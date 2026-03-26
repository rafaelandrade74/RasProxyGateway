# TunnelServer — LLM Project Brief

## Purpose
Simple ASP.NET Core WebSocket server that mimics ngrok-style tunnels. Clients authenticate, create a tunnel (initially Waiting), then connect the tunnel to start TCP forwarding from a public port to the client.

## Key Flows
- WebSocket endpoint: `/ws`
- Messages (JSON):
  - `auth { token }` → responds `authenticated { client_id }`
  - `create_tunnel` → creates tunnel with status `waiting`, responds `tunnel_created { tunnel_id, status }`
  - `connect_tunnel { tunnel_id }` → binds authenticated client to waiting tunnel, starts TCP listener, responds `tunnel_connected { tunnel_id, public_port, status }`
  - `tcp_data { connection_id, data(base64), remote_ip }` → bidirectional payload for active TCP connections
- Tunnel lifecycle: Waiting → Connected → Closed (on client disconnect/cleanup).

## Data Models
- `ConnectedClient`: `ClientId`, `WebSocket Socket`, `IsAuthenticated`, `ConnectedAt`.
- `Tunnel`: `TunnelId`, `ClientId?`, `PublicPort`, `CreatedAt`, `Status (Waiting|Connected|Closed)`.

## Services
- `TunnelManager`: in-memory store (`ConcurrentDictionary`) for clients and tunnels; removes tunnels when client disconnects.
- `TunnelTcpListenerService`: starts per-tunnel `TcpListener` on dynamic port, accepts external `TcpClient`s, assigns `connection_id`, relays data via callbacks, and writes incoming WebSocket `tcp_data` back to TCP sockets.

## WebSocket Handler
- `WebSocketHandler`: handles auth, create_tunnel, connect_tunnel, tcp_data; enforces auth; bridges TCP data; cleans up tunnels/listeners on disconnect.
- Middleware: `WebSocketMiddleware` routes `/ws` requests to handler.

## DI/Boot
- Services registered in `Program.cs`: `ITokenValidator`, `TunnelManager`, `TunnelTcpListenerService`, `WebSocketHandler`.
- App uses `UseWebSockets` with 30s keep-alive and maps controllers plus health GET `/`.

## Protocol Notes
- Subdomain generator: 6–10 lowercase alphanumerics + `.localhost`.
- TCP bridging is raw (not full HTTP reverse proxy). Payloads are base64 over WebSocket.
- No external storage, no SignalR; entirely in-memory MVP.

## Files of Interest
- `WebSockets/WebSocketHandler.cs`
- `Services/TunnelTcpListenerService.cs`
- `Services/TunnelManager.cs`
- `Models/Tunnel.cs`, `Models/ConnectedClient.cs`
- `WebSockets/WebSocketMiddleware.cs`
- `Program.cs`

## Testing Smoke Path
1) Connect to `/ws`, send `auth` with valid token (see `TokenValidator`).
2) `create_tunnel` → get `public_url`.
3) From same or later socket, `connect_tunnel` with that `public_url` → receive `public_port`.
4) Open `tcp://localhost:{public_port}`; observe data bridged via `tcp_data` messages to client.
