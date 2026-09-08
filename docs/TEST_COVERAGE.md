# Test Coverage Summary

**Last audited**: 2026-08-06<br>
**Framework**: xUnit / .NET 10.0<br>
**Required validation status**: passing (`.\build.ps1`, Shared tests, Tray tests)

## Required validation suites

These are the suites every agent must run after code changes, as documented in
`AGENTS.md`.

| Suite | Latest runtime result |
|---|---:|
| `OpenClaw.Shared.Tests` | 3,444 total: 3,412 passed, 32 skipped |
| `OpenClaw.Tray.Tests` | 2,165 total: 2,165 passed, 0 skipped |

Runtime totals come from `dotnet test` on 2026-08-06. They are higher than
method counts because some `[Theory]` tests expand into multiple cases.

## Test project inventory

| Project | Primary scope | Test methods |
|---|---|---:|
| `OpenClaw.Connection.Tests` | Gateway registry, credential resolution, connection manager/state machine, setup codes, pairing, diagnostics | 452 |
| `OpenClaw.Shared.Tests` | Shared models, gateway client, capabilities, MCP, exec approval, A2UI security, URL handling, notification categorization | 2,373 |
| `OpenClaw.Tray.Tests` | Tray state/UI helpers, settings isolation, onboarding, connection page behavior, localization, local gateway setup/uninstall | 1,717 |
| `OpenClaw.Tray.UITests` | Native WinUI/A2UI control, rendering, and accessibility scan coverage | 89 |
| `OpenClaw.WinNode.Cli.Tests` | Windows node CLI argument parsing, command behavior, JSON output, uninstall flow | 89 |
| `OpenClaw.SetupEngine.Tests` | Setup engine, WSL gateway installation, setup-code, and local setup policy coverage | 387 |
| `OpenClawTray.FunctionalUI.Tests` | Functional UI smoke coverage | 19 |
| `OpenClaw.E2ETests` | Gateway-mediated setup/connect, revocation recovery, and network recovery suites | 21 |
| `OpenClaw.Tray.IntegrationTests` | Real-process tray/MCP integration tests gated by `OPENCLAW_RUN_INTEGRATION=1` | 19 |

The method inventory is a source scan of `[Fact]`, `[Theory]`, and repo custom
xUnit attributes such as `[WindowsFact]`, `[E2EFact]`, `[MxcE2EFact]`,
`[IntegrationFact]`, and `[IntegrationTheory]`. Use `dotnet test` for
authoritative runtime totals.

## Coverage highlights

### OpenClaw.Shared.Tests

- **Model and display formatting** - activity glyphs, app version display, session labels, gateway usage/node display, channel status, and rich text helpers.
- **Gateway and WebSocket behavior** - gateway client parsing, session keys, WebSocket base handling, URL normalization, local gateway classification, and token sanitization.
- **Capabilities and MCP** - app/canvas/screen/camera/system capabilities, MCP auth token reset, MCP HTTP server, MCP tool bridge, MXC availability, MXC policy building, and command runners.
- **Exec approval** - legacy policy coverage plus V2 evaluator, input validation, normalization, prompt adapter, routing, coordinator, store, environment sanitizing, and shell-wrapper parsing.
- **A2UI and web bridge** - A2UI capability security, asset hash pinning, web bridge message handling, and channel payload/status tests.
- **Security and localization-adjacent helpers** - HTTP URL validation/risk evaluation, device identity, identity migration, notification categorization, speech language normalization, and non-fatal action handling.

### OpenClaw.Tray.Tests

- **Tray UI and state** - app state, menu display/position/sizing, tray tooltip formatting, activity streams, async list loading, diagnostics contracts, markup regressions, and chat timeline/markdown handling.
- **Connection and pairing** - connection manager node connector tests, connection page approval/channel metrics/row state, operator and Windows tray node pairing approval, and gateway action transport.
- **Settings and startup** - settings round-trip/isolation, consent and settings save, auto-start defaults, startup setup state, existing config guard policy, and local setup progress stage mapping.
- **Onboarding and local gateway setup** - onboarding completion/chat bootstrapper/existing config guard, wizard flow/selection/error/step parsing, setup code decoding, local gateway setup diagnostics, uninstall, WSL keep-alive, and auto-pair flags.
- **Localization and resources** - localization key parity, capability page localization, fluent icon catalog coverage, and installer assertion tests.

