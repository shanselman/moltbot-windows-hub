# Releasing OpenClaw Windows Hub

This repo uses **GitVersion + CI** for release versioning. The canonical release
flow is **tag-driven**: merge to `main`, tag `main`, and let GitHub Actions
build/sign/publish release artifacts.

CI computes GitVersion and stable-correction metadata in the independent
`metadata` job. On `main` and tags, x64 and ARM64 publish jobs start from that
metadata in parallel with tests and E2E, and the stable **CI Gate** requires all
selected lanes before a tag can publish. Pull requests do not produce release
artifacts unless packaging, build, installer, release, workflow, or classifier
infrastructure changes. Those fail-closed pull requests run the x64 publish
smoke only; ARM64 publish remains required on `main` and tags.

## Release checklist

1. Start clean on current `main`.

   ```powershell
   git switch main
   git fetch origin main --prune
   git reset --hard origin/main
   git clean -fd
   git status --short --branch
   ```

2. Confirm the release workflow contains the intended release policy.

   ```powershell
   Select-String .\.github\workflows\ci.yml -Pattern `
     "Verify Release Binary Signing Policy", `
     "OpenClaw.Tray.WinUI.exe", `
     "build-msix:", `
     "MSIX distribution is paused"
   ```

3. Create a new stable, stable correction, or prerelease tag from `origin/main`.
   Never move a previously published tag.

   ```powershell
   # Stable: vX.Y.Z
   # Stable correction on the current Windows latest line: vX.Y.Z-N
   # Prerelease: vX.Y.Z-alpha.N
   $tag = "vX.Y.Z"
   if ((git rev-parse HEAD) -ne (git rev-parse origin/main)) {
       throw "HEAD is not origin/main; do not tag."
   }
   git tag -a $tag -m "OpenClaw Windows Hub $tag"
   git push origin $tag
   ```

4. Watch the tagged workflow.

   ```powershell
   gh run list --repo openclaw/openclaw-windows-node `
     --workflow "Build and Test" `
     --limit 10
   ```

5. Confirm the workflow used the exact tag SemVer. Tagged builds fail before
   publishing if GitVersion disagrees with the tag name.

   ```powershell
   $version = $tag -replace '^v', ''
   .\scripts\Get-OpenClawVersion.ps1 -Variable SemVer
   # Expected: $version
   ```

6. Confirm the GitHub release channel matches the tag. Stable tags should be
   non-prerelease releases; alpha tags should be prereleases and not latest.

   ```powershell
   gh release view $tag --repo openclaw/openclaw-windows-node `
     --json tagName,isPrerelease,isLatest,url,assets
   ```

## Release channel policy

Stable, stable-correction, and alpha tags use the same signed CI release pipeline:

- `vX.Y.Z` creates a normal release eligible to become latest.
- `vX.Y.Z-N` creates a stable correction release eligible to become latest. The
  numeric correction suffix intentionally follows the OpenClaw release
  convention and is not treated as a SemVer prerelease. Windows Hub release tags
  are their own version domain: a correction is a correction of the Windows
  latest release, not a mirror of any other repository's release. CI enforces
  that in `scripts\Test-OpenClawStableCorrectionRelease.ps1`, which requires the
  candidate to stay on the same `X.Y.Z` base line as the current Windows latest
  release and to carry a strictly greater numeric correction. Same, older, and
  different-line corrections fail closed, so GitHub's Latest release marker can
  never move backward. The validator also refuses a candidate that already has a
  published Windows release, and refuses to order against a draft, prerelease,
  or unpublished latest release, so a published tag can never be reused. Every
  numeric-suffix tag, including malformed ones such as `-0` and `-03`, is routed
  through the validator rather than silently classified by GitVersion.
- `vX.Y.Z-alpha.N` creates a prerelease that stable updater checks do not offer.
  The daily workflow evaluates the default branch at 2:00 PM Pacific, skips a
  head already represented by a published release, and defers while an
  unpublished non-alpha tag points at the head. After each successful alpha
  publication, CI removes canonical alpha release objects and assets older than
  30 days so daily builds do not overwhelm the Releases page. Their Git tags
  remain as GitVersion history. If the new release is not yet visible through
  the Releases API, cleanup defers until the next alpha publication.

The validator has no dependency on another repository's release API. Run it
offline against an explicit current release to preview a decision:

```powershell
# Accepted: same 2026.7.1 line, correction 3 > 2
.\scripts\Test-OpenClawStableCorrectionRelease.ps1 `
  -Tag v2026.7.1-3 -CurrentWindowsTag v2026.7.1-2

# Rejected: reuse of a published tag
.\scripts\Test-OpenClawStableCorrectionRelease.ps1 `
  -Tag v2026.7.1-2 -CurrentWindowsTag v2026.7.1-2
