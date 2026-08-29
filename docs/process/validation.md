# Live validation

What "live" means here: a fresh isolated Compose stack on this host, driven against the real lab — vCenter/ESXi/NSX targets, the real VCF Download Tool and depot, STIG Manager — using credentials from the `with-secrets` vault. Read-only scans and downloads are allowed unattended; remediation and anything that mutates a lab system need explicit owner approval per run.
Trigger threshold: 5 `pending-live` merges since the last run.
Zero-workaround bar specifics: no manual database grants, no `docker cp` into containers, no compose overrides beyond documented operator configuration (`deploy/config`, generated dev override), no container restarts to clear state. Any of these during a re-validation is a FAIL with a bug.
Evidence location: the session scratch directory (`/tmp/claude-1000/…/scratchpad/validation-<epic>/`); never inside the repo tree, even untracked — raw captures embed hostnames.
Credentials: via the `with-secrets` skill; inventory pointer and lab specifics in `docs/testing.local.md`.
Validation epic template: github-workflow `references/templates/issue-validation-epic.md`; summaries are posted on the validated epic's thread.