### Additional projects

- **OpenClaw.Connection.Tests** keeps connection architecture tests separate from tray UI concerns.
- **OpenClaw.Tray.UITests** covers A2UI/native WinUI rendering behavior that is awkward to validate through pure unit tests.
- **OpenClaw.WinNode.Cli.Tests** covers the standalone Windows node CLI contract.
- **OpenClaw.SetupEngine.Tests** covers gateway setup and local WSL installation policy.
- **OpenClawTray.FunctionalUI.Tests** covers newer UI surfaces outside the main tray test project.
- **OpenClaw.E2ETests** uses custom `[E2EFact]` / `[MxcE2EFact]` attributes that inherit xUnit `FactAttribute`; CI exercises them with shard filters.
- **OpenClaw.Tray.IntegrationTests** uses custom `[IntegrationFact]` attributes and runs only when `OPENCLAW_RUN_INTEGRATION=1`.
- **PackagingTests** is a legacy PowerShell-script lane under
  `tests\PackagingTests\`, not a dotnet test project.

## Formal validation paths

Use the smallest lane that proves the changed subsystem, but always include the
required closeout lane for code changes.

| Lane | Entry point | Required when |
|---|---|---|
| Required closeout | `.\build.ps1`, Shared tests, Tray tests | Every code change and every agent closeout |
| Proof-pool inventory | `.\scripts\validate-proof-pools.ps1`, `.\scripts\test-proof-pool-validator.ps1`, and `.\scripts\test-validate-docs-proof-pool-flow.ps1` | Every inventory or proof scheduling change; the documentation gate runs core schema and parent-flow checks, while CI runs the full malformed-contract matrix |
| Agent skills | `.\scripts\validate-agent-skills.ps1` and `.\scripts\test-agent-skills-validator.ps1` | Changes under `.agents\skills`; validates skill metadata, agent-facing prose, and local links |
| GitHub-hosted PR/main CI | `.github\workflows\ci.yml` | Every pull request and push to `main`; explicit conservative impact outputs select the required test, E2E, and release-publish lanes |
| Accessibility scan | `.\scripts\run-proof-tests.ps1 -Project 'tests\OpenClaw.Tray.UITests\OpenClaw.Tray.UITests.csproj' -Filter 'Category=Accessibility' -ResultName 'winui-accessibility' -RuntimeIdentifier win-x64` | UI changes and CI quality gate; runs real-process Axe.Windows scans; see `docs\ACCESSIBILITY.md` |
| Local E2E | `OPENCLAW_RUN_E2E=1` with `OpenClaw.E2ETests` | Gateway setup/connect, recovery, or pairing changes that need real WSL Gateway coverage |
| Local MXC E2E | `.\scripts\validate-mxc-e2e.ps1` | MXC sandboxing, `system.run`, exec approvals, Windows node command execution, gateway setup/connect changes that affect MXC |
| Product WSL setup validation | `OPENCLAW_RUN_E2E=1` with `OpenClaw.E2ETests.Setup.SetupAndConnectTests` | Tray onboarding/setup-engine changes that must prove the current product WSL install path |
| Installer source checks | `.\scripts\run-proof-tests.ps1 -Project 'tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj' -Filter 'FullyQualifiedName~InstallerIssAssertionTests' -ResultName 'installer-source'` | Installer source, payload, identity, cleanup, or protocol changes; pair with the clean installer/upgrade pool for runtime claims |
| Legacy installer runtime ordering | `.\tests\PackagingTests\Test-InnoUninstallOrdering.ps1` | Deprecated. It targets the old `[UninstallRun]` layout, while current cleanup is owned by `[Code]`; do not use it as current installer runtime proof |

Capacity-dependent Windows validation is named in
[`PROOF_POOLS.md`](./PROOF_POOLS.md). Pull requests declare the exact pool IDs
they need. A declaration does not claim that the pool ran, and unavailable
proof must remain reported as blocked.

### CI impact fast paths

The `change-classification` job emits explicit booleans for `core_tests`,
`tray_tests`, `ui_tests`, `setup_e2e`, `revocation_e2e`, `network_e2e`,
`x64_release`, `arm64_release`, and `full`. Jobs consume those outputs directly.
The summary classification is `docs_only`, `targeted`, or `full`; it is not the
authority for individual job conditions.

The classifier uses conservative project boundaries:

| Change surface | Core / CLI | Tray / setup / integration | UI / accessibility | Setup E2E | Recovery E2E | Release publish |
|---|---:|---:|---:|---:|---:|---:|
| Maintained docs or non-executable agent-skill content |  |  |  |  |  |  |
| `OpenClaw.Cli`, `OpenClaw.WinNode.Cli` | Yes |  |  |  |  |  |
| Tray WinUI, including pure service logic |  | Yes | Yes |  |  |  |
| Chat, FunctionalUI, XAML, resources, pages, controls |  | Yes | Yes |  |  |  |
| SetupEngine, onboarding, setup, pairing |  | Yes | When UI-bound | Yes |  |  |
| Connection, gateway, node, MCP, WSL paths | As applicable | Yes | When UI-bound | Yes | Yes |  |
| Broad Shared or protocol changes | Yes | Yes | Yes | Yes | Yes |  |
| Test-only change | Corresponding lane | Corresponding lane | Corresponding lane | Corresponding shard | Corresponding shard |  |
| Packaging, project/build files, installer, workflow or classifier contracts | Yes | Yes | Yes | Yes | Yes | x64 |
| Push to `main` or a tag | Yes | Yes | Yes | Yes | Yes | x64 and ARM64 |

Known mixed changes take the union of their lanes. Any unknown path, a mixed
change containing an unrecognized path, invalid or unavailable revisions, an
empty diff, workflow/build infrastructure, project/package metadata, installer
input, or classifier/contract change selects `full` fail-closed. A full pull
request runs all test and E2E lanes plus the x64 publish smoke. Pushes to `main`
and tags also run ARM64 publish.

The former serialized Windows `test` job is split into three clean-runner lanes.
Each lane restores and builds its own minimal project graph and publishes a
separate TRX artifact:

| Lane | Projects |
|---|---|
| `core-tests` | Shared, Connection, WinNode CLI |
| `tray-tests` | Tray, SetupEngine, Tray Integration |
| `ui-tests` | FunctionalUI, Tray UI, Axe.Windows accessibility |

The core lane uses a dedicated runner-temp NuGet package directory and a key
scoped to its complete transitive project graph plus the SDK, NuGet, and imported
MSBuild manifests that affect restore. It has no fallback to the repository-wide
cache. Tray, UI, E2E, and release lanes retain the existing repository-wide
NuGet cache behavior. Only downloaded NuGet packages are cached; build outputs,
test results, source, SDK installs, and Node packages remain uncached. Tray
settings remain isolated through `OPENCLAW_TRAY_DATA_DIR`, integration tests
retain `OPENCLAW_RUN_INTEGRATION=1`, UI tests install WindowsAppRuntime, and
every test project continues to publish TRX output.

`fast-validation` still runs for every invocation and owns repository hygiene,
documentation, agent-skill, classifier, gate, workflow, and release-ordering
contracts. The heavyweight malformed proof-pool matrix moved to the independent
`proof-pool-contracts` job. Its existing decision helper still fails closed,
but ordinary product code no longer waits for the matrix. The three existing
E2E shards are unchanged in scope and have separate job conditions; no E2E
shards were added.

Release metadata is produced by the small `metadata` job only when release
publish validation is required. It preserves GitVersion `semVer`,
`majorMinorPatch`, `isPrerelease`, `isStableCorrection`, and stable-correction
tag validation. The x64 and ARM64 publish jobs depend only on classification
and metadata, so they can run in parallel with tests and E2E. Ordinary product
pull requests produce no release artifact. Packaging/build/release-sensitive
pull requests run only x64 publish smoke; main and tags run x64 plus ARM64.

The always-running **CI Gate** validates every classifier output against the
corresponding job result. A required lane must succeed, an unrequired lane must
be skipped, and classification, fast validation, proof-contract selection,
missing outputs, cancellation, or unexpected results fail closed.

#### Timing and runner-minute model

The pre-split full PR baseline is run `33815204229`: the serialized `test` job
took 22 minutes 11 seconds, including 6 minutes 48 seconds for the malformed
proof-contract matrix and 3 minutes 28 seconds for accessibility. Existing E2E
shards took 14 minutes 43 seconds, 9 minutes 33 seconds, and 11 minutes 7
seconds. x64 and ARM64 publishes took 5 minutes 13 seconds and 6 minutes 17
seconds. The merged docs/skill fast path is run `33819700640` at 51 seconds.

Using those step baselines, expected pull request critical paths are:

| Change class | Expected wall-clock | Expected runner cost relative to old full PR |
|---|---:|---:|
| Docs / agent-skill prose | About 1 minute | About 2% |
| CLI-only | 5 to 7 minutes | About 10% |
| Tray logic | 8 to 12 minutes | About 25% |
| UI / XAML | 8 to 12 minutes | About 25% |
| Setup / connection / Shared | 10 to 16 minutes, governed by required E2E | About 55% to 85% |
| Build or workflow infrastructure | 10 to 16 minutes | Within 15% of the old full runner total |

The full test split duplicates checkout, SDK setup, and cache restore, but removes
the serialized proof matrix from product paths. Estimated full-infrastructure
runner minutes remain below the 30% rejection threshold, while ordinary code
latency falls to the longest required lane instead of the sum of every lane and
release publish.

## Running tests

```powershell
# Required validation after code changes
$env:OPENCLAW_REPO_ROOT = (Get-Location).Path
.\scripts\validate-proof-pools.ps1
.\build.ps1
dotnet test .\tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj --no-restore
dotnet test .\tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj --no-restore

