# AGENTS.md

## Required Validation After Every Change

All agents working in this repository must run validation after each code change before marking work complete.

Required steps:

1. Run full repo build:
   - `./build.ps1`
2. Run shared tests:
   - `dotnet test ./tests/OpenClaw.Shared.Tests/OpenClaw.Shared.Tests.csproj --no-restore`
3. Run tray tests:
   - `dotnet test ./tests/OpenClaw.Tray.Tests/OpenClaw.Tray.Tests.csproj --no-restore`

This is the required local closeout subset for agents. CI also builds and runs additional connection, setup, CLI, UI, accessibility, integration, and E2E suites; see `docs/TEST_COVERAGE.md` for the broader test inventory and `.github/workflows/ci.yml` for the workflow source of truth.

If a command fails:

1. Fix the issue.
2. Re-run the failed command.
3. Re-run all required validation commands before completion.

Notes:

- If a build/test is blocked by an environmental lock (for example running executable locking output assemblies), stop/close the locking process and rerun.
- If validation is blocked by missing local Windows prerequisites, run `.\scripts\setup-dev.ps1` to install/verify developer and agent prerequisites, then rerun validation. Use `.\scripts\setup-dev.ps1 -CheckOnly` when you only need diagnostics.
- **First-run gotcha**: `dotnet test --no-restore` silently no-ops in a fresh worktree where the test `bin/` doesn't exist yet (reports "Build succeeded in 0.5s" then exits 0 with no tests run). For first-run validation, either omit `--no-restore` OR run `dotnet build` on the test project first. Subsequent reruns honor `--no-restore` correctly.
- In linked git worktrees, set `OPENCLAW_REPO_ROOT` to the worktree path before running tests that discover the repository root, for example:
  - `$env:OPENCLAW_REPO_ROOT='D:\github\openclaw-windows-node.<worktree-name>'`
- Tray tests must isolate `SettingsManager` from real user settings. Do not use `new SettingsManager()` in tests unless the test intentionally reads `%APPDATA%\OpenClawTray\settings.json`; pass a temp settings directory or set `OPENCLAW_TRAY_DATA_DIR` before the test process starts.
- Prefer isolated worktrees for PR validation. Use `git-wt` for worktree workflows; `wt.exe` may resolve to WorkTrunk instead of Windows Terminal, so use the full Windows Terminal path when explicitly launching Terminal.
- Do not claim completion without reporting validation results.

## Active Work Status

- Apply the `status: 🚢 actively landing` label when a maintainer or delegated agent takes active ownership of an issue or pull request through implementation, conflict resolution, validation, or merge.
- Apply the label only to work that is actively moving. Do not use it for queued, parked, or indefinitely blocked items.
- Remove the label when active work pauses, ownership returns to the author, or the item closes or merges.
- This maintainer-ownership label may coexist with contributor-facing status labels such as `status: 📣 needs proof`.

## Targeted Validation Paths

Run the required validation above for every code change, then add the targeted path that matches the touched subsystem.

### MXC / `system.run` / Windows node command execution

When changing MXC sandboxing, `system.run`, exec approvals, Windows node command execution, gateway setup/connect E2E behavior, or files under `src\OpenClaw.Shared\Mxc`, run:

```powershell
.\scripts\validate-mxc-e2e.ps1
```

The script sets `OPENCLAW_RUN_E2E` and `OPENCLAW_RUN_MXC_E2E` itself, then runs the real WSL Gateway -> Windows node -> `system.run` MXC E2E proofs. It fails if the MXC proof skips. Use `-AllowSkip` only to document that the current host is not MXC-capable; do not report an `-AllowSkip` run as merge validation for MXC-related work.

## UI, MCP, and PR Proof

Use `.agents/skills/openclaw-proof-validation/SKILL.md` when a change touches tray UX, Settings, onboarding, chat/canvas, Command Center, Windows node capabilities, MCP, gateway connection/pairing, permissions, diagnostics, or agent-facing instructions.

## User-Facing Copy

Do not use em dashes in user-facing prose, including UI text, error messages, CLI output, notifications, and agent-facing help. Use a period, colon, comma, parentheses, or a simple hyphen instead. A standalone em dash is allowed as an unavailable-value or status placeholder.

Policy:

- Required automated/focused tests are mandatory; do not ask to skip them.
- Prefer computer-use as a batched closeout proof pass before PRs. If UI proof is useful mid-development, first ask whether to run computer-use now or provide manual steps so the developer can capture screenshots/output.
- For UI claims, collect current-head visible proof of the active changed state: computer-use screenshot/video, developer-provided screenshot, copied UI diagnostics, or an explicit blocker.
- If the developer captures UI proof manually, run or point them at the current isolated app, provide exact reproduction/capture steps, and verify any PR screenshot/artifact links after updating the PR body.
- For node/MCP changes, prove discovery and invocation with `winnode --list-tools` plus `winnode --command ...`, or raw MCP JSON-RPC `tools/list` plus `tools/call`.
- For gateway-mediated behavior, prove the real gateway path when available; otherwise state the blocker and keep MCP proof.
- Run rubber-duck review before PR publication for non-trivial UI, MCP, node-command, setup, pairing, security, permissions, or diagnostics changes.
- PRs should include `## Required proof pools`, `## Validation`, and `## Real behavior proof`. Select every applicable pool ID from [`docs/PROOF_POOLS.md`](docs/PROOF_POOLS.md), or declare `none` with a reason. Proof must directly show the changed behavior from the current PR head. Fill `Not verified / blocked` for focused proof or unavailable dependencies.

