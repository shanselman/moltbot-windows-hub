---
name: winnode
description: Invoke and troubleshoot OpenClaw Windows-node commands through the local winnode CLI and MCP endpoint, including command argument schemas and A2UI JSONL calls.
---

<!--
  REGENERATE-ME-WHEN-CAPABILITIES-CHANGE

  The list of supported commands below is checked at CI time against the live
  capability surface (see SkillMdDriftTests). When a capability is added,
  removed, or renamed in src/OpenClaw.Shared/Mcp/McpToolBridge.cs
  (CommandDescriptions), update this document so the drift test stays green -
  the test compares command identifiers, so prose can still be tweaked by hand.
-->

# winnode skill reference

`winnode.exe` invokes OpenClaw Windows-node commands on the local tray over a
loopback MCP HTTP endpoint (default `http://127.0.0.1:8765/`). Enable
**Local MCP Server** in the tray's Settings → Advanced before calling.

This document is the agent-facing reference: every supported command, its
argument shape, and the A2UI v0.8 JSONL grammar. It is shipped alongside
`winnode.exe` so an agent can read it once and emit token-efficient calls.

---

## Invocation shape

```
winnode --command <name> [--params '<json-object>'] [--invoke-timeout <ms>] [--identity release|dev]
winnode --list-tools [--mcp-url <url>|--mcp-port <port>] [--identity release|dev]
```

- `--command` (required) - node command (e.g. `system.which`, `canvas.a2ui.push`).
- `--list-tools` - query the live MCP server's `tools/list` method and print the
  advertised tools. Useful when settings-gated capabilities differ from this
  static reference.
- `--params` - single JSON **object** string, default `{}`. Must be a JSON object,
  not an array or scalar. **`--params @<path>`** loads the JSON object from a
  file on disk (useful for big A2UI payloads / `canvas.eval` scripts).
- `--invoke-timeout` - milliseconds, default 15000, max 600000 (10 min). HTTP
  timeout adds a 5s buffer.
- `--node` - accepted for parity with `openclaw nodes invoke`; **ignored**
  locally. Safe to copy/paste from gateway-side commands.
- `--idempotency-key` - accepted for parity; **ignored**, and the CLI emits a
  `[winnode] WARN` to stderr because local MCP does *not* dedupe retries -
  re-running a command after a transient failure can double-execute side
  effects. If you need idempotency, target the gateway, not winnode.
- `--mcp-url <url>` / `--mcp-port <port>` - override the endpoint. Falls back to
  `OPENCLAW_MCP_PORT` env var, then port 8765. `--mcp-port` must be in
  `[1, 65535]`; out of range fails with exit code 2.
- `--mcp-token <token>` - bearer token override (testing / explicit only). The
  literal value is **visible to other same-user processes via the OS process
  listing** (`Get-CimInstance Win32_Process | Select CommandLine`,
  Process Explorer, etc.). The CLI emits a stderr warning when this flag is
  used. **Prefer `OPENCLAW_MCP_TOKEN` (env var) or the on-disk
  `%APPDATA%\OpenClawTray\mcp-token.txt`** which the release tray writes when
  MCP is enabled. Both `OPENCLAW_MCP_TOKEN` and the on-disk file should
  themselves be treated as sensitive operational secrets.
- `--identity release|dev` - selects which tray profile supplies the default
  on-disk MCP token. Defaults to `OPENCLAW_APP_IDENTITY`, then `release`.
  Use `--identity dev` for a side-by-side dev tray; its default token path is
  `%APPDATA%\OpenClawTray-Dev\mcp-token.txt`. `OPENCLAW_TRAY_DATA_DIR` still
  wins for isolated runs and points directly at the data folder.
- `--verbose` - log endpoint + ignored flags to stderr. Without `--verbose`,
  HTTP error bodies are emitted only as the first line; with `--verbose`, the
  full body is shown (after sanitization + token-shape redaction).

**Output contract:** stdout receives the capability payload as pretty-printed
JSON (matches `openclaw nodes invoke`). stderr receives errors. Exit code:

| Code | Meaning |
|------|---------|
| 0    | Success |
| 1    | Tool error, JSON-RPC error, transport failure, or HTTP non-2xx |
| 2    | Argument error (missing/invalid flags, bad `--params` JSON, out-of-range port/timeout, non-http URL) |

**Off-loopback safety:** when `--mcp-url` points at a non-loopback host, the
CLI **refuses to send the auto-loaded local MCP token** (and warns on stderr).
An explicitly supplied `--mcp-token` is honored with a warning. This preserves
the loopback-only threat model the tray's MCP server relies on.

---

## Commands

### system.notify
Show a Windows toast notification.
```
{"title": "OpenClaw", "body": "string", "subtitle": "string", "sound": true}
```
Returns `{ "sent": true }`. All fields optional except `body` in practice.

### system.run
Execute canonical argv. Subject to the local exec approval policy at
`%APPDATA%\OpenClawTray\exec-approvals.json`.
```
{
  "command": ["executable", "arg", ...], // required
  "rawCommand": "string",                // optional display metadata
  "cwd":     "string",
  "timeoutMs": 30000
}
```
Shell behavior must be explicit in the argv, for example
`{"command":["cmd.exe","/d","/s","/c","echo hello"],"rawCommand":"echo hello"}`.
The gateway's `exec host=node` path performs this wrapping automatically.
Non-empty custom `env` is rejected until environment values can be identity-bound
and shown safely during approval.

#### Migration from the pre-V2 low-level contract

The V2 node boundary intentionally rejects string-form `command` with
`command-array-required`. Update raw MCP, `node.invoke`, plugin, and direct
`winnode` callers to send the shell transport explicitly:

```json
// Before
{"command":"echo hello","shell":"cmd"}

// V2
{"command":["cmd.exe","/d","/s","/c","echo hello"],"rawCommand":"echo hello"}
```

PowerShell callers must likewise name `powershell.exe` or `pwsh.exe` and its
`-Command` arguments explicitly. Remove custom `env` from the request; V2 rejects
non-empty environments rather than approving an executable under hidden process
configuration.
Returns `{ stdout, stderr, exitCode, timedOut, durationMs }`.

### system.run.prepare
Pre-flight a `system.run` invocation. Same args as `system.run`. Returns the
parsed plan (`argv`, `cwd`, `rawCommand`, `agentId`, `sessionKey`) without
executing.

### system.which
Resolve binary names to absolute paths.
```
{"bins": ["git", "node", "powershell"]}
```
Returns `{ "bins": { "git": "C:\\...", ... } }`. Names not found are omitted.

### system.execApprovals.get
No params. Returns the active V2 snapshot:
`{ path, exists, hash, file: { version, defaults: { security, ask, askFallback, autoAllowSkills }, agents: { "<agentId>": { security, ask, askFallback, autoAllowSkills, allowlist: [{ id, pattern, lastUsedAt?, lastResolvedPath? }] } } } }`.
`security` is `deny|allowlist|full`; `ask` is `off|on-miss|always|deny`;
`askFallback` is `deny|allowlist|full`. Socket credentials are never returned.
Pass `hash` as `baseHash` when calling `system.execApprovals.set`.

### system.execApprovals.set
Replace the full V2 file using compare-and-swap. `baseHash` is required and must
equal the latest hash returned by `system.execApprovals.get`.
```
{
  "baseHash": "<hash-from-get>",
  "file": {
    "version": 1,
    "defaults": {
      "security": "allowlist",
      "ask": "on-miss",
      "askFallback": "deny",
      "autoAllowSkills": false
    },
    "agents": {
      "main": {
        "security": "allowlist",
        "ask": "on-miss",
        "askFallback": "deny",
        "autoAllowSkills": false,
        "allowlist": []
      }
    }
  }
}
```
Returns the updated snapshot. A remote update may preserve or remove existing
allowlist grants, but it cannot add or change grants or set full access. Missing or
stale hashes are rejected. This command changes future `system.run` authorization
decisions. The `winnode` path is local loopback MCP, so no gateway scope applies.
Remote operators must use the gateway `exec.approvals.node.*` control-plane
methods, which are separately gated by gateway admin scope.

