# Connection protocol research

This document captures the current understanding of OpenClaw gateway connection
and pairing behavior, then maps it to the Windows tray/node implementation.

For the larger operator/node authority model and command execution flow, see
the [Gateway, node, and exec flow FAQ](OPENCLAW_GATEWAY_NODE_EXEC_FAQ.md).

The goal is reliable pairing and reconnect behavior across the operator and
node roles. The important distinction is that there are two related but separate
trust systems:

1. **WebSocket device pairing** approves a device identity for a role and scope
   set during the `connect` handshake.
2. **Gateway-owned node pairing** (`node.pair.*`) approves a node as a trusted
   command host. On current gateways, node commands are hidden until this trust
   approval completes, even if WebSocket device pairing has already succeeded.

## Source baseline

Local Windows code reviewed:

- `src/OpenClaw.Shared/OpenClawGatewayClient.cs`
- `src/OpenClaw.Shared/WindowsNodeClient.cs`
- `src/OpenClaw.Shared/DeviceIdentity.cs`
- `src/OpenClaw.Connection/GatewayConnectionManager.cs`
- `src/OpenClaw.Connection/CredentialResolver.cs`
- `src/OpenClaw.Connection/GatewayRegistry.cs`
- `src/OpenClaw.Connection/NodeConnector.cs`
- `src/OpenClaw.Connection/OperatorScopeHelper.cs`
- `src/OpenClaw.SetupEngine/SetupSteps.cs`
- `src/OpenClaw.Tray.WinUI/App.xaml.cs`
- `docs/CONNECTION_ARCHITECTURE.md`
- `docs/ONBOARDING_WIZARD.md`
- `docs/WINDOWS_NODE_TESTING.md`

Public upstream gateway sources reviewed:

The execution FAQ pins its audit to upstream commit
`db90dff1396fecbf7029e9e9ea19d6c6ca3e644e`. The links below intentionally
follow `main` for ongoing pairing research and must be rechecked before relying
on newer behavior.

- `https://github.com/openclaw/openclaw/blob/main/docs/gateway/protocol.md`
- `https://github.com/openclaw/openclaw/blob/main/docs/gateway/pairing.md`
- `https://github.com/openclaw/openclaw/blob/main/docs/gateway/operator-scopes.md`
- `https://github.com/openclaw/openclaw/blob/main/docs/cli/qr.md`
- `https://github.com/openclaw/openclaw/blob/main/docs/cli/devices.md`
- `https://github.com/openclaw/openclaw/blob/main/src/pairing/setup-code.ts`
- `https://github.com/openclaw/openclaw/blob/main/src/infra/device-bootstrap.ts`
- `https://github.com/openclaw/openclaw/blob/main/src/shared/device-bootstrap-profile.ts`
- `https://github.com/openclaw/openclaw/blob/main/src/gateway/server/ws-connection/auth-context.ts`
- `https://github.com/openclaw/openclaw/blob/main/src/gateway/server/ws-connection/handshake-auth-helpers.ts`
- `https://github.com/openclaw/openclaw/blob/main/src/gateway/server/ws-connection/message-handler.ts`
- `https://github.com/openclaw/openclaw/blob/main/src/gateway/server-methods/nodes.ts`
- `https://github.com/openclaw/openclaw/blob/main/packages/gateway-protocol/src/schema/frames.ts`

The setup engine uses the official installer without `--version`, so npm
`latest` selects the stable OpenClaw package. Setup records the installed CLI
version and requires the gateway handshake to report the same version.

Package selection is independent of the WebSocket protocol range. Windows
currently advertises `minProtocol: 3` and `maxProtocol: 4`; the gateway reports
its current protocol constant in `hello-ok.protocol`, not a per-connection
negotiated value. After the gateway accepts the advertised range, Windows records
that integer constant for diagnostics and requires it to be at least
`MinimumSupportedVersion`; a lower or malformed value is a terminal protocol
failure. A structured `PROTOCOL_MISMATCH` rejection determines which side falls
outside the advertised range. Current upstream gateways use the protocol-3
N-1 node window only when both `role` and `client.mode` are `node` and the
client range does not support the gateway's current protocol. Because Windows
advertises `maxProtocol: 4`, its node connection uses protocol 4 against a
current protocol-4 gateway. Upstream release `v2026.6.11` implements protocol 4.
Existing remote gateways may run another release as long as the negotiated
protocol and methods used by Windows are compatible.

