import { afterEach, describe, expect, it, vi } from "vitest";
import { ApiError, apiFetch, apiGetPaged } from "./api";

/**
 * Unit coverage for `apiFetch`'s deadline, at the layer where the bug lived.
 *
 * `App.test.tsx` proves the *user-visible* consequence (a `/catalog` deep link
 * stops being a permanently blank page); these prove the *mechanism*, on the
 * two shapes the round-1 fix got wrong:
 *
 *   - PR #88 round-2 review, finding 1 — the timer was cleared in a `finally`
 *     attached to the `fetch` call, which resolves when response HEADERS
 *     arrive. `await response.json()` was therefore unbounded, and a 200 whose
 *     body stalls reproduced the round-1 blank page (measured at 25 s in real
 *     Chromium, past an 8 s bound).
 *   - PR #88 round-2 review, finding 4 — `timeoutMs` was silently voided when
 *     the caller also passed a `signal`, so asking for both got neither.
 *
 * Every mock here settles ONLY when the request is aborted, so none of these
 * tests can pass on a timer that does not really reach the request.
 */

const originalFetch = globalThis.fetch;

afterEach(() => {
	globalThis.fetch = originalFetch;
	vi.useRealTimers();
	vi.restoreAllMocks();
});

/** A 200 with headers and a first body chunk, whose body then never
 * completes; the stream errors only if the request's signal aborts. */
function installStallingBody() {
	const seen: { signal?: AbortSignal | null } = {};
	globalThis.fetch = vi.fn(async (_url: string, init?: RequestInit) => {
		seen.signal = init?.signal;
		const stream = new ReadableStream({
			start(controller) {
				controller.enqueue(new TextEncoder().encode('{"partial":'));
				init?.signal?.addEventListener("abort", () => {
					controller.error(new DOMException("The operation was aborted.", "AbortError"));
				});
			},
		});
		return new Response(stream, { status: 200, headers: { "Content-Type": "application/json" } });
	}) as unknown as typeof fetch;
	return seen;
}

/** A `fetch` that never resolves at all unless its signal aborts. */
function installNeverResolving() {
	const seen: { signal?: AbortSignal | null } = {};
	globalThis.fetch = vi.fn(
		(_url: string, init?: RequestInit) =>
			new Promise<Response>((_resolve, reject) => {
				seen.signal = init?.signal;
				const abort = () => reject(new DOMException("The operation was aborted.", "AbortError"));
				// Platform `fetch` rejects immediately for a signal that is
				// already aborted, rather than waiting for an event that will
				// never fire again. The mock has to do the same or the
				// already-aborted case would hang here for reasons that have
				// nothing to do with the code under test.
				if (init?.signal?.aborted) {
					abort();
					return;
				}
				init?.signal?.addEventListener("abort", abort);
			}),
	) as unknown as typeof fetch;
	return seen;
}

async function expectApiError(promise: Promise<unknown>, code: string) {
	await expect(promise).rejects.toBeInstanceOf(ApiError);
	await promise.catch((err: ApiError) => {
		expect(err.code).toBe(code);
		expect(err.status).toBe(0);
	});
}

describe("apiFetch timeoutMs", () => {
	it("bounds a 200 whose response body stalls, not only the connect (round-2 finding 1)", async () => {
		vi.useFakeTimers();
		installStallingBody();

		const promise = apiFetch("/system", { timeoutMs: 8000 });
		const assertion = expectApiError(promise, "timeout");

		// Headers have arrived, so a `finally` on the fetch call has already
		// fired. Nothing may have settled the request.
		await vi.advanceTimersByTimeAsync(7500);
		let settled = false;
		void promise.then(
			() => {
				settled = true;
			},
			() => {
				settled = true;
			},
		);
		await Promise.resolve();
		expect(settled).toBe(false);

		await vi.advanceTimersByTimeAsync(1000);
		await assertion;
	});

	it("still bounds a fetch that never resolves at all", async () => {
		vi.useFakeTimers();
		installNeverResolving();

		const promise = apiFetch("/system", { timeoutMs: 8000 });
		const assertion = expectApiError(promise, "timeout");
		await vi.advanceTimersByTimeAsync(8500);
		await assertion;
	});

	it("clears the timer on a successful response — no late abort, no leaked handle", async () => {
		vi.useFakeTimers();
		const clearSpy = vi.spyOn(globalThis, "clearTimeout");
		let captured: AbortSignal | null | undefined;
		globalThis.fetch = vi.fn(async (_url: string, init?: RequestInit) => {
			captured = init?.signal;
			return new Response(JSON.stringify({ mode: "connected" }), {
				status: 200,
				headers: { "Content-Type": "application/json" },
			});
		}) as unknown as typeof fetch;

		await expect(apiFetch("/system", { timeoutMs: 8000 })).resolves.toEqual({ mode: "connected" });
		expect(clearSpy).toHaveBeenCalled();

		// Well past the deadline the signal must still be un-aborted: a timer
		// that survives a completed request would abort an unrelated later use
		// of the same signal and keep the event loop alive.
		await vi.advanceTimersByTimeAsync(20000);
		expect(captured?.aborted).toBe(false);
	});

	it("keeps a non-JSON body meaning `undefined`, and does not launder a timed-out body into it", async () => {
		globalThis.fetch = vi.fn(async () => new Response("not json at all", { status: 200 })) as unknown as typeof fetch;
		await expect(apiFetch("/system", { timeoutMs: 8000 })).resolves.toBeUndefined();
	});

	it("bounds the body read on an ERROR response too, where parseErrorBody awaits json()", async () => {
		vi.useFakeTimers();
		globalThis.fetch = vi.fn(async (_url: string, init?: RequestInit) => {
			const stream = new ReadableStream({
				start(controller) {
					controller.enqueue(new TextEncoder().encode('{"error":'));
					init?.signal?.addEventListener("abort", () => {
						controller.error(new DOMException("The operation was aborted.", "AbortError"));
					});
				},
			});
			return new Response(stream, { status: 500, headers: { "Content-Type": "application/json" } });
		}) as unknown as typeof fetch;

		const promise = apiFetch("/system", { timeoutMs: 8000 });
		const assertion = expectApiError(promise, "timeout");
		await vi.advanceTimersByTimeAsync(8500);
		await assertion;
	});
});

