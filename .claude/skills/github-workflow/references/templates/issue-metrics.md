# Issue Metrics Closing Comment

Posted by the orchestrator (or the session, in solo mode) on the issue after its PR
merges — the last comment on the issue. It puts the estimate made at filing next to what
actually happened, so planning calibrates against reality. Fill what you know; omit keys
you cannot know (cloud sessions have no session log). The HTML comment is parsed by the
planning skill's `history.sh`; the visible lines are for people.

```markdown
### Closed — metrics

Estimate: <size class> · <est. cycle> · <est. completion date> → Actual: started <date>
(<assigned | first commit | PR open>), merged <date>, <n> review round(s), <k> finding(s),
+<add>/−<del> in <files> files. Deferred issues spawned: <list | none>.

<!-- metrics {"issue":<N>,"pr":<P>,"estimate":{"size":"S|M|L","cycle_days":<x>,"due":"<date>"},
"started":"<ISO>","start_source":"assigned|first-commit|pr-open|issue-created","pr_opened":"<ISO>","merged":"<ISO>",
"rounds":<n>,"findings":[<per round>],"additions":<a>,"deletions":<d>,"files":<f>,"ci_seconds":<s>,
"roles":{"implementer":{"model":"…","tokens":<t>,"seconds":<s>},"fix":{…},"reviewer":{…}},
"dispatch_to_pr_seconds":<s>,"deferred":[<issue numbers>],"verified":"n/a|pending-live"} -->
```

Sources: the session log filtered to this issue (`jq 'select(.issue==N)' session.jsonl`),
the reviewer's task notification (its tokens/duration), the PR (`gh api …/pulls/P`), the
issue timeline (`gh api …/issues/N/timeline` for the first `assigned` event), and the PR's
`## PR Review` comments for rounds and findings.