### canvas.present
Open the WebView2 canvas window.
```
{
  "url":   "string",          // OR "html": "string"
  "html":  "string",
  "width": 800, "height": 600,
  "x": -1, "y": -1,           // -1 centers
  "title": "Canvas",
  "alwaysOnTop": false
}
```
Returns `{ "presented": true }`.

### canvas.hide
No params. Hides the canvas without destroying state.

### canvas.navigate
```
{"url": "https://..."}    // also accepts file:// or local canvas paths
```

### canvas.eval
```
{"script": "document.title"}    // also accepts "javaScript" or "javascript"
```
Returns the evaluated result.

### canvas.snapshot
```
{"format": "png|jpeg", "maxWidth": 1200, "quality": 80}
```
Returns `{ format, base64 }`.

### canvas.a2ui.push
Render an A2UI v0.8 surface in the canvas. The canvas window opens
automatically - no `canvas.present` required.
```
{
  "jsonl":     "string",   // OR jsonlPath
  "jsonlPath": "string",   // must live under %TEMP%
  "props":     {}           // optional
}
```
Returns `{ "pushed": true }`. **See A2UI grammar below.**

### canvas.a2ui.pushJSONL
Alias of `canvas.a2ui.push`. It uses the identical handler, size/line caps, and
`jsonl`/`jsonlPath` contract. It exists for naming parity, not as a separate
streaming path.

### canvas.a2ui.reset
No params. Clears any rendered surfaces. Returns `{ "reset": true }`.

### canvas.a2ui.dump
No params. Returns the current surface graph for introspection. **Read-all:**
this exposes every currently-rendered surface - operators should treat it as
equivalent to a screenshot of every open A2UI surface.

### canvas.caps
No params. Returns renderer capabilities (renderer, snapshot, a2ui version).

### screen.snapshot
```
{
  "format": "png|jpeg", "maxWidth": 1920, "quality": 80,
  "monitor": 0, "screenIndex": 0,    // 0 = primary
  "includePointer": true
}
```
Returns `{ format, width, height, base64, image }` (image is a `data:` URL).
The first use requires remembered user consent. Every invocation displays a
visible notification before screen capture begins.

### screen.record
```
{
  "durationMs": 5000,         // required, max 300000
  "format": "mp4|webm",
  "monitor": 0, "screenIndex": 0,
  "maxWidth": 1920, "fps": 30
}
```
Returns `{ format, durationMs, base64 }`.

### camera.list
No params. Returns `{ cameras: [{ deviceId, name, isDefault }] }`.

### camera.snap
```
{"deviceId": "string", "format": "jpeg|png", "maxWidth": 1280, "quality": 80}
```
Returns `{ format, width, height, base64 }`. `deviceId` defaults to system
default camera.
The first use requires remembered user consent. Every invocation displays a
visible notification before camera capture begins.

### camera.clip
```
{
  "deviceId": "string",       // optional
  "durationMs": 3000,         // required, max 60000
  "format": "mp4|webm",
  "maxWidth": 1280
}
```
Returns `{ format, durationMs, base64 }`.

## Speech-to-text (stt.*)

Local Whisper.net runs on this device - no audio leaves the box. The
model is downloaded on first use; until then every `stt.*` call returns
a clear error pointing the caller at the Voice Settings page.
**Privacy-sensitive: requires `NodeSttEnabled` in tray Settings.**

### stt.transcribe
Bounded fixed-duration mic capture + transcription.
```
{
  "maxDurationMs": 5000,      // required, > 0, max 30000
  "language": "en"            // optional BCP-47 tag or "auto" - falls back to SttLanguage setting
}
```
Returns `{ transcribed, text, durationMs, language, engineEffective: "whisper" }`.

