# Onboarding Wizard

The onboarding wizard installs a new app-owned local WSL gateway on Windows and then runs OpenClaw onboard.

## Overview

On first launch, the wizard appears only when there is no usable saved gateway connection. Users with existing gateways manage connections from the tray app's Connections tab. The local WSL setup affordance in Connections is shown only when setup has not already created an app-owned WSL gateway on this device.

The setup flow walks users through:

1. **Security notice** - Device-trust warning before setup choices
2. **Welcome / Advanced** - Install app-owned WSL gateway or connect existing gateway from Settings
3. **Capabilities** - Recommended profile, inline Windows permission status, and install review
4. **Local setup progress** - Fresh app-owned `OpenClawGateway` WSL installation
5. **Gateway installed** - Explicit handoff from infrastructure setup to OpenClaw onboard
6. **OpenClaw onboard** - Gateway-driven provider/model/key configuration
7. **All set** - Feature summary, startup preference, and completion

The setup flow no longer configures remote/manual gateways inline. The Welcome page's **Connect to an existing gateway** option routes through `AdvancedSetupPage`, closes setup, and opens the tray app's Connections tab.

## Screen Details

### Welcome
Displays the OpenClaw icon, app title, and a brief description. Choosing local gateway setup runs the read-only WSL readiness gate before the Capabilities page or its Local AI decision UI can open. WSL2 environment failures, including disabled hardware virtualization, are shown as WSL readiness failures and block both Local AI and non-Local-AI local gateway setup. The readiness dialog can retry with a fresh inspection after the user resolves the reported problem. If an app-owned local WSL gateway already exists, the primary CTA reads **Install new WSL Gateway** and confirmation warns that the current OpenClaw WSL gateway and distro will be deleted. If only an external gateway exists, the CTA remains **Set up locally** and confirmation explains that the external connection remains available in Connections.

### Local setup progress
Installs and connects a new app-owned `OpenClawGateway` WSL instance from a clean WSL baseline. If the WSL platform is missing or its optional component is not initialized, setup requests administrator approval to install it, re-inspects readiness, and reports when a Windows restart is required. Setup does not export from or mutate an existing user Ubuntu distro; if WSL cannot create the named app-owned distro directly, setup fails with an actionable update message. Cleanup automatically unregisters a distro only when durable OpenClaw evidence is paired with exactly one readable current-user WSL registration whose canonical base path matches the expected managed install path. Automatic orphan-directory cleanup requires a marker bound to that exact path. An unproven same-named distro or leftover data directory is preserved unless the user explicitly confirms its permanent replacement in the setup UI or passes `--confirm-destructive`. When replacing an app-owned local gateway, the removal step is shown as part of progress and can be retried on failure.

The managed distro is locked down and is not intended to be a normal interactive Ubuntu profile. For editing `openclaw.json` as the `openclaw` user and using root for protected-file administration, see [Managing the locked-down WSL gateway](WSL_GATEWAY_ADMIN.md).

### Capabilities and Windows permissions

The Capabilities page applies the selected profile to both setup config and runtime `Node*` settings. Inline Windows permission rows are shown only for capabilities that need OS-level state (camera, microphone, location, screen capture). Notifications are always shown as an app-level permission. Screen capture is passive: Windows asks what to share each capture through the Graphics Capture picker.

### OpenClaw onboard

After OpenClaw onboard completes-or when the user explicitly skips it-local setup runs the installed gateway CLI's non-interactive baseline initializer against the final runtime workspace, then writes fixed Windows-node guidance into a setup-owned managed section of that workspace's `AGENTS.md`. The section is replaced idempotently between markers, preserves user-authored `AGENTS.md` content and file permissions outside those markers, and does not modify OpenClaw source files. This helps the initial companion-app OpenClaw session know to use the Windows node / `nodes` tool for Windows desktop, files, screenshots, camera, notifications, browser proxy, and Windows command tasks.

Renders server-defined setup steps via RPC (`wizard.start` / `wizard.next`). The gateway controls the flow - steps can be:
- **Note** - informational messages
- **Confirm** - yes/no decisions
- **Text** - free-form input (with PasswordBox for sensitive fields like API keys)
- **Select** - radio button choices (e.g., AI provider selection)
- **Progress** - loading indicator for background operations

If the gateway doesn't support the wizard protocol or is unreachable, this screen shows an "offline" message and can be skipped.

The wizard keeps recovery choices visible while setup steps are running so users can start the wizard again or skip it for now if an auth flow stalls. If the gateway restarts or the wizard connection is lost while setup is running, the same recovery choices are presented in the error state so the user is not trapped retrying a broken session.

Gateway-driven onboarding does not show a Back action. There is no dedicated `wizard.back` RPC. Gateways can instead provide in-band `__back` or `back` options, which protocol clients submit through `wizard.next`; Companion intentionally filters those options because its former local payload replay displayed stale state while the authoritative Gateway session remained on a later step. Users can restart onboard or skip and exit from **More options** instead.

Exact Gateway 2026.7.1 has a terminal compatibility path for an app-managed local WSL gateway. When the final `model-check` answer produces WebSocket close 1012 before the gateway can return `done`, setup retries the temporary `NoListener` state and the typed snapshot-changed race that can occur while the listener is restarting. Other unknown or conflicting endpoint ownership fails immediately, and no credential is sent until the managed endpoint is verified again. A retryable startup close 1013 remains inside the existing reconnect timeout. Setup completes only after a fresh authenticated `hello-ok` handshake. Other versions and steps keep the normal managed-local wizard replay behavior with the same bounded ownership wait; remote gateways and other disconnects do not enter this recovery path.