## Wire protocol summary

The gateway WebSocket endpoint is a JSON text-frame protocol.

1. Gateway sends the pre-auth challenge:

   ```json
   {
     "type": "event",
     "event": "connect.challenge",
     "payload": { "nonce": "...", "ts": 1737264000000 }
   }
   ```

2. Client replies with a first-frame `connect` request:

   ```json
   {
     "type": "req",
     "id": "...",
     "method": "connect",
     "params": {
       "minProtocol": 3,
       "maxProtocol": 4,
       "client": {
         "id": "cli",
         "version": "1.2.3",
         "platform": "windows",
         "mode": "cli",
         "displayName": "OpenClaw Windows Tray"
       },
       "role": "operator",
       "scopes": ["operator.admin"],
       "caps": [],
       "commands": [],
       "permissions": {},
       "auth": { "token": "..." },
       "locale": "en-US",
       "userAgent": "openclaw-windows-tray/1.2.3",
       "device": {
         "id": "device_fingerprint",
         "publicKey": "...",
         "signature": "...",
         "signedAt": 1737264000000,
         "nonce": "..."
       }
     }
   }
   ```

3. Gateway replies with `hello-ok`:

   ```json
   {
     "type": "res",
     "id": "...",
     "ok": true,
     "payload": {
       "type": "hello-ok",
       "protocol": 4,
       "server": { "version": "...", "connId": "..." },
       "features": { "methods": ["..."], "events": ["..."] },
       "snapshot": {},
       "auth": {
         "deviceToken": "...",
         "role": "operator",
         "scopes": ["operator.read", "operator.write"]
       },
       "policy": {
         "maxPayload": 26214400,
         "maxBufferedBytes": 52428800,
         "tickIntervalMs": 15000
       }
     }
   }
   ```

The first request must be `connect`; later `connect` RPCs are rejected. The
current protocol schema allows `auth.token`, `auth.bootstrapToken`,
`auth.deviceToken`, `auth.password`, and `auth.approvalRuntimeToken`.

### Device signatures

The client signs a deterministic payload with its Ed25519 private key. Current
Windows clients attempt v3 signatures first, which include platform and device
family, then fall back to v2 when the gateway rejects the signature. Local WSL
gateway records are treated as v2-required to avoid metadata-upgrade churn.

The signature token must match the auth credential used for this connect:

- stored device token for paired reconnects;
- bootstrap token for setup-code/bootstrap connects;
- shared token/password for shared-secret connects.

## Token and credential taxonomy

| Credential | Stored by Windows | Sent as | Main use | Notes |
| --- | --- | --- | --- | --- |
| Shared gateway token | `GatewayRecord.SharedGatewayToken` | `auth.token` for WS, HTTP bearer or URL fragment for HTTP/dashboard | Trusted operator access and HTTP surfaces | HTTP/chat/dashboard prefer this over device tokens. |
| Bootstrap token | `GatewayRecord.BootstrapToken` | `auth.bootstrapToken` | QR/setup-code first pairing | Short-lived, device-bound, role/scope-bounded, not the shared token. |
| Operator device token | per-gateway `device-key-ed25519.json` | `auth.deviceToken` | Durable operator WS reconnect | Carries approved operator scopes. |
| Node device token | per-gateway `device-key-ed25519.json` | `auth.deviceToken` for role `node` | Durable node WS reconnect | Does not by itself guarantee node commands are exposed. |
| Node-pair issued token | gateway node pairing store | `node.pair.verify` / node pairing store | Legacy gateway-owned node trust | Separate from WS device token. |
| MCP bearer token | `%APPDATA%\OpenClawTray\mcp-token.txt` | local HTTP Authorization header | Local MCP only | Not part of gateway pairing. |

