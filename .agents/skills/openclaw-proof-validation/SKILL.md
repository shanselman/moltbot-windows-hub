---
name: openclaw-proof-validation
description: "Plan and collect OpenClaw Windows validation/proof: tests, rubber-duck review, UI evidence, MCP output, and gateway runtime proof."
---

# OpenClaw Proof and Validation

Use for changes that affect tray UX, Settings, onboarding, chat/canvas, Command Center, Windows node capabilities, local MCP, gateway connection/pairing, permissions, diagnostics, or agent-facing instructions.

If the validation host is macOS, read [PARALLELS.md](PARALLELS.md) for the optional local Parallels Windows VM workflow.

## Rules

- Required automated/focused tests are mandatory. Do not ask to skip them.
- Prefer isolated tray data so proof does not mutate `%APPDATA%\OpenClawTray`.
- Computer-use is usually a batched closeout proof pass, not a continuous dev-loop tool. Mid-development use is fine when explicitly requested or needed to unblock work; ask first whether to run it or provide manual screenshot/output steps.
- Local MCP is part of every Windows node command contract: command discovery and invocation must work through `winnode` or raw MCP JSON-RPC.
- Rubber-duck review is required before PR publication for non-trivial UI/MCP/node-command/setup/pairing/security/permissions/diagnostics work; it is also useful mid-development when extra design/testing validation is requested.
- Report blockers explicitly. Do not turn missing UI, MCP, gateway, camera, screen, or permission proof into success-shaped wording.

## Required validation

```powershell
$env:OPENCLAW_REPO_ROOT = (Get-Location).Path
.\build.ps1
dotnet test .\tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj --no-restore
dotnet test .\tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj --no-restore
```

Fresh worktrees may need a first run without `--no-restore`, or a project build first, so tests do not no-op before `bin\` exists.

For `winnode`, command descriptions, or new/renamed node commands, also run:

```powershell
dotnet test .\tests\OpenClaw.WinNode.Cli.Tests\OpenClaw.WinNode.Cli.Tests.csproj --no-restore
```

## Proof checklist

| Surface | Proof to collect |
|---|---|
| UI / WinUI | Launch `.\run-app-local.ps1 -Isolated`, exercise the changed path with computer-use or developer-provided screenshots/output, and include visible evidence or blocker. If the developer captures manually, provide exact steps and confirm screenshot/artifact links resolve after updating the PR body. |
| Local MCP | Enable **Local MCP Server**, run `winnode --list-tools`, then invoke the changed command with `winnode --command <name> --params '<json-object>'`. |
| Raw MCP HTTP | For protocol/server-shape changes, paste JSON-RPC `tools/list` and `tools/call` responses from `http://127.0.0.1:8765/`. |
| Gateway path | When relevant and available, prove `openclaw nodes invoke --command <name> --params '<json-object>'`; otherwise state the gateway blocker. |
| Rubber-duck | Ask a rubber-duck reviewer to inspect the final implementation/proof plan; verify any finding before changing code. |

For isolated tray runs, copy the data directory printed by `run-app-local.ps1 -Isolated` and set it before MCP proof commands:

```powershell
$env:OPENCLAW_TRAY_DATA_DIR = '<isolated-data-dir-from-run-app-local>'
```

Raw token lookup:

```powershell
$tokenPath = if ($env:OPENCLAW_TRAY_DATA_DIR) {
    Join-Path $env:OPENCLAW_TRAY_DATA_DIR 'mcp-token.txt'
} else {
    Join-Path $env:APPDATA 'OpenClawTray\mcp-token.txt'
}
$token = Get-Content $tokenPath -Raw
```

## New Windows node command checklist

1. Register the command in the same `INodeCapability` path used by the gateway node.
2. Add/update `McpToolBridge.CommandDescriptions`.
3. Update `.agents/skills/winnode/SKILL.md` with input shape, output shape, side effects, permissions, and examples.
4. Add/update capability, MCP bridge, `winnode`, and UI/gateway tests as applicable.
5. Prove discovery and invocation with `winnode` or raw MCP JSON-RPC.
6. Prove the gateway path when the behavior is gateway-mediated and a gateway is available.

## PR proof package

Before publishing or updating a PR, collect:

1. `## Validation` with exact commands and pass/fail counts.
2. `## Real behavior proof` with current-head after-change evidence that directly shows the changed behavior: copied live output, screenshot/video, developer-provided screenshot, copied UI diagnostics, `winnode`, raw MCP JSON-RPC, gateway invoke output, redacted runtime log, or linked artifact. For UI changes, prefer screenshots/video of the active changed state, not only adjacent or empty UI.
3. PR body proof must stay in sync with current behavior: remove stale screenshots/claims after design changes and verify embedded image/artifact links resolve.
4. Rubber-duck notes for non-trivial UI/MCP-sensitive work, or why skipped.
5. `Not verified / blocked` notes for focused proof or unavailable dependencies.
