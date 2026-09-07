# OpenClaw Windows Node Global Triage

This portable skill reproduces the full maintainer sweep used for
`openclaw/openclaw-windows-node`. It combines the repository's scheduled
read-only triage report with source review, proof-pool scheduling, active-owner
auditing, adversarial review, landing order, and release planning. It can also
open a live interactive canvas when requested.

## Install

Unzip the package so this file exists:

```text
%USERPROFILE%\.copilot\skills\global-repo-triage\SKILL.md
```

Restart Copilot if the skill is not discovered immediately. The optional
interactive dashboard is available when the repository also contains
`.github\extensions\openclaw-triage-dashboard\extension.mjs`.

## Recommended prompt

```text
Run the OpenClaw Windows Node global triage. Read every open issue and PR,
compare with the previous report, identify what can safely land today, audit
active ownership, schedule required proof pools, save the evidence artifacts,
and save the full Markdown report plus execution handoff. Open the live triage
canvas too. Do not mutate GitHub until I approve an action.
```

The `examples` folder contains the two real reports that established the Markdown
format. When requested, the canvas supplements that report with live checks,
plan gates, and guarded child-session actions. It does not replace the report or
merge directly.
