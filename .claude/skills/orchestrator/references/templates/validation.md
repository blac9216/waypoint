# Live-validation dispatch

```text
Validate epic #<E> in <owner>/<repo> at <commit>. Read AGENTS.md, the epic and all
children, and docs/testing.md. Change no code, spawn no subagents, and do not
close or merge anything.

Prove or disprove these goals on an isolated live stack:
1. <goal and honest success criterion>
2. <goal and honest success criterion>

Sanitization is absolute. Never place lab identifiers, credentials, tokens, logs,
or real artifacts in the repository, GitHub, or your report. Keep raw evidence in
<scratch-path-outside-repo>, never print secrets, and delete sensitive scratch
after producing a sanitized summary.

Use the exact isolation and cleanup recipe in docs/testing.md with unique project
and port values. Verify isolation before trusting results and tear down volumes and
strays at the end. <authorized credential access mechanism, if any>.

[Re-validation: rerun every prior failed or blocked step after <merged fixes>.
No manual grants, copied files, undocumented overrides, or restarts count as a
pass. A required workaround is a failure.]

Record PASS/FAIL/PARTIAL/BLOCKED per step in a sanitized scratch log. For each
failure, duplicate-search first, then file a sanitized bug linked to the epic.
Post a sanitized epic summary using a regular body file. Return outcomes, issue
links, cleanup evidence, scratch disposition, and no-subagents-used confirmation.
```