### stt.listen
Mic capture with voice-activity detection. Returns when the user stops
speaking or after `timeoutMs`. Result is the full silence-bounded
utterance (all Whisper segments concatenated), not a partial first
segment.
```
{
  "timeoutMs": 30000,         // optional, default 30000, range 1000..120000
  "language": "auto"          // optional BCP-47 tag or "auto"
}
```
Returns `{ text, language, durationMs, segments[{ text, startMs, endMs }], engineEffective: "whisper" }`.

### stt.status
Engine readiness. No params. Carries no PII (no transcript history,
no language history, no device IDs, no model paths).
Returns `{ engine: "whisper", readiness, modelDownloadProgress, isListenWithVadSupported, isBoundedTranscribeSupported }`
where `readiness` ∈ `"ready" | "initializing" | "model-downloading" | "model-not-downloaded" | "unavailable"`.

## Text-to-speech (tts.*)

Four providers - Piper (local neural via Sherpa-ONNX, default), Windows
built-in speech, ElevenLabs (cloud), and MiniMax (cloud). Provider +
per-provider voice are configured in tray Settings.

### tts.speak
Speak text aloud on the Windows node.
```
{
  "text": "string",           // required
  "provider": "piper|windows|elevenlabs|minimax",  // optional; omit to use TtsProvider setting
  "voiceId": "string",        // optional, overrides the per-provider configured voice
  "model": "string",          // optional, cloud providers only
  "interrupt": false          // default false; true cuts off any in-progress playback
}
```
When `provider` is omitted and the configured provider isn't usable (no
cloud API key, Piper voice not downloaded), the node falls back to Windows
TTS so playback still happens. Explicit `provider` requests stay strict and
do not silently reroute. Returns `{ spoken, provider, requestedProvider, fellBack, contentType, durationMs }`
where `provider` is the provider that actually spoke.

### tts.status
TTS provider readiness. No params. Carries no PII (no voice ids, no key
fragments, no device names).
Returns `{ configuredProvider, effectiveProvider, willFallBack, providers[{ provider, readiness, isReady }] }`
where `effectiveProvider` is what would run now after fallback and `readiness`
∈ `"ready" | "needs-api-key" | "needs-voice" | "voice-not-downloaded" | "unavailable"`.
The configured/effective view reflects configured defaults only; explicit
`tts.speak` provider requests stay strict and may not match the default
snapshot.

## Local model inference (ollama.*)

Runs against a separately installed Windows Ollama service (not the
app-managed Local AI gateway provider). Any paired active gateway, local
or remote, may invoke it once enabled. **Requires
`NodeOllamaInferenceEnabled` in tray Settings > Permissions (opt-in,
default off).**

### ollama.models
List locally installed Ollama models. Read-only inventory - no prompt
or chat content. No params.
Returns `{ provider, models[{ name, size, modifiedAt, family, parameterSize, quantization, contextWindow, capabilities, loaded }] }`.

### ollama.chat
Send a single-turn chat prompt to a local Ollama model.
**Privacy and resource sensitive** - sends prompt content to a locally
running model and consumes CPU/GPU. Only one chat runs at a time per node;
a concurrent call while one is in flight returns an error.
```
{
  "model": "string",          // required
  "prompt": "string",         // required, max 128000 chars
  "system": "string",         // optional, max 32000 chars
  "temperature": 0.7,         // optional, 0..2
  "maxTokens": 512,           // optional, default 512, max 8192
  "timeoutMs": 120000         // optional, default 120000, max 600000
}
```
Returns `{ provider, model, response, usage?: { promptTokens, completionTokens }, timings?: { loadMs, totalMs } }`.

Newer gateways with the bundled Ollama plugin expose these commands through
the `node_inference` agent tool. Older gateways can invoke them through the
generic `nodes` tool after both exact command names are added to
`gateway.nodes.allowCommands`. Feature-detect the node's effective commands;
do not infer support from the gateway version.

## App control (app.*)

Read-only and small write operations targeting the running tray. Used
by the command palette and by automation that wants to drive the UI.

### app.navigate
Navigate the companion app to a specific page.
```
{"page": "home|sessions|settings|chat|voice|connection|capabilities|conversations|...""}
```
Returns `{ navigated, page }`.