# All local-dev tests in the solution. E2E is intentionally excluded from the
# solution and runs in CI before merge; run it locally only when explicitly needed.
dotnet test

# Explicit local E2E run
$env:OPENCLAW_RUN_E2E = "1"
dotnet test .\tests\OpenClaw.E2ETests\OpenClaw.E2ETests.csproj -r win-x64

# Formal MXC validation path. This sets the required integration/E2E env vars
# itself and fails when MXC proofs skip unless -AllowSkip is explicitly supplied.
.\scripts\validate-mxc-e2e.ps1

# Accessibility scan, matching the CI quality gate.
dotnet test .\tests\OpenClaw.Tray.UITests\OpenClaw.Tray.UITests.csproj -r win-x64 --filter Category=Accessibility

# Single project
dotnet test .\tests\OpenClaw.Connection.Tests\OpenClaw.Connection.Tests.csproj

# Specific test class
dotnet test --filter "FullyQualifiedName~MenuDisplayHelperTests"

# Verbose output
dotnet test --logger "console;verbosity=detailed"
```

In a fresh worktree, run the project once without `--no-restore` or build it
first so `dotnet test --no-restore` cannot no-op before `bin\` exists.

Test-owned TCP listeners must bind only to loopback addresses. A successful
wildcard bind from `testhost.exe` can trigger a per-worktree Windows Defender
Firewall consent dialog and block unattended validation. Tests for production
LAN-bind conflict handling should occupy loopback first so the production
wildcard bind fails without opening a network-reachable listener.

## Not fully covered by automated tests

- Real shell tray hover/click behavior against Explorer.
- Full live gateway/node pairing against a remote gateway.
- Long-running soak behavior for reconnects, high-frequency activity updates,
  and memory usage over multi-day sessions.
- Manual visual acceptance for complex WinUI surfaces where screenshot
  comparison would be brittle.

For these gaps, affected changes must include the manual UI/MCP smoke described
in `AGENTS.md` and `.agents/skills/openclaw-proof-validation/SKILL.md`: launch
the tray from the current worktree, use computer-use / desktop automation for
visible WinUI paths, and validate local MCP with `winnode --list-tools` plus the
changed command when node capabilities are involved.

When node command surfaces change, include
`OpenClaw.WinNode.Cli.Tests` in focused validation because `SkillMdDriftTests`
guards the capability registry, MCP descriptions, and `winnode` skill reference
from drifting apart.