The headless setup engine also treats one terminal wizard payload as completion instead of failure. When the answers applied by the wizard restart the gateway, the gateway can tear down its own hosted wizard TUI and return a terminal payload whose error is exactly `Error: TUI exited from signal SIGTERM`. Setup accepts that result only when the payload is terminal and the request it just sent answered the authoritative final step, so the wizard is not cancelled after it already finished. The final step must be a plain acknowledgement note with no options whose id or title normalizes to `done`, and when the gateway supplies step position metadata it must also be the last step. An earlier `SIGTERM`, a progress poll, a replayed wizard session, any answerable step, any other step id or title, a non-terminal payload, and any other message (different signal, extra text, or different casing) all keep the wizard failure. Only surrounding whitespace is tolerated in the message. Reload-mode restoration, the one-shot managed restart, health verification, and provenance checks are unchanged and still fail closed.

When the gateway config wizard surfaces an error and the active gateway is an app-managed WSL distro, the error state also offers **Open terminal** and **Restart gateway**. The wizard does not parse or classify the gateway's error text; it leaves the message visible and selectable so the user can copy any command the gateway reports. The buttons reuse the shared `GatewayTerminalLauncher` and `WslGatewayController` (in `OpenClaw.Connection`, also used by the Connections tab). Restart re-enters the gateway config wizard (the provider/model onboarding step - not the whole V2 onboarding, and without re-installing the WSL distro) so fixes such as newly-installed tools are picked up on `PATH`. Because the gateway restart clears its wizard session, this resumes at the first config question rather than the exact step that failed. Detection is gated on `GatewayRecord.SetupManagedDistroName`, so it never appears for remote/SSH gateways.

### All set
Displays a completion summary, a Launch at startup toggle, and a Finish button that saves the startup preference before restarting the tray. Launch at startup defaults on so OpenClaw is ready after reboot.

## Security

The onboarding wizard follows these security practices:

- **Input validation**: Setup codes limited to 2KB, decoded JSON validated, gateway URLs checked via `GatewayUrlHelper`
- **URI scheme whitelists**: Only `ms-settings:` for permissions and `http/https` for browser-launch links
- **Token protection**: Query params stripped from all log output
- **Gateway-owned pairing**: Device approval uses the gateway CLI/API path so scope checks, token issuance, audit, and broadcasts stay centralized
- **Error sanitization**: Exception details logged but not shown to users

## Credential Storage

Gateway credentials are registry-backed. Setup codes and QR payloads create or update a `GatewayRecord`; bootstrap credentials live in `GatewayRecord.BootstrapToken`, long-lived manual tokens live in `GatewayRecord.SharedGatewayToken`, and post-pairing device tokens are saved in the per-gateway identity directory. `SettingsManager` may read legacy `Token` / `BootstrapToken` JSON fields for migration, but it does not write them back.

## Localization

All user-visible strings use `LocalizationHelper.GetString()` with the `Onboarding_*` key namespace. Supported languages are discovered from the `Strings/<locale>/Resources.resw` directories; the current locales are English, French, Dutch, Chinese Simplified, and Chinese Traditional.

Translations are AI-generated following the repo convention. Technical terms (Gateway, Token, Node Mode) are kept in English across all locales.

## Developer Guide

See [DEVELOPMENT.md](../DEVELOPMENT.md#developing--testing-the-onboarding-wizard) for build instructions, environment variables, and testing workflow.

### Test Isolation

`SettingsManager` loads `%APPDATA%\OpenClawTray\settings.json` by default. Onboarding tests must not use `new SettingsManager()` without an isolated settings directory, because local user settings such as `EnableNodeMode=true` change setup behavior.

Use a temp settings directory for tests that construct `SettingsManager`, or set `OPENCLAW_TRAY_DATA_DIR` before the test process starts.

### Key Files

| Path | Purpose |
|------|---------|
| `src/OpenClaw.SetupEngine.UI/SetupWindow.xaml(.cs)` | Tray-hosted setup shell, run lock, preview routing, and page navigation |
| `src/OpenClaw.SetupEngine.UI/Pages/SecurityNoticePage.xaml(.cs)` | First-run device-trust warning before setup choices |
| `src/OpenClaw.SetupEngine.UI/Pages/WelcomePage.xaml(.cs)` | Install-new-WSL vs connect-existing choice and existing-gateway replacement prompt |
| `src/OpenClaw.SetupEngine.UI/Pages/AdvancedSetupPage.xaml(.cs)` | Connect-existing handoff to Connection settings |
| `src/OpenClaw.SetupEngine.UI/Pages/CapabilitiesPage.xaml(.cs)` | Capability profile, inline Windows permission status, and install review |
| `src/OpenClaw.SetupEngine.UI/Pages/ProgressPage.xaml(.cs)` | WSL gateway install progress and gateway-installed handoff |
| `src/OpenClaw.SetupEngine.UI/Pages/WizardPage.xaml(.cs)` | OpenClaw onboard provider/model/key wizard driven by gateway `wizard.*` frames |
| `src/OpenClaw.SetupEngine/GatewayWizardRestartRecoveryPolicy.cs` | Exact terminal-restart classification and bounded restart provenance/reconnect retry policy |
| `src/OpenClaw.SetupEngine.UI/Pages/CompletePage.xaml(.cs)` | Success, failure, log/help, and startup preference summary |
| `src/OpenClaw.SetupEngine.UI/Pages/SetupPermissionHelper.cs` | Passive Windows permission checks and inline permission rows |
| `src/OpenClaw.Connection/GatewayRegistry.cs` | Persistent gateway records and migration target |
| `src/OpenClaw.Connection/GatewayConnectionManager.cs` | Operator/node connection lifecycle used by onboarding |
| `src/OpenClaw.Tray.WinUI/Services/SetupExistingGatewayClassifier.cs` | Existing gateway classification for Welcome and startup gating |