For WebSocket connects, `CredentialResolver` currently resolves credentials in
this order:

1. stored role-specific device token;
2. shared gateway token;
3. bootstrap token;
4. no credential.

This is correct for avoiding paired-device downgrades. HTTP/dashboard surfaces
use `InteractiveGatewayCredentialResolver`, which prefers shared gateway tokens
because those HTTP endpoints expect the gateway shared token, not per-device
WebSocket tokens.

## Operator scopes

The current upstream operator scope set is:

- `operator.read`
- `operator.write`
- `operator.admin`
- `operator.pairing`
- `operator.approvals`
- `operator.talk.secrets`

`operator.admin` satisfies all operator scopes. `operator.write` satisfies
normal mutating operator work and read behavior. `operator.pairing` allows
pairing-management methods to be reached, but approval handlers may require
extra authority based on what is being approved.

Important approval-time checks:

- `device.pair.approve` can be reachable with `operator.pairing`, but approving
  non-operator roles, including `role: node`, requires `operator.admin`.
- Operator-device approvals can only mint/preserve scopes held by the caller
  unless the caller has `operator.admin`.
- `node.pair.approve` is reachable with `operator.pairing`, then derives extra
  requirements from the pending node command list:
  - commandless request: `operator.pairing`;
  - non-exec commands: `operator.pairing` plus `operator.write`;
  - `system.run`, `system.run.prepare`, or `system.which`:
    `operator.pairing` plus `operator.admin`.

This means a QR/setup-code bounded operator handoff token is not enough to
approve Windows node command trust. The default bootstrap handoff token includes
`operator.approvals`, `operator.read`, `operator.talk.secrets`, and
`operator.write`; it intentionally excludes `operator.admin` and
`operator.pairing`.

## Flow 1: shared-token operator connect

Shared-token connect is the manual/trusted operator path.

1. User supplies URL and shared gateway token, or setup engine writes the shared
   token into the gateway registry.
2. Windows creates/loads the per-gateway device identity.
3. `OpenClawGatewayClient` connects as `role: operator` and sends
   `auth.token`.
4. If no operator device token exists, it requests `operator.admin` by default
   for non-bootstrap shared-token auth.
5. Gateway verifies shared secret and device signature.
6. Gateway may silently approve local/shared-secret equivalent device pairing
   when allowed by locality rules.
7. `hello-ok.auth` returns negotiated scopes and may include an operator
   `deviceToken`.
8. Windows persists the operator device token for future reconnects.

Reliability risks:

- Shared gateway auth rotation can invalidate device tokens issued under the
  previous shared-secret generation.
- If Windows keeps retrying a stale device token without recovery, it can stay
  disconnected after gateway reset or token rotation.
- `ConnectWithSharedTokenAsync` intentionally clears stored device tokens. That
  is safe only for explicit credential replacement, not for "add/update shared
  token for dashboard" flows.

## Flow 2: QR/setup-code bootstrap

`openclaw qr` emits a base64url setup code containing:

```json
{
  "url": "ws://...",
  "bootstrapToken": "..."
}
```

The setup code does not contain the shared gateway token/password. The
bootstrap token is a short-lived bearer credential with a default TTL of 10
minutes. It is bound to the first device identity and public key that redeems
it.

Upstream default setup-code profile:

- roles: `node`, `operator`;
- operator scopes: `operator.approvals`, `operator.read`,
  `operator.talk.secrets`, `operator.write`;
- no `operator.admin`;
- no `operator.pairing`.

Expected flow:

1. Windows decodes the setup code and stores the bootstrap token in
   `GatewayRecord.BootstrapToken`.
2. Windows clears existing per-gateway role tokens to force a fresh bootstrap
   handshake.
3. Client sends `auth.bootstrapToken` with signed device identity.
4. Gateway verifies the bootstrap token, binds it to the device identity, and
   checks that requested role/scopes fit the issued bootstrap profile.
