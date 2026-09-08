---
name: windows-app-mcp
description: Use the OpenClaw Windows app's local MCP server and winnode CLI for development, debugging, proof, and automation.
---

# Windows App Local MCP

Use this skill when developing, debugging, or validating the OpenClaw Windows
app through its same-machine MCP server. This is a developer and automation
surface, not the OpenClaw gateway-to-node transport.

## Keep the transports distinct

| Path | Purpose | Transport |
|---|---|---|
| OpenClaw node mode | Product path for an OpenClaw agent to use a paired Windows machine | Gateway WebSocket and `node.invoke`; agents use the gateway `nodes` tool |
| Windows app local MCP | Developer/debugging path for same-machine clients to inspect and invoke the running Windows app | Authenticated loopback MCP HTTP; developers use MCP tools or `winnode` |

Do not describe a local MCP invocation as gateway proof. Do not use local MCP
as a substitute when the behavior being validated depends on gateway pairing,
allowlists, reapproval, routing, or `node.invoke`.

## Start an isolated development app

Prefer isolated tray data so tests do not change
`%APPDATA%\OpenClawTray`:

```powershell
.\run-app-local.ps1 -Isolated -AllowNonMain
```

Copy the data directory printed by the launcher:

```powershell
$env:OPENCLAW_TRAY_DATA_DIR = '<isolated-data-dir>'
```

Enable **Local MCP Server** in Permissions. MCP-only mode is supported:
`EnableMcpServer=true` with `EnableNodeMode=false` starts local capabilities
without requiring gateway credentials.

## Discover the live surface

Always discover before invoking because settings determine which capabilities
are currently exposed:

```powershell
winnode --list-tools
```

The live `tools/list` response is authoritative. The complete static command
reference is `.agents\skills\winnode\SKILL.md`, and
`McpToolBridge.CommandDescriptions` is the canonical description source.

## Invoke through winnode

```powershell
winnode --command app.status --params '{}'
winnode --command app.connection.status --params '{}'
winnode --command system.which --params '{"bins":["git","node"]}'
```

`winnode` targets the local Windows app only. Its `--node` argument is accepted
for CLI parity but ignored. It loads the MCP token from the selected tray
profile; do not place the token on the command line.

Use `--invoke-timeout <ms>` for bounded long-running calls. Parameters must be
a JSON object. Prefix with `@` to load a large JSON object from a file.

## Raw MCP protocol proof

Use raw JSON-RPC when changing server or protocol shape. Read the token from
the selected isolated profile without printing it:

```powershell
$tokenPath = Join-Path $env:OPENCLAW_TRAY_DATA_DIR 'mcp-token.txt'
$token = Get-Content $tokenPath -Raw
```

Send `initialize`, `tools/list`, and `tools/call` to the loopback endpoint
printed by the app. Use `notifications/cancelled` with the active JSON-RPC
request ID to prove cancellation. Never publish the bearer token, settings,
gateway records, device identities, or raw secret-bearing responses.

## Useful developer-only app commands

Local MCP exposes `app.*` commands that are not advertised to the gateway node:

- `app.status`, `app.connection.status`, `app.connection.gateways`
- `app.navigate`, `app.menu`, `app.search`
- `app.chat.snapshot`, `app.chat.send`, `app.chat.reset`
- `app.chat.queue.list`, `app.chat.queue.cancel`
- `app.settings.get`, `app.settings.set`

Use these to set up deterministic UI state, navigate the current build, inspect
connection state, and drive functional proof. Re-read `tools/list` for the
exact commands available on the running build.

## Capability commands

The same `INodeCapability` registration seam serves both local MCP and gateway
node mode, so local MCP can exercise capability implementations directly:

- `system.*`, `device.*`, `screen.*`, `camera.*`, `location.*`
- `canvas.*`, including A2UI
- `browser.proxy`
- `tts.*`, `stt.*`
- `ollama.models`, `ollama.chat` when **Share Windows Ollama** is enabled

This proves the local capability path only. Add a real gateway invocation when
the change affects paired-gateway behavior.

## Required closeout

For a new or changed Windows node command:

1. Register it through the shared `INodeCapability` path.
2. Update `McpToolBridge.CommandDescriptions`.
3. Update `.agents\skills\winnode\SKILL.md`.
4. Add capability, MCP bridge, and `winnode` tests.
5. Prove `winnode --list-tools` and `winnode --command ...`.
6. Prove the gateway path separately when gateway-mediated behavior changed.

Follow `.agents\skills\openclaw-proof-validation\SKILL.md` for the complete
validation and PR evidence checklist.
