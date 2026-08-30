#!/usr/bin/env bash
# grant.sh — OWNER-run: add the automation account (and optional reviewer account) as repo collaborator + Project admin; verify.
# Usage: GH_TOKEN=<owner token> grant.sh --repo owner/name --owner <login> --project <number> --machine <login> [--reviewer <login>] [--audit]
source "$(dirname "$0")/_lib.sh"
REPO=""; OWNER=""; NUM=""; MACHINE=""; REVIEWER=""; AUDIT=0
while [ $# -gt 0 ]; do case $1 in --repo) REPO=$2; shift 2;; --owner) OWNER=$2; shift 2;; --project) NUM=$2; shift 2;; --machine) MACHINE=$2; shift 2;; --reviewer) REVIEWER=$2; shift 2;; --audit) AUDIT=1; shift;; *) say "unknown arg $1"; exit 2;; esac; done
[ -n "$REPO" ] && [ -n "$OWNER" ] && [ -n "$NUM" ] && [ -n "$MACHINE" ] || { say "usage: grant.sh --repo o/n --owner <login> --project <n> --machine <login> [--reviewer <login>]"; exit 2; }
proj=$(gh api graphql -f query='query($o:String!,$n:Int!){user(login:$o){projectV2(number:$n){id viewerCanUpdate}}}' -F o="$OWNER" -F n="$NUM")
PID=$(jq -r '.data.user.projectV2.id' <<<"$proj")
VIEWER_CAN_UPDATE=$(jq -r '.data.user.projectV2.viewerCanUpdate' <<<"$proj")
# own-account signal: viewerCanUpdate reflects the token this script is currently running as,
# not any named account, so it only narrows the check for whichever account IS that token's
# owner — never inferred for the other account (#1250).
VIEWER_LOGIN=$(gh api user --jq .login 2>/dev/null || echo "")
drift=0; applied=0; failed=0
for acct in $MACHINE $REVIEWER; do
  perm=$(gh api "repos/$REPO/collaborators/$acct/permission" --jq .permission 2>/dev/null || echo none)
  if [ "$perm" != write ] && [ "$perm" != admin ]; then drift=1; say "collaborator $acct: $perm -> write"; [ $AUDIT = 1 ] || run gh api -X PUT "repos/$REPO/collaborators/$acct" -f permission=push >/dev/null; fi
  # A failed/unresolvable node-id lookup (deleted/renamed login, transient 5xx, rate limit) is a
  # reported, counted outcome — not a mid-loop abort under set -e — so the remaining accounts
  # still get processed and the run still ends with a summary + nonzero exit (#1326).
  uid=$(gh api "users/$acct" --jq .node_id 2>/dev/null) || uid=""
  if [ -z "$uid" ]; then
    drift=1; failed=$((failed + 1))
    say "project admin $acct: could not resolve account node id — skipped (deleted/renamed login, API error, or rate limit)"
    continue
  fi
  # ProjectV2 has NO queryable field for a NAMED collaborator's role: confirmed by schema
  # introspection (`__type(name:"ProjectV2"){fields{name}}` lists no `collaborators` field;
  # `ProjectV2Collaborator`/`ProjectV2Roles` exist only as the updateProjectV2Collaborators
  # mutation's input types, never as query output). This is a permanent API gap, not a
  # transient failure, so there is nothing to retry or parse here for the general case — never
  # infer "missing" or "in sync" from a malformed/empty response. Two partial signals DO exist
  # and narrow the blanket unknown where they apply (#1250, see #1218 for the introspection
  # evidence): ProjectV2.viewerCanUpdate proves absence (not presence) for the account that
  # owns the running token, and the updateProjectV2Collaborators mutation's own response
  # roster confirms the account landed in apply mode. Neither distinguishes ADMIN from WRITER,
  # so both still land on "unknown" rather than a fabricated role — only the message and the
  # (audit-mode) drift signal get more specific.
  if [ $AUDIT = 1 ]; then
    if [ -n "$VIEWER_LOGIN" ] && [ "$acct" = "$VIEWER_LOGIN" ] && [ "$VIEWER_CAN_UPDATE" = "false" ]; then
      drift=1
      say "project admin $acct: missing — viewerCanUpdate=false for this token on project $NUM (see #1218); apply the grant: rerun without --audit"
    else
      say "project admin $acct: unknown — no GraphQL/REST field exposes ProjectV2 collaborator roles (see #1218); verify manually: https://github.com/users/$OWNER/projects/$NUM/settings/access"
    fi
  else
    # Apply mode asserts the grant, so it must say so: the operator is entitled to a record of
    # every privileged mutation, and the audit-mode "verify manually" pointer would describe a
    # state this branch has just changed. drift=1 keeps the summary from claiming "in sync";
    # applied/failed keep it from claiming "applied" for a mutation that did not land — a
    # swallowed failure here would let the numbered owner sequence in SKILL.md walk past a
    # missing Project admin grant (see the round-2 review on #1243).
    drift=1; say "project admin $acct: unknown -> ADMIN (re-asserted unconditionally; role is unreadable, see #1218)"
    if resp=$(gql 'mutation($p:ID!,$u:ID!){updateProjectV2Collaborators(input:{projectId:$p,collaborators:[{userId:$u,role:ADMIN}]}){collaborators(first:100){nodes{__typename ... on User{login} ... on Team{slug}}}}}' "$(jq -n --arg p "$PID" --arg u "$uid" '{p:$p,u:$u}')"); then
      applied=$((applied + 1))
      if [ "$DRY" = 1 ]; then
        :
      elif landed=$(jq -r --arg a "$acct" '[.data.updateProjectV2Collaborators.collaborators.nodes[]? | (.login // .slug) | select(.==$a)] | length' <<<"$resp" 2>/dev/null) && [ "$landed" -gt 0 ] 2>/dev/null; then
        say "project admin $acct: ADMIN grant applied — confirmed present in post-grant collaborator roster"
      else
        say "project admin $acct: ADMIN grant mutation succeeded but $acct not found in the returned roster — verify manually: https://github.com/users/$OWNER/projects/$NUM/settings/access"
      fi
    else
      failed=$((failed + 1)); say "project admin $acct: ADMIN grant mutation FAILED — no grant landed; check token scopes/permissions"
    fi
  fi
done
say "token scopes needed on the automation account: repo, project, read:org (check: gh auth status)"
# Summary + exit status are conditional on what actually happened: audit mode reports drift and
# exits 1 on it; apply mode exits nonzero if any ADMIN mutation failed and never says "applied"
# for work that did not land. DRY_RUN says "would apply" because it mutated nothing.
if [ $AUDIT = 1 ]; then
  [ $drift = 0 ] && { say "grants: in sync"; exit 0; }
  exit 1
fi
if [ $failed -gt 0 ]; then say "grants: $applied applied, $failed failed"; exit 1; fi
if [ $drift = 0 ]; then say "grants: in sync"
elif [ "$DRY" = 1 ]; then say "grants: would apply (DRY_RUN — nothing mutated)"
else say "grants: applied"; fi