5. For setup-code profile, gateway can silently approve the bootstrap device
   pairing and issue a primary role token.
6. `hello-ok.auth.deviceToken` contains the primary role token; when additional
   role handoff tokens are issued, `hello-ok.auth.deviceTokens[]` contains the
   additional role entries.
7. Windows must persist every returned role token before considering the device
   paired for restart.

Critical local risk:

- Local `GatewayConnectionManager.HandleDeviceTokenReceived` currently clears
  `GatewayRecord.BootstrapToken` when the node role token is received. In an
  operator bootstrap flow that also emits a node token, the node-token event can
  fire before the operator-token persistence has been proven durable. Clearing
  bootstrap before all required role tokens are durably readable can strand the
  record if a later persistence write fails.

Required invariant:

- Clear bootstrap only after durable, readable persistence of the role tokens
  required by the active flow. For setup-code pairing, that should include the
  node token and the bounded operator token when both are returned by
  `hello-ok`.

## Flow 3: operator device-token reconnect

1. Windows loads the per-gateway operator device token.
2. Client sends `auth.deviceToken`, not `auth.token`.
3. Requested scopes should stay within the token's approved scope baseline.
4. Gateway verifies token, role, scope set, device identity, public key, and
   metadata pinning rules.
5. Gateway returns `hello-ok`.

Failure modes:

- `AUTH_DEVICE_TOKEN_MISMATCH` or similar token mismatch after gateway reset.
- `AUTH_SCOPE_MISMATCH` when the app requests broader scopes than the device
  token carries.
- `NOT_PAIRED` with details when role/scope/public-key/metadata upgrades need
  explicit approval.
- v3 signature rejection requiring v2 fallback.

Current Windows recovery:

- `GatewayConnectionManager.TryScheduleOperatorTokenRecovery` can clear a stale
  operator token and reconnect with bootstrap when a bootstrap token still
  exists. This helps only if bootstrap was not prematurely cleared.

## Flow 4: node device-token reconnect

1. Windows loads the per-gateway node device token.
2. `WindowsNodeClient` sends `role: node`, empty scopes, declared `caps`,
   declared `commands`, granular `permissions`, and `auth.deviceToken`.
3. Gateway verifies the node device token and applies global node command
   allow/deny policy.
4. Gateway reconciles gateway-owned node pairing state. On current gateways,
   unapproved nodes have commands filtered until `node.pair.approve` succeeds.
5. `hello-ok` returns effective node session state.

Important distinctions:

- A node can be WebSocket-authenticated and still have zero effective commands
  if `node.pair.*` command trust is pending or command policy filters the
  declared commands.
- Commands queued before node-pair approval are dropped, not deferred.

Local implementation risk:

- `NodeConnector` intentionally raises `ClientCreated` before `ConnectAsync`
  so `NodeService.AttachClient` can register capabilities before the node
  connect frame is serialized. If the tray bridge misses this event or
  `NodeService` is not available, the node may connect with `caps=0/cmds=0`.

## Flow 5: WebSocket device-pair approval

Device pairing is the durable trust record for device identity, role, scopes,
public key, and approved metadata.

When a device is not paired or requests an upgrade, gateway returns
`NOT_PAIRED` with structured details. The client should preserve the request id
and surface an approval path.

Approval facts:

- `openclaw devices approve --latest` previews the selected request and does
  not mint tokens until rerun with the exact request id.
- Pending requests can be superseded by reconnects with changed auth details.
- Approving `role: node` requires `operator.admin`.
- Non-admin paired-device token sessions are self-scoped for device management.

Reliability risks:

- Approving a stale request id fails.
- A reconnect can supersede the pending entry, requiring the latest request id.
- Treating device pairing and node pairing as the same flow produces false
  "paired" states.

## Flow 6: gateway-owned node-pair command trust

`node.pair.*` is a gateway-owned node trust store. It is separate from the
WebSocket device-pair store.

Events:

