# OpenClaw Windows Hub

![OpenClaw Windows Node banner](docs/assets/readme-banner.jpg)

[![CI](https://img.shields.io/github/actions/workflow/status/openclaw/openclaw-windows-node/ci.yml?branch=main&style=flat-square&label=ci)](https://github.com/openclaw/openclaw-windows-node/actions/workflows/ci.yml)
[![.NET](https://img.shields.io/badge/.NET-10.0-512bd4?style=flat-square)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License: MIT](https://img.shields.io/badge/license-MIT-green?style=flat-square)](LICENSE)
[![Discord](https://img.shields.io/discord/1456350064065904867?label=discord&logo=discord&logoColor=white&color=5865F2&style=flat-square)](https://discord.gg/clawd)

The native Windows companion for [OpenClaw](https://github.com/openclaw/openclaw). Connect your PC to a gateway, chat with your agents, and choose which Windows capabilities they can use.

[Download](https://docs.openclaw.ai/platforms/windows) | [Setup guide](docs/SETUP.md) | [Windows docs](https://docs.openclaw.ai/platforms/windows) | [Discord](https://discord.gg/clawd)

## Install

| Architecture | Installer |
|---|---|
| x64 | [OpenClawCompanion-Setup-x64.exe](https://github.com/openclaw/openclaw-windows-node/releases/latest/download/OpenClawCompanion-Setup-x64.exe) |
| ARM64 | [OpenClawCompanion-Setup-arm64.exe](https://github.com/openclaw/openclaw-windows-node/releases/latest/download/OpenClawCompanion-Setup-arm64.exe) |

Requires Windows 10 20H2 or later, or Windows 11. No source build is required.

On first launch, the setup wizard can install a dedicated local gateway in WSL or connect OpenClaw Companion to an existing gateway. If you do not have a gateway yet, choose **Install a local gateway (WSL)**.

## Uninstall

Go to **Settings → Apps → Installed apps**, find **OpenClaw Companion**, and click **Uninstall** (or use **Add or Remove Programs** in Control Panel). You'll be asked whether to also remove the local WSL gateway; choose **Yes** to unregister its WSL distro and generated state, or **No** to leave the gateway and that state in place.

Your settings file at `%APPDATA%\OpenClawTray\settings.json` is not removed automatically, and device identity files for gateways unrelated to the one you removed are preserved. Choosing **Yes** does also remove the removed gateway's own identity directory under `%APPDATA%\OpenClawTray\gateways\` and, if it was your only gateway, clears your root device tokens; delete `%APPDATA%\OpenClawTray\` manually for a fully clean uninstall. See [docs/SETUP.md](docs/SETUP.md#uninstalling) for details, including the headless `--uninstall --confirm-destructive` CLI path used for testing.

## 🔌 Node mode (agent control)

Use OpenClaw Companion for normal setup. You should not need to edit `openclaw.json` by hand.

1. Open **Companion Settings…** from the tray menu.
2. Open **Connection** and connect to your gateway. Complete any pending pairing approval shown by the app.
3. Open **Sandbox** and choose how agent-run programs should be contained.
4. Open **Permissions** and turn on **Node mode**.
5. Choose the capabilities this PC should offer. Changes save automatically.
6. Open **Command Center** to verify the node is connected and to resolve any gateway allowlist or reapproval warnings.

Node mode registers this PC as a node and advertises only the capabilities enabled in **Permissions**. Gateway policy and local Windows checks can still block a capability.

### Capabilities

| Capability | What it lets agents do |
|---|---|
| **System tools** | Run shell commands and scripts, subject to local exec approvals and sandbox policy |
| **Browser control** | Drive a compatible Chromium browser on this PC |
| **Camera** | Capture still images and short camera clips |
| **Canvas** | Present and interact with visual content in a hosted window |
| **Screen capture** | Take screenshots and short screen recordings |
| **Location** | Read this PC's approximate location |
| **Text-to-speech** | Speak text aloud through this PC's speakers |
| **Speech-to-text** | Transcribe microphone audio locally |

Notifications and basic device status are available when Node mode is active. Windows may request consent before camera, microphone, location, or screen features can run.

Privacy-sensitive capabilities should stay off unless you intend to use them. This includes camera capture, screen recording, microphone transcription, spoken output, and command execution.

### Gateway approvals and allowlists

OpenClaw applies more than one trust check:

- **Permissions** controls what this PC advertises.
- **Connection** shows pairing and reapproval requests.
- **Command Center** explains commands filtered by gateway policy and provides copyable repair commands for safe capabilities.
- **Advanced > Config** provides a schema-guided editor for the connected gateway's configuration.

After changing gateway command policy, approve any `pending-reapproval` request shown by the app and reconnect the node. The app never silently opts into privacy-sensitive gateway commands.

<details>
<summary>Advanced: externally managed gateway allowlist shape</summary>

Use your gateway's supported configuration tools when OpenClaw Companion cannot manage that gateway. Preserve existing entries and add only the exact commands you need. Wildcards such as `canvas.*` are not expanded.

```json
{
  "gateway": {
    "nodes": {
      "allowCommands": [
        "system.notify",
        "canvas.present",
        "canvas.hide",
        "screen.snapshot",
        "device.info",
        "device.status"
      ]
    }
  }
}
```

Canonical paired Windows nodes already receive the desktop `system.*` defaults,
including `system.run`, `system.run.prepare`, and `system.which`. Windows still
applies the local **Run system tools** switch, V2 exec approvals, and sandbox
policy. Commands outside the Windows gateway defaults, including
`screen.record`, `camera.snap`, `camera.clip`, `stt.transcribe`, and
`tts.speak`, require deliberate gateway opt-in. Reapprove and reconnect the
node after changing the effective command set.

**Share Windows Ollama** in the Permissions page is a separate opt-in. It
advertises `ollama.models` and `ollama.chat` so the active paired gateway,
whether local or remote, can use an Ollama service running on Windows loopback.
It does not change or reuse the app-managed Local AI gateway provider. Older
gateways require both exact Ollama commands in `gateway.nodes.allowCommands`;
newer gateways with the bundled Ollama plugin can expose them through the
`node_inference` agent tool.

</details>

See [Operator and node concepts](docs/OPERATOR_NODE_CONCEPTS.md) for the pairing and trust model, and [Windows node testing](docs/WINDOWS_NODE_TESTING.md) for command-level reference material.

## Sandbox command execution

The **Sandbox** page controls programs launched through the Windows node's `system.run` capability:

- **Locked Down** blocks internet, clipboard, and standard user folders.
- **Recommended** enables internet, read-only access to common folders, and clipboard read access.
- **Unprotected** allows broad folder and clipboard access. Use it only when you accept the added risk.
- Custom controls set folder access, network access, clipboard access, timeout, and output limits.

When enabled and available, the Windows node uses MXC process isolation for `system.run`. If MXC is unavailable and strict fallback blocking is off, OpenClaw can fall back to uncontained host execution for compatibility. The **Sandbox** page shows the current state and lets you choose the appropriate policy.

This sandbox covers commands run through the Windows node. Commands run directly on the gateway use the gateway's separate security controls.

## Features

- Native tray flyout with gateway, session, usage, channel, node, and activity status
- Companion Settings for connections, permissions, gateway configuration, diagnostics, and updates
- Native chat and Quick Send with the `Ctrl+Alt+Shift+C` global hotkey
- Command Center diagnostics with copyable repair guidance
- Toast notifications with smart categorization
- WebView2 Canvas and A2UI rendering
- Local MCP server for local tool integrations
- Background updates from GitHub Releases
- `openclaw://` deep links for automation

### Useful deep links

| Link | Action |
|---|---|
| `openclaw://settings` | Open Companion Settings |
| `openclaw://setup` | Open the setup wizard |
| `openclaw://chat` | Open Chat |
| `openclaw://commandcenter` | Open Command Center |
| `openclaw://send?message=Hello` | Open Quick Send with pre-filled text |
| `openclaw://logs` | Open the current log file |
| `openclaw://support-context` | Copy redacted support context |
| `openclaw://capability-diagnostics` | Copy capability and allowlist diagnostics |

Deep links are forwarded through IPC when OpenClaw Companion is already running.

### Local files

| Data | Default path |
|---|---|
| App settings | `%APPDATA%\OpenClawTray\settings.json` |
| Gateway registry | `%APPDATA%\OpenClawTray\gateways.json` |
| Logs | `%LOCALAPPDATA%\OpenClawTray\openclaw-tray.log` |
| Exec approvals | `%APPDATA%\OpenClawTray\exec-approvals.json` |

The default local gateway URL is `ws://localhost:18789`.

## For contributors

### Projects

| Project | Purpose |
|---|---|
| **OpenClaw.Tray.WinUI** | WinUI 3 tray app and Companion Settings |
| **OpenClaw.Connection** | Gateway registry, credential resolution, and connection manager |
| **OpenClaw.Shared** | Gateway client, Windows capabilities, diagnostics, and MCP bridge |
| **OpenClaw.Chat** | Native chat model and timeline reducer |
| **OpenClaw.WinNode.Cli** | `winnode` CLI for local Windows node and MCP invocation |
| **OpenClaw.SetupEngine** | WSL gateway installation and setup-code pairing |
| **OpenClaw.SetupEngine.UI** | WinUI setup wizard pages |
| **OpenClaw.Cli** | Gateway WebSocket validation CLI |
| **OpenClawTray.FunctionalUI** | Declarative WinUI helpers used by newer surfaces |

### Prepare the checkout

```powershell
.\scripts\setup-dev.ps1
.\scripts\setup-dev.ps1 -CheckOnly
.\scripts\setup-dev.ps1 -RunValidation
```

### Build

```powershell
.\build.ps1
.\build.ps1 -Project WinUI
.\build.ps1 -CheckOnly
```

Direct WinUI builds require a runtime identifier:

```powershell
dotnet build .\src\OpenClaw.Tray.WinUI\OpenClaw.Tray.WinUI.csproj -r win-x64
dotnet build .\src\OpenClaw.Tray.WinUI\OpenClaw.Tray.WinUI.csproj -r win-arm64
dotnet build .\src\OpenClaw.Tray.WinUI\OpenClaw.Tray.WinUI.csproj -r win-x64 -p:PackageMsix=true
```

### Run

`run-app-local.ps1` allows `main` by default. Pass `-AllowNonMain` when previewing a feature branch or linked worktree.

```powershell
.\run-app-local.ps1
.\run-app-local.ps1 -NoBuild
.\run-app-local.ps1 -AllowNonMain -Isolated
.\run-app-local.ps1 -AllowNonMain -Dev -Isolated
.\run-app-local.ps1 -AllowNonMain -Configuration Release -Isolated -UpdateChannel alpha
```

### Test

Set the repository root explicitly so tests also work in linked worktrees:

```powershell
$env:OPENCLAW_REPO_ROOT = (Get-Location).Path
dotnet test .\tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj
dotnet test .\tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj
```

These commands restore and build the test projects when needed. Use `--no-restore` only after each test project has built successfully in the current worktree.

### Documentation

| Topic | Document |
|---|---|
| Architecture ownership | [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) |
| Audio model asset integrity | [docs/AUDIO_MODEL_ASSETS.md](docs/AUDIO_MODEL_ASSETS.md) |
| Connection and pairing | [docs/CONNECTION_ARCHITECTURE.md](docs/CONNECTION_ARCHITECTURE.md) |
| Gateway, node, and exec flow FAQ | [docs/OPENCLAW_GATEWAY_NODE_EXEC_FAQ.md](docs/OPENCLAW_GATEWAY_NODE_EXEC_FAQ.md) |
| Onboarding wizard | [docs/ONBOARDING_WIZARD.md](docs/ONBOARDING_WIZARD.md) |
| Windows node behavior | [docs/WINDOWS_NODE_TESTING.md](docs/WINDOWS_NODE_TESTING.md) |
| Local MCP mode | [docs/MCP_MODE.md](docs/MCP_MODE.md) |
| Managed WSL gateway | [docs/WSL_GATEWAY_ADMIN.md](docs/WSL_GATEWAY_ADMIN.md) |
| Development | [DEVELOPMENT.md](DEVELOPMENT.md) |

## License

[MIT](LICENSE)
