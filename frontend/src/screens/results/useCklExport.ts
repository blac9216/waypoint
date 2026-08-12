/**
 * CKL bundle export action — extracted from `ResultsScreen.tsx` (issue #416
 * decomposition, no behavior change). Fetches every job's CKL and zips them
 * client-side (AC3). Uses a raw `fetch` rather than `lib/api.ts`'s `apiGet`
 * because the artifact route returns binary XML, not the JSON `apiFetch`
 * always parses — but still attaches the bearer token by hand (`useAuth()`'s
 * `token`, the same value `lib/events.ts`'s SSE connection authenticates
 * with) so this doesn't silently downgrade to an unauthenticated request the
 * way a bare `fetch` with no headers would.
 */
import { useCallback, useState } from "react";
import { artifactDownloadUrl, type RunJobItem, type RunListItem } from "./results";
import { buildZip, type ZipEntry } from "./zip";

async function downloadCklBundle(runId: string, jobs: RunJobItem[], token: string | null): Promise<void> {
	const entries: ZipEntry[] = [];
	for (const job of jobs) {
		const url = artifactDownloadUrl(job.id, "ckl");
		try {
			const headers: Record<string, string> = { Accept: "application/octet-stream" };
			if (token) {
				headers.Authorization = `Bearer ${token}`;
			}
			const response = await fetch(url, { headers });
			if (!response.ok) {
				continue;
			}
			const data = new Uint8Array(await response.arrayBuffer());
			const name = `${job.target_name ?? job.id}.ckl`;
			entries.push({ name, data });
		} catch {
			// One target's CKL failing to fetch must not abort the whole bundle —
			// same "individual target failures must not halt" rule CLAUDE.md's Key
			// Constraints applies to runs applies here to the export.
		}
	}
	const blob = buildZip(entries);
	const objectUrl = URL.createObjectURL(blob);
	const link = document.createElement("a");
	link.href = objectUrl;
	link.download = `${runId}-ckl-bundle.zip`;
	document.body.appendChild(link);
	link.click();
	document.body.removeChild(link);
	URL.revokeObjectURL(objectUrl);
}

export interface UseCklExportResult {
	exporting: boolean;
	handleExport: () => Promise<void>;
}

export function useCklExport(run: RunListItem | null, jobs: RunJobItem[], token: string | null): UseCklExportResult {
	const [exporting, setExporting] = useState(false);

	const handleExport = useCallback(async () => {
		if (!run) {
			return;
		}
		setExporting(true);
		try {
			await downloadCklBundle(run.id, jobs, token);
		} finally {
			setExporting(false);
		}
	}, [run, jobs, token]);

	return { exporting, handleExport };
}
