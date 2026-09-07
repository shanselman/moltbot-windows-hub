---
name: global-repo-triage
description: "Run complete OpenClaw Windows Node issue and PR triage, including landing queues, proof pools, ownership audits, and release planning."
---

# OpenClaw Windows Node Global Triage

Run a complete maintainer triage of `openclaw/openclaw-windows-node`. Read every
open issue and pull request, identify safe landing lanes, schedule OpenClaw's
Windows proof pools, and produce the same evidence-backed Markdown report used
for the August 27 and September 1 maintainer sweeps. Open the interactive
OpenClaw triage canvas when the user requests it as an additional handoff.

This skill is repository-specific. If the current repository is not
`openclaw/openclaw-windows-node`, stop and say that this skill applies only to
the OpenClaw Windows Node repository.

## Trigger

Use when the user asks for:

- global triage, backlog triage, or a full repository sweep
- today's OpenClaw landing queue
- low-hanging issues or PRs
- what Karen or another maintainer can do in parallel
- release-train contents or a correction-release decision
- a comparison with a prior OpenClaw triage report

## Required output

Create these session artifacts:

```text
global-triage-YYYY-MM-DD.md
triage-YYYY-MM-DD\issues-summary.csv
triage-YYYY-MM-DD\prs-summary.csv
triage-YYYY-MM-DD\pr-<number>.patch
```

Keep raw JSON when practical. Do not add triage artifacts to the repository.
Use `templates\global-triage-report.md` as the report skeleton and
`examples\global-triage-2026-08-27.md` plus
`examples\global-triage-2026-09-01.md` as quality references. Use
`templates\triage-state.template.json` only when producing the optional
interactive dashboard.

## Ground rules

1. **Read-only unless explicitly authorized.** Do not merge, close, label,
   comment, push, rerun CI, or create sessions from a triage request alone.
2. **Cover the entire backlog.** Fetch every open issue and PR. A report is not
   global if fetched and classified counts differ.
3. **Read discussions and code.** For every item that could be taken, closed,
   repaired, or assigned, inspect the body, comments, reviews, inline threads,
   timeline, checks, files, commits, and patch.
4. **Use the 90% TAKE bar.** Safe-looking is not enough. TAKE requires clear
   value, bounded scope, current-head validation, and no missing custom proof.
5. **ClawSweeper is advisory.** Read its durable review comment and labels, then
   independently verify each finding. Do not wait solely for bot wording when
   exact-head source review, proof, required validation, and branch protection
   establish safety.
6. **Do not blame a PR for a baseline flake.** Compare a failing test with the
   changed surface, the exact base commit, nearby `main` runs, and focused local
   reproduction. Rerun only after identifying the likely owner.
7. **Never treat unavailable proof as passed.** Report it as
   `NEEDS_HUMAN_TEST` or `Not verified / blocked`.
8. **Do not revive stale architecture wholesale.** Transplant the smallest
   useful fix onto current owners rather than rebasing obsolete god-object or
   pre-ledger branches.
9. **Canvas actions are requests, not mutations.** The dashboard may refresh
   read-only GitHub state and route a guarded action request to one dedicated
   child project session per item. Reuse that session for later actions on the
   same PR or issue. The dashboard must never merge, close, label, comment, push,
   rerun CI, or delete a session directly.

## Decision vocabulary

| Decision | OpenClaw meaning |
|---|---|
| TAKE | At least 90% take confidence; validated and safe to land. |
| TAKE_AFTER_CHECKS | Likely landable after ordinary exact-head CI, tests, or a small maintainer edit. |
| NEEDS_HUMAN_TEST | Requires a named Windows proof pool, hardware, signing, installer, UI, Gateway, or MXC environment. |
| NEEDS_INFO | Current repro, route, topology, logs, ownership, or product decision is missing. |
| HOLD_FOR_AUTHOR | Direction is useful, but code, conflicts, scope, or proof must change. |
| DECLINE | Superseded, stale, unsafe, duplicative, speculative, or not worth maintaining. |

Report both **take confidence** and **recommendation confidence**. A confident
HOLD or DECLINE is not a high take-confidence item.

