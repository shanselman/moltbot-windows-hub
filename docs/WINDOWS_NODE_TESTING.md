# Windows Node Testing Guide

## Overview

The Windows Node feature allows the tray app to receive commands from the OpenClaw agent (canvas, screenshots, screen recordings, camera, location, notifications, and controlled command execution). This is **experimental** and must be explicitly enabled in Settings.

## How to Enable

1. Open the tray app
2. Right-click → Settings
3. Scroll to "ADVANCED (EXPERIMENTAL)"
4. Toggle "Enable Node Mode" ON
5. Click Save

## Companion-App Setup Guidance

For app-owned local WSL setup, after OpenClaw onboard completes or is explicitly skipped, setup runs the pinned gateway CLI's non-interactive baseline initializer against the final runtime workspace and then injects fixed Windows-node guidance into that workspace's `AGENTS.md`. The injected block is setup-owned and idempotently replaced between managed markers, preserving user-authored content and file permissions outside those markers and leaving OpenClaw source files unchanged.

**Note on the apply script's WSL invocation.** The `WindowsNodeBootstrapContextStep` apply and rollback scripts are piped to `bash -s` via stdin (`RunInWslAsync(..., inputViaStdin: true)`) rather than the default `bash -c` argv path. This is required because `wsl.exe` performs shell variable expansion on argv before invoking bash, which would drop user-defined `$var` references in the multi-line script (`workspace='...'` followed by `mkdir -p "$workspace"` becomes `mkdir -p ""`). See `docs/WSL_EXE_ARGV_PITFALL.md` for the full writeup.

The guidance helps the first companion-app OpenClaw session route Windows desktop, files, screenshots, camera, notifications, browser proxy, and Windows command tasks through the Windows node / `nodes` tool.

## What You Can Test Now

### Agent-driven UI and MCP validation

For changes touching tray UX, Settings, onboarding, chat/canvas, Command Center, Windows node capabilities, local MCP, gateway pairing/connection, permissions, or diagnostics, use `.agents/skills/openclaw-proof-validation/SKILL.md`.

Short version: run required tests, collect a closeout proof pass with `.\run-app-local.ps1 -Isolated` when UI is involved, use computer-use or developer-provided screenshots/output for the active changed UI state, prove MCP with `winnode` or raw JSON-RPC, prove gateway paths when available, and include current-head concrete output under `## Real behavior proof`. Mid-development computer-use/MCP/rubber-duck validation is fine when explicitly requested or needed to unblock work.

### New command MCP contract

Every new Windows node call must be exposed through local MCP and `winnode`: register the capability, update `McpToolBridge.CommandDescriptions`, update `.agents/skills/winnode/SKILL.md`, add focused tests, and prove discovery/invocation with `winnode` or raw MCP JSON-RPC.

### 1. Settings Toggle
- Verify the toggle appears in Settings under "ADVANCED"
- Verify it saves and persists across app restarts

### 2. Node Connection
- Enable Node Mode and save
- Watch for "🔌 Node Mode Active" toast notification
- Check logs at `%LOCALAPPDATA%\OpenClawTray\openclaw-tray.log` for:
  ```
  [INFO] Starting Windows Node connection to ws://...
  [INFO] Node connected, waiting for challenge...
  [INFO] Registered capability: screen (2 commands)
  [INFO] All capabilities registered
  [INFO] Node status: Connected
  ```

### 3. Screen Capture Notification
- The first screen capture requires explicit remembered consent.
- Every screen capture shows a "📸 Capturing screen" toast before sensor access.