- `node.pair.requested`
- `node.pair.resolved`

Methods:

- `node.pair.request`
- `node.pair.list`
- `node.pair.approve`
- `node.pair.reject`
- `node.pair.remove`
- `node.pair.verify`

Current upstream behavior:

- `node.pair.request` is idempotent per node and refreshes metadata/declared
  command snapshots.
- Pending requests expire after 5 minutes.
- Approval always generates a fresh token in the node-pairing store.
- On gateway `2026.3.31+`, node commands are disabled until node pairing is
  approved.

Approval authority:

- commandless: `operator.pairing`;
- non-exec commands: `operator.pairing` plus `operator.write`;
- exec-capable commands: `operator.pairing` plus `operator.admin`.

Windows implications:

- Windows nodes declare `system.run`, `system.run.prepare`, and `system.which`
  when system capability is enabled. Those requests require admin to approve.
- A bounded QR/bootstrap operator token should not auto-approve node command
  trust.
- `GatewayConnectionManager` only auto-approves explicitly typed device-pair
  role upgrades. Node-pair command trust, including reapproval, remains pending
  for an explicit operator decision.
- Device role-upgrade auto-approval diagnostics must report missing
  `operator.admin` instead of a generic failure.

## Flow 7: local WSL setup-engine flow

The setup engine provisions an app-owned WSL gateway and has extra local
authority:

1. Install/configure WSL gateway.
2. Generate/read shared token and setup-code/bootstrap token.
3. Pair operator.
4. Drain pending device approvals before node pairing.
5. Pair node with capabilities registered before connect.
6. Approve node/device pairing through local WSL CLI commands when configured.
7. Verify end-to-end behavior.

Important local behaviors:

- Local gateway records force v2 signatures.
- `PairNodeStep` drains pending device approvals before node connect so a
  WSL CLI scope-upgrade request does not block Windows node approval.
- `AutoApproveNodePairing` chooses device vs node approval command depending on
  whether a request id is available.
- Node finalization is intentionally skipped in one path to avoid rotating
  tokens and invalidating the operator token.

Reliability risks:

- WSL cold start can make connect/pair timeouts look like auth failures.
- Pending device approvals can block node pairing.
- CLI `--latest` preview behavior can be mistaken for approval.
- Token rotation during finalization can invalidate the companion's current
  operator session.

## Initial Windows gap matrix

