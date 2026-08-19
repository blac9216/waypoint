/**
 * `GET /api/v1/schedules` (issue #513's SCHEDULES sidebar card; backend
 * `SchedulesController`/`ScheduleResponse`, ScheduleContracts.cs). Field
 * names/nullability match the wire exactly (snake_case, per `lib/api.ts`'s
 * convention) — this module only reads, the write surface (#31's create/
 * update/delete UI) is not part of this screen.
 */
import { apiGet } from "./api";

export interface ScheduleListItem {
	id: string;
	name: string;
	job_type: string;
	cron_expression: string;
	scope: string;
	credential_id: string | null;
	enabled: boolean;
	paused_reason: string | null;
	next_run_at: string | null;
	last_run_at: string | null;
	last_run_id: string | null;
	last_result: string | null;
	created_by: string;
	created_at: string;
	updated_at: string;
}

export function fetchSchedules(): Promise<ScheduleListItem[]> {
	return apiGet<ScheduleListItem[]>("/schedules");
}

/** Sidebar status dot color key + short state label for one schedule row —
 * paused (auto or operator-disabled) reads distinctly from a due/healthy one,
 * matching the prototype's colored-spine ATTENTION/SCHEDULES treatment. */
export function scheduleStateLabel(schedule: ScheduleListItem): { label: string; tone: "ok" | "warn" | "bad" } {
	if (schedule.paused_reason) {
		return { label: "auto-paused", tone: "warn" };
	}
	if (!schedule.enabled) {
		return { label: "paused", tone: "warn" };
	}
	if (schedule.last_result === "failed") {
		return { label: "last failed", tone: "bad" };
	}
	return { label: "active", tone: "ok" };
}
