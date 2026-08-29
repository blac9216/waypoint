# Propagating the skills to a repository

Every repository carries its own byte-identical copies of `github-workflow`,
`github-pr-review` and `configure-workflow` under `.claude/skills/` (cloud sessions and
other machines cannot see `~/.claude/skills`). The home copy is canonical.

Procedure (an agent does this on request; there is no script by design):
1. `diff -r ~/.claude/skills/<skill> <repo>/.claude/skills/<skill>` for each skill.
2. Copy over any that differ (`rm -rf` the old, `cp -r` the new — no merging).
3. If the repository keeps a `.agents/skills/` discovery directory, symlink there.
4. Commit `AI: sync workflow skills`. Protected default branch → branch + PR (the docs fast
   path does not apply: too many files); unprotected → push to `main` only when the owner has
   authorised direct pushes for skill propagation.
5. Verify: `md5sum` of each `SKILL.md` equals the home copy's.

After `capture` changes a manifest, propagate `configure-workflow` the same way.
