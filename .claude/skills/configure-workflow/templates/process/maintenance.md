# Maintenance

| Setting | Value |
|---|---|
| Worktree root | {{WORKTREE_ROOT}} |
| Agent scratch dir | {{SCRATCH_DIR}} |
| Allowed write locations | repo tree, worktree root, scratch dir |
| Test resource prefix (containers/volumes/networks) | {{TEST_PREFIX}} |
| Host thresholds | load > {{LOAD_MAX}}, mem free < {{MEM_MIN_PCT}}%, disk delta > {{DISK_DELTA_GB}} GB → investigate |
| Shared sequence resources (serialise) | {{SEQUENCE_RESOURCES}} |