### app.status
Current connection / node state.
No params. Returns `{ connectionStatus, overallState, operatorState, nodeState, nodeConnected, nodePaired, nodePendingApproval, nodeError, gatewayVersion, sessionCount, nodeCount }`.
For agent-facing connection troubleshooting, prefer `app.connection.status`.

### app.sessions
Active sessions, optionally filtered by agent.
```
{"agentId": "string"}        // optional
```
Returns array of `{ Key, Status, Model, AgeText, tokens }`.

### app.agents
List agents from the connected gateway. No params. Returns the raw
agents JSON array.

### app.nodes
List connected nodes and their capabilities. No params. Returns array
of `{ DisplayName, NodeId, IsOnline, Platform, CapabilityCount }`.

### app.config.get
Read gateway configuration value at a dot-path.
```
{"path": "string"}           // optional; omit to fetch the full config tree
```
Returns the config subtree (or full config) as JSON.

### app.settings.get
Read a local app setting by name.
```
{"name": "string"}           // required
```
Returns the setting value (type depends on the setting).

### app.settings.set
Set a local app setting, persist it, and apply the same reconnect/reload behavior as saving settings in the app UI.
```
{"name": "string", "value": "string"}  // both required
```
Returns `{ name, value }`; runtime apply failures surface as tool errors.

### app.menu
Get tray menu state (status, session count, node count). No params.
Returns array of menu items; the status item includes `status`, `overallState`, `nodeState`, and `nodeError`.

### app.search
Search the command palette and return matching commands.
```
{"query": "string"}          // required
```
Returns array of `{ Title, Subtitle, Icon }`.

### app.dashboard.url
Build the same gateway dashboard URL the tray opens.
```
{"path": "string"}           // optional
```
Returns `{ url, credentialSource, usesSharedGatewayToken, hasTokenQuery }`.

## App connection diagnostics and setup (app.connection.*)

Local MCP-only tools for connection diagnostics, setup, pairing approvals, and
targeted reconnects. These tools are not advertised to the remote gateway node
transport. Read-only diagnostics redact token values and expose credential
presence/outcome only.

### app.connection.status
Read-only connection diagnostics for agents and CLIs. No params. Returns:
`{ schemaVersion, connectionState, effectiveMode, legacyConnectionStatus, gateway, protocol, operator, node, mcp, browserProxy, pendingActions, retry, diagnostics }`.

The payload includes the active gateway id/name/url, operator and node role
states, credential sources/statuses, MCP enabled/running/error state, browser
proxy shared-token caveat, pending approval commands, retry hints inferred from
recent diagnostics, and recent connection diagnostic events. `effectiveMode`
reflects Settings mode (`EnableNodeMode` / `EnableMcpServer`); `node.intended`
reflects the manager snapshot plus current Node mode setting. `gateway.packageVersion`
is the installed Gateway npm package version reported by `hello-ok`; it is
separate from the wire protocol. `protocol` reports the accepted Gateway wire
protocol, the Windows-supported current/minimum/maximum values (currently
4/3/4), normalized compatibility
(`compatible`, `gateway_too_old`, `gateway_too_new`, `mismatch`, or `unknown`),
the operator/node source, available Gateway protocol expectations, and whether
the condition is retryable. During an active mismatch, `selectedProtocol` comes
only from that connection attempt and is otherwise null, even if cached Gateway
self information remains available.

### app.connection.gateways
Read-only saved gateway diagnostics. No params. Returns:
`{ activeGatewayId, count, gateways[] }`.

Each gateway includes id/name/url, active flag, local/v2 flags, lastConnected,
credential presence booleans (`hasSharedGatewayToken`, `hasBootstrapToken`),
browser-control port/caveat, and SSH tunnel metadata. Token values are never
returned.

### app.connection.applySetupCode
Apply a setup or QR code and connect the tray to that gateway.
```
{"setupCode": "string"}      // required
```
Returns `{ outcome, error, gatewayUrl, connected }`. Side effects: creates or
updates a gateway record, sets it active, persists credentials from the setup
code, and asks `GatewayConnectionManager` to connect.