| Area | Local surface | Current read | Classification | Next action |
| --- | --- | --- | --- | --- |
| WS credential precedence | `CredentialResolver` | Device token beats shared/bootstrap for WS; shared token beats bootstrap. | Aligned | Preserve invariant. |
| HTTP credential precedence | `InteractiveGatewayCredentialResolver` | Shared token preferred for HTTP/dashboard. | Aligned | Preserve and validate dashboard path. |
| QR setup-code shape | `SetupCodeDecoder` | Decodes `{ url, bootstrapToken }`; size and URL checks exist. | Aligned | Cite in protocol doc. |
| Bootstrap token storage | `ApplySetupCodeAsync` | Stores token as `BootstrapToken`, clears role tokens, preserves existing shared token. | Needs integration validation | E2E setup-code with real WSL gateway. |
| Bootstrap clear | `HandleDeviceTokenReceived` | Clears bootstrap on node-token receipt. | Likely surgical fix | Gate clear on durable required-role persistence. |
| Multi-role handoff parsing | `OpenClawGatewayClient` | Parses `auth.deviceTokens[]` and emits role-specific token events. | Needs integration validation | Real gateway bootstrap fixture/E2E. |
| Loopback QR dedupe | `GatewayRegistry.FindByUrl` | Exact URL matching can treat `localhost` and `127.0.0.1` as different records. | Fixed | Match loopback-equivalent same-port URLs so QR reapply preserves shared token. |
| QR bootstrap immediate credential | `ApplySetupCodeAsync`, `CredentialResolver` | Preserved shared token can win over fresh bootstrap token. | Fixed | Force the fresh bootstrap token for the immediate setup-code connect. |
| QR post-bootstrap operator reconnect | `GatewayConnectionManager` | The validated Gateway recommendation may return only a node token on bootstrap; operator must reconnect via the preserved shared token. | Fixed | Schedule post-bootstrap operator reconnect using a durable operator token or preserved shared token. |
| Node token parsing | `WindowsNodeClient` | Parses direct `auth.deviceToken` for node. | Aligned for direct node connect | Validate post-approval reconnect. |
| Node command trust | `GatewayConnectionManager.OnNodePairingStatusChangedAsync` | Explicit node-pair and unknown requests remain pending; only explicitly typed device-pair role upgrades may auto-approve. | Fixed | Preserve explicit operator approval for command trust and reapproval. |
| Approval scope helper | `OperatorScopeHelper.CanApproveDevices` | Checks only admin/pairing. | Needs protocol-specific split | Do not add `operator.approvals` for `node.pair.*`; add clearer helpers. |
| Capability registration | `NodeConnector`, `App.xaml.cs`, `NodeService` | Event-before-connect pattern exists; app warns if binding unavailable. | Likely surgical fix | Fail/hold/reconnect on binding miss instead of silently connecting with no commands. |
| Stale node events | `GatewayConnectionManager` node handlers | Operator events are generation-guarded; node handlers are not equivalently guarded. | Likely surgical fix | Add generation or client-instance stale-event guard. |
| Pending device-pair blockage | `EnsureNodeConnectedAsync` | Existing comments say role-upgrade pending-device-pair can surface as timeout. | Likely surgical fix | Dedicated diagnostic/state and safe recovery path. |
| Shared-token replacement | `ConnectWithSharedTokenAsync` | Clears stored role tokens intentionally. | Needs caller audit | Ensure only explicit replacement flows call it; add non-destructive update if needed. |
| Node-only startup | `ConnectNodeOnlyAsync`, tray startup | Supports node token without operator credential. | Needs compatibility validation | Startup matrix against real/fixture records. |

## Existing validation coverage

Current automated and scripted coverage is useful but not yet enough for the
95% confidence target.

Existing coverage:

- Synthetic connection/pairing state coverage in
  `tests/OpenClaw.Connection.Tests/PairingFlowTests.cs` and
  `tests/OpenClaw.Connection.Tests/NodePairAutoApproveTests.cs`.
- Setup-code decode and credential-precedence coverage in
  `tests/OpenClaw.Connection.Tests/SetupCodeFlowTests.cs`.
- Full setup smoke coverage in
  `tests/OpenClaw.E2ETests/Setup/SetupAndConnectTests.cs`, including headless
  setup, tray connection, node capabilities, gateway config, keepalive,
  dashboard URL token fragment, and WSL PATH behavior.
- MCP local integration coverage in
  `tests/OpenClaw.Tray.IntegrationTests/McpHttpServerIntegrationTests.cs`.
- Real WSL gateway validation script coverage in
  `scripts/validate-wsl-gateway.ps1`, including relay probe, QR/bootstrap
  handoff, operator reconnect proof, and Windows-node pairing proof.
- Real tray-driven E2E coverage now includes:
  - `RealGateway_QrSetupCodeFlow_ReconnectsThroughTrayMcp`, which generates a
    real `openclaw qr --json` setup code in WSL, applies it through the tray MCP
    `app.connection.applySetupCode` tool, waits for durable operator/node
    credentials, then verifies the app reaches ready state.
  - `RealGateway_SharedTokenFlow_ReconnectsThroughTrayMcp`, which reconnects
    through the tray MCP `app.connection.connectSharedToken` tool using the
    real gateway shared token and verifies dashboard/shared-token behavior plus
    durable role tokens.
  - `FullSetup_GatewayCliShowsPairedDeviceAndNode`, which validates gateway
    `devices list --json` has no pending requests and `nodes list --json`
    reports the Windows node from the gateway side.
  - `FullSetup_GatewayRestart_ReconnectsTrayAndNode`, which restarts the real
    WSL gateway service and verifies the tray/node reconnect with persisted
    credentials.
  - Dashboard validation now fetches the generated URL and checks the returned
    HTML for token/auth error markers, not just HTTP status.
  - `app.status` validation includes negotiated operator scopes and asserts the
    operator has `operator.admin` and `operator.pairing` on the normal local
    setup/shared-token path. Explicitly configured SetupEngine onboarding may
    use those scopes to approve command trust without a leftover approval
    banner; `GatewayConnectionManager` runtime leaves command-trust approval
    pending for the operator.
