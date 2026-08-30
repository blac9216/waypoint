#!/usr/bin/env bash
# grant.sh — OWNER-run: add the automation account (and optional reviewer account) as repo collaborator + Project admin; verify.
# Usage: GH_TOKEN=<owner token> grant.sh --repo owner/name --owner <login> --project <number> --machine <login> [--reviewer <login>] [--audit]
source "$(dirname "$0")/_lib.sh"
REPO=""; OWNER=""; NUM=""; MACHINE=""; REVIEWER=""; AUDIT=0
while [ $# -gt 0 ]; do case $1 in --repo) REPO=$2; shift 2;; --owner) OWNER=$2; shift 2;; --project) NUM=$2; shift 2;; --machine) MACHINE=$2; shift 2;; --reviewer) REVIEWER=$2; shift 2;; --audit) AUDIT=1; shift;; *) say "unknown arg $1"; exit 2;; esac; done
[ -n "$REPO" ] && [ -n "$OWNER" ] && [ -n "$NUM" ] && [ -n "$MACHINE" ] || { say "usage: grant.sh --repo o/n --owner <login> --project <n> --machine <login> [--reviewer <login>]"; exit 2; }
PID=$(gh api graphql -f query='query($o:String!,$n:Int!){user(login:$o){projectV2(number:$n){id}}}' -F o="$OWNER" -F n="$NUM" --jq '.data.user.projectV2.id')
drift=0
for acct in $MACHINE $REVIEWER; do
  perm=$(gh api "repos/$REPO/collaborators/$acct/permission" --jq .permission 2>/dev/null || echo none)
  if [ "$perm" != write ] && [ "$perm" != admin ]; then drift=1; say "collaborator $acct: $perm -> write"; [ $AUDIT = 1 ] || run gh api -X PUT "repos/$REPO/collaborators/$acct" -f permission=push >/dev/null; fi
  uid=$(gh api "users/$acct" --jq .node_id)
  # ProjectV2 has NO queryable field for a collaborator's role: confirmed by schema
  # introspection (`__type(name:"ProjectV2"){fields{name}}` lists no `collaborators`
  # field; `ProjectV2Collaborator`/`ProjectV2Roles` exist only as the
  # updateProjectV2Collaborators mutation's input types, never as query output). This
  # is a permanent API gap, not a transient failure, so there is nothing to retry or
  # parse here — never attempt the old query, never infer "missing" or "in sync" from
  # it, and never let a malformed/empty response satisfy any check. Audit mode reports
  # unknown and points at the manual check; apply mode re-issues the ADMIN grant
  # unconditionally (idempotent — a no-op when already ADMIN) since it cannot first
  # read current state, and announces that it did. See #1218 for the introspection evidence.
  if [ $AUDIT = 1 ]; then
    say "project admin $acct: unknown — no GraphQL/REST field exposes ProjectV2 collaborator roles (see #1218); verify manually: https://github.com/users/$OWNER/projects/$NUM/settings/access"
  else
    # Apply mode asserts the grant, so it must say so: the operator is entitled to a record of
    # every privileged mutation, and the audit-mode "verify manually" pointer would describe a
    # state this branch has just changed. drift=1 keeps the summary from claiming "in sync".
    drift=1; say "project admin $acct: unknown -> ADMIN (re-asserted unconditionally; role is unreadable, see #1218)"
    if ! gql 'mutation($p:ID!,$u:ID!){updateProjectV2Collaborators(input:{projectId:$p,collaborators:[{userId:$u,role:ADMIN}]}){clientMutationId}}' "$(jq -n --arg p "$PID" --arg u "$uid" '{p:$p,u:$u}')" >/dev/null; then
      say "project admin $acct: ADMIN grant mutation failed — check token scopes/permissions"
    fi
  fi
done
say "token scopes needed on the automation account: repo, project, read:org (check: gh auth status)"
[ $drift = 0 ] && say "grants: in sync" || { [ $AUDIT = 1 ] && exit 1; say "grants: applied"; }
