# Gateway 2026.9.3 candidate proof

Captured on Windows ARM64 from production commit `5c1fb828` on September 8, 2026.

The repository validator was run against the public npm package and GitHub release:

```powershell
.\scripts\Test-GatewayReleaseCandidate.ps1 -Version 2026.9.3 -SummaryPath .\docs\proof\gateway-release\2026.9.3-preflight.json
```

The command rejected the candidate, as expected. The linked [preflight report](./2026.9.3-preflight.json) records:

- Protocol generation 4 and security-floor checks passed.
- npm SHA-512 integrity and both registry signatures verified.
- Package build version and commit matched the exact Git tag.
- The stable GitHub release, release manifest, and verified tag checks passed.
- npm SLSA provenance did not verify against the package digest and OpenClaw release identity, so `eligible` remained `false`.

The current-head `GatewayReleasePolicyTests` passed 26 of 26. They prove that the embedded recommendation remains 2026.6.34, the Plugin readiness signal remains false, the 2026.9.3 evidence entry remains rejected, and exact selection of an evidence-rejected release fails before setup can install it. The full SetupEngine suite previously passed 1,100 of 1,100 on the same production commit.

The production `OpenClaw.SetupEngine.Program.Main` entry point was then invoked from a minimal executable host with the checked-in [rejected config](./setup-policy-rejected.json). It stopped before setup or installation and returned the configuration-error exit code:

```text
OpenClaw Setup Engine v1.0.0
─────────────────────────────
Loading config from: .\docs\proof\gateway-release\setup-policy-rejected.json
ERROR: Gateway 2026.9.3 is not an eligible protocol-v4 Windows release. npm SLSA provenance did not verify against the package digest and OpenClaw release identity.
EXIT_CODE=2
```

The same current production entry point accepted the checked-in [recommended config](./setup-policy-recommended.json), retained 2026.6.34, and completed dry-run validation:

```text
OpenClaw Setup Engine v1.0.0
─────────────────────────────
Loading config from: .\docs\proof\gateway-release\setup-policy-recommended.json
Log file: .\.proof-runtime\data\Logs\Setup\setup-engine-20260908-185533.jsonl
Distro: OpenClawGatewayProofRecommended
Gateway: ws://127.0.0.1:18789
Gateway release: 2026.6.34 (recommended, protocol v4)
Mode: SETUP

DRY RUN: config validated, exiting.
EXIT_CODE=0
```

Current-head full-suite validation also passed: Shared 3,943 with 32 environment-gated skips, Tray 2,861, and SetupEngine 1,100.

No candidate package was installed. This PR records an evidence decision without changing the recommended or fallback Gateway.