### 4. Command Center
- Open the tray status detail or launch `openclaw://commandcenter`
- In Node Mode, verify the window shows gateway channel health from node `health` events plus a synthesized local Windows node when operator `node.list` is not connected
- Check diagnostics for pairing approval, pending reapproval, stale health, all-stopped channels, allowlist filtering, browser control host availability for `browser.proxy`, and usage-cost gaps
- When only the synthesized local Windows node is available, verify its locally declared capabilities/commands are labeled unverified and are not counted as approved/effective
- For `pending-reapproval`, verify effective capabilities/commands remain unchanged, pending declarations are listed separately, and the copy action emits `openclaw nodes approve <pendingRequestId>`
- During a changed-command handshake, verify authoritative `pending-reapproval` replaces the generic node-pair approval card and exposes only the node-list trust command; explicitly typed device role-upgrade, Node mode off/hidden, and failure cards remain higher priority
- If the gateway omits a safe pending request ID, verify the copy action emits `openclaw nodes pending`, labels it as discovery only, and does not offer reconnect-after-approval yet
- Approve the request explicitly, reconnect the node, and verify the effective capability/command counts update and the pending reapproval warning clears
- Use "Copy fix" only for safe repair commands; privacy-sensitive commands remain informational unless you explicitly opt in on the gateway

## What Requires Gateway Support

These features need the gateway to send `node.invoke` commands:

| Command | Description | Expected Behavior |
|---------|-------------|-------------------|
| `canvas.present` | Show WebView2 window | Opens floating window with URL or HTML |
| `canvas.hide` | Hide canvas window | Closes the canvas window |
| `canvas.eval` | Execute JavaScript | Runs JS in canvas, returns result |
| `canvas.snapshot` | Capture canvas | Returns base64 PNG of canvas content |
| `canvas.a2ui.pushJSONL` | Legacy A2UI JSONL push | Routes through same renderer path as `canvas.a2ui.push` |
| `screen.snapshot` | Take screenshot | Requires remembered consent, shows a notification before every capture, and returns base64 |
| `screen.record` | Record short screen clip | Returns MP4/base64 metadata; requires explicit gateway allowlist |
| `system.notify` | Show notification | Displays toast notification |
| `system.run` | Controlled command execution | Uses local exec approval policy. A simple unquoted gateway command can bind to an allowlisted executable and run as direct argv; shell syntax remains one-time. Prompt decisions show a Windows Allow once / Always allow / Deny dialog when Allow always is safe. |
| `system.run.prepare` | Pre-flight command execution | Parses and validates a `system.run` invocation without executing it |
| `system.which` | Resolve executables | Returns absolute paths for requested binaries |
| `camera.list` | Enumerate cameras | Returns device IDs and names |
| `camera.snap` | Capture photo | Requires remembered consent, shows a notification before every capture, and returns a base64 image (NV12 fallback) |
| `camera.clip` | Capture video clip | Returns MP4/base64 metadata |
| `location.get` | Get Windows location | Requires remembered consent, shows a notification before every access, and uses Windows location permission/settings |
| `device.info` / `device.status` | Device metadata/status | Returns host/app/locale plus battery/storage/network/uptime payloads |
| `browser.proxy` | Proxy browser-control host requests | Requires Browser proxy bridge enabled, a compatible browser-control host listening on gateway port + 2, and matching browser-control auth |
| `tts.speak` | Speak text aloud | Requires Text-to-speech playback enabled in Settings; gateway mode also requires `tts.speak` in `gateway.nodes.allowCommands` |
| `stt.transcribe` | Bounded microphone transcription | Requires Speech-to-text enabled in Settings; uses local Whisper.net |
| `stt.listen` | Voice-activity microphone transcription | Returns when the user stops speaking or timeout expires |
| `stt.status` | Speech-to-text readiness | Returns Whisper.net model download/readiness state |
| `ollama.models` | Discover local Ollama chat models | Requires Share Windows Ollama in Permissions; reads the separately installed Ollama service on `127.0.0.1:11434` |
| `ollama.chat` | Run bounded local Ollama inference | Requires Share Windows Ollama, gateway command approval, and an exact local model returned by `ollama.models` |

### Cancelling an invocation

