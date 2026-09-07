# OpenClaw Windows Node global triage

Snapshot: 2026-08-27, 71 open issues and 31 open pull requests.

## Executive queue

1. **PR #1205** is the closest merge. CI and proof are green and both adversarial reviewers found no critical/high defects. Before merging, keep **More options** available during slow wizard startup and correct the documentation to distinguish the missing `wizard.back` RPC from Gateway-provided in-band `__back` options.
2. **PRs #1202 then #1203** are green, mergeable, and actively landing. Treat them as one WSL recovery/readiness train and validate the rebased second PR after the first lands.
3. **PR #1225** is a small, valuable archive-traversal hardening patch. Resolve its two-file conflict, rerun normal validation, and land it.
4. **PR #1130** is the owned fix for issue #894. Resolve its chat-owner conflicts and rerun its existing proof.
5. **PR #1166** is technically plausible but cannot cross the 90% bar without real MXC E2E proof.
6. Queue new narrow fixes for **#1243**, **#1218**, and **#980**. They reuse established routes/contracts and avoid new architecture.
7. Close or decline superseded PRs **#1237, #1222, #1206, #1174, #1116, #1073**. Confirm and close locally completed issues **#1184, #840, #881**.

No PR should be merged untouched from this snapshot. PR #1205 is one small maintainer edit away; #1202/#1203 are already in an active landing lane.

## Pull requests