## 1. Start from the repository's own triage data

Read:

- `AGENTS.md`
- `docs\REPOSITORY_TRIAGE.md`
- `docs\PROOF_POOLS.md`
- `docs\TEST_COVERAGE.md`
- `docs\ARCHITECTURE.md`
- `docs\RELEASING.md` when a release is in scope

Confirm the deterministic triage implementation is healthy:

```powershell
node --test .github\scripts\repository-triage.test.cjs .github\extensions\openclaw-triage-dashboard\triage-state.test.mjs
```

Prefer the latest successful scheduled `Repository Triage Report` artifact as
the inventory baseline:

```powershell
$repo = "openclaw/openclaw-windows-node"
gh run list --repo $repo --workflow repository-triage.yml --limit 10
gh run download <run-id> --repo $repo --name repository-triage-report `
  --dir <session-artifact-dir>\triage-YYYY-MM-DD\scheduled
```

If the artifact is missing, stale, or partial, fetch directly with `gh`. Preserve
collection warnings. Never silently omit an item after an API failure.

## 2. Build complete summaries

Fetch all open work with pagination:

```powershell
gh issue list --repo $repo --state open --limit 1000 `
  --json number,title,author,labels,assignees,createdAt,updatedAt,url,comments
gh pr list --repo $repo --state open --limit 1000 `
  --json number,title,author,labels,createdAt,updatedAt,url,reviewDecision,mergeable,isDraft,baseRefName,headRefName,additions,deletions,changedFiles,statusCheckRollup
```

Write CSV summaries matching the proven artifacts.

PR columns:

```text
N,Title,Author,Draft,Merge,Review,Files,Delta,Failed,Pending,Labels
```

Issue columns:

```text
N,Title,Author,Labels,Comments,Updated
```

For every potentially actionable PR:

```powershell
gh pr view <number> --repo $repo `
  --json number,title,author,body,url,state,isDraft,mergeable,mergeStateStatus,reviewDecision,baseRefName,headRefName,headRefOid,additions,deletions,changedFiles,files,commits,comments,reviews,statusCheckRollup,labels,createdAt,updatedAt
gh pr diff <number> --repo $repo --patch > <artifact-dir>\pr-<number>.patch
```

Also inspect unresolved inline threads and relevant timeline events. A
ClawSweeper comment may be edited in place, so use its current `updated_at` and
reviewed head SHA rather than assuming the oldest comment body is stale.

## 3. Classify OpenClaw-specific risk

Apply the repository's current owners and guardrails:

| Changed surface | Required scrutiny |
|---|---|
| `App.xaml.cs`, `ConnectionPage.xaml.cs`, chat provider/state | Read `docs\ARCHITECTURE.md`; reject reintroduced closed responsibilities. |
| Gateway registry, credentials, pairing, setup | Read connection/onboarding/setup docs; protect device-token precedence and setup-managed ownership. |
| `system.run`, MXC, exec approvals | Treat as a security boundary; require real `validate-mxc-e2e.ps1` proof without `-AllowSkip`. |
| New/changed node command or MCP output | Require capability registration, MCP description, `winnode` docs/tests, discovery, and invocation proof. |
| WinUI behavior | Require current-head visible proof using isolated tray data and `windows-winui-interactive`. |
| Installer, signing, packaging, update policy | Treat as release risk; require signed-artifact and clean upgrade proof. |
| Local AI GPU/model/runtime | Require affected hardware proof, not only mocked detection. |
| Localization | Run all-locale key/placeholder/all-or-none tests and obtain visual proof or an explicit blocker. |

Bot, Copilot, repo-assist, and ClawSweeper-authored PRs are untrusted
contributions. Do not lower the bar because automation produced them.

## 4. Map required proof pools

Select every applicable pool from `.github\proof-pools.json`:

