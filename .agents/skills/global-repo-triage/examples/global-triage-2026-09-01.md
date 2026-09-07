# OpenClaw Windows Node global triage

Snapshot: 2026-09-01. Read-only review of 60 open issues and 31 open pull requests, including item bodies, discussions, reviews, inline comments, timelines, checks, files, commits, and PR patches.

## Change since August 27

| Change | Items |
|---|---|
| New issues | #1280, #1271, #1270, #1256 |
| Issues no longer open | #1245, #1243, #1219, #1218, #1192, #1184, #1172, #1121, #1117, #1115, #995, #980, #901, #881, #840 |
| New PRs | #1283, #1282, #1281, #1279, #1277, #1275, #1274, #1273, #1268, #1267, #1265, #1258, #1253 |
| PRs no longer open | #1237, #1225, #1222, #1212, #1206, #1205, #1204, #1203, #1202, #1174, #1116, #1073, #1071 |

The open backlog dropped from 102 to 91 items. Most of the August execution queue landed or closed. The new work is concentrated in Local AI, setup reliability, one current crash loop, and two platform/security proof lanes.

## Executive queue

1. **TAKE PR #1273.** It is a one-line Windows SDK BuildTools patch update with all CI green. Add the required proof-pool, validation, and not-applicable behavior sections, then merge.
2. **TAKE_AFTER_CHECKS PR #1283.** It is a one-file Crabbox guidance refresh with strong exact-head validation. Current upstream source independently confirms its local-WSL2 versus native-Windows `--github-runner` hydration contract. Wait for current CI, then merge.
3. **Repair PR #1265 before validation.** Both reviewers reproduced deterministic localization-policy failures: roughly 410 all-or-none translation violations and two missing Ollama permission keys. Rebase, reconcile the catalog without weakening the gate, then run full Windows CI.
4. **Redesign PR #1277 before native proof.** Both reviewers require Windows Server and MXC-client proof, and Opus found a concrete sticky-setting path that can persist `SystemRunSandboxEnabled=false` and later bypass guarded HostFallback behavior.
5. **Repair small active defects:** #1282 needs mixed protected/managed duplicate coverage plus a real setup rerun; #1279 needs its existing queued-delivery tests updated plus latest-snapshot burst proof; #1258/#1267/#1268 need deadline/cancellation fixes.
6. **Promote issue #1256.** Replace `curl | bash` with bounded download-to-temp, execute only after a complete download, preserve the real curl error, and prove fresh-WSL DNS recovery.
7. **Repair or close stale PRs:** transplant #1130 and #1141 onto current ownership; obtain proof for #1157; close #938, #962, #1002, #1068, #1164, #1175, #1207, and #1239 rather than spending rebase effort on unsafe or superseded branches.
8. **Clean stale ownership labels.** Several `actively landing` labels no longer describe active implementation, especially #1153, #1154, #1155, #1157, #1232, and #1141.

## Pull requests