| Item | Decision | Take confidence | Effort / risk | Owner and next action |
| --- | --- | ---: | --- | --- |
| #1239 Shared-memory capacity semantics | HOLD_FOR_AUTHOR | 45% | Moderate / High | Remove or split the unused PCI join; provide exact-head DGX proof. |
| #1237 DGX Spark investigation | DECLINE | 0% | Quick / High | Close as research-only and superseded by #1239. |
| #1232 Resume Local AI setup with a new model | HOLD_FOR_AUTHOR | 35% | Large / High | Preserve prior-install provenance across restart and reject combined GPU/model changes before replacement. |
| #1225 Reject archive traversal before extraction | TAKE_AFTER_CHECKS | 88% | Quick / Medium | Maintainer resolves two-file conflict, reruns build/tests/CodeQL. |
| #1222 Require verified Local AI evidence | DECLINE | 10% | Quick / Medium | Close as superseded by #1212. |
| #1212 Prevent routing to unverified model | NEEDS_HUMAN_TEST | 65% | Moderate / High | Diagnose red CI and prove real llama-server/WSL withdrawal and recovery. |
| #1207 Preserve trailing command output | NEEDS_HUMAN_TEST | 72% | Moderate / Medium | Rebase and prove complete output plus bounded inherited-pipe behavior in real WSL. |
| #1206 Preserve installed runtime controls | DECLINE | 5% | Quick / Low | Close as already incorporated into #1204. |
| #1205 Fix onboarding Back desynchronization | TAKE_AFTER_CHECKS | 94% | Quick / Low-Medium | Small maintainer edit for startup recovery affordance and accurate protocol wording, then merge. |
| #1204 Clarify unavailable Local AI states | HOLD_FOR_AUTHOR | 30% | Large / High | Rebuild from current main with only intended Local AI commits. |
| #1203 Gate Local AI on WSL readiness | TAKE_AFTER_CHECKS | 89% | Moderate / Medium | Land after #1202, rebase, validate combined disabled-feature states. |
| #1202 Recover uninitialized WSL automatically | TAKE_AFTER_CHECKS | 90% | Quick / Medium | Land first in coordinated WSL train; refresh exact-head proof metadata. |
| #1201 Bind WSL relay trust to package | NEEDS_INFO | 40% | Moderate / High | Define trusted signer/package provenance; do not accept Developer signatures generically. |
| #1175 Retry failed WSL setup | HOLD_FOR_AUTHOR | 15% | Moderate / High | Remove destructive no-clean behavior or add explicit deletion confirmation; rebase and prove recovery. |
| #1174 llama.cpp serving WIP | DECLINE | 1% | Large / High | Close as superseded by newer Local AI work. |
| #1166 Bound MXC termination/output cleanup | NEEDS_HUMAN_TEST | 88% | Quick / Medium | Run required validation and `validate-mxc-e2e.ps1` without `-AllowSkip`. |
| #1164 Permissioned Codex session access | HOLD_FOR_AUTHOR | 20% | Large / High | Decide Core versus extension ownership before rebase or implementation. |
| #1160 Truthful capability readiness | NEEDS_INFO | 48% | Large / High | Decide status schema v2, then rebase after #1157 and prove MCP/Gateway/UI transitions. |
| #1159 Guide channel readiness handoff | HOLD_FOR_AUTHOR | 55% | Moderate / Medium | Rebase onto current routing, localize copy, prove setup-to-Channels handoff. |
| #1158 Bound streaming UI dispatch | HOLD_FOR_AUTHOR | 58% | Large / High | Rebase onto current chat ownership and capture sustained streaming responsiveness proof. |
| #1157 Browser proxy readiness | NEEDS_HUMAN_TEST | 86% | Moderate / Medium | Prove successful MCP action and guided blocked state on Windows. |
| #1141 Extended-stable update suppression | TAKE_AFTER_CHECKS | 87% | Moderate / Medium | Resolve conflicts without duplicating connection ownership; refresh Gateway/UI proof. |
| #1130 Preserve history cache correlation | TAKE_AFTER_CHECKS | 89% | Moderate / Medium | Resolve current chat-owner conflicts; rerun history-collision UI proof. |
| #1128 Upgrade legacy Gateways to Tailscale auth | TAKE_AFTER_CHECKS | 86% | Large / High | Approve security contract, resolve conflicts, rerun positive/negative Tailscale proof. |
| #1116 Clean Windows validation infrastructure | DECLINE | 5% | Very large / High | Close omnibus PR; split into independently owned validation lanes. |
| #1073 Single MSIX lifecycle proposal | DECLINE | 10% | Large / High | Close stale patch; preserve useful research in a narrow current proposal. |
| #1071 Wake phrase voice assistant | HOLD_FOR_AUTHOR | 10% | Large / High | Resolve microphone bug and product sponsorship; rebase and provide live proof. |
| #1068 Replaceable node runtime/sidecar proof | HOLD_FOR_AUTHOR | 15% | Large / High | Wait for upstream protocol, then split runtime seam, dispatcher, and adapter. |
| #1002 Trimmed winnode publishing experiment | HOLD_FOR_AUTHOR | 65% | Quick / Medium | Decide whether standalone distribution is supported; if yes, add MCP/published-tool proof. |
| #962 Seed design system | HOLD_FOR_AUTHOR | 20% | Moderate / Medium | Make native UI canonical and remove stale React/catalog guidance. |
| #938 Native Windows and WSL gateway modes | HOLD_FOR_AUTHOR | 20% | Large / High | Rebase and define endpoint/runtime ownership and containment before further validation. |

## Issues