```

`scripts\test-stable-correction-release-validator.ps1` runs the full accept and
reject matrix deterministically and is also enforced in CI.

The live `v2026.7.1-2` annotated tag dereferences to commit `f46400aa`, which is
the correction-aware implementation ("Support upstream stable correction release
versions", #1266). Release run 33221425381 built and signed that release's
assets, so `v2026.7.1-2` is the current correction-aware Windows latest release.
Never move, rebuild, or reuse that tag; the next correction on this line is a new
`v2026.7.1-3` tag.

Clients on older unsuffixed `2026.7.1` builds predate the correction-aware
update path. They use Updatum's default parser, which drops the numeric
correction suffix before comparison, so they may need a manual transition to a
correction release. Clients already on `2026.7.1-2` are correction-aware: even
though Updatum 1.3.4's default parsing does not rank `2026.7.1-3` above
`2026.7.1-2`, the `OpenClawReleaseVersion` fallback in the update check pipeline
compares releases under OpenClaw correction ordering and discovers `2026.7.1-3`.

Gateway versions are a separate, independently pinned domain. A Windows Hub
correction release does not change `GatewayReleasePolicy.RecommendedVersion` or
its evidence gates; see [`adr/0001-gateway-release-policy.md`](adr/0001-gateway-release-policy.md).

```powershell
git tag -a vX.Y.Z-alpha.N -m "OpenClaw Windows Hub vX.Y.Z-alpha.N"
git push origin vX.Y.Z-alpha.N
```

Current release artifacts are:

- Inno setup installers:
  - `OpenClawCompanion-Setup-x64.exe`
  - `OpenClawCompanion-Setup-arm64.exe`
- Portable ZIP payloads for Updatum:
  - `OpenClawTray-<version>-win-x64.zip`
  - `OpenClawTray-<version>-win-arm64.zip`

MSIX artifacts remain paused while the supported distribution path uses Inno
installers and signed portable update payloads. This pause is independent of
whether a tag is stable or alpha. Re-enable MSIX only with packaged
camera/microphone consent validation and release coverage.

## Binary signing policy

Only OpenClaw-owned binaries should be signed by the OpenClaw release signing
identity.

OpenClaw-owned binaries:

- `OpenClaw.Tray.WinUI.exe`
- `OpenClaw.Tray.WinUI.dll`
- `OpenClaw.Chat.dll`
- `OpenClaw.Connection.dll`
- `OpenClaw.SetupEngine.UI.dll`
- `OpenClaw.SetupEngine.dll`
- `OpenClaw.Shared.dll`
- `OpenClawTray.FunctionalUI.dll`

Third-party/runtime executables that must not be OpenClaw-signed:

- `tools\mxc\<arch>\wxc-exec.exe`
- `createdump.exe`
- `RestartAgent.exe`
- `SetupEngine\RestartAgent.exe`

CI enforces this with `scripts\Test-ReleaseExecutableSignatures.ps1`. The
verifier inspects every shipped `.exe` and `.dll`, fails closed on unknown
executables and unknown OpenClaw-named binaries, and rejects an OpenClaw
signature on third-party/runtime binaries. When release signing is required,
every allowlisted OpenClaw binary must have a valid signature from the expected
OpenClaw release signer; a valid signature from another publisher is rejected.

CI also checks native runtime dependencies before release packaging. Both the
x64 and ARM64 portable payloads must ship `vcruntime140.dll` in the payload
root for the native speech stack. Both build legs source their loose VC runtime
DLLs from the Visual Studio install on the CI runner (resolved via `vswhere` in
`src\Directory.Build.targets`). This ensures the bundled CRT is new enough for
`onnxruntime` - the `VCRuntime.CefSharp.140` NuGet is only used as a dev-time
convenience for local `dotnet build` (not publish). The release validation
script enforces a minimum VC++ runtime version floor (currently 14.38) to
prevent regressions, and the x64 verifier load-probes the native TTS stack
(`onnxruntime.dll`, `sherpa-onnx.dll`, and `sherpa-onnx-c-api.dll`) from the
published payload so app-local runtime mismatches are caught before release.
The release job must Authenticode-verify Microsoft's x64 and ARM64 Visual C++
Runtime redistributables before passing the
architecture-matching redistributable to Inno. The installer runs the
redistributable before launching the tray so clean or stale Windows hosts can
repair the runtime before native speech components initialize, and it
skips the post-install tray launch if the runtime installer fails.

The current Azure Artifact Signing resource is:

- Account: `openclaw`
- Certificate profile: `openclaw`
- Endpoint: `https://eus.codesigning.azure.net/`
- Public trust certificate subject:
  `CN=OpenClaw Foundation, O=OpenClaw Foundation, L=Mill Valley, S=California, C=US`