| Item | Type / signal | Decision | Take confidence | Recommendation confidence | Effort | Risk | Owner / required validation |
|---|---|---|---:|---:|---|---|---|
| #1283 Refresh Windows Crabbox guidance | Human docs/skill, one file, upstream contract verified | TAKE_AFTER_CHECKS | 93% | 98% | Quick | Low | Current CI must finish. Upstream `openclaw/crabbox` confirms local hydration for Windows WSL2 and `--github-runner` for native Windows. |
| #1282 Prevent Local AI routing ambiguity after rerun | Human bug fix, 2 files, failing unrelated Axe job | HOLD_FOR_AUTHOR | 82% | 97% | Moderate | Medium | Add mixed protected/managed duplicate regression and real WSL setup-rerun proof; rerun full CI. |
| #1281 Recommend Qwen3.8 with measured profiles | Human Local AI redesign, 31 files, red CI | HOLD_FOR_AUTHOR | 20% | 99% | Large | High | Restore consent and installed-model migration, settle quality-versus-context policy, then provide real 16/24/32 GiB proof. |
| #1279 Coalesce Publish bursts | Agent-authored crash mitigation, 1 file | HOLD_FOR_AUTHOR | 76% | 99% | Quick | Medium | Update existing two-callback tests for the changed contract, add latest-snapshot burst coverage, and capture auditable live stress proof. |
| #1277 Start node when MXC probe is unsupported | Copilot bot, sandbox boundary | HOLD_FOR_AUTHOR | 35% | 99% | Moderate | High | Add a distinct unsupported-SKU outcome that does not persist sandbox-disabled state, retain a controlled probe escape hatch, test the real native detector, then run client MXC E2E and Server fallback/strict-denial proof. |
| #1275 Add uninstall documentation and skill | Human docs/skill with destructive instructions | HOLD_FOR_AUTHOR | 40% | 99% | Quick | High | Restrict process termination to intended PID/path, fix AppData postconditions, remove prohibited copy, and prove dry-run/full uninstall on disposable state. |
| #1274 Preselect Local AI on eligible PCs | Human UX change, red CI | DECLINE | 18% | 99% | Moderate | High | Current patch overwrites explicit opt-out and loses choice across restart. Replace with persisted explicit-choice state and full navigation/resume tests. |
| #1273 Bump Windows SDK BuildTools | Dependabot, one-line patch, green CI | TAKE | 96% | 98% | Quick | Low | Add required proof-pool/validation/real-behavior rationale, then merge. |
| #1268 Bound Piper extraction wait | Human reliability fix | HOLD_FOR_AUTHOR | 64% | 98% | Moderate | Medium | Make stderr drain cancellation-aware, add lifecycle regression, and prove success/timeout/cancel on Windows. |
| #1267 Bound Tailscale status read | Human setup reliability fix | HOLD_FOR_AUTHOR | 70% | 98% | Moderate | Medium | Clamp all work to one five-second deadline, add elapsed-time test, and prove a hung Windows Tailscale probe. |
| #1265 Brazilian Portuguese localization | Human localization, stale catalog, deterministic test failures | HOLD_FOR_AUTHOR | 40% | 99% | Large | Medium | Rebase, add the two Ollama permission keys, reconcile about 410 all-or-none translation violations without weakening the gate, then run full Tray localization tests and a pt-BR UI smoke. |
| #1258 Retry CLI installer downloads | Human setup reliability fix | HOLD_FOR_AUTHOR | 68% | 96% | Quick | Medium | Add per-transfer timeout and bound total retry amplification; prove managed-WSL success/failure. |
| #1253 Use CUDA for allocatable GPU memory | Human Local AI capacity work, 18 files | HOLD_FOR_AUTHOR | 40% | 98% | Large | High | Define WDDM/UMA-safe capacity, retain partial facts, and prove allocation plus inference on affected hardware. |
| #1239 Shared-memory capacity and rollback | Human Local AI follow-up, red CI | DECLINE | 15% | 99% | Moderate | High | Close current patch. Dedicated-memory direction is superseded and rollback can leave llama-server running. |
| #1232 Resume Local AI setup with new model | Human lifecycle fix, stale active owner | HOLD_FOR_AUTHOR | 45% | 98% | Moderate | High | Persist model transition across process restart and add crash/resume/rollback tests. |
| #1207 Preserve trailing command output | Draft, conflicting, superseded implementation | DECLINE | 20% | 97% | Moderate | Medium | Close current branch. Reopen narrowly only if current main still demonstrates truncation after the landed drain changes. |
| #1201 Bind WSL relay trust to installed package | Maintainer security draft | NEEDS_HUMAN_TEST | 84% | 97% | Moderate | High | Natural unsigned ARM64 WSL 2.6.3 proof: package path, ACL, version binding, listener provenance, and spoof rejection. |
| #1175 Retry failed WSL setup | Draft, conflicting, destructive legacy flow | DECLINE | 20% | 98% | Moderate | High | Close and reimplement only on current ownership/path/live-registration protections if the issue still reproduces. |
| #1166 Bound MXC termination and drain cleanup | Human focused fix, green CI | NEEDS_HUMAN_TEST | 89% | 98% | Quick | Medium | Run `validate-mxc-e2e.ps1` without skip and capture Gateway-to-node `system.run` proof. |
| #1164 Permissioned Codex session access | Draft, conflicting, 59 files | DECLINE | 5% | 99% | Large | High | Close branch; preserve design in an issue or extension-sized proposal. |
| #1160 Truthful node capability readiness | Draft public-schema feature | HOLD_FOR_AUTHOR | 40% | 96% | Large | High | Sponsor schema first, port onto current schema v2, resolve #1157 stacking, and prove MCP/Gateway transitions. |
| #1159 Guide channel readiness handoff | Draft, current routing drift | HOLD_FOR_AUTHOR | 42% | 97% | Moderate | Medium | Port to ActivationRouter/WindowManager, localize copy, and prove setup completion to Channels. |
| #1158 Bound streaming UI dispatch | Draft, stale chat ownership | HOLD_FOR_AUTHOR | 18% | 96% | Large | High | Reimplement against current projector/conversation ownership and stress-test event storms/session switching. |
| #1157 Preflight browser proxy readiness | Draft, green, proof-only blocker | NEEDS_HUMAN_TEST | 85% | 98% | Moderate | Medium-high | Prove MCP discovery, real `browser.proxy`, Gateway invocation, and Command Center states. Active label appears stale. |
| #1141 Suppress extended-stable updates | Human fix, conflicting, old proof | HOLD_FOR_AUTHOR | 74% | 97% | Moderate | Medium | Transplant onto current connection ownership and confirm Core `effectiveChannel` contract. Clear stale active label until work resumes. |
| #1130 Preserve history cache correlation | Human chat fix, conflicting, credible proof | HOLD_FOR_AUTHOR | 84% | 98% | Moderate | Medium-high | Transplant the focused ID-correlation fix onto current provider ownership; rerun collision/UIA proof. |
| #1128 Upgrade legacy Gateways to Tailscale auth | Human security migration, conflicting | NEEDS_INFO | 68% | 99% | Large | High | Maintainer must approve/reject the legacy trust expansion before rebase and current-head authorization proof. |
| #1068 Replaceable node runtime/sidecar proof | Draft, conflicting speculative bundle | DECLINE | 4% | 99% | Large | High | Close current branch; it lacks current compatibility propagation and protected IPC proof. |
| #1002 Trimmed winnode publishing experiment | Maintainer draft experiment | DECLINE | 12% | 99% | Quick | Medium | Close unless standalone distribution is a committed product; `--help` is not MCP proof. |
| #962 Seed design system | Draft, stale parallel design authority | DECLINE | 3% | 99% | Quick | Medium | Close obsolete guidance and avoid a parallel source of UI truth. |
| #938 Native Windows and WSL gateway modes | Draft, conflicting, 79 files | DECLINE | 2% | 99% | Large | Critical | Close current branch and redesign as small PRs; endpoint ownership and PATH fallback remain unsafe. |

