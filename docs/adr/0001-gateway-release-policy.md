# ADR 0001: Gateway release selection

## Status

Accepted on 2026-08-06.

## Context

Windows setup must avoid two unsafe extremes:

1. Installing the mutable npm `latest` tag without Windows compatibility proof.
2. Keeping a Windows-owned last-known-good version indefinitely after upstream
   security and reliability releases advance.

The Gateway release package does not publish a first-class Windows
compatibility field. Protocol generation is authoritative in the tagged
`packages/gateway-protocol/src/version.ts` source. Stable release manifests or
verified signed upstream extended-stable tags, npm integrity, signatures,
provenance, and Windows end-to-end proof provide the remaining promotion
evidence.

## Decision

- Product setup installs the exact `GatewayReleasePolicy.RecommendedVersion`.
- Official setup also pins and verifies Node `24.19.0`; the installer runtime is
  part of the compatibility tuple rather than a floating dependency.
- Protocol generation 4 is required.
- `2026.6.11` is the current minimum security floor.
- npm dist-tags only discover candidates. They never select a product install.
- External evidence files are discovery artifacts only. They cannot authorize
  an unembedded release; candidate execution requires a reviewed embedded
  `GatewayReleaseStatus.Candidate` policy entry.
- Numeric correction releases such as `2026.7.1-2` are stable-version syntax,
  but still require the complete evidence gate. This applies to the Gateway
  package version only. Windows Hub application release tags are a separate
  version domain validated by
  [`../RELEASING.md`](../RELEASING.md); a Windows Hub correction release neither
  changes `GatewayReleasePolicy.RecommendedVersion` nor supplies any Gateway
  promotion evidence.
- Prerelease labels, versions below the floor, missing provenance, missing
  stable upstream attestation, package/tag commit mismatch, unbound candidate
  provenance, and protocol-generation mismatch fail closed.
- A candidate is promoted only after exact-version Windows setup, operator and
  node pairing, restart/reconnect, recovery, and representative
  Gateway-to-node invocation proof.
- A fallback must be a distinct validated release at or above the floor.
  Fallback is never automatic and is offered only after a typed version or
  protocol compatibility failure.
- Custom installer URLs require an exact version, are reported as unverified,
  and must pass the same post-install protocol and server-version checks.
- Setup retains Gateway reload mode `hybrid`. The bounded
  `GatewayWizardRestartRecoveryPolicy` handles only the exact, provenance-safe
  restart cases recognized by current main.
- Setup refreshes the bundled plugin registry before writing `device-pair`
  configuration. A missing or stale registry fails setup before service start.

## Current candidate decision

`2026.6.34` is the recommendation. Node `24.19.0` replaces the previously
validated `22.22.3` runtime because the current official installer rejects Node
22 and selects `24.19.0` by default. The Gateway version remains unchanged;
the updated runtime tuple requires the same exact Windows setup, pairing,
restart, recovery, and Gateway-to-node proof before landing.

`2026.6.11` remains a distinct validated fallback at the security floor. It is
never selected automatically.

`2026.7.1` is runtime-rejected. Its exact package installed, negotiated protocol
v4, reported the exact server version, paired operator and node identities, and
passed initial connectivity. The expanded wizard then restarted the Gateway and
clean setup failed because trusted endpoint ownership could not be re-established
in the reconnect window. Static compatibility is therefore insufficient for
promotion. Current main now has guarded recovery for that exact restart shape,
but that does not retroactively promote the release without a new complete
candidate proof.

`2026.7.1-2` is evidence-rejected because its npm metadata does not publish SLSA
provenance and its GitHub release does not publish stable release-validation
evidence.

The Windows Extensions hub needs the stable Plugins lifecycle first released in
`2026.8.1`. The newest stable candidate checked on 2026-09-08, `2026.9.3`, is
evidence-rejected. It passed protocol v4, the security floor, SHA-512 package
integrity, registry signatures, exact package-build/tag commit binding, the
stable release manifest, and verified tag checks. Its npm SLSA provenance did
not verify against the package digest and OpenClaw release identity. The
Windows recommendation therefore remains `2026.6.34`; runtime UI uses advertised
Gateway methods and fails closed when the Plugins APIs are absent.

## Consequences

- Setup is reproducible and cannot silently move when npm tags change.
- Compatibility failures are terminal and distinct from retryable network,
  authentication, pairing, and process failures.
- Release automation opens evidence-only candidate pull requests. A human-reviewed
  policy update and current-head Windows proof are required for promotion.