### app.connection.connectSharedToken
Connect with a shared gateway token.
```
{
  "gatewayUrl": "string",    // required
  "token": "string"          // required
}
```
Returns `{ outcome, error, gatewayUrl, connected }`. Side effects: creates or
updates a gateway record, stores the shared token on that record, sets it active,
and asks `GatewayConnectionManager` to connect.

### app.connection.pendingApprovals
Read pending device/node pairing approvals from the connected gateway. No params.
Returns `{ connected, error, totalPending, devicePending[], nodePending[] }`.

### app.connection.approveDevicePairing
Approve a pending device pairing request.
```
{"requestId": "string"}      // required; "id" alias accepted
```
Returns the refreshed pending approvals payload plus `{ decision }`. Side effect:
calls the connected operator client's device-pair approval RPC.

### app.connection.rejectDevicePairing
Reject a pending device pairing request.
```
{"requestId": "string"}      // required; "id" alias accepted
```
Returns the refreshed pending approvals payload plus `{ decision }`. Side effect:
calls the connected operator client's device-pair rejection RPC.

### app.connection.approveNodePairing
Approve a pending Windows node pairing or command-trust request.
```
{"requestId": "string"}      // required; "id" alias accepted
```
Returns the refreshed pending approvals payload plus `{ decision }`. Side effect:
calls the connected operator client's node-pair approval RPC.

### app.connection.rejectNodePairing
Reject a pending Windows node pairing or command-trust request.
```
{"requestId": "string"}      // required; "id" alias accepted
```
Returns the refreshed pending approvals payload plus `{ decision }`. Side effect:
calls the connected operator client's node-pair rejection RPC.

### app.connection.reconnect
Reconnect the active gateway through `GatewayConnectionManager`. No params.
Returns `{ reconnected, error? }`. Side effect: disconnects and reconnects the
operator/node lifecycle for the active gateway.

### app.connection.reconnectNode
Reconnect only the Windows node role for the active gateway through
`GatewayConnectionManager`. No params. Returns `{ reconnected, error? }`.
Side effect: restarts node connection intent without changing the active gateway.

### app.chat.snapshot
Read the current native chat snapshot for local automation and diagnostics.
**READ-ALL:** returns recent chat text from the selected timeline and outgoing
queue text.
```
{"threadId": "string"}       // optional; "sessionKey" alias also accepted
```
Returns `{ connectionStatus, defaultThreadId, composeTarget, threads, queue, selectedTimeline }`.

### app.chat.send
Send a message through the same native chat provider used by the Chat UI.
```
{
  "message": "string",       // required
  "threadId": "string"       // optional; defaults to compose/default thread
}
```
Returns `{ sent, threadId, entryCount, turnActive, error? }`.

### app.chat.reset
Reset the target chat session through the gateway `sessions.reset` path.
```
{"threadId": "string"}       // optional; "sessionKey" alias also accepted
```
When `threadId`/`sessionKey` is omitted this resets the current compose/default
thread, which usually means the active chat session. Returns `{ reset, threadId,
error? }`.

### app.chat.queue.list
List native chat outgoing queue entries.
**READ-ALL:** returns queued message text.
```
{"threadId": "string"}       // optional; "sessionKey" alias also accepted
```
When `threadId`/`sessionKey` is omitted this returns all queued threads. Returns:
`{ defaultThreadId, requestedThreadId, totalCount, selectedThread, threads }`
where each thread has `{ threadId, count, messages }`, and each message has
`{ id, text, createdAt, sendState, errorText, canCancel }`. Queue message IDs
are ephemeral UI/provider IDs; read them from the current `app.chat.queue.list`
or `app.chat.snapshot` response and do not cache them across app lifetimes.