## Issues

| Item | Signal | Decision | Take confidence | Recommendation confidence | Effort | Risk | Owner / next action |
|---|---|---|---:|---:|---|---|---|
| #1280 Stronger model versus reduced context | Product recommendation policy, linked #1281 | HOLD_FOR_AUTHOR | 55% | 95% | Moderate | Medium | Decide quality versus context priority and minimum recommended context; do not merge #1281 first. |
| #1271 Visible New Chat/New Session action | Source-confirmed UX gap, hidden `/new` path exists | NEEDS_INFO | 75% | 90% | Moderate | Low | Choose placement, default agent behavior, and conditional multi-agent picker. |
| #1270 LayoutCycleException crash loop | Current-release P0 crash report, missing stack owner | NEEDS_INFO | 55% | 92% | Moderate | High | Obtain sanitized full WER dump/stack and minimized throughput sequence before assigning a renderer owner. |
| #1256 Fresh-WSL DNS failure is masked | Source-proven P1 setup bug | TAKE_AFTER_CHECKS | 88% | 97% | Moderate | Medium | Manually promote: bounded download-to-temp, execute only after success, preserve curl error, and real fresh-WSL proof. |
| #1251 Intermittent Local AI provider-auth error | Reproduced on rerun, route still unclear | NEEDS_INFO | 50% | 90% | Moderate | Medium | Capture effective provider/model route, redacted error, and current-main Gateway trace. |
| #1250 Audit Local AI warning severity/copy | Post-availability UX audit now unblocked | TAKE_AFTER_CHECKS | 82% | 92% | Quick | Low | Audit current #1263 behavior, shorten copy, and align warning severity with definitive versus retryable states. |
| #1249 Back/Restart/Cancel during download | Source-confirmed UX gap with artifact policy question | NEEDS_INFO | 70% | 92% | Moderate | Medium | Decide partial-download retention and restart/cancel semantics before UI work. |
| #1248 Refresh managed model catalog | Product/artifact decision, overlaps #1281 | HOLD_FOR_AUTHOR | 55% | 92% | Moderate | Medium | Approve immutable artifacts and hardware/profile policy before catalog replacement. |
| #1247 Preselect Local AI on eligible systems | Product default change; PR #1274 is unsafe | HOLD_FOR_AUTHOR | 35% | 98% | Moderate | Medium | Define persisted explicit choice and preserve opt-out across Back/restart/resume. |
| #1246 Safely back out after Gateway install | Source-proven but maintainer chose forward-only for now | HOLD_FOR_AUTHOR | 60% | 92% | Moderate | Medium | Keep current flow; retain candidate branch only as research until product direction changes. |
| #1244 Fresh-system WSL inspection error | Source-proven P0 onboarding blocker | TAKE_AFTER_CHECKS | 85% | 96% | Moderate | Medium | Classify known fresh/pre-reboot states at Welcome and route to installer/restart before inspection. |
| #1242 Intermittent ARM64/Blackwell llama crash | P0 release blocker, upstream runtime sensitivity | NEEDS_HUMAN_TEST | 88% | 95% | Moderate | High | Reproduce and qualify current runtime on affected ARM64/DGX/Blackwell hardware; capture native CUDA failure. |
| #1236 Session-backed Companion architecture | Broad architecture proposal | DECLINE | 10% | 96% | Large | High | Retain current WSL/Gateway ownership; require RFC sponsorship for a new session runtime. |
| #1220 Show response duration | UX feature, timing authority undefined | NEEDS_INFO | 55% | 90% | Moderate | Low | Define per-entry timing authority and persistence before rendering. |
| #1216 Add ClawHub catalog page | Broad catalog/trust feature | DECLINE | 10% | 96% | Large | High | Define upstream catalog, provenance, permissions, and install ownership before UI. |
| #1214 Keep llama-server KV cache warm | Runtime/product policy | NEEDS_INFO | 45% | 90% | Moderate | Medium | Define idle retention and GPU memory budget; collect trace on qualified hardware. |
| #1213 llama-server parallelism/context | Runtime product policy | NEEDS_INFO | 35% | 90% | Moderate | Medium | Document serial managed default and qualify concurrency separately. |
| #1211 Prefill Local AI system prompt | Cross-boundary warmup request | NEEDS_INFO | 45% | 92% | Moderate | Medium | Define Gateway-owned warmup and cache invalidation contract. |
| #1200 Qwen 9B MTP cold-load failure | Reproduced recipe defect in active Local AI work | HOLD_FOR_AUTHOR | 65% | 92% | Quick | Medium | Carry bounded micro-batch recipe and real cold-load proof in current Local AI lane. |
| #1199 Node favoring/routing investigation | No concrete current failure trace | NEEDS_INFO | 25% | 95% | Unknown | Unknown | Request host selection, node state, route, and failure trace. |
| #1197 NVIDIA remediation links | Product/redirect policy | NEEDS_INFO | 35% | 92% | Moderate | Medium | Approve owner and allowlisted destination before adding outbound guidance. |
| #1196 Explicit NVIDIA GPU selection | Runtime lifecycle/product feature | DECLINE | 15% | 94% | Large | High | Defer until managed runtime/device-selection ownership is defined. |
| #1195 Per-device CUDA capability | Valid requirement, not reproduced on main alone | HOLD_FOR_AUTHOR | 55% | 92% | Moderate | Medium | Fold into safe capacity work with mixed-GPU and missing-query fail-closed coverage. |
| #1194 Preserve trailing command output | Source-proven class, linked obsolete #1207 | TAKE_AFTER_CHECKS | 70% | 92% | Moderate | Medium | Reconfirm against current main; if still reproducible, create a new narrow fix rather than reviving #1207. |
| #1191 Do not count DXGI shared memory | Source-proven, linked #1253 | HOLD_FOR_AUTHOR | 60% | 94% | Large | High | Settle WDDM/UMA-safe admission policy and prove on discrete/unified hardware. |
| #1190 `system.run` exceeds Gateway timeout | Source-proven P1 security/availability | NEEDS_HUMAN_TEST | 85% | 96% | Moderate | High | Define cross-repo deadline/cancellation contract; preserve MXC containment and prove real Gateway path. |
| #1189 MXC job-object failure on one node | Source-proven P1 platform-specific failure | NEEDS_HUMAN_TEST | 75% | 95% | Moderate | High | Reproduce exact launcher failure on affected host; approve only narrow unavailable classification, not generic fallback. |
| #1183 Session-switching UX unclear | Broad UX request | NEEDS_INFO | 40% | 90% | Moderate | Low | Specify journey and relationship to #1271 before implementation. |
| #1173 Reactor access-violation crash loop | P1 crash evidence, needs current dump | NEEDS_HUMAN_TEST | 72% | 94% | Moderate | High | Capture sanitized current-head WinDbg/WER dump and identify mount owner. |
| #1167 Session switch hangs on 200 messages | P1 hang, current trace absent | NEEDS_HUMAN_TEST | 68% | 92% | Moderate | High | Capture current-head Windows trace and measure history projection/UI bottleneck. |
| #1161 Session maintenance settings UI | Product choice, no active PR | NEEDS_INFO | 45% | 94% | Moderate | Low | Decide schema-guided editor versus dedicated typed surface; clear stale active label. |
| #1156 Terminal/channel setup handoff | UX gap, stale draft #1159 | HOLD_FOR_AUTHOR | 55% | 94% | Moderate | Medium | Rebase #1159 only after routing/localization plan; clear active label while paused. |
| #1155 Truthful capability readiness | Architecture/product contract, draft #1160 | HOLD_FOR_AUTHOR | 45% | 94% | Large | High | Sponsor authoritative schema/projection before implementation; clear stale active label. |
| #1154 Browser proxy readiness | Source-proven P1, draft #1157 | NEEDS_HUMAN_TEST | 85% | 98% | Moderate | Medium-high | Current-head MCP/Gateway/browser proof; clear active label unless proof is being captured now. |
| #1153 Smart App Control unsigned DLL | Code/signing fix landed; SAC-On acceptance remains | NEEDS_HUMAN_TEST | 92% | 98% | Quick | High | Install signed candidate on SAC-On Windows 11 and prove app plus OpenClaw.Chat.dll accepted. |
| #1152 CJK clipping | Old renderer report, current status unclear | NEEDS_INFO | 55% | 92% | Quick | Low | Reproduce on current Reactor renderer with CJK fallback fonts or close as resolved. |
| #1150 Agent-event stream hang/crash | Source-proven P1, stale draft #1158 | HOLD_FOR_AUTHOR | 75% | 96% | Large | High | Reimplement coalescing against current chat ownership and capture sustained load proof. |
| #1148 Auto-scroll to latest message | Current code appears to auto-follow | NEEDS_INFO | 60% | 92% | Quick | Low | Confirm active surface/current main and decide whether return-to-bottom button is separate. |
| #1146 MXC granted paths doubled/unusable | P1 security/data-loss, no capable proof host | NEEDS_HUMAN_TEST | 55% | 97% | Moderate | Critical | Reproduce allowed read/write and denied-write control on real MXC 0.7 host; keep stale/paused. |
| #1145 Chat text right-edge clipping | Old renderer report | NEEDS_INFO | 58% | 92% | Quick | Low | Recheck current Reactor renderer; close if not reproducible. |
| #1144 Chat selection unreliable | Surface unspecified | NEEDS_INFO | 50% | 92% | Quick | Low | Clarify native Companion versus Gateway WebView2 before routing. |
| #1124 Voice Talk Mode proposal | Product/design proposal | HOLD_FOR_AUTHOR | 25% | 94% | Large | High | Sponsor persistent microphone/talk-mode semantics after wake reliability work. |
| #1122 Consolidated audit handoff | Useful research, stale omnibus tracker | HOLD_FOR_AUTHOR | 40% | 90% | Large | High | Split surviving findings into current narrow issues; close tracker after migration. |
| #1118 Remove FunctionalUI from Cron | Stale refactor proposal | HOLD_FOR_AUTHOR | 25% | 92% | Large | Medium | Reconfirm current renderer ownership and narrow the change before implementation. |
| #1087 Simplified Chinese STT output | Valid UX request with output-policy choice | NEEDS_INFO | 50% | 92% | Moderate | Medium | Decide opt-in script conversion and MCP override, then validate Mandarin output. |
| #1009 Edge TTS support | New cloud text-egress provider | DECLINE | 10% | 95% | Moderate | Medium | Do not add unauthenticated cloud egress without provider/privacy policy. |
| #1000 Stable/beta release alignment | Cross-repo lifecycle policy | NEEDS_INFO | 45% | 92% | Moderate | High | Wait for Core managed-Gateway contract, then scope thin Windows integration only. |
| #953 Large session-list scaling | UX/performance contract missing | NEEDS_INFO | 45% | 92% | Large | Medium | Choose searchable virtualized picker and bounded paging. |
| #908 QR generation mirror | Upstream/context details missing | NEEDS_INFO | 25% | 94% | Quick | Low | Request upstream issue URL, Gateway version, and failed QR output. |
| #907 Mobile pairing falls back to TUI | P0 report without current trace | NEEDS_INFO | 30% | 95% | Moderate | High | Request client/Gateway versions and pairing transcript. |
| #906 Battery drain with concurrent sessions | Performance report without workload/budget | NEEDS_INFO | 35% | 94% | Large | High | Capture CPU/wake/refresh trace and define freshness budget. |
| #894 Native/browser tool identity mismatch | Source-proven, conflicting PR #1130 | HOLD_FOR_AUTHOR | 84% | 98% | Moderate | Medium-high | Transplant #1130’s focused correlation fix onto current chat ownership. |
| #871 Workspace file rail gaps | Product contract missing | NEEDS_INFO | 35% | 92% | Large | Medium | Define typed artifact and preview/safety policy. |
| #863 Agent creation and syncing | Gateway mutation/UI contract missing | NEEDS_INFO | 30% | 92% | Large | Medium | Define creation/refresh ownership and target-agent semantics. |
| #859 Improve chat experience | Broad bucket with some concrete papercuts | HOLD_FOR_AUTHOR | 40% | 90% | Large | Medium | Split current reproducible defects, including identity/avatar fallback, into narrow issues. |
| #847 Wizard combination validation | Credentialed E2E strategy absent | NEEDS_INFO | 30% | 94% | Large | High | Define approved test accounts, secret handling, and validation matrix. |
| #844 Local/remote pairing reliability umbrella | Too broad, topology acceptance undefined | NEEDS_INFO | 30% | 94% | Large | High | Split by topology and provide current traces; close umbrella when migrated. |
| #843 First-run local Gateway onboarding | Largely completed/duplicate direction | DECLINE | 80% | 94% | Quick | Low | Close as covered by current onboarding; keep narrower terminal/channel issues. |
| #554 App/ConnectionPage god-file tracker | Canonical architecture tracker | HOLD_FOR_AUTHOR | 60% | 96% | Large | High | Keep open; sponsor one ledger-backed ownership seam at a time. |
| #246 Packaging and auto-update strategy | Security/product strategy | HOLD_FOR_AUTHOR | 40% | 96% | Large | High | Continue MSIX-first decision work with legacy migration and update-isolation proof. |