| Item | Decision | Take confidence | Effort / risk | Owner and next action |
| --- | --- | ---: | --- | --- |
| #1251 Intermittent Local AI provider-auth error | NEEDS_INFO | 25% | Moderate / Medium | Capture effective route and redacted failure output from second setup. |
| #1250 Local AI audit warning severity | NEEDS_HUMAN_TEST | 15% | Moderate / Low | Reassess after #1204; maintainer chooses severity/copy. |
| #1249 Back/Restart/Cancel during model download | NEEDS_INFO | 20% | Moderate / Medium | Define partial-artifact retention and cancellation semantics first. |
| #1248 Refresh Local AI model catalog | NEEDS_INFO | 15% | Moderate / Medium | Approve artifact revisions and hardware-tier matrix. |
| #1247 Preselect Local AI on eligible systems | NEEDS_INFO | 20% | Moderate / Medium | Product decision on explicit opt-in and durable choice. |
| #1246 Cannot safely back out after Gateway install | TAKE_AFTER_CHECKS | 75% | Moderate / Medium | Review existing fix branch; test navigation, disposal, and history reset. |
| #1245 Local AI port error on rerun | NEEDS_HUMAN_TEST | 35% | Moderate / Medium | Identify listener ownership before adding recovery. |
| #1244 Fresh-system WSL inspection error | TAKE_AFTER_CHECKS | 80% | Small / Low | Route pre-reboot/fresh-install states to installer or restart before inspection. |
| #1243 Change Local AI setup after install | TAKE | 92% | Small / Low | Agent: add localized page action that reuses existing setup/onboarding route. |
| #1242 ARM64/Blackwell llama runtime crash | NEEDS_HUMAN_TEST | 88% | Small / Medium | Bump qualified llama runtime and validate on affected ARM64 Spark hardware. |
| #1236 Session-backed OpenClaw architecture | DECLINE | 5% | Large / High | Retain current WSL boundary; no parallel architecture. |
| #1220 Show response duration | NEEDS_INFO | 25% | Moderate / Low | Define timing authority and persistence contract. |
| #1219 Markdown tables render raw | TAKE_AFTER_CHECKS | 80% | Moderate / Medium | Reuse GFM table renderer in Reactor path; add narrow-width/code-fence coverage. |
| #1218 Start-menu launch does not restore tray window | TAKE_AFTER_CHECKS | 88% | Small / Low | Agent: route no-argument secondary launch through existing hub activation. |
| #1216 Add ClawHub catalog page | DECLINE | 10% | Large / High | Needs upstream catalog and trust design; do not create parallel installer. |
| #1214 llama-server unload latency | NEEDS_INFO | 30% | Moderate / Medium | Define warm-residency policy and GPU memory budget. |
| #1213 llama-server parallelism | NEEDS_INFO | 20% | Large / Medium | Document serial default; qualify concurrency separately. |
| #1211 Prefill Local AI system prompt | NEEDS_INFO | 25% | Moderate / Medium | Define Gateway-owned warmup and invalidation contract. |
| #1200 Qwen cold load failure | HOLD_FOR_AUTHOR | 60% | Small / Medium | Carry micro-batch fix and cold-load proof in active Local AI work. |
| #1199 Node favoring/routing investigation | NEEDS_INFO | 15% | Unknown / Unknown | Request concrete host-selection/node-state trace. |
| #1197 NVIDIA remediation links | NEEDS_INFO | 20% | Moderate / Medium | Approve owner and allowlisted redirect policy. |
| #1196 Explicit NVIDIA GPU selection | DECLINE | 10% | Large / High | Defer until runtime lifecycle is defined. |
| #1195 Per-device CUDA capability | HOLD_FOR_AUTHOR | 55% | Moderate / Medium | Carry in active Local AI work; fail closed when capability unavailable. |
| #1194 Preserve trailing command output | HOLD_FOR_AUTHOR | 85% | Moderate / Medium | Keep open; #1235 reduced the race but #1207 owns complete drain semantics. |
| #1192 Verified model evidence before publish | HOLD_FOR_AUTHOR | 80% | Moderate / Medium | Covered by #1212; do not start duplicate fix. |
| #1191 Do not count DXGI shared memory | HOLD_FOR_AUTHOR | 80% | Moderate / Medium | Carry in active Local AI/DGX work with discrete/unified coverage. |
| #1190 system.run Gateway timeout | NEEDS_HUMAN_TEST | 85% | Moderate / High | Prove cancellation and MXC path; never fall back from slow sandbox to host. |
| #1189 MXC backend unavailable/job object failure | NEEDS_HUMAN_TEST | 75% | Moderate / High | Reproduce on MXC-capable host before changing fallback policy. |
| #1184 Cloud provider setup in onboarding | TAKE | 96% | None / Low | Close as satisfied by released Gateway-owned provider/OAuth flow. |
| #1183 Chat/session switching UX clarification | NEEDS_INFO | 40% | Moderate / Medium | Request exact journey/screenshot before choosing UX. |
| #1173 Reactor access-violation crash loop | NEEDS_HUMAN_TEST | 78% | Moderate / High | Capture protected WER/WinDbg evidence on current head. |
| #1172 Wizard Back desynchronization | TAKE_AFTER_CHECKS | 94% | Small / Low-Medium | PR #1205 owns it; apply two small review fixes, then merge/close. |
| #1167 Session-switch WinUI hang | NEEDS_HUMAN_TEST | 80% | Moderate / Medium | Capture current-head trace with about 200 history messages. |
| #1161 Session maintenance settings UI | NEEDS_INFO | 75% | Moderate / Low | Decide schema-guided editor versus dedicated typed surface. |
| #1156 Terminal-required setup guidance | HOLD_FOR_AUTHOR | 72% | Moderate / Low | PR #1159 must rebase and provide native handoff proof. |
| #1155 Truthful capability state | HOLD_FOR_AUTHOR | 68% | Large / Medium | Decide authoritative projection shape; PR #1160 is draft. |
| #1154 Browser proxy readiness | NEEDS_HUMAN_TEST | 85% | Small / Low | PR #1157 needs Windows MCP/Gateway recovery proof. |
| #1153 Smart App Control unsigned DLL | NEEDS_HUMAN_TEST | 92% | Validation / Medium | Install signed prerelease on SAC-On Windows 11 and capture acceptance proof. |
| #1152 CJK clipping | NEEDS_HUMAN_TEST | 65% | Small / Low | Recheck current Reactor renderer; close if no longer reproducible. |
| #1150 Agent event stream hang | HOLD_FOR_AUTHOR | 92% | Moderate / Medium | PR #1158 owns it; rebase and provide stress responsiveness proof. |
| #1148 Chat auto-scroll | NEEDS_INFO | 70% | Small / Low | Confirm current native behavior and whether a return-to-bottom action is desired. |
| #1146 MXC doubled granted paths | NEEDS_HUMAN_TEST | 55% | Moderate / High | Reproduce on real MXC 0.7 host with allowed/denied controls. |
| #1145 Chat right-edge clipping | NEEDS_HUMAN_TEST | 68% | Small / Low | Recheck current Reactor renderer and close if resolved. |
| #1144 Chat text selection | NEEDS_INFO | 60% | Small / Low | Clarify native Companion versus Gateway WebView2 surface. |
| #1124 Voice Talk Mode proposal | HOLD_FOR_AUTHOR | 15% | Large / High | Product decision after wake reliability work. |
| #1122 Consolidated audit handoff | TAKE_AFTER_CHECKS | 65% | Moderate / Medium | Split already-fixed, narrow Windows, and upstream findings; start only clear seams. |
| #1121 Future telemetry enhancements | DECLINE | 5% | Moderate / Low | Close speculative proposal until a concrete need exists. |
| #1118 Cron UI FunctionalUI cleanup | HOLD_FOR_AUTHOR | 25% | Large / Medium | Confirm current ownership and narrow scope after renderer changes. |
| #1117 Tailscale identity adoption | HOLD_FOR_AUTHOR | 50% | Small / Medium | Security contract decision; #1128 owns implementation. |
| #1115 Reactor timeline coverage | HOLD_FOR_AUTHOR | 40% | Small / Low | Define stable timeline behavior contract before test-only work. |
| #1087 Simplified Chinese STT output | NEEDS_INFO | 30% | Small / Low | Decide opt-in script conversion and MCP override policy. |
| #1009 Edge TTS support | DECLINE | 10% | Moderate / Medium | Do not add unauthenticated cloud text egress without provider policy. |
| #1000 Stable/beta release alignment | TAKE_AFTER_CHECKS | 55% | Moderate / Medium | Extract only thin integration fixes after upstream Core behavior is known. |
| #995 Cross-control chat selection | HOLD_FOR_AUTHOR | 35% | Large / Medium | Decide whether per-message selection is sufficient. |
| #980 Restore reasoning after Off | TAKE_AFTER_CHECKS | 88% | Small / Low | Agent: add Default action that sends the existing null-clear contract; upstream metadata remains separate. |
| #953 Large session-list scaling | HOLD_FOR_AUTHOR | 40% | Moderate / Medium | Choose searchable virtualized picker and bounded paging contract. |
| #908 QR generation failure | HOLD_FOR_AUTHOR | 10% | Large / Low | Monitor upstream dependency; retest when resolved. |
| #907 Mobile pairing failure | NEEDS_INFO | 5% | Moderate / Medium | Obtain current mobile/gateway repro and logs. |
| #906 Battery drain and slowdown | HOLD_FOR_AUTHOR | 15% | Large / High | Requires performance trace and architecture investigation. |
| #901 WSL Gateway version lockstep | HOLD_FOR_AUTHOR | 20% | Moderate / Medium | Maintainer defines version/upgrade strategy. |
| #894 Native versus browser tool rendering | TAKE_AFTER_CHECKS | 89% | Moderate / Medium | PR #1130 owns remaining history correlation fix; resolve conflicts and land it. |
| #881 Completed session clutter | TAKE | 96% | None / Low | Close Windows issue as locally completed; keep ACP lifecycle upstream. |
| #871 Workspace file rail gaps | HOLD_FOR_AUTHOR | 25% | Large / Medium | Product prioritization needed. |
| #863 Agent creation/sync UX | HOLD_FOR_AUTHOR | 20% | Moderate / Medium | Define journey and ownership before implementation. |
| #859 Improve chat experience | HOLD_FOR_AUTHOR | 20% | Moderate / Medium | Split broad request into concrete current defects. |
| #847 Automation validation wizards | HOLD_FOR_AUTHOR | 25% | Moderate / Medium | Define automation test scope and ownership. |
| #844 Connection/pairing reliability | NEEDS_INFO | 10% | Large / High | Narrow environment and current failure mode. |
| #843 First-run onboarding defaults | HOLD_FOR_AUTHOR | 30% | Moderate / Low | Product decision; parts overlap shipped WSL onboarding. |
| #840 Default local gateway | TAKE | 95% | None / Low | Close if app-owned WSL is accepted as the intended local default. |
| #554 App/ConnectionPage god files | HOLD_FOR_AUTHOR | 35% | Large / High | Keep canonical tracker; select one ledger-backed seam at a time. |
| #246 Packaging and auto-update strategy | HOLD_FOR_AUTHOR | 15% | Large / High | Strategic packaging/signing decision, not a quick fix. |

