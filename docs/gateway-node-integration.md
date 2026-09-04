# OpenClaw Gateway ↔ Windows Node Integration Guide

> Last reviewed for the end-to-end FAQ: 2026-08-06
> Source audited at: [`openclaw/openclaw` commit `db90dff`](https://github.com/openclaw/openclaw/blob/db90dff1396fecbf7029e9e9ea19d6c6ca3e644e/src/gateway/node-command-policy.ts)

This document captures everything we've learned about how the OpenClaw gateway handles node commands, platform allowlists, and the QR bootstrap pairing flow. It exists because these details are not obvious from the docs alone and caused real debugging sessions.

For operator versus node roles, exec approvals, shell wrapping, protocol
versioning, and Windows MXC, see the
[Gateway, node, and exec flow FAQ](OPENCLAW_GATEWAY_NODE_EXEC_FAQ.md).

---

## 1. The Gateway Command Allowlist System

Every command a node sends must pass **two** gates before it works:

1. **The node must declare it** - in the `commands` array of the `connect` handshake
2. **The gateway must allow it** - via a per-platform allowlist in `node-command-policy.ts`

If either gate fails, the command is silently dropped or rejected with:
```
node command not allowed: "X" is not in the allowlist for platform "Y"
```

### 1.1 Per-Platform Default Allowlists

The gateway has hardcoded defaults per platform (from `PLATFORM_DEFAULTS`):

| Platform | Default Commands |
|----------|-----------------|
| **macOS** | canvas.*, camera.list, location.get, device.info/status, contacts.search, calendar.events, reminders.list, photos.latest, motion.*, system.run/which/notify, screen.snapshot, browser.proxy |
| **iOS** | canvas.*, camera.list, location.get, device.info/status, contacts.*, calendar.*, reminders.*, photos.latest, motion.*, system.notify |
| **Android** | canvas.*, camera.list, location.get, notifications.*, device.*, contacts.*, calendar.*, callLog.search, reminders.*, photos.latest, motion.*, system.notify |
| **Windows** | camera.list, location.get, device.info, device.status, system.*, browser.proxy, screen.snapshot |
| **Linux** | system.run, system.run.prepare, system.which, system.notify, browser.proxy |
| **Unknown** | canvas.*, camera.list, location.get, system.notify |

Desktop host commands such as `system.run`, `browser.proxy`, and
`screen.snapshot` are filtered unless the node reports canonical desktop
metadata and the command is approved through pairing/live session state or
explicit config.

### 1.2 "Dangerous" Commands (Always Need Explicit Opt-In)

These commands are **never** in any platform's defaults, regardless of platform:

```typescript
CAMERA_DANGEROUS_COMMANDS = ["camera.snap", "camera.clip"]
SCREEN_DANGEROUS_COMMANDS = ["screen.record"]
CONTACTS_DANGEROUS_COMMANDS = ["contacts.add"]
CALENDAR_DANGEROUS_COMMANDS = ["calendar.add"]
REMINDERS_DANGEROUS_COMMANDS = ["reminders.add"]
SMS_DANGEROUS_COMMANDS = ["sms.send", "sms.search"]
```

Even macOS doesn't get `camera.snap` or `camera.clip` by default! They must be added via `gateway.nodes.allowCommands`.

### 1.3 How to Enable Privacy-Sensitive Commands for Windows

Normal first-party Windows companion commands should work after pairing when the
node reports canonical `platform: "windows"` and `deviceFamily: "Windows"`.
Only add privacy-sensitive commands when you explicitly want the gateway to
allow camera capture or screen recording:

```json5
{
  gateway: {
    nodes: {
      allowCommands: [
        "camera.snap",
        "camera.clip",
        "screen.record",
      ]
    }
  }
}
```

After changing config:
```bash
openclaw gateway restart
```

After changing the node's command list, approve the pending reapproval request:
```bash
openclaw nodes pending
openclaw nodes approve <pendingRequestId>
```

`openclaw nodes pending` only discovers request IDs; it does not approve declarations.

Older gateways without pending reapproval support may still require rejecting and re-pairing the node.

### 1.4 Why Reapproval is Needed

The gateway snapshots the node's declared `commands` array at **pairing approval time**. When a newer gateway detects changed declarations, `node.list` and `node.describe` keep the existing `caps`, `commands`, and `permissions` as the approved/effective snapshot and report the proposed replacement under `pendingDeclaredCaps`, `pendingDeclaredCommands`, and `pendingDeclaredPermissions`. Approve the reported request explicitly:

```powershell
openclaw nodes approve <pendingRequestId>
```

Then reconnect the node and verify the effective command and capability counts update. Pending declarations are never effective before approval. Older gateways that do not report pending reapproval fields may still require rejecting and re-pairing the node.

### 1.5 `denyCommands`

You can also explicitly deny commands:
```json5
{ gateway: { nodes: { denyCommands: ["system.run"] } } }
```
`denyCommands` wins over `allowCommands`.

---

## 2. Command Name Mismatches (Bugs We Found)

### 2.1 `screen.capture` → Should Be `screen.snapshot`

The Windows node previously registered `screen.capture` as a command name. The gateway calls it **`screen.snapshot`**:

```typescript
// Gateway source (node-command-policy.ts)
const SCREEN_COMMANDS = ["screen.snapshot"];
```

The macOS node uses `screen.snapshot`. `screen.capture` is not recognized by the gateway at all - it's silently filtered out of the declared commands.

**Fixed locally**: `ScreenCapability.cs` now advertises and handles `screen.snapshot`.

### 2.2 `screen.list` - Not a Gateway Command

Our node previously registered `screen.list`. This command does not exist in the gateway's command policy. It's never in any default allowlist.

**Fixed locally**: `screen.list` is no longer advertised.

### 2.3 `screen.record.start` / `screen.record.stop` - Not Mac/Gateway Commands

PR #159 originally explored session-based start/stop recording commands, but the current Mac node and gateway command surface only define fixed-duration `screen.record`.

**Fixed locally**: Windows now implements only fixed-duration `screen.record`; `screen.record.start` and `screen.record.stop` are intentionally not advertised.

### 2.4 Verified Correct Names

| Our Command | Gateway Canonical | Status |
|-------------|-------------------|--------|
| `camera.list` | `camera.list` | ✅ Match |
| `camera.snap` | `camera.snap` | ✅ Match (dangerous) |
| `camera.clip` | `camera.clip` | ✅ Match (dangerous) |
| `screen.snapshot` | `screen.snapshot` | ✅ Match |
| `location.get` | `location.get` | ✅ Match |
| `system.notify` | `system.notify` | ✅ Match |
| `system.run` | `system.run` | ✅ Match |
| `system.run.prepare` | `system.run.prepare` | ✅ Match |
| `system.which` | `system.which` | ✅ Match |
| `canvas.present` | `canvas.present` | ✅ Match |
| `canvas.hide` | `canvas.hide` | ✅ Match |
| `canvas.navigate` | `canvas.navigate` | ✅ Match |
| `canvas.eval` | `canvas.eval` | ✅ Match |
| `canvas.snapshot` | `canvas.snapshot` | ✅ Match |
| `canvas.a2ui.push` | `canvas.a2ui.push` | ✅ Match |
| `canvas.a2ui.pushJSONL` | `canvas.a2ui.pushJSONL` | ✅ Match (legacy alias) |
| `canvas.a2ui.reset` | `canvas.a2ui.reset` | ✅ Match |
| `device.info` | `device.info` | ✅ Match |
| `device.status` | `device.status` | ✅ Match |
| `screen.record` | `screen.record` | ✅ Match (dangerous) |

### 2.5 Remaining Command Gaps vs Current Mac Node

| Command | macOS | Windows | Notes |
|---------|-------|---------|-------|
| `browser.proxy` | ✅ | ✅ | Local browser-control bridge; requires browser control host on gateway port + 2, retries with password/basic auth if bearer auth is rejected, and managed SSH tunnel mode forwards local+2 to remote+2 when enabled |

### 2.6 Safe Gateway-Policy Gaps to Consider

The gateway's macOS/iOS default allowlists include other mobile-oriented commands such as contacts, calendar, reminders, photos, and motion. Those remain outside the Windows tray's current companion-node scope.

---

## 3. Platform Detection

The gateway detects platform from two fields in the `connect` handshake:

```typescript
// Our connect payload
client: {
  platform: "windows",    // ← Primary signal
  mode: "node",
}
```

Detection logic (from `node-command-policy.ts`) now treats desktop command
defaults as a stricter, canonical-platform path:

1. Normalize `platform` and `deviceFamily`.
2. Match only canonical platform IDs such as `windows`, `macos`, and `linux`.
3. Require desktop platforms to have a matching desktop family, for example
   `platform: "windows"` with `deviceFamily: "Windows"`.
4. If metadata is missing or noncanonical, fall back to `"unknown"` and a
   conservative allowlist.

Our node should therefore send canonical Windows metadata. SetupEngine also
writes `gateway.nodes.allowCommands` from its enabled capability configuration
for local WSL gateway installs so the first-party Windows companion flow has an
explicit gateway policy matching the node's advertised commands.

---

## 4. The QR / Bootstrap Token Flow

### 4.1 What `openclaw qr` Does

1. Calls `issueDeviceBootstrapToken()` on the gateway
2. Generates a **short-lived, single-use** `bootstrapToken`
3. Encodes `{ url, urls?, bootstrapToken }` as base64url. The gateway enforces
   the 10-minute token lifetime; the payload does not include expiry metadata.
4. Renders as QR code or pasteable setup code

### 4.2 bootstrapToken vs gateway.auth.token

| | `bootstrapToken` | `gateway.auth.token` |
|---|---|---|
| **Purpose** | Initial device pairing | Shared-secret auth for operators |
| **Lifetime** | Short-lived, single-use | Permanent until changed |
| **Scope** | Server-enforced full or limited setup profile | Full operator access |
| **Generated by** | `openclaw qr` / `/pair` | User config in `openclaw.json` |
| **Auto-approval** | Yes - gateway auto-approves bootstrap-token handshakes | No - manual `devices approve` needed |

### 4.3 The Auth Cascade (How the Gateway Resolves Auth)

When a node connects with `auth: { token: "...", bootstrapToken: "..." }`, the gateway tries (from `auth-context.ts`):

1. **Shared-secret auth** - `auth.token` vs `gateway.auth.token/password`
2. **Bootstrap token** - `auth.bootstrapToken` vs issued bootstrap tokens
   - If valid: `authMethod = "bootstrap-token"`, auto-approved!
   - Preferred over shared-secret even if both succeed (QR flow relies on this)
3. **Device token** - `auth.token` as device-token fallback (for already-paired devices)

### 4.4 Setup Wizard Entry Points

The setup code and QR code are the same bootstrap concept in different packaging:

```text
QR image
  -> decodes to setup code text
    -> decodes to JSON payload
      -> contains gateway URL(s) + bootstrapToken
```

Advanced users can drop into setup at any level:

| Entry point | User has | Wizard behavior |
|---|---|---|
| QR image | A saved/screenshot/email attachment containing the QR | Import or paste the image, decode QR text, then decode the setup payload |
| Setup code | The pasteable text from `openclaw qr` | Paste the text directly, then decode the setup payload |
| Manual URL + token | Gateway URL/IP and a long-lived gateway token | Skip bootstrap; connect with `auth.token` and use manual approval if required |

The QR/setup-code path is preferred for first-time node onboarding because it avoids telling users to copy permanent gateway secrets and enables auto-approval.

### 4.5 What Our Setup Wizard Does

The Windows Setup Wizard:
1. Accepts a QR image, clipboard QR image, pasteable setup code, or manual gateway URL/token.
2. For QR/setup-code input, decodes `{ url, bootstrapToken }`; the optional
   upstream `urls` fallback list is not used by the current decoder.
3. Stores `bootstrapToken` in the active `GatewayRecord.BootstrapToken`; manual long-lived tokens are stored as `GatewayRecord.SharedGatewayToken`.
4. Sends it as `auth.bootstrapToken` in the node connect handshake.

This lets the gateway correctly classify QR setup as a bootstrap-token handshake, which enables:
- Silent auto-approval (no manual `devices approve` needed)
- Bootstrap token revocation after pairing
- Bounded operator token handoff (if configured)

### 4.6 Post-Pairing: Device Tokens

After a successful bootstrap-token pairing:
1. Gateway issues a `deviceToken` in `hello-ok.auth.deviceToken`
2. Node should **save** this device token
3. Future connections use `auth.token = <deviceToken>` (device-token auth path)
4. The bootstrap token is revoked and no longer valid

Windows stores `hello-ok.auth.deviceToken` in the per-gateway device identity file and prefers that saved device token on future node connections. The bootstrap token is only used when there is no saved device token yet.

### 4.7 Bootstrap Flow

```
1. User runs `openclaw qr` on gateway host
2. User imports/scans QR image or pastes setup code into Windows Setup Wizard
3. Wizard decodes → { url, bootstrapToken }
4. Node connects with: auth: { bootstrapToken: "<token>" }
5. Gateway auto-approves pairing (bootstrap-token auth method)
6. Gateway returns hello-ok with: auth: { deviceToken: "<token>" }
7. Node saves deviceToken to identity store
8. Future connections use: auth: { token: "<deviceToken>" }
9. No manual `devices approve` needed!
```

Manual URL/token setup remains useful for advanced troubleshooting and environments where QR/bootstrap is unavailable. In that path, the tray may show a pairing notification with an `openclaw devices approve <device-id>` command that must be run on the gateway host.

---

## 5. Recommendations

### 5.0 Design Conclusion: Safe Windows/macOS Parity

The root issue is not that the gateway fails to recognize Windows. It recognizes Windows correctly. The problem is that `platform: "windows"` currently gets only the headless exec-host defaults, while the Windows tray app is now a full node that can declare canvas, camera, location, and screen capabilities.

The simplest upstream fix is to make Windows match macOS for **safe declared commands**, while keeping dangerous commands explicit opt-in.

This does **not** make every Windows node capable of camera/canvas/location/screen. A command still has to pass both gates:

1. The node must declare the command.
2. The gateway policy must allow the command.

So a headless Windows node host that only declares `system.run` / `system.which` remains exec-only. Expanding the Windows default allowlist just stops the gateway from filtering safe commands that a Windows node explicitly advertises.

Recommended gateway defaults:

| Command bucket | Windows default? | Reason |
|----------------|------------------|--------|
| Safe declared companion commands: `canvas.*`, `camera.list`, `location.get`, `screen.snapshot`, `device.info`, `device.status` | Yes | Matches macOS parity and only applies when declared by the node |
| Dangerous/privacy-heavy commands: `camera.snap`, `camera.clip`, `screen.record`, write commands like `contacts.add` | No | Existing gateway model already requires explicit `gateway.nodes.allowCommands` |
| Exec commands: `system.run`, `system.run.prepare`, `system.which`, `system.notify`, `browser.proxy` | Yes | Existing Windows headless-host behavior |

For the first-party Windows companion node, the practical local solution is:

1. Keep declaring the correct command names from the Windows node.
2. Send canonical connect metadata: `platform: "windows"` and
   `deviceFamily: "Windows"`.
3. Reapprove command-list changes because the gateway snapshots commands at approval time; older gateways may require re-pairing.

### 5.1 Gateway Node Allowlist Configuration

`gateway.nodes.allowCommands` is the explicit opt-in list the gateway uses after
platform defaults. It should contain exact command names, not broad wildcard
grants, and should not be needed for the normal first-party Windows companion
commands that are allowed by canonical Windows platform policy and declared by
the live node.

`gateway.nodes.denyCommands` can be used as a final explicit blocklist when you want to suppress a command even if a platform default or allowlist entry would otherwise allow it.

Privacy-sensitive commands should stay out of the default safe list and should only be added deliberately:

```text
camera.snap
camera.clip
screen.record
```

After changing either `gateway.nodes.allowCommands` or `gateway.nodes.denyCommands`, check Command Center for `pending-reapproval`. Copy and run its exact `openclaw nodes approve <pendingRequestId>` command, reconnect the Windows node, and verify the effective command and capability counts update. A gateway restart alone does not approve pending declarations. Older gateways without pending reapproval diagnostics may still require re-pairing.

### 5.2 Immediate Code Fixes (This Branch)

- [x] Rename `screen.capture` → `screen.snapshot` in `ScreenCapability.cs`
- [x] Remove `screen.list` from declared commands
- [x] Remove debug logging from `WindowsNodeClient.cs`
- [x] Add Mac-compatible fixed-duration `screen.record`; do not add `screen.list` or record start/stop commands

### 5.3 Setup Wizard Improvements

- [x] Send `bootstrapToken` in correct field: `auth.bootstrapToken` not `auth.token`
- [x] Handle `hello-ok.auth.deviceToken` - save it for future connections
- [x] Accept QR images and clipboard setup content as alternate ways to enter the same bootstrap payload
- [x] Show "auto-paired!" vs "waiting for approval" based on auth method
- [x] Add Settings toggles for optional Windows node capability groups (`canvas`, `screen`, `camera`, `location`, `browser.proxy`)

### 5.4 Upstream Alignment

- [x] **Use canonical Windows node metadata** - Windows sends
  `platform: "windows"` and `deviceFamily: "Windows"` so the gateway can apply
  desktop command policy without a global allowlist workaround.
- [x] **Keep privacy-sensitive commands explicit opt-in** - `camera.snap`,
  `camera.clip`, and `screen.record` remain behind `gateway.nodes.allowCommands`.
- [x] **Add `canvas.a2ui.pushJSONL`** - current Mac supports it as a legacy JSONL alias; Windows routes it through the same A2UI push handler

The gateway still enforces both gates: the node must declare a command in
`commands`, and gateway policy must allow it. Headless Windows node hosts that
only declare `system.run` / `system.which` remain exec-only.

### 5.5 User-Facing Documentation

When shipping the Windows node, README/wiki should tell users that normal
first-party companion commands are available after pairing when the node reports
canonical Windows metadata. Users should add `camera.snap`, `camera.clip`, and
`screen.record` to `gateway.nodes.allowCommands` only when they explicitly want
to allow privacy-sensitive camera or screen capture.
> The Windows tray Command Center (`openclaw://commandcenter`) surfaces policy
> problems directly, including pending pairing approval and privacy-sensitive
> opt-ins.

---

## 6. Reference: Gateway Source Files

| File | What It Does |
|------|-------------|
| `src/gateway/node-command-policy.ts` | Platform allowlists, dangerous commands, command filtering |
| `src/gateway/device-metadata-normalization.ts` | Platform string normalization |
| `src/infra/node-commands.ts` | Constants: `system.run/which/notify`, `browser.proxy`, `execApprovals.*` |
| `src/gateway/server/ws-connection/auth-context.ts` | Auth cascade: shared-secret → bootstrap-token → device-token |
| `extensions/device-pair/index.ts` | QR generation, bootstrap token issuance, pairing flow |
| `src/cli/nodes-screen.ts` | CLI screen record helpers (confirms `screen.record` naming) |