describe("apiFetch timeoutMs composed with a caller signal (round-2 finding 4)", () => {
	it("still times out when the caller also supplies a signal", async () => {
		// The regression: `timeoutMs` used to be dropped entirely whenever a
		// `signal` was present, so a caller asking for both got neither and the
		// request stayed outstanding indefinitely (verified unsettled at 4.5x
		// the timeout).
		vi.useFakeTimers();
		installNeverResolving();
		const caller = new AbortController();

		const promise = apiFetch("/system", { timeoutMs: 8000, signal: caller.signal });
		const assertion = expectApiError(promise, "timeout");
		await vi.advanceTimersByTimeAsync(8500);
		await assertion;
		expect(caller.signal.aborted).toBe(false);
	});

	it("lets the caller's signal abort first, reported as a network error rather than a timeout", async () => {
		vi.useFakeTimers();
		installNeverResolving();
		const caller = new AbortController();

		const promise = apiFetch("/system", { timeoutMs: 8000, signal: caller.signal });
		const assertion = expectApiError(promise, "network_error");
		await vi.advanceTimersByTimeAsync(100);
		caller.abort();
		await assertion;
	});

	it("honours a signal that is already aborted before the call", async () => {
		installNeverResolving();
		const caller = new AbortController();
		caller.abort();

		await expectApiError(apiFetch("/system", { timeoutMs: 8000, signal: caller.signal }), "network_error");
	});

	it("leaves a caller signal with no timeoutMs behaving exactly as before", async () => {
		const seen = installNeverResolving();
		const caller = new AbortController();

		const promise = apiFetch("/system", { signal: caller.signal });
		const assertion = expectApiError(promise, "network_error");
		// No wrapping controller is created in this case, so the caller's own
		// signal is what reaches `fetch`.
		expect(seen.signal).toBe(caller.signal);
		caller.abort();
		await assertion;
	});
});

/**
 * `apiGetPaged` (issue #531) — the first list caller that needs the real
 * `X-Total-Count` rather than `results.ts`'s `items.length` placeholder.
 * Covers the header read and the fallback when a server omits the header.
 */
describe("apiGetPaged", () => {
	afterEach(() => {
		globalThis.fetch = originalFetch;
	});

	it("reads X-Total-Count and pairs it with the parsed array", async () => {
		globalThis.fetch = vi.fn(
			async () =>
				new Response(JSON.stringify([{ id: "a" }, { id: "b" }]), {
					status: 200,
					headers: { "Content-Type": "application/json", "X-Total-Count": "42" },
				}),
		) as unknown as typeof fetch;

		const result = await apiGetPaged("/audit?limit=2&offset=0");
		expect(result.items).toEqual([{ id: "a" }, { id: "b" }]);
		expect(result.totalCount).toBe(42);
	});

	it("falls back to items.length when X-Total-Count is absent", async () => {
		globalThis.fetch = vi.fn(
			async () =>
				new Response(JSON.stringify([{ id: "a" }]), {
					status: 200,
					headers: { "Content-Type": "application/json" },
				}),
		) as unknown as typeof fetch;

		const result = await apiGetPaged("/audit");
		expect(result.totalCount).toBe(1);
	});

	it("surfaces a non-2xx response as an ApiError, same as apiFetch", async () => {
		globalThis.fetch = vi.fn(
			async () =>
				new Response(JSON.stringify({ error: { code: "forbidden", message: "nope" } }), {
					status: 403,
					headers: { "Content-Type": "application/json" },
				}),
		) as unknown as typeof fetch;

		await expect(apiGetPaged("/audit")).rejects.toBeInstanceOf(ApiError);
	});
});