### app.chat.queue.cancel
Cancel/remove one native chat outgoing queue entry before it is sent.
```
{
  "queuedMessageId": "string", // required; from the current app.chat.queue.list or app.chat.snapshot queue
  "threadId": "string"         // required; "sessionKey" alias also accepted
}
```
Only `Queued` and `Failed` entries can be removed. `Sending` entries may already
have reached the gateway and are not canceled. Returns
`{ canceled, threadId, queuedMessageId, remainingCount, error? }`.

## Location (location.*)

### location.get
Get the device's current geographic location.
```
{
  "accuracy": "default|high",  // optional, default "default"
  "maxAge": 30000,             // ms; return a cached fix if younger than this
  "locationTimeout": 10000     // ms; fail if no fix within this time
}
```
Returns `{ latitude, longitude, accuracy (meters), timestamp (ms) }`.
Requires the Location capability to be enabled and OS location permission granted to the app.
The first use also requires remembered OpenClaw consent. Every invocation
displays a visible notification before location access begins.
Error `LOCATION_PERMISSION_REQUIRED` if the user has not granted location access.

## Device (device.*)

### device.info
Get static device metadata. No params.
Returns `{ deviceName, modelIdentifier, systemName, systemVersion, appVersion, appBuild, locale }`.

### device.status
Get live system health data.
```
{
  "sections": ["os","cpu","memory","disk","battery"]  // optional; omit for all
}
```
Returns a map with `collectedAt` (ISO-8601 string) and one key per section.
Each section may contain `{ error: "collection failed" }` if data was unavailable.
Legacy fields always present: `thermal`, `storage`, `network`, `uptimeSeconds`.

Battery sub-object: `{ level, state ("charging"|"discharging"|"unknown"), lowPowerModeEnabled }`.

**Privacy note**: `device.status` reveals battery level, network type, and disk usage.
Agents should request only the sections they need.

## Browser control proxy (browser.*)

### browser.proxy
Proxy an HTTP request to the local OpenClaw browser control host (Chrome DevTools Protocol server) running on gateway port + 2.
```
{
  "path": "/json/list",        // required - local control path
  "method": "GET",             // optional, default GET; allowed: GET|POST|DELETE
  "body": {},                  // JSON object, for POST/DELETE
  "query": {},                 // appended as query-string params
  "profile": "Default",        // optional browser profile name
  "timeoutMs": 20000           // optional, max 120000
}
```
Returns `{ result, files? }` - `files` is an array of `{ path, base64, mimeType }` if the response referenced local file paths.

Requires the gateway URL to have an explicit port (e.g. `ws://localhost:8080`).
The browser control host must be running locally on `127.0.0.1:<gatewayPort + 2>`.

---

## A2UI v0.8 grammar (for canvas.a2ui.push)

The `jsonl` argument is a string of newline-separated JSON-RPC-like messages.
Three message kinds are supported. **createSurface and v0.9 messages are
rejected.**

### Message kinds

```jsonc
// 1. Declare components for a surface (creates the surface if new).
{"surfaceUpdate": {
  "surfaceId": "string",
  "components": [ ComponentDef, ... ]
}}

// 2. Pick the root component and (optionally) styles. Send AFTER surfaceUpdate.
{"beginRendering": {
  "surfaceId": "string",
  "root": "componentId",
  "styles": { "primaryColor": "#FF6F61", "radius": 8.0, "spacing": 12.0 }
}}

// 3. Seed/update the data model bound by Path() values.
{"dataModelUpdate": {
  "surfaceId": "string",
  "contents": [
    {"key": "headline", "valueString":  "Hi"},
    {"key": "agreed",   "valueBoolean": false},
    {"key": "volume",   "valueNumber":  20.0}
  ]
}}

// 4. (Optional) Remove a surface.
{"deleteSurface": {"surfaceId": "string"}}
```

### ComponentDef

```jsonc
{"id": "uniqueId", "component": {"<ComponentName>": { ...props }}}
```

### Value bindings (inside a component prop)

| Form                          | Meaning                                |
|-------------------------------|----------------------------------------|
| `{"literalString": "x"}`      | Literal string                         |
| `{"path": "/key"}`            | Read/write the data model              |
| Plain string `"x"`            | Component-id reference (e.g. `child`)  |
| Plain number / bool           | Used directly for numeric/bool props   |

