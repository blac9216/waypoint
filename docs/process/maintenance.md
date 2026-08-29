# Maintenance

| Setting | Value |
|---|---|
| Worktree root | `/workspaces/git/Personal/waypoint-worktrees/` (`issue-<N>`, `review-pr<N>`) |
| Agent scratch dir | the session's `scratchpad` under `/tmp/claude-1000/-workspaces-git-Personal-waypoint/<session>/` — never `/tmp` directly; Docker cannot see `/tmp` |
| Allowed write locations | repo tree (worktrees only — never the main checkout while agents run), the worktree root, the scratch dir |
| Test resource prefix (containers/volumes/networks) | Compose projects named by slug (`issue<N>-<role>`, `review-<N>`), `wp-test-pg*` Postgres fixture containers |
| Host thresholds | load > 2× cores, mem free < 10%, disk delta > 5 GB since the last pass → investigate before dispatching |
| Shared sequence resources (serialise) | numbered SQL migrations under `backend/Waypoint.Infrastructure/Data/Migrations/` and the schema-ledger test that enumerates them; the `frontend/package-lock.json` |
| Cleanup rules | never a blanket `docker system prune` — other sessions share the daemon; remove only resources carrying a known slug/prefix |