## Active ownership audit

The September snapshot still shows `status: actively landing` on issues #1194, #1161, #1156, #1155, #1154, #1153, and #1150, and PRs #1232, #1175, #1157, and #1141.

| Item | Assessment |
|---|---|
| #1194 | Recent activity and linked PR, but current PR is obsolete/conflicting. Confirm ownership or clear. |
| #1161 | No active implementation PR. Clear until a product decision is made. |
| #1156 | Draft #1159 exists but is not actively landing. Clear while author work is paused. |
| #1155 | Draft #1160 exists but schema decision blocks it. Clear while paused. |
| #1154 / #1157 | Proof-only lane. Keep active only if a named maintainer is capturing MCP/Gateway proof now. |
| #1153 | Proof-only SAC-On lane. Keep active only if a SAC-On host is scheduled. |
| #1150 | Draft #1158 is stale against current ownership. Clear until reimplementation starts. |
| #1232 | Author work remains; label has not had fresh activity since August 28. Clear unless owner confirms. |
| #1175 | Branch received recent activity but remains unsafe/conflicting. Active label is plausible only during redesign. |
| #1141 | Conflicting since August 24. Clear until transplant begins. |

## Adversarial review

### PR #1265 Brazilian Portuguese localization

| Finding | Opus | Codex | Consensus | Final assessment |
|---|---|---|---|---|
| About 410 keys violate the repository all-or-none translation invariant | CRITICAL | CRITICAL | HIGH | Blocking. Reconcile values with the established non-English locale consensus; do not weaken the validation rule. |
| Missing `PermissionsPage_Cap_Ollama_Label` and `PermissionsPage_Cap_Ollama_Description` after main advanced | HIGH | HIGH | HIGH | Blocking. Rebase and add both keys before exact-head validation. |
| Normal build/Tray localization tests never ran on the PR head | HIGH | MEDIUM | HIGH | Full Windows CI is mandatory after catalog repair. |
| Translation quality, XML, placeholders, and layout expansion | Clean | Clean | HIGH | The translation craft is good; consistency policy and staleness are the blockers. |

