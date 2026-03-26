# ProxyGateway — Túnel WebSocket para encaminhamento TCP

Plataforma mínima estilo ngrok que cria túneis TCP via WebSocket. Um **cliente** conecta‑se ao **servidor**, autentica com token, cria um túnel e recebe uma porta pública local. Qualquer conexão feita a essa porta é repassada ao processo alvo do cliente.

## Visão geral
- **TunnelServer (ASP.NET Core)**: aceita WebSockets em `/ws`, valida tokens, cria túneis em memória e abre um `TcpListener` dinâmico para cada túnel.
- **TunnelClient (console)**: conecta ao servidor, autentica, cria o túnel e faz o bridging entre a porta pública e um destino local (`host:porta`).
- **TunnelShared**: modelos, enumerações e serialização JSON (System.Text.Json) compartilhados.

## Fluxo do túnel
1. Cliente abre WebSocket para `ws://<server>/ws`.
2. Envia `{"Type":"Auth","Token":"..."}`; servidor responde `{"Type":"Authenticated","ClientId":"..."}`.
3. Envia `{"Type":"CreateTunnel"}`; recebe `{"Type":"TunnelCreated","TunnelId":"...","Status":"waiting"}`.
4. Envia `{"Type":"ConnectTunnel","TunnelId":"..."}`; servidor sobe um `TcpListener` dinâmico e responde `{"Type":"TunnelConnected","PublicPort":51987,"Status":"connected"}`.
5. Tráfego:
   - Conexões TCP externas entram pela `PublicPort`.
   - Servidor encapsula bytes em `{"Type":"TcpData","ConnectionId":"...","Data":"<base64>","RemoteIp":"..."}`
   - Cliente cria/reutiliza conexão local para destino e devolve `TcpData` na volta (mesmo formato).
6. Encerramento: queda do socket fecha túnel e conexões associadas.

## Protocolo de mensagens (JSON)
Valores de `Type` são **PascalCase**, conforme o `enum MessageType`:
- `Auth { Token }`
- `Authenticated { ClientId }`
- `CreateTunnel`
- `TunnelCreated { TunnelId, Status }`
- `ConnectTunnel { TunnelId }`
- `TunnelConnected { TunnelId, PublicPort, Status }`
- `TcpData { ConnectionId, Data(base64), RemoteIp }` (bidirecional)
- `Error { Message }`

## Pré-requisitos
- .NET SDK **10.0** (TargetFramework `net10.0` em todos os projetos).
- Windows/Linux/macOS. Nenhum banco ou cache externo.

## Como rodar localmente
Na raiz (`ProxyGateway.sln`):
```bash
dotnet restore
```

### Servidor
```bash
dotnet run --project TunnelServer --urls http://localhost:5000
# WebSocket: ws://localhost:5000/ws
# Health:     GET http://localhost:5000/
# Status:     GET http://localhost:5000/api/status
```
Tokens válidos ficam em `TunnelServer/Services/TokenValidator.cs` (padrão: `secret-token-1/2/3`).

### Cliente
Escolha o processo local a ser exposto, por exemplo um HTTP em `localhost:8080`:
```bash
dotnet run --project TunnelClient -- \
  --server ws://localhost:5000/ws \
  --token secret-token-1 \
  --target-host localhost \
  --target-port 8080
```
Variáveis de ambiente equivalentes:
- `TUNNEL_SERVER` (default `ws://localhost:5000/ws`)
- `TUNNEL_TOKEN` (obrigatório)
- `TUNNEL_TARGET_HOST` (default `localhost`)
- `TUNNEL_TARGET_PORT` (default `8080`)

Saída típica do cliente:
```
➡️  Conectando ao servidor ws://localhost:5000/ws (destino localhost:8080)
✅ WebSocket conectado.
🔐 Autenticado. ClientId = ...
🛠️  Tunnel criado aguardando conexão. Id = ...
🌐 Tunnel conectado. Porta pública: 51987
```
Abra um `tcp://localhost:51987` (HTTP, banco ou outro protocolo) e o tráfego será repassado.

## Estrutura de pastas
- `TunnelServer/` – WebSocket + TCP bridge; controllers de health/status; serviços `TunnelManager`, `TunnelTcpListenerService`, `TokenValidator`.
- `TunnelClient/` – console que mantém o socket vivo, gerencia conexões locais e faz forward/base64.
- `TunnelShared/` – `ClientMessage`, `MessageType`, `MessageSerializer`.
- `ProxyGateway.sln` – solução para abrir no Visual Studio / VS Code.

## Pontos de extensão rápidos
- **Tokens**: ajustar lista em `TunnelServer/Services/TokenValidator.cs` ou trocar por storage externo.
- **Porta/URL do servidor**: use `--urls` no `dotnet run` ou variável `ASPNETCORE_URLS`.
- **Observabilidade**: logs via `ILogger` já presentes; adicione Serilog/Application Insights conforme necessário.
- **Persistência**: hoje tudo é in-memory; para produção, mover `TunnelManager` para store distribuído e proteger public ports com firewall.

## Troubleshooting
- Conexão WebSocket fecha imediatamente: verifique token (`Invalid token`) e URL (`/ws`).
- Porta pública não recebe conexões: confirme que `TunnelTcpListenerService` retornou a porta (log do servidor) e que firewall local permite `localhost`.
- Latência ou quedas: o cliente reconecta automaticamente; observe console para "Conexão perdida. Tentando reconectar...".

## Teste rápido manual (sem cliente)
Usando `websocat` ou `wscat`:
```
websocat ws://localhost:5000/ws
{"Type":"Auth","Token":"secret-token-1"}
{"Type":"CreateTunnel"}
{"Type":"ConnectTunnel","TunnelId":"<id recebido>"}
```
Depois envie `TcpData` manualmente com o `ConnectionId` criado pelo servidor (apenas para depuração).

---
Projeto criado como MVP de túnel TCP via WebSocket para desenvolvimento local e experimentos.