### Component catalog

| Category     | Name          | Notable props |
|--------------|---------------|---------------|
| Container    | `Row`         | `children: {explicitList: ["id", ...]}` |
| Container    | `Column`      | `children: {explicitList: ["id", ...]}` |
| Container    | `List`        | `children`, `dataBinding` |
| Container    | `Card`        | `child: "id"` |
| Container    | `Tabs`        | `tabItems: [{title, child}]` |
| Container    | `Modal`       | `child` |
| Container    | `Divider`     | `axis: "horizontal" | "vertical"` |
| Display      | `Text`        | `text: Lit/Path`, `usageHint: "h1|h2|h3|h4|h5|body|caption"` |
| Display      | `Image`       | `url: Lit/Path`, `fit: "contain|cover|fill|none"`, `usageHint` |
| Display      | `Icon`        | `name: Lit("settings"\|...)` |
| Display      | `Video`       | `url`, `autoplay`, `controls` |
| Display      | `AudioPlayer` | `url`, `controls` |
| Interactive  | `Button`      | `child`, `primary: bool`, `action: {name, ...context}` |
| Interactive  | `CheckBox`    | `label: Lit/Path`, `value: Path` |
| Interactive  | `TextField`   | `value: Path`, `textFieldType: "shortText|longText|obscured"` |
| Interactive  | `DateTimeInput` | `value: Path`, `mode: "date|time|datetime"` |
| Interactive  | `MultipleChoice` | `value: Path`, `options: [{value, label}]` |
| Interactive  | `Slider`      | `value: Path`, `minValue`, `maxValue`, `step` |

Lit/Path = the value-binding shapes from the previous section.

### Minimal "hello world" payload

```
{"surfaceUpdate":{"surfaceId":"hello","components":[{"id":"helloText","component":{"Text":{"text":{"literalString":"Hello, world!"},"usageHint":"h1"}}}]}}
{"beginRendering":{"surfaceId":"hello","root":"helloText"}}
```

Pass this as the `jsonl` value (a single JSON string with `\n` between messages).

---

## Token-efficient call patterns

1. **Skip `--node` / `--idempotency-key`** - they're ignored locally; including
   them just costs tokens. `--idempotency-key` triggers a stderr warning.
2. **Omit `--params` when the command takes no args** (`camera.list`,
   `canvas.hide`, `canvas.a2ui.reset`, `canvas.a2ui.dump`, `canvas.caps`,
   `system.execApprovals.get`).
3. **Large A2UI payloads** - write the JSONL to a file under the system temp
   directory and pass `{"jsonlPath": "<path>"}`. The capability rejects paths
   outside `%TEMP%`. Or pass `--params @<path>` to load the entire JSON
   argument object from disk.
4. **Big binary results (snapshots, captures)** - output is base64 in stdout.
   Pipe to a file (`> capture.json`) instead of letting the agent read it
   inline.
5. **Errors are exit-code-driven** - check `$LASTEXITCODE` (or `$?` in bash)
   first, then read stderr only on non-zero. Exit 2 = your call is malformed.
6. **Debug with `--verbose`, not by sharing transcripts** - without
   `--verbose` the CLI shows only the first line of an HTTP error body and
   redacts long base64url runs. With `--verbose` it shows the full sanitized
   body. Treat any verbose output as containing potentially sensitive paths
   or partial command output before pasting it elsewhere.

## What's NOT exposed
- Pairing / device approval (gateway concept; doesn't apply locally).
- `chat.send`, `sessions.list`, `usage.list`, `node.list` - these belong to the
  operator-side `OpenClaw.Cli.exe`, not `winnode.exe`.
- Idempotency. The gateway de-dupes retries against `--idempotency-key`; local
  MCP does not. Retrying a `system.run` / `system.notify` / `canvas.present`
  call after a transient failure can double-execute the side effect.
- Wildcards in `--command`. The MCP server has an explicit allowlist; unknown
  commands return `Unknown tool: <name>`.