The gateway may send the `node.invoke.cancel` event with
`payload.invokeId` matching an active `node.invoke.request`. The Windows node
cancels only that invocation and completes its original result with
`ok: false, error: "cancelled"`; unknown or already-completed IDs are ignored.
The legacy `payload.requestId` spelling is also accepted for compatibility.
Operation completion is the linearization point: once capability execution
returns and atomically marks the invocation complete, later cancellation is too
late and the completed result is preserved.

For local MCP, send a JSON-RPC `notifications/cancelled` notification with
`params.requestId` matching the active `tools/call` JSON-RPC ID. Cancellation
must stop queued camera admission, recording delays/frame waits, and active
recording cleanup rather than only abandoning the HTTP waiter.

## Capabilities Advertised

When the node connects, it advertises these capabilities:
- `canvas` - WebView2-based canvas window
- `screen` - Screen snapshot and recording via Windows.Graphics.Capture
- `system` - Notifications, command execution (`system.run`, `system.run.prepare`, `system.which`), exec approval policy
- `camera` - MediaCapture photo/video capture (frame reader fallback)
- `location` - Windows.Devices.Geolocation
- `device` - Host/app metadata and lightweight status
- `browser` - Local `browser.proxy` bridge to a browser-control host on gateway port + 2, when enabled in Settings
- `tts` - Windows speech synthesis or ElevenLabs playback, when enabled in Settings
- `stt` - Local speech-to-text via Whisper.net, when enabled in Settings
- `local-inference` - `ollama.models` and `ollama.chat` against a separately installed Windows Ollama service, when explicitly enabled in Permissions

Local MCP clients also see MCP-only `app.*` commands such as `app.navigate`, `app.status`, `app.chat.snapshot`/`app.chat.send`/`app.chat.reset`, and `app.chat.queue.list`/`app.chat.queue.cancel`. Connection diagnostics and setup tools live under `app.connection.*`; use `app.connection.status` to inspect active gateway, operator/node credential state, MCP runtime status, browser proxy caveat, pending approval commands, and recent diagnostics, and `app.connection.gateways` to list saved gateway records without token values. These are local testing and automation hooks registered with the tray's MCP server and are not advertised to the gateway WebSocket.

## Security Features

- **URL Validation**: Canvas blocks `file://`, `javascript:`, localhost, private IPs, IPv6 localhost
- **Sensitive Capture Consent**: Screen snapshots, camera photos, and location reads require remembered per-capability consent
- **Per-use Capture Notification**: Every screen snapshot, camera photo, and location read is visibly indicated before sensor access
- **Screen Recording Allowlist**: `screen.record` must be explicitly allowed by the gateway and does not leave a hidden local MP4 copy on Windows
- **Session Attribution**: Only the optional top-level `sessionKey` stamped by the Gateway on `node.invoke.request` is trusted. Older Gateways omit it, so those invokes remain unattributed; a caller-supplied nested `args.sessionKey` is never used as a fallback.
- **Command Center Redaction**: recent node invoke activity records command name, status, duration, node id, and privacy class only; it does not store base64 payloads, screenshots, recordings, tokens, or command arguments
- **Node Mode Toggle**: Must be explicitly enabled by user
- **Ollama Sharing Toggle**: Off by default; enabling it shares Windows Ollama compute with the active paired local or remote gateway without changing the app-managed Local AI provider
- **Command Validation**: Only alphanumeric commands with dots/hyphens allowed

## Troubleshooting

### Node doesn't connect
- Check the active gateway in Connection settings. Gateway records live in `%APPDATA%\OpenClawTray\gateways.json`; post-pairing device tokens live under `%APPDATA%\OpenClawTray\gateways\<gateway-id>\device-key-ed25519.json`.
- Check logs for connection errors
- If logs report that the saved device identity could not be loaded, fix access to the existing identity file or use an explicit reset/re-pair action. The tray preserves an unreadable or corrupt identity instead of replacing it automatically.
- Verify gateway is running and accessible
- If only a bootstrap token exists, finish pairing or approve the device; paired device tokens take precedence on future connects.