| Pool | Use |
|---|---|
| `windows-11-sac-on` | Signed installer and Smart App Control acceptance |
| `windows-wsl-mxc` | Gateway to Windows node `system.run` containment |
| `windows-11-arm64` | Native ARM64 build and runtime behavior |
| `windows-wsl-dgx-blackwell` | Local AI GPU setup, restart, and inference |
| `windows-clean-installer-upgrade` | Install, upgrade, repair, and uninstall |
| `windows-wsl-gateway-e2e` | Product WSL setup, pairing, recovery, and invocation |
| `windows-winui-interactive` | Visual, accessibility, consent, and diagnostics |

PR bodies must contain:

- `## Required proof pools`
- `## Validation`
- `## Real behavior proof`

Proof declarations schedule work. They do not prove it ran.

## 5. Apply the OpenClaw validation floor

Every code change needs:

```powershell
$env:OPENCLAW_REPO_ROOT = (Get-Location).Path
.\build.ps1
dotnet test .\tests\OpenClaw.Shared.Tests\OpenClaw.Shared.Tests.csproj --no-restore
dotnet test .\tests\OpenClaw.Tray.Tests\OpenClaw.Tray.Tests.csproj --no-restore
```

Fresh worktrees must restore or build a test project before trusting
`--no-restore`.

Add the focused owner suite:

- connection: `OpenClaw.Connection.Tests`
- setup: `OpenClaw.SetupEngine.Tests`
- WinUI: focused Tray tests plus UI/accessibility proof
- node/MCP docs or output: `OpenClaw.WinNode.Cli.Tests`
- setup/connect/recovery: relevant `OpenClaw.E2ETests` shard
- MXC or `system.run`: `.\scripts\validate-mxc-e2e.ps1`

For non-trivial setup, pairing, UI, MCP, permission, security, or diagnostics
work, require rubber-duck review and final autoreview.

## 6. Audit ownership and duplicate work

`status: 🚢 actively landing` means a maintainer or delegated agent is actively
implementing, validating, or merging now.

In the report:

- name the owner or label actor
- identify linked open PRs
- warn against duplicate fixes
- flag ownership with no trusted human activity for 7 full days
- never suggest automatic removal for P0, security, `no-stale`, assigned, or
  provenance-incomplete items

Do not mutate labels during report-only triage. If the user authorizes cleanup,
use the repository workflow's guarded
`remove-expired-active-ownership` operation rather than ad hoc label removal.

## 7. Review likely landing candidates

Use direct review for small changes. Invoke `hanselman-code-review` for:

- more than 300 changed lines
- security, auth, setup, storage, release, signing, installer, shell, MXC,
  concurrency, or data-loss risk
- bot/repo-assist changes beyond a trivial dependency bump
- conflicting GitHub state or disputed findings
- any candidate below 95% recommendation confidence that might still be taken

Cross-reference both reviewers in the report, then verify each accepted finding
against the code. Do not paste raw reviewer output as the decision.

## 8. Build the landing and release plan

Order work by dependency, not PR number:

1. release blockers and current user breakage
2. small green fixes with clear ownership
3. coordinated trains where one PR changes the next PR's base
4. repair/transplant lanes
5. custom proof lanes
6. closure and stale-label cleanup

Use one isolated worktree/session per PR. Keep dependent PRs serial. Parallelize
independent implementation and human proof. Do not force-push contributor
branches; use a current-main maintainer replacement when the original branch
cannot be advanced safely.

For each active landing:

- apply `status: 🚢 actively landing`
- merge current `origin/main` when the base changed
- rerun exact-head validation and proof
- update the PR body
- resolve or explicitly disposition review findings
- verify GitHub state after merge
- remove the active label and archive the work session

If CI fails only in unchanged infrastructure:

1. inspect the exact failed test and log
2. compare with the exact base or nearby main run
3. run the focused test locally
4. check whether a fix has since landed on main
5. refresh the branch before rerunning stale-base CI

For release planning:

- compare the latest tag with `main`
- list which candidate PRs are already shipped
- prefer a narrow correction for a bounded user-facing fix
- exclude broad localization, feature, refactor, conflicted, or unproven work
- tag only exact `origin/main`
- never move or reuse a published tag
- require x64/ARM64 signed installer and portable ZIP verification

## 9. Write the proven report format

The Markdown report must use this order:

1. `# OpenClaw Windows Node global triage`
2. Snapshot sentence with exact open issue and PR counts plus collection scope
3. `## Change since <prior date>`
4. `## Executive queue`
5. `## Pull requests`
6. `## Issues`
7. `## Active ownership audit`
8. `## Adversarial review` for selected risky candidates
9. `## Day plan`
10. `## Automation and testability`

The PR table columns are:

```text
Item | Type / signal | Decision | Take confidence |
Recommendation confidence | Effort | Risk | Owner / required validation
```

The issue table columns are:

```text
Item | Signal | Decision | Take confidence |
Recommendation confidence | Effort | Risk | Owner / next action
```

Every row needs a concrete owner and next action. Cover all open items, including
low-priority and declined work.

## 10. Optionally publish the interactive dashboard

Only produce the interactive canvas when the user requests it in addition to the
static Markdown report. Create `global-triage-YYYY-MM-DD.json` as its session
state artifact.

Write `global-triage-YYYY-MM-DD.json` using
`templates\triage-state.template.json`. Every item needs:

- its PR or issue number, title, URL, decision, both confidence values, effort,
  risk, owner, and concrete next action
- exact reviewed head SHA for PRs
- expected required check names for PRs; issues must use an empty `expectedChecks`
  array
- review status and proof status
- every applicable proof-pool ID, validated against
  `.github\proof-pools.json`
- dependencies on other items in the same triage

Populate `report` so the dashboard tabs preserve the report template's
decision context:

- `changes`
- `ownership`
- `reviews`
- `automation`

Use `plan` as the single ordered execution plan. Set each step's optional
`horizon` to `today` or `later`; omitted values default to `today`. A plan step
may declare `dependsOn` with other plan-step IDs and `gates` that point to an
item's `inventory`, `review`, `checks`, `proof`, or `landing` stage. Dependencies
must form an acyclic graph. The `landing` stage applies only to pull requests.
The canvas derives each gated step's live status
whenever GitHub checks change. It renders connected steps as dependency lanes
and independent steps as parallel work. Plan order breaks ties within a lane.
Do not repeat plan steps in `report`.

Then:

1. Call `list_canvas_capabilities` for `openclaw-triage-dashboard`.
2. Open `openclaw-triage-dashboard` with the JSON object as input and use a
   stable instance ID such as `global-triage-YYYY-MM-DD`.
3. Reopen that same instance ID whenever triage decisions, proof status, review
   status, expected checks, or plan steps change.
4. Leave the canvas open. It refreshes GitHub PR and issue state at the declared
   30-to-300-second interval and pushes updates to the panel.

The canvas exposes:

- search and readiness filters
- live check totals and missing expected jobs
- item stages and plan-gate status
- proof, review, exact-head, draft, and mergeability gates
- `Request next step`, which creates or reuses the item's child project session
  and sends a read-only-by-default request there
- `Prepare merge`, enabled only for an exact-head `TAKE` at 90% or higher with
  complete review and proof, clean merge state, and all expected checks present
  and passing

`Prepare merge` must only ask the item's child session to re-fetch evidence and
request explicit confirmation. Every item action uses the same routing rule:
find the exact `Triage PR #<number>` or `Triage Issue #<number>` session and
append to it, or create it once when absent. The extension never calls a GitHub
mutation command.

## Completion bar

Do not call the triage complete until:

- open issue and PR counts match the report rows
- actionable discussions, reviews, checks, and patches were inspected
- current-head versus stale-head evidence is distinguished
- issue-to-PR ownership and duplicate risks are mapped
- active ownership is audited
- proof pools and human hosts are scheduled explicitly
- a numbered day plan identifies safe parallelism and dependency order
- release contents and exclusions are explicit when a release is discussed
- the Markdown report, CSV summaries, and reviewed PR patches are saved

When the optional dashboard was requested, also require:

- the versioned triage-state JSON is saved
- the dashboard opens successfully and its fetched item count matches the state
  artifact
- every plan gate references an item and stage in the state artifact
- item actions remain child-session-routed and merge requests stay
  confirmation-gated
