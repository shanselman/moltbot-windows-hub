# ADR 0001: Gateway release selection

## Status

Superseded on 2026-09-01.

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

## Superseding decision

- Product setup uses the official installer without `--version`, which selects
  the npm `latest` tag.
- Setup reads the installed CLI version, records it in runtime setup state, and
  requires `hello-ok.server.version` to report the same value.
- Gateway protocol generation 4 remains required.
- Exact versions remain available only for explicit release-candidate
  validation and custom installer URLs.
- The recommended/fallback selection model, embedded release evidence, security
  floor, fallback UI, and candidate-promotion workflow are removed.
- Setup continues to pin and verify Node `24.19.0`. This decision changes the
  OpenClaw npm package selection, not the managed Node runtime.

## Consequences

- New managed gateways receive the current stable npm release without waiting
  for a Windows-owned recommendation update.
- Setup remains fail-closed when the installed CLI version is malformed, the
  gateway reports a different version, or protocol v4 is unavailable.
- Exact candidate validation remains isolated from normal product setup.
