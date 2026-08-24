/**
 * Blocked banner (README screen 1: "explains the halt and offers 'Change
 * credential & resume' (Admin only)"). The #146 unblock flow: Admin picks a
 * replacement credential from the stored (service/shared) list and calls
 * `POST /runs/{id}/resume-blocked`. Non-Admin roles still see the button —
 * visible-but-disabled with the role reason — never hidden. The credential
 * picker itself only renders for Admin, since a disabled `<select>` with no
 * chance of ever being used would just be UI noise for every other role.
 */
import { useEffect, useState } from "react";
import { ApiError } from "../../lib/api";
import { roleGateProps, type Role } from "../../lib/roles";
import { fetchCredentials, type Credential } from "../configuration/credentials";
import { resumeBlockedRun } from "./liverun";

export function BlockedBanner({
	runId,
	reason,
	role,
	onError,
}: {
	runId: string;
	reason: string;
	role: Role | undefined;
	onError: (message: string | null) => void;
}) {
	const isAdmin = role === "Admin";
	const [credentials, setCredentials] = useState<Credential[]>([]);
	const [selectedId, setSelectedId] = useState("");
	const [resuming, setResuming] = useState(false);
	const [loadedCredentials, setLoadedCredentials] = useState(false);

	useEffect(() => {
		if (!isAdmin || loadedCredentials) {
			return;
		}
		setLoadedCredentials(true);
		fetchCredentials()
			.then((list) => setCredentials(list))
			.catch(() => {
				// Non-fatal: the picker just stays empty and resume stays disabled
				// until a credential id is chosen; the banner itself must still render.
			});
	}, [isAdmin, loadedCredentials]);

	async function handleResume() {
		if (!selectedId) {
			return;
		}
		onError(null);
		setResuming(true);
		try {
			await resumeBlockedRun(runId, selectedId);
		} catch (err) {
			onError(err instanceof ApiError ? err.message : "Could not resume the run.");
		} finally {
			setResuming(false);
		}
	}

	const resumeGate = isAdmin
		? { disabled: !selectedId || resuming }
		: roleGateProps(role ?? "Viewer", "Admin", "Requires Admin — credential swap & resume is not available to your role");

	return (
		<div className="live-run__blocked-banner">
			<span className="live-run__blocked-dot" />
			<span>Queue halted — {reason}</span>
			{isAdmin && (
				<select
					className="live-run__credential-select"
					value={selectedId}
					onChange={(e) => setSelectedId(e.target.value)}
					aria-label="Replacement credential"
				>
					<option value="">Select replacement credential…</option>
					{credentials.map((c) => (
						<option key={c.id} value={c.id}>
							{c.name}
						</option>
					))}
				</select>
			)}
			<button type="button" {...resumeGate} onClick={handleResume}>
				{resuming ? "Resuming…" : "Change credential & resume"}
			</button>
		</div>
	);
}
