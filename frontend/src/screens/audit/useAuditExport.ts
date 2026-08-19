/**
 * CSV export action for `AuditScreen` (issue #531) — same shape as
 * `results/useCklExport.ts`: a raw `fetch` (not `lib/api.ts`'s `apiGet`,
 * which always parses JSON) with the bearer token attached by hand, so a
 * `text/csv` response downloads instead of failing to parse. Exports the
 * exact current filter (no page params — `AuditController.List`'s `asCsv`
 * branch ignores paging and returns the whole filtered set), matching the
 * issue's "CSV export of the current filter" requirement.
 */
import { useCallback, useState } from "react";
import { API_BASE } from "../../lib/api";
import { auditCsvQuery, type AuditFilter } from "./audit";

async function downloadAuditCsv(filter: AuditFilter, token: string | null): Promise<void> {
	const headers: Record<string, string> = { Accept: "text/csv" };
	if (token) {
		headers.Authorization = `Bearer ${token}`;
	}
	const response = await fetch(`${API_BASE}/audit${auditCsvQuery(filter)}`, { headers });
	if (!response.ok) {
		throw new Error(`Export failed with status ${response.status}`);
	}
	const blob = await response.blob();
	const objectUrl = URL.createObjectURL(blob);
	const link = document.createElement("a");
	link.href = objectUrl;
	link.download = "audit-log.csv";
	document.body.appendChild(link);
	link.click();
	document.body.removeChild(link);
	URL.revokeObjectURL(objectUrl);
}

export interface UseAuditExportResult {
	exporting: boolean;
	exportError: string | null;
	handleExport: () => Promise<void>;
}

export function useAuditExport(filter: AuditFilter, token: string | null): UseAuditExportResult {
	const [exporting, setExporting] = useState(false);
	const [exportError, setExportError] = useState<string | null>(null);

	const handleExport = useCallback(async () => {
		setExporting(true);
		setExportError(null);
		try {
			await downloadAuditCsv(filter, token);
		} catch (err) {
			setExportError(err instanceof Error ? err.message : "Could not export the audit log.");
		} finally {
			setExporting(false);
		}
	}, [filter, token]);

	return { exporting, exportError, handleExport };
}
