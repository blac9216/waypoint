#!/usr/bin/env bash
# Provision the canonical github-workflow label set on this repository.
#
# The label set (names AND colors) is the contract the .claude/skills/github-workflow
# skill leans on for classification, triage, the deferred/backlog queues, and the
# review gate. Run this once when adopting the workflow, and again any time a label
# is missing or its color has drifted.
#
# Idempotent: creates what is missing, corrects color/description on what exists.
#
# Requires the `gh` CLI, authenticated with repo scope. Run from anywhere:
#   ./scripts/provision-labels.sh [owner/repo]
# Defaults to blac9216/waypoint.

set -euo pipefail

REPO="${1:-blac9216/waypoint}"

# name|color|description  — must match .claude/skills/github-workflow/SKILL.md
LABELS=(
	"bug|d73a4a|Something isn't working"
	"enhancement|a2eeef|New feature or improvement"
	"chore|cfd3d7|Maintenance — deps, refactor, lint, build, infra"
	"epic|5319e7|Meta-issue tracking a multi-issue / multi-PR goal"
	"documentation|0075ca|Docs-only work, or a found docs gap — coexists with a type"
	"help|008672|Requires human intervention — agent is blocked"
	"severity:critical|b60205|Breaks runtime, blocks merge, or causes data loss"
	"severity:major|d93f0b|Affects a core path but has a workaround"
	"severity:minor|fbca04|Cosmetic, edge case, or low-frequency"
	"priority:high|cf222e|Sequencing — pull this ahead of other work"
	"priority:medium|dbab09|Sequencing — normal ordering"
	"priority:low|0e8a16|Sequencing — do after higher-priority work"
	"regression|e99695|A previously-fixed issue has returned, or a recent change broke something that worked"
	"backlog|bfdadc|Filed during planning; not active work yet — sequence it with a priority:*"
	"deferred|c5def5|Real but intentionally out of scope for now"
	"concern:style|d4c5f9|Found-issue dimension — style / formatting / naming drift"
	"concern:lint|d4c5f9|Found-issue dimension — linter warning not blocked on"
	"concern:tests|d4c5f9|Found-issue dimension — missing/thin tests or coverage gap"
	"concern:refactor|d4c5f9|Found-issue dimension — code smell, duplication, dead code"
	"concern:perf|d4c5f9|Found-issue dimension — performance / inefficiency"
	"concern:security|d4c5f9|Found-issue dimension — non-blocking or pre-existing security concern"
)

echo "Provisioning canonical labels on ${REPO}"

for entry in "${LABELS[@]}"; do
	IFS='|' read -r name color description <<< "${entry}"
	if gh label create "${name}" --repo "${REPO}" --color "${color}" \
		--description "${description}" >/dev/null 2>&1; then
		echo "  created  ${name}"
	else
		gh label edit "${name}" --repo "${REPO}" --color "${color}" \
			--description "${description}" >/dev/null
		echo "  updated  ${name}"
	fi
done

echo "Done. ${#LABELS[@]} labels reconciled."
