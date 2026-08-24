/**
 * The hand-rolled router (lib/router.tsx) is screen-key-only today — its own
 * doc comment: "screens that need nested/param routes can graduate this to a
 * library later." Rather than pulling one in for this single param, the run
 * id rides a `?run=` query string on the existing `/live-run` path. `popstate`
 * covers back/forward navigation to a different `?run=`; nothing in this
 * read-only slice navigates programmatically within the screen, but #284's
 * Start-a-Scan wizard (which will land a run and route here) will need the
 * same read, so this stays its own small hook rather than inlined.
 */
import { useEffect, useState } from "react";

export function useRunIdFromQuery(): string | undefined {
	const [runId, setRunId] = useState<string | undefined>(
		() => new URLSearchParams(window.location.search).get("run") ?? undefined,
	);
	useEffect(() => {
		const sync = () => setRunId(new URLSearchParams(window.location.search).get("run") ?? undefined);
		window.addEventListener("popstate", sync);
		return () => window.removeEventListener("popstate", sync);
	}, []);
	return runId;
}
