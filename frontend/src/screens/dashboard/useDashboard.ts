/**
 * Loads `GET /dashboard` and `GET /schedules` in parallel on mount (issue
 * #513). The two are independent per docs/api-contract.md, so one failing
 * must not blank the other — same "Promise.allSettled, degrade
 * independently" posture `SystemProvider` uses for `/system` + `/stigman`.
 * A `/schedules` failure renders the SCHEDULES card's empty state rather than
 * an error, since it is a secondary sidebar card, not the primary content.
 */
import { useEffect, useState } from "react";
import { ApiError } from "../../lib/api";
import { fetchSchedules, type ScheduleListItem } from "../../lib/schedules";
import { fetchDashboard, type DashboardData } from "./dashboard";

export interface UseDashboardResult {
	data: DashboardData | null;
	loading: boolean;
	error: string | null;
	schedules: ScheduleListItem[];
}

export function useDashboard(): UseDashboardResult {
	const [data, setData] = useState<DashboardData | null>(null);
	const [schedules, setSchedules] = useState<ScheduleListItem[]>([]);
	const [loading, setLoading] = useState(true);
	const [error, setError] = useState<string | null>(null);

	useEffect(() => {
		let cancelled = false;

		Promise.allSettled([fetchDashboard(), fetchSchedules()])
			.then(([dashboardResult, schedulesResult]) => {
				if (cancelled) {
					return;
				}
				if (dashboardResult.status === "fulfilled") {
					setData(dashboardResult.value);
				} else {
					const err = dashboardResult.reason;
					setError(err instanceof ApiError ? err.message : "Could not load the dashboard.");
				}
				if (schedulesResult.status === "fulfilled") {
					setSchedules(schedulesResult.value);
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
	}, []);

	return { data, loading, error, schedules };
}