**Decision:** HOLD_FOR_AUTHOR, 40% take confidence. It is not safe to merge after a simple rerun because the deterministic localization tests fail.

### PR #1277 Windows Server MXC probe skip

| Finding | Opus | Codex | Consensus | Final assessment |
|---|---|---|---|---|
| No real Windows Server fallback/strict-denial proof and no client MXC E2E | HIGH | HIGH | HIGH | Blocking security-boundary proof. |
| Native Server SKU detector is not directly tested | HIGH | MEDIUM | HIGH | Add a Windows-only native detector/layout assertion. |
| Unsupported-SKU result can persist `SystemRunSandboxEnabled=false`, changing later execution from guarded HostFallback to direct Host | HIGH | Not raised | LOW, accepted | Concrete downstream code path. Add a distinct outcome and exclude it from persistent toggle normalization. |
| Hard-coded SKU skip has no controlled future-support escape hatch | HIGH | Not raised | LOW, accepted | Add an explicit, logged force-probe override or equivalent supported mechanism. |
| Probe no-throw/log/UI remediation contracts are incomplete | MEDIUM | LOW | LOW | Guard P/Invoke, emit structured outcome, and do not offer Windows Update for unsupported Server SKU. |

**Decision:** HOLD_FOR_AUTHOR, 35% take confidence. Native proof is necessary but not sufficient; the persistent security-setting behavior must be fixed first.

