# One Project shared by several repositories

The manifest describes one repository's board. When the owner says the board is shared:

1. Apply the baseline first: `project.sh` against the shared Project (fields, standard views).
2. Add, by hand (API for views, UI for workflows):
   - one **Auto-add** workflow entry per repository, filter `is:issue is:open`;
   - a **Repository** column on *All issues*, *In flight*, *Triage*, *Ready queue*;
   - one view per repository named after it: a copy of *All issues* with filter
     `repo:<owner>/<name>`.
3. Record the repository list in `manifests/project.json` under `captured_from.shared_repos`
   when you `capture`, so the next apply knows.
4. Claims already carry the repo slug (`<repo>-<NN>`), so two repositories' orchestrators do
   not collide on ids. Milestones stay per repository.
