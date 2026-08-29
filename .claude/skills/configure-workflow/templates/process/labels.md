# Labels

The canonical set (types, severity, priority, concern, regression, backlog, deferred, help, question, documentation) is provisioned by the configure-workflow skill and is a closed set — never invent a label. This file owns the repo-specific **area** set: what part of the codebase an issue touches, so orchestrators can deconflict parallel work. At least one per issue; several when the work is cross-cutting. Keep it coarse.

| Label | Colour | Covers |
|---|---|---|
{{AREA_ROWS}}

Adding an area: add a row here, then run `configure-workflow/scripts/labels.sh`.
