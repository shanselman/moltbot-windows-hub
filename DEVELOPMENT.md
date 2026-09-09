# Development Guide

A comprehensive guide for building, running, and contributing to the OpenClaw Windows Hub.

## Table of Contents

- [Prerequisites](#prerequisites)
- [Project Structure](#project-structure)
- [Building](#building)
- [Architecture Overview](#architecture-overview)
- [Documentation and Diagrams](#documentation-and-diagrams)
- [Testing](#testing)
- [CI/CD](#cicd)
- [Contributing](#contributing)

## Prerequisites

### Required

- **.NET 10 SDK** - [Download here](https://dotnet.microsoft.com/download)
- **Windows 10/11** - WinUI 3 and Windows App SDK require Windows 10 version 1903 or later
- **Node.js LTS with npm** - Required by the WinUI build to restore JavaScript build assets
- **Windows 10 SDK** - Required for WinUI builds
- **WebView2 Runtime** - Usually pre-installed on Windows 10+ ([Manual download](https://developer.microsoft.com/microsoft-edge/webview2/))
- **Visual Studio 2022** (optional) - For easier development and debugging with WinUI 3 designer support

Run `.\scripts\setup-dev.ps1` from the repository root to install or verify local prerequisites with winget. Agents can use `.\scripts\setup-dev.ps1 -RunValidation` to prepare the worktree and run the required closeout validation.

### For Testing

- **A running OpenClaw gateway instance** - The gateway provides the backend for chat, sessions, and notifications when validating gateway-mediated flows
  - Default gateway URL: `ws://localhost:18789`
  - You'll need a valid authentication token from your OpenClaw instance
- **Local MCP Server** - Windows node capabilities can also be validated without a gateway by enabling Local MCP Server in the tray Settings UI and using `winnode`

## Project Structure

This monorepo contains these main projects:

```
openclaw-windows-hub/
├── src/
│   ├── OpenClaw.Shared/              # Shared gateway client library
│   │   ├── OpenClawGatewayClient.cs  # WebSocket client for gateway protocol
│   │   ├── Models.cs                 # Data models (SessionInfo, ChannelHealth, etc.)
│   │   └── IOpenClawLogger.cs        # Logging interface
│   │
│   ├── OpenClaw.Connection/          # Gateway registry, credentials, connection manager
│   │
│   ├── OpenClaw.Chat/                # Native chat model and reducer
│   │   ├── ChatModels.cs             # Threads, entries, events, provider contract
│   │   └── ChatTimelineReducer.cs    # Timeline state transitions
│   │
│   ├── OpenClaw.Cli/                 # WebSocket connect/send/probe validator
│   │
│   ├── OpenClaw.WinNode.Cli/         # winnode local MCP/Windows-node CLI
│   │
│   ├── OpenClaw.SetupEngine/         # Local WSL gateway setup and setup-code support
│   │
│   ├── OpenClaw.SetupEngine.UI/      # WinUI setup wizard pages hosted by the tray app
│   │
│   ├── OpenClawTray.FunctionalUI/    # Small in-repo declarative WinUI helper
│   │   └── FunctionalUI.cs           # Components, hooks, elements, host control
│   │
│   └── OpenClaw.Tray.WinUI/          # WinUI 3 system tray application (primary)
│   │   ├── App.xaml.cs               # Main application, tray icon, gateway connection
│   │   ├── Services/                 # Settings, logging, hotkeys, deep links
│   │   ├── Windows/                  # UI windows (Settings, WebChat, Status, etc.)
│   │   ├── Dialogs/                  # Modal dialogs
│   │   └── Helpers/                  # Icon generation, utilities
│   │
├── tests/
│   ├── OpenClaw.Shared.Tests/        # Unit tests for shared library/capabilities/MCP
│   ├── OpenClaw.Connection.Tests/    # Gateway registry and connection manager tests
│   ├── OpenClaw.Tray.Tests/          # Tests for tray helpers (menu, settings, deep links)
│   ├── OpenClaw.WinNode.Cli.Tests/   # winnode CLI contract tests
│   ├── OpenClaw.SetupEngine.Tests/      # Setup engine tests
│   ├── OpenClaw.Tray.UITests/           # Native WinUI/A2UI UI tests
│   ├── OpenClawTray.FunctionalUI.Tests/ # FunctionalUI smoke tests
│   ├── OpenClaw.Tray.IntegrationTests/  # Real-process tray/MCP integration tests
│   └── OpenClaw.E2ETests/               # Gateway-mediated setup/connect E2E suites
│
├── .github/workflows/
│   └── ci.yml                        # GitHub Actions CI/CD workflow
│
├── openclaw-windows-node.slnx        # Solution file
├── README.md                         # User-facing documentation
└── DEVELOPMENT.md                    # This file
```

### Project Dependencies

```
OpenClaw.Tray.WinUI  ──depends on──▶  OpenClaw.Shared + OpenClaw.Connection + OpenClaw.Chat + OpenClaw.SetupEngine.UI
OpenClaw.WinNode.Cli  ──depends on──▶  OpenClaw.Shared
OpenClaw.SetupEngine.UI  ──wraps──▶  OpenClaw.SetupEngine
OpenClaw.SetupEngine  ──supports──▶  local WSL gateway setup
OpenClaw.*.Tests  ──test──▶  corresponding shared, connection, tray, setup, and CLI surfaces
```

### Key Subsystems

| Subsystem | Location | Purpose |
|-----------|----------|---------|
| **Gateway Communication** | `OpenClaw.Shared/OpenClawGatewayClient.cs` | WebSocket client with protocol v3, reconnect/backoff logic |
| **Connection Management** | `OpenClaw.Connection/` | Gateway registry, credential precedence, pairing, tunnels, and reconnect policy |
| **Notification System** | `OpenClaw.Tray.WinUI/App.xaml.cs` | Event routing, toast notifications, classification |
| **WebView2 Integration** | `OpenClaw.Tray.WinUI/Windows/ChatWindow.xaml.cs` | Embedded chat panel with lifecycle management |
| **Tray Icon Management** | `OpenClaw.Tray.WinUI/Helpers/IconHelper.cs` | GDI handle management, dynamic icon generation |
| **Session Tracking** | `OpenClaw.Shared/OpenClawGatewayClient.cs` | Session state, activity tracking, polling |
| **Settings & Logging** | `OpenClaw.Tray.WinUI/Services/` | JSON settings persistence, file rotation logging |

## Building

### Build the Entire Solution

From the repository root:

```bash
./build.ps1
```

This restores prerequisites as needed and builds the shared library, tray app, setup engine, and CLI tools with the required Windows runtime identifiers.

### Build Individual Projects

**Shared Library:**
```bash
dotnet build src/OpenClaw.Shared
```

**Tray App (WinUI):**
```bash
dotnet build src/OpenClaw.Tray.WinUI -r win-x64
```

### Platform and Architecture Notes

#### x64 vs ARM64

The solution supports both Intel/AMD (x64) and ARM (arm64) architectures:

- **Tray App**: Can be built for either architecture
  ```bash
  dotnet build src/OpenClaw.Tray.WinUI -r win-x64
  dotnet build src/OpenClaw.Tray.WinUI -r win-arm64
  ```

#### Cross-Platform Building

The Shared library is cross-platform and can be built on Windows, Linux, or macOS:

```bash
cd src/OpenClaw.Shared
dotnet build
```

The WinUI Tray app is Windows-only but can be built on Linux using:

```bash
dotnet build src/OpenClaw.Tray.WinUI -r win-x64 -p:EnableWindowsTargeting=true
```

### Running in Debug Mode

#### Visual Studio

1. Open `openclaw-windows-node.slnx` in Visual Studio 2022
2. Set `OpenClaw.Tray.WinUI` as the startup project
3. Press F5 to run with debugging

#### Command Line

```bash
dotnet run --project src/OpenClaw.Tray.WinUI -r win-x64
```

For verbose output:

```bash
dotnet run --project src/OpenClaw.Tray.WinUI -c Debug -r win-x64
```

### Publishing (Self-Contained)

For distribution:

```bash
dotnet publish src/OpenClaw.Tray.WinUI -c Release -r win-x64 --self-contained -o publish
```

This creates a standalone executable with all dependencies bundled.

#### Local Inno Installer Iteration

Use the local helper to build unsigned installer EXEs without waiting for CI:

```powershell
# Fast x64 installer for Windows Sandbox smoke tests
.\scripts\build-inno-local.ps1 -Arch x64 -Fast

# Recompile Inno only after changing installer.iss
.\scripts\build-inno-local.ps1 -Arch x64 -Fast -NoPublish

# Build both release-shaped architectures locally
.\scripts\build-inno-local.ps1 -Arch All
```

`-Fast` uses ZIP/no-solid compression for quick local iteration. CI release builds keep the default LZMA solid compression and Azure signing.

#### Dev identity and side-by-side installs

Release identity is the default for every configuration. Use `-DevBuild` on `build.ps1` or `-Dev` on `run-app-local.ps1` when you explicitly want the side-by-side dev identity:

```powershell
.\build.ps1 -Project WinUI -DevBuild
.\run-app-local.ps1 -Dev -Isolated
```

`-DevBuild` passes `-p:DevBuild=true` to the WinUI project and produces the dev app identity marker that CI verifies in the **Verify DevBuild identity marker** step. `installer.iss` also accepts `/DDevBuild=1` for side-by-side dev installers; `.\scripts\build-inno-local.ps1 -Dev` wires that flag for local installer iteration.

#### Onboarding and setup workflow helpers

The first-run Windows gateway onboarding wizard lives in `OpenClaw.SetupEngine.UI` and is hosted by `OpenClaw.Tray.WinUI`; see [docs/ONBOARDING_WIZARD.md](docs/ONBOARDING_WIZARD.md) for the page flow. The setup pipeline itself is documented in [docs/SETUP_ENGINE_REDESIGN.md](docs/SETUP_ENGINE_REDESIGN.md), including the Windows node context step that injects agent instructions into the WSL workspace.

Useful local scripts:

- `.\scripts\dev-reset-rebuild-launch.ps1` resets tray data, rebuilds, and optionally launches the app; add `-WipeWslDistro` for a full local WSL gateway reset.
- `.\scripts\validate-mxc-e2e.ps1` runs the formal WSL Gateway -> Windows node -> `system.run` MXC proof path for MXC-sensitive changes.

## Architecture Overview

### Native chat surface (Reactor + OpenClaw.Chat)

The Hub Chat tab (`src/OpenClaw.Tray.WinUI/Pages/ChatPage.xaml`) and the
tray ChatWindow popup (`src/OpenClaw.Tray.WinUI/Windows/ChatWindow.xaml`)
render their conversations with native WinUI 3 controls via the in-repo
Reactor components and `OpenClaw.Chat` model/reducer code.
The standard WebView2-hosted gateway web client remains available as a
settings-controlled fallback.

**Layering:**

```
src/OpenClaw.Tray.WinUI/Chat/    OpenClawReactorChatRoot · ReactorChatTimeline · ReactorChatComposer
                                 OpenClawChatDataProvider (adapts OpenClawGatewayClient → IChatDataProvider)
                                 ReactorChatHostExtensions (mounts Reactor into a XAML <Border>)
                                 IChatGatewayBridge        (testability seam over OpenClawGatewayClient)
        ▲ depends on
src/OpenClaw.Chat/               ChatThread · ChatTimelineState · IChatDataProvider · ChatTimelineReducer
        ▲ rendered by
Reactor.WinUI                    Component · hooks · ReactorHostControl · WinUI elements
```

**Lifecycle:**

- One `OpenClawChatDataProvider` instance lives on `App` (`App.ChatProvider`),
  created in `InitializeGatewayClient` and disposed inside
  `UnsubscribeGatewayEvents`. Both the Hub Chat tab and the tray ChatWindow
  consume the same provider - opening either surface shows identical state.
- `ReactorChatHostExtensions` mounts a dedicated `ReactorHostControl` for each
  XAML surface (`ChatPage`, `ChatWindow`) as the child of its
  `<Border x:Name="ChatHost"/>`. The
  surrounding chrome (NavigationView, popup header) stays XAML.
- Provider events fire on the WebSocket-receive thread; the provider
  marshals `Changed` / `NotificationRequested` callbacks through a
  dispatcher post delegate (`DispatcherQueue.AsPost()`), so Reactor
  components observe state on the UI thread.

**Adding new chat behavior:** model new events in `OpenClaw.Chat`'s
`ChatEvent` discriminated union, handle them in `ChatTimelineReducer`, and
emit them from `OpenClawChatDataProvider` in response to gateway signals.

### Gateway WebSocket Connection

The `OpenClawGatewayClient` manages the connection to the OpenClaw gateway:

**Connection Flow:**
1. WebSocket connects to gateway URL (default: `ws://localhost:18789`)
2. Client waits for `challenge` event from gateway
3. Client responds with authentication token
4. Gateway sends `connected` event confirming authentication
5. Client begins receiving events and can send requests

**Reconnect & Backoff Logic:**
- Automatic reconnection on disconnect or error
- Exponential backoff: 1s, 2s, 4s, 8s, 15s, 30s, 60s (max)
- Resets backoff counter on successful connection
- Connection state exposed via `StatusChanged` event

**Implementation:**
```csharp
// Backoff sequence in milliseconds
private static readonly int[] BackoffMs = { 1000, 2000, 4000, 8000, 15000, 30000, 60000 };
```

### Event Parsing and Notification Types

The gateway sends structured events over WebSocket. The client parses these into typed notifications:

#### Event Types

| Event Type | Handler | Description | UI Result |
|------------|---------|-------------|-----------|
| `challenge` | Initial handshake | Gateway requests authentication | Client sends token |
| `connected` | Authentication success | Gateway confirms connection | Status → Connected |
| `agent` (stream=job) | `HandleJobEvent` | Job/task activity | Activity indicator, tray badge |
| `agent` (stream=tool) | `HandleToolEvent` | Tool execution (exec, read, write, etc.) | Activity with tool name + args |
| `chat` | `HandleChatEvent` | Assistant chat messages | Toast notification for short messages |
| `health` | `ParseChannelHealth` | Channel health status | Channel status in tray menu |
| `session` | `HandleSessionEvent` | Session list updates | Session display refresh |
| `usage` | `ParseUsage` | Token usage, cost, requests | Usage info in status window |

#### Notification Classification

Notifications are classified using two strategies:

1. **Structured** (preferred): Events with explicit `type`, `category`, or `notificationType` fields
2. **Text-based** (fallback): Keyword matching on notification content

**Categories:**
- `health` - Blood sugar, glucose, CGM readings
- `urgent` - Critical alerts requiring immediate attention
- `reminder` - Calendar reminders, tasks
- `stock` - Stock price alerts
- `email` - Email notifications
- `calendar` - Calendar events
- `error` - Error messages
- `build` - CI/CD build status
- `info` - General information (default)

**Routing:**
- Notifications trigger Windows toast notifications (if enabled in settings)
- Stored in notification history for later review
- Can be filtered by category

### WebView2 Lifecycle

The `ChatWindow` uses Microsoft Edge WebView2 for embedded web content:

**Initialization:**
1. WebView2 control created in XAML
2. `CoreWebView2` environment initialized on window load
3. User data folder: `%LOCALAPPDATA%\OpenClawTray\WebView2`
4. Navigation guard prevents external navigation

**Lifecycle:**
```
Window Created → WebView2.EnsureCoreWebView2Async() → Navigate to Chat URL → User Interaction → Window Hidden (not disposed)
```

**Key Design Decisions:**
- **Singleton pattern**: Only one chat window instance exists
- **Hidden instead of disposed**: Window is hidden when closed to preserve state
- **Separate user data folder**: Isolates cookies/storage from browser
- **Navigation guard**: Prevents accidental navigation away from chat

**Implementation:**
```csharp
// Initialize WebView2 environment
await WebView.EnsureCoreWebView2Async();
WebView.CoreWebView2.Navigate(chatUrl);

// Navigation guard
WebView.CoreWebView2.NavigationStarting += (s, e) => {
    if (!e.Uri.StartsWith(allowedHost)) {
        e.Cancel = true;
    }
};
```

### GDI Handle Management

The tray icon system uses GDI handles for icon creation. Proper management prevents handle leaks:

**Icon Creation Pattern:**
```csharp
// Create bitmap
using var bitmap = new Bitmap(16, 16);
using var graphics = Graphics.FromImage(bitmap);
graphics.DrawSomething(...);

// Convert to icon (creates GDI handle)
var hIcon = bitmap.GetHicon();
var icon = Icon.FromHandle(hIcon);

// Clone to own the data
var result = (Icon)icon.Clone();

// CRITICAL: Destroy the GDI handle
DestroyIcon(hIcon);

return result;
```

**Why This Matters:**
- GDI handles are a limited system resource (10,000 per process on Windows)
- Not calling `DestroyIcon()` causes handle leaks
- Each tray icon update could leak a handle without proper cleanup
- The pattern: Create → Clone → Destroy ensures we own the icon data and release the GDI handle

**Caching:**
Icons are cached to avoid repeated GDI operations:
```csharp
private static Icon? _connectedIcon;
private static Icon? _disconnectedIcon;
// ... etc
```

### Session Tracking and Polling

The client tracks active agent sessions with intelligent display logic:

**Session State:**
- Main session: Primary user conversation
- Sub-sessions: Background tasks, tool executions
- Each session has: key, status, model, channel, activity

**Polling:**
- `RequestSessionsAsync()` called periodically (every 5 seconds when connected)
- Gateway responds with session list
- Client updates internal `_sessions` dictionary

**Display Selection Algorithm:**
1. Active main session always takes priority
2. Currently displayed session kept if still active (prevents flipping)
3. Falls back to most recently active sub-session
4. 3-second debounce prevents jitter during rapid changes

**Why This Matters:**
Without stable selection, the activity display would rapidly flip between sessions during concurrent operations, creating a poor user experience.

### Logging

File-based logging with automatic rotation:

**Log File:**
- Location: `%LOCALAPPDATA%\OpenClawTray\openclaw-tray.log`
- Rotation: When log exceeds 5MB, old log → `openclaw-tray.log.old`
- Thread-safe: Uses lock for concurrent writes

**Easy-button setup diagnostics:**
- Human summary: `%LOCALAPPDATA%\OpenClawTray\Logs\Setup\easy-setup-latest.txt`
- Machine-readable latest trace: `%LOCALAPPDATA%\OpenClawTray\Logs\Setup\easy-setup-latest.jsonl`
- Per-run traces: `%LOCALAPPDATA%\OpenClawTray\Logs\Setup\setup-*.jsonl`
- Contents are redacted and cover setup phases, WSL commands, pairing, gateway checks, repair, and remove lifecycle steps.

**Log Levels:**
- `INFO` - Normal operation (connections, events)
- `WARN` - Recoverable issues (reconnects, timeouts)
- `ERROR` - Failures (connection errors, exceptions)
- `DEBUG` - Detailed diagnostics (only in DEBUG builds)

**Format:**
```
[2026-02-01 12:34:56.789] [INFO] Gateway connected, waiting for challenge...
[2026-02-01 12:34:57.123] [WARN] Reconnecting with 2000ms backoff...
[2026-02-01 12:34:58.456] [ERROR] Connection failed: Host not found
```

**Debug Output:**
In DEBUG builds, logs are also written to Visual Studio Output window via `System.Diagnostics.Debug.WriteLine()`.

**Security:**
Sensitive data (authentication tokens) are never logged.

## Documentation and Diagrams

Maintained architecture, data-flow, and sequence diagrams use a paired source
and rendered artifact:

```text
docs/diagrams/<name>.excalidraw
docs/diagrams/<name>.svg
```

Embed the SVG in Markdown so GitHub renders it, and place an adjacent link to
the `.excalidraw` source so contributors can edit it. Keep labels synchronized
between both files. Every text element in the Excalidraw JSON must have explicit
`width` and `height` and use black text; container boxes remain transparent.

Do not add new Mermaid or maintained ASCII-art architecture diagrams. Small
state notations, directory trees, wire examples, and command-output snippets
may remain as fenced text when their value is the literal text rather than a
visual layout. Historical design documents may retain clearly labeled inline
sketches, but they must link to the current canonical diagram when one exists.

Run documentation validation directly with:

```powershell
.\scripts\validate-docs.ps1
```

`.\build.ps1` runs the same validator before compiling. It checks the named
custom Windows proof-pool inventory, maintained Markdown links and anchors,
rejects Mermaid and em dashes, verifies every Excalidraw/SVG pair, requires SVG
accessibility metadata, and confirms rendered labels match the editable source.

## Testing

Required agent validation lives in [AGENTS.md](AGENTS.md). For changes touching
tray UX, Settings, onboarding, chat/canvas, Command Center, Windows node
capabilities, local MCP, gateway pairing/connection, permissions, or
diagnostics, use the repo-local skill
`.agents/skills/openclaw-proof-validation/SKILL.md`: run the build/tests,
validate local MCP with `winnode --list-tools` plus the changed command, run
rubber-duck review for non-trivial changes, then launch the tray from this
worktree and drive the changed UI with computer-use / desktop automation as one
batched closeout pass before PR publication. Mid-development rubber-duck,
computer-use, or MCP validation is also appropriate when explicitly requested or
needed to unblock the work; agents should ask whether to run computer-use or
provide manual UI proof steps, while still enforcing required automated tests.

PRs should include `## Required proof pools`, `## Validation`, and
`## Real behavior proof` sections. Select stable pool IDs from
[`docs/PROOF_POOLS.md`](docs/PROOF_POOLS.md), or declare `none` with a reason.
Pool selection schedules capacity-dependent work but does not claim it ran.
Paste concrete after-change output, visible UI evidence for visual changes,
`winnode` output or raw MCP server JSON-RPC output for node commands, and
gateway invoke output for gateway-mediated behavior when available. The default
PR template includes these prompts.

### Running Unit Tests

The repository has multiple unit, UI, integration, and E2E test projects. Use [docs/TEST_COVERAGE.md](docs/TEST_COVERAGE.md) as the inventory of record.

```bash
# Run local-dev tests. E2E is intentionally excluded from the solution and
# runs in CI before merge; run it locally only when explicitly needed.
dotnet test

# Run with detailed output
dotnet test --verbosity detailed

# Run specific test class
dotnet test --filter "FullyQualifiedName~AgentActivityTests"
```

**Test Coverage:**
- `OpenClaw.Shared.Tests` covers models, gateway client behavior, capabilities, URL helpers, notification categorization, shell quoting, MCP, device identity, and WinNode client contracts.
- `OpenClaw.Tray.Tests` covers settings isolation, deep link parsing, onboarding state, setup code decoding, gateway health/chat helpers, security validation, wizard step parsing, gateway discovery, localization, and tray UI helpers.
- Additional projects cover connection management, setup engine behavior, `winnode`, FunctionalUI, native UI/A2UI, integration, and gateway-mediated E2E flows.
- See [docs/TEST_COVERAGE.md](docs/TEST_COVERAGE.md) for current method counts, runtime totals, and which lanes require network, WSL, real-process, or desktop prerequisites.

See [tests/OpenClaw.Shared.Tests/README.md](tests/OpenClaw.Shared.Tests/README.md) for detailed test documentation.

### Manual Testing Without Live Gateway

You can test the UI and basic functionality without a running gateway:

**Tray App:**
1. Launch the app: `dotnet run --project src/OpenClaw.Tray.WinUI`
2. Right-click tray icon → **Settings**
3. Enter a dummy gateway URL (e.g., `ws://localhost:18789`)
4. The app will show "Disconnected" status but you can:
   - Test the tray menu structure
   - Open the Settings page and configure preferences
   - Test auto-start functionality
   - View logs

### Manual Test Scenarios

#### Tray Icon States

1. **Disconnected (Gray)**: 
   - Start app without gateway running
   - Verify icon is gray
   - Verify tooltip shows "Disconnected"

2. **Connecting (Amber)**:
   - Configure valid gateway URL but don't start gateway yet
   - Restart app
   - Briefly observe amber icon during connection attempt

3. **Connected (Green)**:
   - Start gateway
   - Verify icon turns green
   - Verify tooltip shows "Connected"

4. **Error (Red)**:
   - Connect to gateway, then stop gateway
   - Verify icon turns red after timeout

5. **Activity Badge**:
   - Connect to gateway
   - Send a chat message that triggers tool use
   - Verify small colored dot appears on tray icon during tool execution

#### Notifications

1. **Toast Notifications**:
   - Connect to gateway
   - Send a message that triggers a chat response
   - Verify Windows toast notification appears (if enabled)
   - Click toast → should open relevant UI

2. **Activity / notification history**:
   - Right-click tray → **Activity Stream** or **Notification History**
   - Verify past notifications are listed
   - Test filtering by category

3. **Notification Settings**:
   - Settings → Disable notifications
   - Send a chat message
   - Verify no toast appears (but history still records it)

#### WebChat Panel

1. **Open WebChat**:
   - Right-click tray → **Open Web Chat**
   - Verify window opens with WebView2 content
   - Test sending a message

2. **Window State Persistence**:
   - Move/resize WebChat window
   - Close and reopen
   - Verify position/size restored (future feature)

3. **WebView2 Fallback**:
   - Test on system without WebView2 Runtime
   - Verify graceful fallback (opens browser instead)

## CI/CD

### GitHub Actions Workflow

The repository uses GitHub Actions for continuous integration and release automation.

**Workflow File:** `.github/workflows/ci.yml`

**Trigger Events:**
- Push to `main` branch
- Pull requests to `main`
- Git tags matching `v*` (e.g., `v1.2.3`) for releases

### Gateway release policy

- `src/OpenClaw.SetupEngine/GatewayReleasePolicy.cs` embeds the exact Gateway
  recommendation, protocol generation, security floor, validation evidence, and
  any distinct validated fallback for the Windows release.
- Setup and E2E install the exact recommendation. Product setup never resolves
  a moving npm dist-tag at runtime.
- `Gateway.Selection` supports `recommended`, `fallback`, and `exact`.
  `fallback` currently resolves to exact validated release `2026.6.11` and is
  never automatic. `exact` accepts only an embedded validated official release
  in product mode.
- A custom `Gateway.InstallUrl` must also specify an exact `Gateway.Version`.
  Setup labels it unverified and still requires an exact protocol-v4 handshake
  and matching server version after installation.
- `.github/workflows/gateway-release-candidate.yml` discovers official stable
  candidates and opens an evidence-only draft PR. It does not promote a
  candidate. Promotion requires exact-version Windows setup, pairing,
  reconnect, recovery, and Gateway-to-node invocation proof.
- `scripts/Test-GatewayReleaseCandidate.ps1` verifies stable GitHub release
  classification, SHA-512 npm integrity, registry signature, SLSA provenance,
  exact package/tag commit identity, stable release soak evidence, and protocol
  v4 at that exact commit. Unembedded candidates require provenance whose source
  commit matches the tag. Existing embedded recommendation/fallback evidence
  may use the explicit `-AllowEmbeddedPolicyEvidence` compatibility switch only
  when the integrity-verified package build commit matches the exact tag and
  the package integrity is already embedded in policy.
- Candidate evidence is discovery input only and cannot authorize an
  unembedded release. To exercise a candidate, first add a reviewed
  `GatewayReleaseStatus.Candidate` entry to `GatewayReleasePolicy`, then set
  `OPENCLAW_E2E_GATEWAY_VERSION` and run the setup/connect and recovery E2E
  shards with `--validate-gateway-candidate`.

### Build Matrix

The CI builds multiple configurations:

**Test Job:**
- Runs on `windows-latest`
- Builds the Shared library, Tray app, and eight test projects: Shared, Tray,
  Connection, WinNode CLI, Tray Integration, FunctionalUI, SetupEngine, and
  Tray UI
- Runs unit, integration, native UI, and accessibility tests across those
  projects; see [docs/TEST_COVERAGE.md](docs/TEST_COVERAGE.md)
- Verifies the WinUI DevBuild identity marker after native UI and accessibility
  tests
- Uses GitVersion for semantic versioning

**Build Job (Tray):**
- Matrix: `win-x64`, `win-arm64`
- Builds WinUI Tray app for both architectures
- Publishes self-contained executables
- Signs with Azure Trusted Signing (on tag releases only)

### Artifacts

On every build, the following artifacts are uploaded:

| Artifact | Contents | Purpose |
|----------|----------|---------|
| `openclaw-tray-win-x64` | x64 Tray app binaries | Testing, distribution |
| `openclaw-tray-win-arm64` | ARM64 Tray app binaries | Testing, distribution |

### Release Process

When a tag is pushed (e.g., `git tag v1.2.3 && git push origin v1.2.3`):

1. **Build & Sign:**
   - All artifacts built for x64 and ARM64
   - Executables signed with Azure Trusted Signing certificate

2. **Create Installers:**
   - Inno Setup creates Windows installers
   - Separate installers for x64 and ARM64

3. **GitHub Release:**
   - Automatic release created with tag name
   - Includes:
     - Installers: `OpenClawCompanion-Setup-x64.exe`, `OpenClawCompanion-Setup-arm64.exe`
     - Portable ZIPs: `OpenClawTray-{version}-win-x64.zip`, `OpenClawTray-{version}-win-arm64.zip`
   - Release notes auto-generated from commits

### Monitoring CI

**Check Latest Build:**
```bash
gh run list --repo shanselman/openclaw-windows-hub --limit 5
```

**View Specific Run:**
```bash
gh run view <run-id> --repo shanselman/openclaw-windows-hub
```

**Download Artifacts:**
```bash
gh run download <run-id> --repo shanselman/openclaw-windows-hub
```

### What CI Checks

✅ **Build Success:**
- All projects compile without errors
- Both x64 and ARM64 builds succeed
- Dependencies restore correctly

✅ **Unit Tests:**
- All tests pass
- No test failures or skips

✅ **Code Signing:**
- Executables signed (on releases)
- Signature verification passes

❌ **Not Currently Checked:**
- Linting/code style (no linter configured)
- Integration tests (no integration test suite)
- Code coverage metrics (no coverage reporting)

## Contributing

### Development Workflow

1. **Fork and Clone:**
   ```bash
   git clone https://github.com/YOUR_USERNAME/openclaw-windows-hub.git
   cd openclaw-windows-hub
   ```

2. **Create Feature Branch:**
   ```bash
   git checkout -b feature/my-new-feature
   ```

3. **Make Changes:**
   - Follow existing code style and patterns
   - Add tests for new functionality
   - Update documentation as needed

4. **Test Locally:**
   ```bash
   dotnet build
   dotnet test
   dotnet run --project src/OpenClaw.Tray.WinUI
   ```

5. **Commit and Push:**
   ```bash
   git add .
   git commit -m "Add my new feature"
   git push origin feature/my-new-feature
   ```

6. **Open Pull Request:**
   - Go to GitHub and open a PR from your branch
   - Describe your changes
   - Wait for CI to pass
   - Address review feedback

### Code Style

- **C#**: Follow standard .NET conventions
- **XAML**: Consistent indentation, organize resources logically
- **Naming**: Descriptive names, avoid abbreviations
- **Comments**: Explain "why", not "what"
- **Error Handling**: Use try-catch for expected failures, let unexpected exceptions bubble

### Adding New Features

**Example: Adding a New Gateway Event Type**

1. **Add Model** (`OpenClaw.Shared/Models.cs`):
   ```csharp
   public class MyNewEventData
   {
       public string Property { get; set; } = "";
   }
   ```

2. **Add Event** (`OpenClaw.Shared/OpenClawGatewayClient.cs`):
   ```csharp
   public event EventHandler<MyNewEventData>? MyNewEvent;
   ```

3. **Parse Event** (`OpenClawGatewayClient.cs`, in `ListenForMessagesAsync`):
   ```csharp
   if (eventType == "my_new_event")
   {
       var data = JsonSerializer.Deserialize<MyNewEventData>(json);
       MyNewEvent?.Invoke(this, data);
   }
   ```

4. **Handle in Tray App** (`OpenClaw.Tray.WinUI/App.xaml.cs`):
   ```csharp
   _gatewayClient.MyNewEvent += OnMyNewEvent;
   
   private void OnMyNewEvent(object? sender, MyNewEventData e)
   {
       _dispatcherQueue?.TryEnqueue(() =>
       {
           // Update UI
       });
   }
   ```

5. **Add Tests** (`tests/OpenClaw.Shared.Tests/`):
   ```csharp
   [Fact]
   public void MyNewEventData_DisplaysCorrectly()
   {
       var data = new MyNewEventData { Property = "test" };
       Assert.Equal("test", data.Property);
   }
   ```

### Troubleshooting

**Common Issues:**

1. **Build Error: "Windows SDK not found"**
   - Install Windows 10 SDK 19041 or later
   - Or build Shared library only: `dotnet build src/OpenClaw.Shared`

2. **WebView2 Error 0x8007000B on ARM64**
   - The tray app must be built for ARM64
   - Rebuild: `dotnet build src/OpenClaw.Tray.WinUI -r win-arm64`

3. **Tray Icon Not Appearing**
   - Check Windows notification area settings
   - Verify app is running (Task Manager)
   - Check logs: `%LOCALAPPDATA%\OpenClawTray\openclaw-tray.log`

4. **Gateway Connection Fails**
   - Verify gateway is running: `curl http://localhost:18789/health`
   - Check gateway URL in settings
   - Verify authentication token is correct
   - Check firewall settings

5. **Piper voice preview fails with "Piper TTS native library could not be loaded"**
   - The build copied a Visual C++ runtime that is too old for `onnxruntime`
     next to the exe. Local x64 builds take a compatible 14.38-or-newer runtime
     from your Visual Studio install. If the component is missing, stale, or
     incomplete for x64, the build falls back to a 14.29 runtime and warns
     (`OPENCLAW0001`).
   - Install or update "C++ Redistributable Update" via the Visual Studio
     Installer (Individual Components), rebuild, and confirm `msvcp140.dll` beside
     `OpenClaw.Tray.WinUI.exe` is 14.38 or newer.
   - The same failure shows up in `%LOCALAPPDATA%\OpenClawTray\openclaw-tray.log`
     as `DllNotFoundException` from `PiperTextToSpeechClient`.

### Getting Help

- **Issues**: [GitHub Issues](https://github.com/shanselman/openclaw-windows-hub/issues)
- **Discussions**: [GitHub Discussions](https://github.com/shanselman/openclaw-windows-hub/discussions)
- **Documentation**: [OpenClaw Docs](https://docs.molt.bot)

## Developing & Testing the Onboarding Wizard

The onboarding wizard is a 6-screen flow built with OpenClaw's minimal FunctionalUI helper layer for declarative C# WinUI. The chat page uses a WebView2 overlay for visual consistency with the post-setup chat experience.

### Building

The WinUI project requires platform-specific build targets. Use the build script:

```bash
./build.ps1 -Project WinUI   # Builds with correct -r win-x64 targets
```

Direct `dotnet build` without the script will fail with "WindowsAppSDKSelfContained requires a supported Windows architecture".

### Environment Variables

| Variable | Purpose |
|----------|---------|
| `OPENCLAW_FORCE_ONBOARDING=1` | Show onboarding wizard even if a token already exists |
| `OPENCLAW_SKIP_UPDATE_CHECK=1` | Skip the update dialog (useful during testing) |
| `OPENCLAW_LANGUAGE=fr-fr` | Override UI language (validated: en-us, fr-fr, nl-nl, pt-br, zh-cn, zh-tw) |
| `OPENCLAW_GATEWAY_PORT=19001` | Override default gateway port for local dev |
| `OPENCLAW_VISUAL_TEST=1` | Enable automatic screenshot capture on page transitions |
| `OPENCLAW_VISUAL_TEST_DIR=path` | Output directory for visual test screenshots |

### Testing the Wizard Locally

1. Start a local gateway (e.g., in WSL): `cd ~/openclaw && npx openclaw gateway`
2. Set env vars:
   ```powershell
   $env:OPENCLAW_FORCE_ONBOARDING = "1"
   $env:OPENCLAW_SKIP_UPDATE_CHECK = "1"
   ```
3. Build and run: `./build.ps1 -Project WinUI` then launch the exe
4. Navigate through all 6 screens to verify

### Architecture

- **FunctionalUI**: `src/OpenClawTray.FunctionalUI/` - Minimal declarative WinUI helper layer used by onboarding
- **Pages**: `src/OpenClaw.Tray.WinUI/Onboarding/Pages/` - Functional UI components for each wizard screen
- **Services**: `src/OpenClaw.Tray.WinUI/Onboarding/Services/` - State management, setup code decoder, permission checker, health check, input validation
- **Widgets**: `src/OpenClaw.Tray.WinUI/Onboarding/Widgets/` - Shared UI components (cards, step indicators, feature rows)
- **Window**: `src/OpenClaw.Tray.WinUI/Onboarding/OnboardingWindow.cs` - Host window with WebView2 overlay for chat
- **Helpers**: `src/OpenClaw.Tray.WinUI/Helpers/GatewayChatHelper.cs` - Shared WebView2 chat URL builder

---

*Made with 🦞 love by Scott Hanselman and the OpenClaw community*
