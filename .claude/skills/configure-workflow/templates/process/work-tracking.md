# Work tracking — the four-layer shape in this repository

<!-- Scaffolded by configure-workflow. The general shape is defined by the github-workflow skill; this file holds only what is specific here. -->

| Layer | Here |
|---|---|
| Project board | [{{PROJECT_TITLE}} #{{PROJECT_NUMBER}}]({{PROJECT_URL}}) — owner `{{PROJECT_OWNER}}`; automation account `{{MACHINE_ACCOUNT}}` (admin) |
| Milestones | delivery stories only; hardening/CI/tooling stay milestone-less |
| Epics | one domain per epic; events in comments |
| Issues | ≥1 `area:*` from [labels.md](labels.md) |

Board field ids (for scripts and dispatch prompts):
{{FIELD_ID_TABLE}}

Reviewer identity: {{REVIEWER_IDENTITY}}  <!-- "none — single account; the review comment plus the merge are the verdict of record" or "<login> via GH_TOKEN; native reviews required by the ruleset" -->

Local deviations from the skill's defaults: <!-- owner: none, or list them -->
