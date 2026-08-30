# Rationale Index — <area>/

Kind: explanation

The evicted "why" for `<area>/`. Code there carries only short section markers and
terse one-line warnings; a warning that needs a why points here:
`# why: docs/rationale/<area>.md#<kebab-slug>`. Format rules live in the design-docs
standard: one `##` section per source file, one `###` kebab-slug entry per pointer,
slugs unique across this whole file (prefix with the service/file), 2–6 lines of
reasoning, a closing `Refs:` line that is the only home for issue/ADR/PR provenance.

## <source file or small directory>

### <prefix>-<slug>

<2–6 lines: the constraint, trade-off or history that makes the code non-obvious.
Not what the code does.>

Refs: #NNN, ADR-NNNN
