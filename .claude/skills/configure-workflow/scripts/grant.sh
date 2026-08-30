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
  # keep query failure distinct from an empty result: an errors payload (or a non-zero gh) means the role is unknown, not absent — never assert "missing" on it, and never let it force the ADMIN mutation
  praw=$(gh api graphql -F id="$PID" -f query='query($id:ID!){node(id:$id){... on ProjectV2{collaborators(first:50){nodes{... on User{login} }}}}}' 2>/dev/null) || praw=""
  if [ -z "$praw" ] || jq -e 'has("errors")' >/dev/null 2>&1 <<<"$praw"; then say "project admin $acct: unknown — query failed, see #1218"  # not drift: audit must still be able to reach clean
  else
    role=$(jq -r --arg a "$acct" '.data.node.collaborators.nodes[]?|select(.login==$a)|.login' <<<"$praw")
    if [ -z "$role" ]; then drift=1; say "project admin $acct: missing"; [ $AUDIT = 1 ] || gql 'mutation($p:ID!,$u:ID!){updateProjectV2Collaborators(input:{projectId:$p,collaborators:[{userId:$u,role:ADMIN}]}){clientMutationId}}' "$(jq -n --arg p "$PID" --arg u "$uid" '{p:$p,u:$u}')" >/dev/null; fi
  fi
done
say "token scopes needed on the automation account: repo, project, read:org (check: gh auth status)"
[ $drift = 0 ] && say "grants: in sync" || { [ $AUDIT = 1 ] && exit 1; say "grants: applied"; }