Every new Windows node call must be exposed, documented, and tested through MCP before completion:

1. Register the capability/command in the tray node capability registry.
2. Add/update `McpToolBridge.CommandDescriptions`.
3. Update `.agents/skills/winnode/SKILL.md`.
4. Add/update capability, MCP bridge, `winnode`, and UI/gateway tests as appropriate.
5. Run required validation plus `dotnet test .\tests\OpenClaw.WinNode.Cli.Tests\OpenClaw.WinNode.Cli.Tests.csproj --no-restore` when `winnode`, MCP output, or command docs change.

## Telemetry Guardrails

Read `docs/TELEMETRY.md` before adding or changing OpenTelemetry configuration, exporters, instrumentation helpers, span attributes, metric tags, or exported log fields.

- Do not add OpenTelemetry SDK/exporter package references to `OpenClaw.Shared`; shared helpers may use `System.Diagnostics.ActivitySource`, `Activity`, and `System.Diagnostics.Metrics` only.
- Do not export user content, prompts, screenshots, file contents, raw command input/output, credentials, API keys, gateway tokens, device tokens, or arbitrary existing local logs.
- Keep telemetry opt-in: an empty endpoint must mean no OpenTelemetry export, and export must go only to the user-configured endpoint.
- Prefer low-cardinality operational diagnostics: operation names, outcomes, durations, counts, protocol choices, and coarse error categories.
- Add focused tests for new span names, metric names, tag keys, log categories, filtering behavior, and endpoint/protocol behavior.
- For exporter or protocol changes, provide current-head collector proof for every affected signal and protocol, or document the blocker.

## Architecture Context for New Agents

Start with these docs before changing connection, pairing, node, MCP, or tray UX behavior:

- `docs/ARCHITECTURE.md` - **the living architecture ledger**. Required reading before touching any god object it lists. Records which responsibilities have been extracted (`authoritative`) and which must not be re-added to a god object (`closed`). Do not reintroduce a `closed` responsibility.
- `docs/CONNECTION_ARCHITECTURE.md` - current gateway registry, connection manager, credential precedence, migration, MCP-only, and tray action behavior.
- `docs/MCP_MODE.md` - local MCP server mode and the `EnableNodeMode` / `EnableMcpServer` matrix.
- `docs/WINDOWS_NODE_TESTING.md` - Windows node capabilities, manual smokes, and gateway-dependent behavior.
- `docs/ONBOARDING_WIZARD.md` - first-run setup flow, setup-code/bootstrap pairing, and test isolation.
- `docs/SETUP_ENGINE_REDESIGN.md` - setup pipeline, rollback/logging, Windows node context injection, and setup CLI contract.
- `docs/WSL_EXE_ARGV_PITFALL.md` - wsl.exe argv variable-expansion pitfall; required reading before adding any multi-line WSL script through `RunInWslAsync`.

## Architecture Guardrails for Large Refactors

`src\OpenClaw.Tray.WinUI\App.xaml.cs` and `src\OpenClaw.Tray.WinUI\Pages\ConnectionPage.xaml.cs` are active god-file reduction targets. When touching either file:

- **Read `docs/ARCHITECTURE.md` first.** It is the living ownership ledger. Before editing any file it lists, check its row(s); do not re-add anything marked `closed`. When you extract a responsibility, update the ledger in the same PR (flip/add the new owner to `authoritative`, mark the vacated responsibility `closed`) and add a guard test for high-regression closures. A PR that re-adds a `closed` responsibility must be rejected in review.
- Prefer completing a real ownership transfer over moving code to partial classes. A new partial file is not progress unless it introduces a narrower owner, pure projection, policy, service, or tested seam.
- Keep `App` as the composition root. Shrink it by delegating cohesive behavior to focused services, but do not relocate startup ordering into another god object.
- Keep `ConnectionPage.xaml.cs` as the WinUI applicator until a pure row/plan/workflow seam exists. Do not move named-control setters into a presenter that just wraps the page.
- Add characterization tests before moving startup, credential, pairing, node/MCP, tray action, or direct-connect rollback behavior. Source-text contract tests are acceptable for WinUI-only seams, but prefer pure unit tests for policies and projections.
- Keep PRs small and reviewable: one seam per PR, with a clear invariant protected by tests. Stop and re-plan if a PR moves hundreds of lines without behavior coverage.
- In PR descriptions and handoffs, name the old owner, new owner, preserved invariant, and validation run so future agents do not reintroduce duplicate paths or grow new god objects.

Important current facts:

- Gateway credentials are no longer stored in `SettingsData.Token` / `SettingsData.BootstrapToken`. `SettingsManager` may read legacy JSON fields only for one-time migration; new writes must go through `GatewayRegistry`.
- Active gateway records live in `%APPDATA%\OpenClawTray\gateways.json`; per-gateway identity files live under `%APPDATA%\OpenClawTray\gateways\<gateway-id>\device-key-ed25519.json`.
- Credential precedence is device token, then shared gateway token, then bootstrap token. Do not downgrade a paired device from its stored device token back to a bootstrap/shared token.
- `GatewayConnectionManager` owns operator/node connection state. UI surfaces should observe it or call its reconnect/disconnect APIs instead of constructing parallel gateway clients.
- Chat/canvas/tray actions must visibly route users to Connection settings when pairing is incomplete or credentials are missing; avoid silent no-ops.
- MCP-only mode (`EnableMcpServer=true`, `EnableNodeMode=false`) must start local `NodeService` without requiring a gateway credential.