## Day plan

1. Update and merge #1273; merge #1283 after its current CI finishes.
2. Repair #1265's catalog consistency and missing keys, then run exact-head localization tests and a pt-BR smoke.
3. Rework #1277's unsupported-SKU state and persistence boundary, then schedule Server and MXC-client proof. Independently schedule proof for #1166, #1201, and #1153.
4. Repair #1282 and #1279; then address deadline/cancellation findings in #1258, #1267, and #1268.
5. Manually promote #1256 and #1244 as the best issue-to-PR candidates.
6. Transplant #1130 and #1141 only if owners want them; do not rebase obsolete ownership wholesale.
7. Close the nine decline PRs and issue #843 after concise disposition comments.
8. Clear stale active-landing labels that have no current owner or proof appointment.

## Automation and testability

The repository now has the report-only triage workflow and schema-backed proof pools landed from the August pass. The next automation work should be tuning, not another framework.

| Opportunity | Why | Owner | Effort | Notes |
|---|---|---|---|---|
| Run the triage workflow on schedule and inspect `unknown`/warning rates | Validates live GraphQL/report behavior without mutation | Maintainer | Quick | Keep report-only as default. |
| Add a short proof-pool CI lane separate from the main test critical path | Full schema mutation matrices can delay all builds | Maintainer | Moderate | Preserve PS5/PS7 parity while parallelizing cost. |
| Use proof-pool reservations for SAC-On, MXC, ARM64/DGX, and clean installer work | Turns custom proof into scheduled capacity | Maintainer | Moderate | Do not treat an unavailable pool as passing evidence. |
| Auto-suggest clearing stale active ownership | Reduces duplicate-work blocking | Existing triage workflow | Quick | Keep removal manually gated and audited. |
| Track repeated unrelated UI readiness flakes | Stops each feature PR from rediscovering the same baseline failures | Test owner | Moderate | Repair the test owner, never waive a changed-surface failure. |
