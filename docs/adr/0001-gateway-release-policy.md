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
- Official installs accept the same selector forms as upstream: npm channel tags
  or an exact OpenClaw package version. An omitted selector defaults to `latest`.
- The requested selector remains separate from the exact installed version used
  by runtime compatibility checks.
- An operator may configure an exact stable `FallbackVersion`. Setup offers it
  only after a typed installed-version, protocol, or server-version failure.
- Legacy `recommended`, `exact`, and `fallback` configurations are translated
  into the new selector model. Legacy selections without a recorded version
  follow the upstream `latest` recommendation or `extended-stable` fallback
  tag. An explicitly recorded legacy version remains exact. Versionless legacy
  configurations with a custom installer retain their former exact pins because
  custom installers cannot resolve npm tags.
- Embedded release evidence, the product security floor, and the
  candidate-promotion workflow are removed.
- Setup continues to pin and verify Node `24.19.0`. This decision changes the
  OpenClaw npm package selection, not the managed Node runtime.

## Consequences

- New managed gateways receive the current stable npm release without waiting
  for a Windows-owned recommendation update.
- Setup remains fail-closed when the installed CLI version is malformed, the
  gateway reports a different version, or protocol v4 is unavailable.
- Staged candidate-package validation remains isolated from normal npm setup.
- Existing exact pins continue to install the requested release. Existing
  shipped Windows builds retain their compiled release policy.