- Test coverage docs explicitly call out that full live gateway/node pairing
  against a remote gateway and long-running reconnect soak behavior are not
  fully covered by automated tests.

Coverage gaps:

- No automated real WSL gateway E2E that intentionally restarts the gateway
  while the Windows node is connected and asserts recovery.
- No live token revocation/rotation scenario that verifies the Windows app
  stops unsafe reconnect loops and routes to pairing recovery.
- No end-to-end proof that device-token precedence survives gateway restart
  and does not downgrade to bootstrap/shared credentials.
- No dashboard/browser-proxy auth parity probe across real gateway reconnect.
- No automated tray MCP token lifecycle smoke across tray restart.

## Surgical fix plan

Do these only after the gap matrix confirms the exact local behavior:

1. **Token durability before bootstrap clear**
   - Track which role tokens were received during a bootstrap handoff.
   - Persist via the identity store.
   - Re-read the identity file before clearing `GatewayRecord.BootstrapToken`.
   - If persistence fails, keep bootstrap and add a diagnostic.
   - Implemented local fix: bootstrap clearing now waits until both operator
     and node role tokens are readable from the per-gateway identity file.
   - Implemented local fix: an explicit shared-token reconnect clears stale
     bootstrap tokens.
   - Implemented local fix: setup-code apply forces the fresh bootstrap token
     for the immediate connect, even when a shared token is preserved for
     HTTP/dashboard use.
   - Implemented local fix: after bootstrap handoff, the operator role is
     reconnected using either the durable operator token or the preserved shared
     token. This covers the validated recommendation behavior where QR bootstrap returns a
     durable node token but not an operator handoff token.

2. **Pairing authority diagnostics**
   - Rename/split helper concepts:
     - device-pair management authority;
     - node-pair command-trust authority;
     - can approve exec-capable node commands.
   - Never treat `operator.approvals` as authority for `node.pair.approve`.
   - Parse/surface gateway `missing scope: ...` errors from `node.pair.approve`.
   - Implemented local fix: `NodePairApproveAsync` now waits for the gateway
     response instead of treating "frame sent" as approval success, and the
     connection manager records missing-scope-oriented diagnostics.
   - Implemented local fix: normal shared-token operator connects request
     `operator.pairing` with `operator.admin`, because current gateways require
     both scopes to approve Windows node command-trust requests that include
     `system.*` commands.

3. **Post-approval reconnect recovery**
   - Scope `_lastAutoApprovedRequestId` to in-flight or completed flows.
   - Reset it on reconnect timeout/failure if gateway reuses request ids.
   - Add diagnostics for same-request-id suppression.

4. **Capability binding hardening**
   - Make `NodeConnector` fail/return a distinct result when `ClientCreated`
     subscribers fail to register capabilities.
   - Or trigger an immediate reconnect after late capability binding.
   - Do not silently connect a production Windows node with zero commands unless
     node mode intentionally has zero enabled capabilities.
   - Implemented local fix: a `ClientCreated` handler failure now aborts the
     node connection before the handshake instead of continuing with empty
     capabilities.

5. **Node stale-event guards**
   - Add generation or client-instance tokens to node status/pairing callbacks.
   - Ignore node events from disposed/replaced clients after reconnect or
     gateway switch.
   - Implemented local fix: `NodeConnector` now forwards status/pairing events
     only from the current client generation.

