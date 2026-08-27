/**
 * Loads the STIG Manager upload-attempt history (issue #744 remainder,
 * `GET /jobs/{id}/upload-attempts`) for one selected job — the attempt
 * history drill-down the design brief's Results map calls for alongside the
 * existing per-target artifacts table. Only fetches once a job is actually
 * selected (mirrors `ComponentJobBoard`'s "events load only for the selected
 * item" idiom from #941) — never eagerly loads every job's history up
 * front.
 */
import { useEffect, useState } from "react";
import { fetchUploadAttempts, type UploadAttempt } from "./component-results";

export interface UseUploadAttemptsResult {
	attempts: UploadAttempt[];
	loading: boolean;
	unavailable: boolean;
}

export function useUploadAttempts(jobId: string | null): UseUploadAttemptsResult {
	const [attempts, setAttempts] = useState<UploadAttempt[]>([]);
	const [loading, setLoading] = useState(false);
	const [unavailable, setUnavailable] = useState(false);

	useEffect(() => {
		if (!jobId) {
			setAttempts([]);
			setUnavailable(false);
			return;
		}

		let cancelled = false;
		setLoading(true);
		setUnavailable(false);

		fetchUploadAttempts(jobId)
			.then((result) => {
				if (!cancelled) {
					setAttempts(result);
				}
			})
			.catch(() => {
				if (!cancelled) {
					setAttempts([]);
					setUnavailable(true);
				}
			})
			.finally(() => {
				if (!cancelled) {
					setLoading(false);
				}
			});

		return () => {
			cancelled = true;
		};
	}, [jobId]);

	return { attempts, loading, unavailable };
}
