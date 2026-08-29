#!/usr/bin/env bash
# audit.sh — one command: is this repo fully configured for the github-workflow skill? Exit 1 on any gap. Read-only.
# Usage: audit.sh --owner <login> --project <number> --machine <login> [--repo owner/name]
source "$(dirname "$0")/_lib.sh"
OWNER=""; NUM=""; MACHINE=""; REPO=""
while [ $# -gt 0 ]; do case $1 in --owner) OWNER=$2; shift 2;; --project) NUM=$2; shift 2;; --machine) MACHINE=$2; shift 2;; --repo) REPO=$2; shift 2;; *) say "unknown arg $1"; exit 2;; esac; done
[ -n "$OWNER" ] && [ -n "$NUM" ] && [ -n "$MACHINE" ] || { say "usage: audit.sh --owner <login> --project <n> --machine <login>"; exit 2; }
[ -n "$REPO" ] || REPO=$(repo_nwo); fail=0; ok(){ say "  ok   $*"; }; bad(){ say "  GAP  $*"; fail=1; }
say "== labels";   "$HERE/labels.sh" --repo "$REPO" --audit >/dev/null 2>&1 && ok "canonical + area labels in sync" || bad "labels drift (run labels.sh)"
say "== project";  "$HERE/project.sh" --owner "$OWNER" --project "$NUM" --audit >/dev/null 2>&1 && ok "fields/views/workflows match manifest" || bad "project drift (run project.sh; check UI workflows)"
say "== grants";   sc=$(gh auth status 2>&1 | grep -o "scopes: .*"); grep -q "project" <<<"$sc" && ok "automation token has project scope" || bad "automation token lacks 'project' scope ($sc)"
perm=$(gh api "repos/$REPO/collaborators/$MACHINE/permission" --jq .permission 2>/dev/null || echo unknown); [ "$perm" = write ] || [ "$perm" = admin ] && ok "$MACHINE has $perm on $REPO" || bad "$MACHINE permission: $perm"
say "== ruleset";  rs=$(gh api "repos/$REPO/rules/branches/$(gh api repos/$REPO --jq .default_branch)" --jq 'map(.type)|unique|join(",")' 2>/dev/null || echo ""); grep -q pull_request <<<"$rs" && ok "default branch requires PRs ($rs)" || bad "no pull_request rule visible on the default branch (owner: rulesets.sh) — or the automation account cannot read rules"
say "== docs/process"; for f in work-tracking labels testing validation maintenance overnight failure-modes; do p="docs/process/$f.md"; if [ ! -f "$p" ]; then bad "$p missing"; elif grep -qE '\{\{[A-Z_]+\}\}|<owner' "$p"; then bad "$p has unfilled markers"; else ok "$p"; fi; done
say "== repo files"; grep -qs '^\*\.local\.md' .gitignore && ok ".gitignore ignores *.local.md" || bad ".gitignore lacks '*.local.md'"
grep -qs 'docs/process' AGENTS.md CLAUDE.md 2>/dev/null && ok "agent instructions point at docs/process" || bad "AGENTS.md/CLAUDE.md do not mention docs/process (agent: propose the pointer)"
say "== skills"; for s in github-workflow github-pr-review; do [ -d ".claude/skills/$s" ] && ok "$s present in repo" || bad ".claude/skills/$s missing (propagate from ~/.claude/skills)"; done
[ $fail = 0 ] && say "AUDIT: configured" || { say "AUDIT: gaps found"; exit 1; }