### No "Node Mode Active" notification
- Ensure Windows notifications are enabled for the app
- Check if notification settings in the app are enabled

### Browser control stays enabled but never declares `browser`
- Setup-code / QR pairing can connect with a device token and leave `GatewayRecord.SharedGatewayToken` empty. Browser control will not declare `browser` / `browser.proxy` until a shared gateway token is saved for that gateway.
- Expect Connection capability pills to say **Needs gateway shared token** (not "Enabled, not active yet") only while the node WebSocket session is live and the shared token is missing. Disconnected or attached-but-disconnected states should ask for reconnect, not a token paste. The pill keeps that short label; its tooltip matches Command Center remediation detail.
- Command Center, Connection pill tooltips, and `app.connection.status` / `app.connection.gateways` use the same live-session rule for the shared-token caveat. For a remote (non-loopback) gateway without an explicit `BrowserControlPort` or SSH browser-proxy forward - including SSH tunnels whose effective URL is `127.0.0.1` - that caveat also mentions the endpoint/forward requirement; the shared token alone is not enough for usable remote browser.proxy.
- Enter the gateway shared token in Settings, save, and reconnect node mode. Bootstrap tokens are not the shared gateway token.

### `browser.proxy` reports no browser-control host
- Confirm the Browser proxy bridge toggle is enabled in Settings, then save and reconnect or re-pair if the gateway keeps an older command snapshot.
- The bridge is local-only: it calls `http://127.0.0.1:<gateway-port+2>` from Windows. For a gateway on `ws://127.0.0.1:18789`, the browser-control host must listen on `127.0.0.1:18791`.
- In managed SSH tunnel mode, keep Browser proxy bridge enabled so the tray forwards local gateway port + 2 to remote gateway port + 2. Settings shows a selectable preview of the exact `ssh -N -L ...` command.
- If using a manual SSH tunnel, add both forwards, for example: `ssh -N -L 18789:127.0.0.1:18789 -L 18791:127.0.0.1:18791 <user>@<host>`. If the SSH daemon is not listening on port 22, include `-p <ssh-port>`. If local and remote gateway ports differ, forward `<local-gateway-port+2>` to `127.0.0.1:<remote-gateway-port+2>`.
- Advanced split/remote topologies can pin the browser-control listener with the active gateway record's `BrowserControlPort` field in `%APPDATA%\OpenClawTray\gateways.json`. This value is a local TCP port on Windows and is scoped to that gateway record. Configure it only for a trusted browser-control forward, because `browser.proxy` sends the saved shared gateway token to the selected local listener for browser-control authentication. When a gateway uses SSH, tunnel-derived `localPort + 2` browser-control routing is used only when that gateway's managed tunnel has `IncludeBrowserProxyForward` enabled; otherwise set `BrowserControlPort` to a trusted manual forward.
- A local SSH forward is not enough if the remote browser-control host is not running. Command Center port diagnostics should show whether the local gateway and browser-control ports are listening and which process owns them.
- If Command Center shows the browser-control port listening but `browser.proxy` returns an auth error, verify the Windows Settings gateway token matches the browser-control host token/password. QR/bootstrap pairing can connect the node without saving a shared gateway token, but browser-control auth may still require one.
- A local smoke can verify the host dependency without proving gateway invoke auth: start the upstream browser-control host with a temporary no-secret config, confirm `http://127.0.0.1:<gateway-port+2>/` and `/tabs` return HTTP 200, then stop the captured host process. The full parity smoke is not complete until `openclaw nodes invoke --command browser.proxy` succeeds through the active gateway.

### Canvas window doesn't appear
- Check logs for `canvas.present` command received
- Verify URL is not blocked by security validation

### Camera permission denied
- If you see "Camera access blocked", enable camera access for desktop apps in Windows Privacy settings
- Packaged MSIX builds will show the system consent prompt automatically

