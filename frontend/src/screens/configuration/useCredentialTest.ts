/**
 * Credential-test SSE tracking (issue #418 extraction from CredentialsTab.tsx).
 *
 * Test action (issue #323, reworking #247's stub for #245/PR #322's job-backed
 * `POST /credentials/{id}/test`): the request returns `202 + {run_id,
 * job_id}` instead of an inline result, so `doTest` follows the job via the
 * same SSE plumbing the Live Run board uses (`lib/events.ts`'s
 * `connectEventStream`, not a fresh EventSource) rather than reading a
 * result that no longer exists on the response. It watches `job.state`
 * events scoped to that `job_id` until a terminal state (`done`/`failed`/
 * `auth-failed`), then re-fetches the credential — its `health` field is the
 * server-side source of truth, flipped by `CredentialTestJobHandler`, not by
 * this call — and reflects both the health pill and the terminal note on the
 * row.
 *
 * Stream teardown: since a test is started from a click handler rather than
 * render, its `connectEventStream` `close()` is tracked in a ref keyed by
 * credential id (`activeStreams`) rather than returned from a `useEffect` —
 * but the lifecycle guarantee matches `useLiveRun.ts`'s `return close`
 * cleanup: an unmount closes every still-open stream, a `mountedRef` guard
 * makes any terminal event that arrives afterward a no-op instead of a
 * setState-after-unmount, and starting a new test on a row closes that row's
 * prior stream first so overlapping clicks never leak two.
 */
import { useCallback, useEffect, useRef, useState, type Dispatch, type SetStateAction } from "react";
import { useAuth } from "../../lib/auth-context";
import { API_BASE, ApiError } from "../../lib/api";
import { connectEventStream } from "../../lib/events";
import { fetchCredential, testCredential, type Credential } from "./credentials";

/** `job.state` terminal values (api-contract.md's Simple-job state machine —
 * `credential-test` is a `JobShape.Simple` job: queued -> running ->
 * done|failed|auth-failed). */
const TERMINAL_JOB_STATES = new Set(["done", "failed", "auth-failed"]);

interface JobStateEventData {
	to?: string;
	note?: string;
}

export interface CredentialTestMessage {
	id: string;
	succeeded: boolean;
	message: string;
}

export interface UseCredentialTestResult {
	/** ids with an in-flight test, so multiple rows can be tested
	 * independently and a stale spinner never lingers on the wrong row. */
	testing: Set<string>;
	testMessage: CredentialTestMessage | null;
	doTest: (id: string) => Promise<void>;
}

export function useCredentialTest(setCredentials: Dispatch<SetStateAction<Credential[]>>): UseCredentialTestResult {
	const { token } = useAuth();
	const [testing, setTesting] = useState<Set<string>>(new Set());
	const [testMessage, setTestMessage] = useState<CredentialTestMessage | null>(null);

	/** Token getter for `connectEventStream`, kept live via a ref so an
	 * in-flight test's stream always reads the current token rather than
	 * one captured by a stale closure. */
	const tokenRef = useRef(token);
	tokenRef.current = token;

	/** id -> active test stream's `close`, so an unmount (or a second Test
	 * click on the same row) can tear down a still-open stream instead of
	 * leaking its reconnect/abort loop — mirrors useLiveRun.ts's
	 * `useEffect(() => close, ...)` cleanup, just keyed by credential id
	 * since a test is started from a click handler, not render. */
	const activeStreams = useRef<Map<string, () => void>>(new Map());
	/** Guards the terminal-event setState calls below: once unmounted, a
	 * terminal `job.state` that arrives afterward (or the refetch that
	 * follows it) is a no-op instead of a post-unmount setState. */
	const mountedRef = useRef(true);

	useEffect(() => {
		const streams = activeStreams.current;
		return () => {
			mountedRef.current = false;
			for (const close of streams.values()) {
				close();
			}
			streams.clear();
		};
	}, []);

	const doTest = useCallback(
		async (id: string) => {
			// A prior test on this same row (if any) is still following its own
			// stream — close it before starting a new one so overlapping clicks
			// never leave two streams open for one credential.
			activeStreams.current.get(id)?.();
			activeStreams.current.delete(id);

			setTesting((prev) => new Set(prev).add(id));
			setTestMessage(null);

			const stopTesting = () => {
				if (!mountedRef.current) {
					return;
				}
				setTesting((prev) => {
					const next = new Set(prev);
					next.delete(id);
					return next;
				});
			};

			let queued: { run_id: string; job_id: string };
			try {
				queued = await testCredential(id);
			} catch (err) {
				if (mountedRef.current) {
					setTestMessage({ id, succeeded: false, message: err instanceof ApiError ? err.message : "Test failed." });
				}
				stopTesting();
				return;
			}

			if (!mountedRef.current) {
				// Unmounted while the POST was in flight; nothing left to follow.
				return;
			}

			// Follow the queued job to its terminal state via the same SSE plumbing
			// the Live Run board uses (lib/events.ts's connectEventStream) rather
			// than reading a result inline — the 202 response carries no outcome
			// (issue #323). Once terminal, `credentials.health` is re-fetched
			// because the job handler, not this response, is the source of truth
			// for the pill.
			let settled = false;
			const close = connectEventStream(`${API_BASE}/runs/${queued.run_id}/events`, {
				getToken: () => tokenRef.current,
				onEvent: (event) => {
					if (settled || event.type !== "job.state" || event.job_id !== queued.job_id) {
						return;
					}
					const data = event.data as JobStateEventData;
					if (!data.to || !TERMINAL_JOB_STATES.has(data.to)) {
						return;
					}
					settled = true;
					const succeeded = data.to === "done";
					const message = data.note ?? (succeeded ? "Test succeeded." : "Test failed.");
					if (mountedRef.current) {
						setTestMessage({ id, succeeded, message });
					}
					fetchCredential(id)
						.then((refreshed) => {
							if (mountedRef.current) {
								setCredentials((prev) => prev.map((c) => (c.id === id ? refreshed : c)));
							}
						})
						.catch(() => {
							// The row keeps its last-known health; the terminal message
							// above already told the operator the outcome.
						})
						.finally(() => {
							stopTesting();
							activeStreams.current.delete(id);
							close();
						});
				},
			});
			activeStreams.current.set(id, close);
		},
		[setCredentials],
	);

	return { testing, testMessage, doTest };
}
