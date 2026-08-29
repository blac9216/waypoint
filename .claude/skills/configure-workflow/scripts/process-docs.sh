#!/usr/bin/env bash
# process-docs.sh — scaffold docs/process/*.md from the skill's templates, filling what is programmatically derivable.
# Never overwrites an existing file (use --force to replace one). Leaves {{MARKERS}} and <owner: …> for the agent/owner to fill.
# Usage: process-docs.sh --owner <login> --project <number> --machine <login> [--dir docs/process] [--force <file>]
source "$(dirname "$0")/_lib.sh"
OWNER=""; NUM=""; MACHINE=""; DIR="docs/process"; FORCE=""
while [ $# -gt 0 ]; do case $1 in --owner) OWNER=$2; shift 2;; --project) NUM=$2; shift 2;; --machine) MACHINE=$2; shift 2;; --dir) DIR=$2; shift 2;; --force) FORCE=$2; shift 2;; *) say "unknown arg $1"; exit 2;; esac; done
[ -n "$OWNER" ] && [ -n "$NUM" ] && [ -n "$MACHINE" ] || { say "usage: process-docs.sh --owner <login> --project <n> --machine <login>"; exit 2; }
T="$HERE/../templates/process"; mkdir -p "$DIR"; REPO=$(repo_nwo)
P=$(gh api graphql -f query='query($o:String!,$n:Int!){user(login:$o){projectV2(number:$n){id title url fields(first:40){nodes{... on ProjectV2FieldCommon{id name dataType} ... on ProjectV2SingleSelectField{options{id name}}}}}}}' -F o="$OWNER" -F n="$NUM" --jq .data.user.projectV2)
ftable=$(jq -r '.fields.nodes[]|select(.name=="Status" or .name=="Verified" or .name=="Claimed by")|"- \(.name): `\(.id)`" + (if .options then " — " + ([.options[]|"\(.name) `\(.id)`"]|join(" · ")) else "" end)' <<<"$P")
checks=$(gh api "repos/$REPO/commits/$(gh api repos/$REPO --jq .default_branch)/check-runs" --jq '[.check_runs[].name]|unique|map("- "+.)|join("\n")' 2>/dev/null || true)
[ -n "$checks" ] || checks="- <owner: check names from CI>"
detect(){ # $1 = kind → best-effort command from the repo tree
  case $1 in
    unit) { [ -f package.json ] && jq -r '.scripts.test//empty' package.json | sed 's/^/npm test  # /'; ls *.sln */*.csproj 2>/dev/null | head -1 | sed 's/^/dotnet test /'; [ -f pyproject.toml ] && echo "pytest"; [ -f go.mod ] && echo "go test ./..."; true; } | head -1;;
    lint) { [ -f package.json ] && jq -r '.scripts.lint//empty' package.json | sed 's/^/npm run lint  # /'; ls *.sln 2>/dev/null | head -1 | sed 's/^/dotnet format --verify-no-changes /'; [ -f pyproject.toml ] && echo "ruff check ."; [ -f go.mod ] && echo "go vet ./..."; true; } | head -1;;
  esac; }
u=$(detect unit); l=$(detect lint)
render(){ sed -e "s|{{PROJECT_TITLE}}|$(jq -r .title <<<"$P")|; s|{{PROJECT_NUMBER}}|$NUM|; s|{{PROJECT_URL}}|$(jq -r .url <<<"$P")|; s|{{PROJECT_OWNER}}|$OWNER|; s|{{MACHINE_ACCOUNT}}|$MACHINE|; s|{{REVIEWER_IDENTITY}}|<owner: none — single account \| <login> via GH_TOKEN>|; s|{{UNIT_CMD}}|${u:-<owner>}|; s|{{LINT_CMD}}|${l:-<owner>}|; s|{{INTEGRATION_CMD}}|<owner>|; s|{{UNIT_ENV}}||; s|{{INTEGRATION_ENV}}|<owner>|; s|{{COVERAGE_CMD}}|<owner>|; s|{{COVERAGE_GATE}}|80% and no regression vs base|; s|{{SANITIZE_CMD}}|<owner>|; s|{{PENDING_LIVE_THRESHOLD}}|5|; s|{{WORKTREE_ROOT}}|$(dirname "$PWD")/$(basename "$PWD")-worktrees|; s|{{SCRATCH_DIR}}|<owner>|; s|{{TEST_PREFIX}}|<owner>|; s|{{LOAD_MAX}}|<owner>|; s|{{MEM_MIN_PCT}}|10|; s|{{DISK_DELTA_GB}}|5|; s|{{SEQUENCE_RESOURCES}}|<owner: e.g. numbered migrations — or none>|; s|{{AREA_ROWS}}|<owner: rows proposed by the agent — confirm before labels.sh runs>|" | awk -v ft="$ftable" -v ck="$checks" '{gsub(/\{\{FIELD_ID_TABLE\}\}/,ft); gsub(/\{\{REQUIRED_CHECKS\}\}/,ck); print}'; }
for f in "$T"/*.md; do b=$(basename "$f"); out="$DIR/$b"
  if [ -e "$out" ] && [ "$FORCE" != "$b" ]; then say "keep    $out (exists)"; continue; fi
  render < "$f" > "$out"; say "wrote   $out"; done
say "next: fill every <owner: …> marker and {{…}} left in $DIR (audit.sh fails on them); propose area rows; then labels.sh"