GitHub Actions authenticates with Azure through OIDC, not a stored client
secret. The release job runs in the `release-signing` environment and requires:

- `AZURE_CLIENT_ID`
- `AZURE_TENANT_ID`
- `AZURE_SUBSCRIPTION_ID`

Do not add `AZURE_CLIENT_SECRET` back to the release workflow. The Entra app
registration should have a federated credential for:
`repo:openclaw/openclaw-windows-node:environment:release-signing`.

## How CI signs payload binaries

The release workflow does not recursively sign every `.exe`. Instead it creates
temporary signing input directories with hardlinks to only the OpenClaw-owned
executables and DLLs from the x64 and ARM64 payloads, then runs Azure Artifact
Signing on those allowlists. Because these are NTFS hardlinks, signing the
staged file signs the real payload file.

After signing, CI verifies the actual payload directory, not the staging folder.
If hardlink signing does not affect the payload, the verifier fails before
release artifacts are created.

## Expected release workflow jobs

For release tags, the **Build and Test** workflow should run:

- `change-classification` with the `full` result
- `fast-validation`
- `test`
- `e2etests` shards: `setup-connect`, `revocation-recovery`, and `network-recovery`
- `build` matrix entries shown by GitHub as `build (win-x64)` and `build (win-arm64)`
- `CI Gate`
- `release`

The `setup-connect` E2E shard contains the MXC proof tests for the gateway ->
Windows node -> `system.run` path and validates that the expected proof test
names appear in the TRX output. GitHub-hosted runners may report those MXC
proofs as skipped when the host is not MXC-capable; use
`.\scripts\validate-mxc-e2e.ps1` for required local/self-hosted MXC merge
validation. Release tags cannot enter the `release` job until **CI Gate**
confirms classification, fast validation, tests, E2E, and release builds all
succeeded. The `build-msix` job is disabled with `if: false` while MSIX
distribution is paused, so it should not appear in the required run list.

The release job should:

1. Download x64/ARM64 tray payload artifacts.
2. Authenticate to Azure with OIDC in the `release-signing` environment.
3. Sign only the OpenClaw-owned EXEs and DLLs in both payloads.
4. Verify binary signing policy.
5. Create the portable x64 and ARM64 ZIPs.
6. Build Inno installers.
7. Sign installers.
8. Create a GitHub release whose prerelease flag matches the tag, with installer
   and portable ZIP assets.

## Post-release verification

After the release exists, download an installer and both portable ZIPs and
verify:

```powershell
$tag = "v0.6.12" # replace with the tag being verified
gh release view $tag --repo openclaw/openclaw-windows-node `
  --json tagName,isPrerelease,isLatest,url,assets
```

Expected:

- Stable tags: `isPrerelease` is `false`.
- Alpha tags: `isPrerelease` is `true` and `isLatest` is `false`.
- Installer EXEs are signed.
- In ZIP payload:
  - `OpenClaw.Tray.WinUI.exe` is OpenClaw-signed.
  - All listed OpenClaw-owned DLLs are OpenClaw-signed.
  - `wxc-exec.exe`, `createdump.exe`, and `RestartAgent.exe` are not
    OpenClaw-signed.

## If a tag build fails

Do not move a published tag. After the fix is merged to `main`, create a new
tag: increment `alpha.N` for a prerelease, or choose the next intended stable
version.

Use these commands to inspect state:

```powershell
git status --short --branch
git rev-parse HEAD
git rev-parse origin/main
$tagPrefix = "vX.Y.Z" # use the stable or prerelease version family being fixed
git ls-remote --tags origin "refs/tags/$tagPrefix*"

gh run list --repo openclaw/openclaw-windows-node `
  --workflow "Build and Test" `
  --limit 10
```

Only tag when `HEAD == origin/main`.

## Versioning rules

- Do not manually bump project or manifest versions for routine releases.
- Do not add csproj `<Version>` release fallbacks; product versions come from
  GitVersion/tag history.
- Release versions come from the tag (`vX.Y.Z` or `vX.Y.Z-alpha.N`).
- Untagged `master` builds are prerelease builds. After `vX.Y.Z-alpha.N`, an
  untagged commit may resolve to the next alpha prerelease, for example
  `X.Y.Z-alpha.(N+1)`.
- CI computes GitVersion outputs for artifact naming, while product builds use
  GitVersion-backed assembly metadata.