## PR #1205 adversarial consensus

| Finding | Opus | Codex | Consensus | Final assessment |
| --- | --- | --- | --- | --- |
| No critical/high correctness or security defect | Clean | Clean | High | Architecture/state fix is sound. |
| Protocol wording ignores in-band Gateway back options | Medium | Not raised | Low | Valid documentation issue; fix before merge. |
| No interactive recovery control during slow wizard startup | Medium | Not raised | Low | Valid UX regression; keep More options available before merge. |
| No direct route back to Welcome | Low | Not raised | Low | Deliberate compatibility tradeoff; optional follow-up if cancel-first navigation is desired. |
| Proof is preview-only and body is stale | Low | Not raised | Low | Update PR body/proof record, but CI and deletion-only patch reduce risk. |

## Day plan

1. Top queue: make two small corrections to PR #1205 and merge it; land green PRs #1202 then #1203 as a coordinated WSL train; repair conflicts in #1225; queue narrow fixes for #1243, #1218, and #980; close six superseded PRs and three locally completed issues.
2. Close the six superseded PRs and three locally completed issues after posting concise disposition comments.
3. Reserve human/custom environments for #1153 SAC-On, #1166 MXC, #1242 ARM64 Spark, and crash/hang traces.

## Automation opportunities

| Opportunity | Why | Owner | Effort |
| --- | --- | --- | --- |
| Auto-report PR mergeability, stale base, checks, proof labels, and active owner | Removes most daily inventory work without auto-merging | Agent/workflow | Quick |
| Route docs-only and small dependency PRs to a low-risk lane | Reduces noise while retaining human merge authority | Workflow | Quick |
| Add expiry checks for `actively landing` labels | Prevents abandoned ownership from blocking parallel fixes | Workflow | Quick |
| Track issue-to-open-PR ownership explicitly | Makes duplicate-fix prevention reliable | Workflow | Moderate |
| Maintain named Windows proof pools (SAC-On, MXC, ARM64/DGX, clean installer) | Makes custom validation schedulable instead of ad hoc | Maintainer | Moderate |
