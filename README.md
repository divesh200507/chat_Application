# TCP Chat Application (.NET 8)

Small TCP chat server + CLI client with JSON protocol.

## Features

- Protocol (newline-delimited JSON):
  - `LOGIN_REQ { type, username, password }`
  - `LOGIN_RESP { type, ok, reason? }`
  - `DM { type, to, msg }`
  - `MULTI { type, to:[u1,u2,...], msg }`
  - `BROADCAST { type, msg }`
  - `HEARTBEAT { type }`
  - Server responses: `DM`, `MULTI`, `BROADCAST`, `SYSTEM`, `ERROR`, `LOGIN_RESP`

- Server:
  - Manages sessions and routing
  - Basic auth with static credential list
  - Idle timeout (default 120s)
  - Per-client send queue (backpressure)
  - Handles malformed JSON by closing the session
  - Metrics: online users, msgs/sec
  - Audit log: timestamp, from, to, type, bytes, latency

- Client (CLI “frontend”):
  - Text UI / REPL
  - Commands:
    - `/dm <user> <msg>`
    - `/multi <u1,u2,...> <msg>`
    - `/broadcast <msg>`
    - `/help`
    - `/quit`
  - Pretty-prints received messages
  - Heartbeat to keep connection alive

## Sample credentials

Hard-coded in server:

- `Divesh / password1`
- `smit / password2`
- `priyank / password3`

## Build steps

From root:

```bash
dotnet build