### Local sandbox validation
- Sandbox integration tests are intended for local Windows development machines and may skip when the required local sandbox prerequisites are unavailable.
- Build the tray app before running local sandbox validation so the required sandbox helper binaries are present in the app output.
- MXC path grants use absolute Windows paths. OpenClaw adds each granted volume root as read-only only when the host probe selects BaseContainer and the exact emitted config remains BaseContainer-compatible: no backend `deniedPaths`, proxy/directional networking, denial capture, or least-privilege mode. MXC 0.8's request selector keeps that policy on BaseContainer, whose root grants are documented not to cascade. This supplies the root metadata access needed by common Windows path APIs without exposing child directories.
- OpenClaw supports MXC sandboxing for `system.run` only when `wxc-exec --probe` reports `tier: "base-container"` and `needsDaclAugmentation: false`. BFS, DACL, unknown, and augmented tiers are treated as unavailable for the Node Sandbox. General MXC and `isolation_session` diagnostics remain separate from this process-containment decision.
- OpenClaw classifies the Windows SKU before resolving or launching `wxc-exec`. Windows Server is explicitly unsupported because the current MXC probe can crash there. An indeterminate SKU check also fails closed and skips the native probe. This suppression does not erase the user's sandbox preference, so the existing host-fallback or strict-block policy remains visible and recoverable after an OS/runtime fix.
- OpenClaw never invokes `wxc-host-prep` and never adds a volume-root policy grant to BFS or the DACL fallback. DACL directory grants intentionally propagate, and [microsoft/mxc#648](https://github.com/microsoft/mxc/issues/648) documents unsafe descendant ACL rewriting in the current privileged helper.
- The upstream BaseContainer readiness and root-grant behavior is tracked in [microsoft/mxc#1109](https://github.com/microsoft/mxc/issues/1109).
- For MXC-related merge validation, prefer the formal script below because it sets the required gates and fails if MXC is skipped.

  ```powershell
  .\scripts\validate-mxc-e2e.ps1
  ```

### Full Gateway `system.run` MXC runtime proof
- The focused E2E below provisions a fresh WSL Gateway, starts an isolated tray instance, enables the explicit Windows UI API sandbox opt-in, sets local exec approval policy, invokes `system.run` through the real Gateway `node.invoke` path, and verifies tray MXC diagnostics show contained `mxc-direct-appc` execution for a bound `hostname.exe` allowlist rule, PowerShell and full-policy shell execution, and denied writes to the tray data directory.
- Run it when validating the Gateway/Windows node runtime path, not just direct MCP or shared library behavior.
- GitHub-hosted Actions runners do not provide a working MXC/AppContainer runtime. The regular cloud E2E matrix should report these MXC proofs as skipped while still running the rest of setup-connect. Run the proof on a local MXC-enabled Windows machine. Only set `OPENCLAW_RUN_MXC_E2E=1` in GitHub Actions when using an MXC-enabled self-hosted runner.
- Use `.\scripts\validate-mxc-e2e.ps1` for normal local validation. It sets `OPENCLAW_RUN_E2E` and `OPENCLAW_RUN_MXC_E2E`, runs the real Gateway MXC proofs, and fails if the MXC proof skips. `-AllowSkip` is only for documenting a non-MXC host, not for merge validation of MXC-related work.
- When reproducing this manually against an existing Gateway, confirm
  `gateway.nodes.denyCommands` does not block `system.run`,
  `system.run.prepare`, or `system.which`, then approve any
  `pending-reapproval` request with
  `openclaw nodes approve <pendingRequestId>`. Current gateways include these
  commands in the canonical Windows desktop defaults. Older or deliberately
  customized gateways may still need exact `gateway.nodes.allowCommands`
  entries.

  ```powershell
  .\build.ps1
  $env:OPENCLAW_REPO_ROOT = (Get-Location).Path
  $env:OPENCLAW_RUN_E2E = "1"
  dotnet test .\tests\OpenClaw.E2ETests\OpenClaw.E2ETests.csproj `
    --no-restore `
    --filter "FullyQualifiedName~RealGateway_SystemRun" `
    --logger "console;verbosity=normal" `
    -r win-x64
  ```

- Expected proof markers:
  - The bound-hostname proof succeeds with a local `**/hostname.exe` rule, logs `promptAttempted=false`, and reaches MXC as `shell=<direct-argv>`.
  - Gateway response contains PowerShell output `OPENCLAW_GATEWAY_SYSTEM_RUN_MXC_OK` with `exitCode=0`.
  - The denied-write proof targets a fresh file under the isolated tray data directory, returns non-zero, and leaves that file absent.
  - `openclaw-tray.log` contains `[mxc] system.run sandbox request` with `executor=mxc-direct-appc`, `contained=True`, `shell=<direct-argv>`, and `uiAllowWindows=True` for the PowerShell proof.
  - `openclaw-tray.log` contains `[mxc] system.run sandbox result` with `containment=mxc` for both the successful execution and the denied write.
- E2E artifacts are written under `TestResults\E2E\<run-id>` and skip known secret-bearing files such as gateway records and settings.

## Remaining Work (Roadmap)

1. ~~**system.run + exec approvals**~~ ✅ Implemented
    - `system.run` with PowerShell/cmd support
    - `system.run.prepare` pre-flight command
    - `system.which` command lookup
    - `system.execApprovals` allowlist flow with base-hash optimistic concurrency for remote edits
    - `system.run` environment override sanitizer blocks path/toolchain injection and secret-looking variables
2. ~~**screen.record**~~ ✅ Implemented
    - Graphics Capture video recording (MP4/base64)
3. ~~**camera.clip**~~ ✅ Implemented
    - Short webcam video capture (MediaCapture + encoding)
4. ~~**A2UI pushJSONL alias + device status**~~ ✅ Implemented
    - Legacy `canvas.a2ui.pushJSONL`
    - Safe `device.info` / `device.status`
5. ~~**Command Center diagnostics**~~ ✅ Implemented
    - Channel/node/usage/pairing/allowlist diagnostics and recent invoke timeline
6. **Packaging & consent prompts**
    - MSIX packaging with camera/screen capabilities for system prompts
7. **Test matrix & polish**
    - Canvas/screen/camera regression tests
    - Handle timeouts/disconnects, reduce verbose logging

## Files Involved

- `src/OpenClaw.Shared/WindowsNodeClient.cs` - Node protocol client
- `src/OpenClaw.Shared/Capabilities/*.cs` - Capability handlers
- `src/OpenClaw.Tray.WinUI/Services/Connection/GatewayRegistry.cs` - persistent gateway records
- `src/OpenClaw.Connection/GatewayConnectionManager.cs` - public lifecycle façade, operator/state/tunnel orchestration
- `src/OpenClaw.Connection/NodeConnectionCoordinator.cs` - node generation, start/recovery, connector events, and telemetry
- `src/OpenClaw.Connection/BootstrapTokenLifecycle.cs` - bootstrap/device-token handoff and durable clear timing
- `src/OpenClaw.Connection/DevicePairApprovalCoordinator.cs` - device role-upgrade approval and bounded reconnect
- `src/OpenClaw.Connection/CredentialResolver.cs` - device-token/shared/bootstrap credential precedence
- `src/OpenClaw.Tray.WinUI/Services/NodeService.cs` - Orchestrates capabilities
- `src/OpenClaw.Tray.WinUI/Services/ScreenCaptureService.cs` - screen snapshots
- `src/OpenClaw.Tray.WinUI/Services/ScreenRecordingService.cs` - screen recordings
- `src/OpenClaw.Tray.WinUI/Services/CameraCaptureService.cs` - camera photo/video capture
- `src/OpenClaw.Tray.WinUI/Windows/CanvasWindow.xaml` - WebView2 canvas