6. **Blocked device-pair state**
   - Detect `NOT_PAIRED`/pending device-pair details from node connect.
   - Surface a dedicated diagnostic such as "WS device approval required" rather
     than generic timeout.
   - Route to safe local CLI approval only when local authority is proven.

7. **Shared-token semantics**
   - Audit callers of `ConnectWithSharedTokenAsync`.
   - Keep it destructive and explicit.
   - Add a separate non-destructive `UpdateSharedGatewayToken` path if any UI
     flow needs to add HTTP/dashboard token after pairing.

## Validation strategy

Prefer real WSL gateway validation over weak unit tests. Add unit tests only
when they are high-signal, behavior-focused, and cannot be covered through the
real gateway path.

### Existing required validation

After code changes, run:

```powershell
.\build.ps1
dotnet test .\tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj --no-restore
dotnet test .\tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj --no-restore
```

If setup-engine or gateway setup behavior changes, also run the strongest
feasible setup/E2E validation using the real WSL gateway.

### High-value real-gateway scenarios

1. **QR/bootstrap happy path**
   - Install/start app-owned WSL gateway.
   - Generate setup-code with `openclaw qr --json`.
   - Apply setup-code through Windows code path.
   - Verify per-gateway identity has operator and node device tokens.
   - Verify bootstrap token is cleared only after required tokens are readable.

2. **Device-pair vs node-pair distinction**
   - Force a fresh Windows node identity.
   - Approve WS device pairing.
   - Verify node commands are still unavailable until `node.pair.approve`.
   - Approve node pairing and verify effective commands appear.

3. **Exec-command approval authority**
   - Enable Windows system capability so node declares `system.run`,
     `system.run.prepare`, or `system.which`.
   - Attempt node-pair approval with insufficient scopes.
   - Verify gateway returns missing `operator.admin` and Windows diagnostics
     surface that exactly.

4. **Restart/reconnect persistence**
   - Complete pairing.
   - Restart tray/app process.
   - Restart WSL gateway.
   - Verify operator and node reconnect using persisted device tokens, not
     bootstrap/shared downgrade.

5. **Gateway restart while connected**
   - Keep Windows node connected.
   - Restart `openclaw-gateway.service` inside WSL.
   - Verify connection state transitions through disconnected/reconnecting and
     returns to connected without stale node events.

6. **Token revocation/auth failure**
   - Revoke or rotate the active operator/node token from the WSL gateway.
   - Verify Windows does not enter a tight reconnect loop.
   - Verify UX routes to pairing/connection recovery with sanitized diagnostics.

7. **Dirty network or half-open behavior**
   - If feasible, temporarily block the local gateway port or use a safe proxy
     disruption.
   - Verify heartbeat/timeout detection eventually transitions out of connected.

8. **Concurrent command during reconnect**
   - Issue lightweight node invocations or status probes while forcing gateway
     reconnect.
   - Verify requests either succeed or fail cleanly and no stale events mutate
     the new connection.

9. **Shared-token replacement vs update**
   - For an already paired record, confirm explicit shared-token replacement
     clears device tokens.
   - Confirm any token-edit-only/dashboard-token flow preserves device tokens.

10. **Startup compatibility matrix**
    - operator device token only;
    - node device token only;
    - shared token only;
    - bootstrap only;
    - paired node with absent/insufficient operator scopes;
    - local WSL record and, if in scope, SSH/remote record.

## Refactor threshold

Do not refactor first. Refactor only if the gap matrix shows multiple
contradictory state machines or repeated token/pairing rules that cannot be
made reliable surgically.

If needed, introduce a protocol-level pairing coordinator in
`OpenClaw.Connection` that owns:

- auth-flow classification;
- role-token persistence and bootstrap clearing;
- device-pair vs node-pair decision logic;
- approval authority and missing-scope diagnostics;
- post-approval reconnect sequencing;
- stale-event suppression.

Keep `OpenClaw.Shared` focused on wire protocol and token extraction. Keep the
tray UI consuming snapshots/diagnostics rather than constructing gateway
clients directly.
