import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AuthProvider } from "../../lib/auth";
import type { WaypointEvent } from "../../lib/events";
import { clampDrawerHeight, JobLogDrawer } from "./JobLogDrawer";

/** README "Global job log drawer" / "Drawer resize": "Drag-resize clamps to
 * [96px, 40% of window height]." — this is the load-bearing piece of the
 * whole drawer per the handoff's "Layout Rules Learned the Hard Way" #6. */
describe("clampDrawerHeight", () => {
	it("clamps below the 96px floor up to 96px", () => {
		expect(clampDrawerHeight(10, 1000)).toBe(96);
		expect(clampDrawerHeight(-500, 1000)).toBe(96);
	});

	it("clamps above 40vh down to 40vh", () => {
		expect(clampDrawerHeight(10000, 1000)).toBe(400);
	});

	it("passes through values inside the clamp untouched", () => {
		expect(clampDrawerHeight(200, 1000)).toBe(200);
	});

	it("re-derives the ceiling from the current viewport height (a resize can lower the ceiling below the floor)", () => {
		// A short window (e.g. 200px tall) has a 40vh ceiling of 80px, below
		// the 96px floor — the floor must still win so the drawer never
		// disappears entirely.
		expect(clampDrawerHeight(200, 200)).toBe(96);
	});

	it("boundary values are inclusive", () => {
		expect(clampDrawerHeight(96, 1000)).toBe(96);
		expect(clampDrawerHeight(400, 1000)).toBe(400);
	});
});

// ---------------------------------------------------------------------------
// Component-level tests (issue #69).
//
// The clamp tests above cover 4 lines of a 238-line component. Everything the
// drawer actually promises — issue #11's own acceptance criterion, "streams
// live events, follow-tail works, resize clamps per the handoff" — had no
// regression test. The handoff is explicit that follow-tail must "set
// scrollTop = scrollHeight; never scrollIntoView", and before these tests a
// refactor to scrollIntoView would have passed.
// ---------------------------------------------------------------------------

/** Height reported by every element's scrollHeight while these tests run.
 * jsdom does no layout, so follow-tail has nothing to read otherwise. */
const FAKE_SCROLL_HEIGHT = 4242;
const VIEWPORT_HEIGHT = 1000; // → a 40vh ceiling of exactly 400px.

/** A `fetch` mock for `/api/v1/events` whose stream stays open until the test
 * pushes into it, so frames can be delivered one at a time and asserted
 * against — the drawer's real input, not a stubbed-out event handler. */
function createDriveableSse() {
	const encoder = new TextEncoder();
	const queued: string[] = [];
	let resolveNext: ((r: { value: Uint8Array | undefined; done: boolean }) => void) | null = null;

	const reader = {
		read(): Promise<{ value: Uint8Array | undefined; done: boolean }> {
			const next = queued.shift();
			if (next !== undefined) {
				return Promise.resolve({ value: encoder.encode(next), done: false });
			}
			return new Promise((resolve) => {
				resolveNext = resolve;
			});
		},
		releaseLock() {},
	};

	return {
		response: { ok: true, status: 200, body: { getReader: () => reader } } as unknown as Response,
		/** Deliver raw SSE text to the client as one reader chunk. */
		push(text: string) {
			if (resolveNext) {
				const resolve = resolveNext;
				resolveNext = null;
				resolve({ value: encoder.encode(text), done: false });
			} else {
				queued.push(text);
			}
		},
	};
}

/** One SSE frame carrying a Waypoint event envelope (docs/api-contract.md). */
function frame(event: WaypointEvent): string {
	return `id: ${event.seq}\ndata: ${JSON.stringify(event)}\n\n`;
}

function logEvent(seq: number, data: Record<string, unknown>): WaypointEvent {
	return { seq, ts: `2026-08-02T14:07:${String(seq % 60).padStart(2, "0")}Z`, type: "job.log", job_id: "j-3021", data };
}

describe("JobLogDrawer", () => {
	let originalFetch: typeof fetch;
	let sse: ReturnType<typeof createDriveableSse>;
	let scrollIntoView: ReturnType<typeof vi.fn>;

	beforeEach(() => {
		originalFetch = globalThis.fetch;
		sse = createDriveableSse();
		globalThis.fetch = vi.fn(async (url: string) => {
			if (typeof url === "string" && url.startsWith("/api/v1/events")) {
				return sse.response;
			}
			return new Response("{}", { status: 404 });
		}) as unknown as typeof fetch;

		// A signed-in session, restored by AuthProvider on mount — the drawer
		// renders nothing until status === "signed-in". Shape matches what
		// lib/auth.tsx's login() actually persists (issue #64): token + role +
		// expires_at-derived expiresAt + username from /auth/me, not a nested
		// `user` object.
		window.sessionStorage.setItem(
			"waypoint.session",
			JSON.stringify({
				token: "tok-1",
				username: "j.moreno",
				role: "Admin",
				expiresAt: new Date(Date.now() + 60_000).toISOString(),
			}),
		);

		// jsdom has no layout: scrollHeight is always 0 and scrollTop is not
		// stored. Give both real behaviour so follow-tail is observable.
		Object.defineProperty(HTMLElement.prototype, "scrollHeight", {
			configurable: true,
			get: () => FAKE_SCROLL_HEIGHT,
		});
		Object.defineProperty(HTMLElement.prototype, "scrollTop", {
			configurable: true,
			get(this: HTMLElement & { _scrollTop?: number }) {
				return this._scrollTop ?? 0;
			},
			set(this: HTMLElement & { _scrollTop?: number }, value: number) {
				this._scrollTop = value;
			},
		});

		// jsdom does not implement scrollIntoView at all; install a spy so
		// "never scrollIntoView" is an assertion rather than an assumption.
		scrollIntoView = vi.fn();
		(Element.prototype as unknown as { scrollIntoView: unknown }).scrollIntoView = scrollIntoView;

		Object.defineProperty(window, "innerHeight", { configurable: true, writable: true, value: VIEWPORT_HEIGHT });
	});

	afterEach(() => {
		globalThis.fetch = originalFetch;
		window.sessionStorage.clear();
		Reflect.deleteProperty(HTMLElement.prototype, "scrollHeight");
		Reflect.deleteProperty(HTMLElement.prototype, "scrollTop");
		Reflect.deleteProperty(Element.prototype, "scrollIntoView");
	});

	/** Mounts the drawer signed-in and opens it. Returns the scrolling log
	 * pane, which is the single scroll container per layout rule #6. */
	async function mountOpenDrawer() {
		const { container } = render(
			<AuthProvider>
				<JobLogDrawer />
			</AuthProvider>,
		);
		await waitFor(() => expect(screen.getByText("JOB LOG")).toBeInTheDocument());
		fireEvent.click(screen.getByText("JOB LOG"));
		const body = await waitFor(() => {
			const el = container.querySelector(".job-log-drawer__body");
			expect(el).not.toBeNull();
			return el as HTMLElement;
		});
		return { container, body };
	}

	/** Pushes SSE text and lets the client's read loop drain it. */
	async function deliver(text: string) {
		await act(async () => {
			sse.push(text);
			await new Promise((resolve) => setTimeout(resolve, 0));
		});
	}

	it("renders streamed job.log lines in arrival order with their level", async () => {
		const { body } = await mountOpenDrawer();

		await deliver(
			frame(logEvent(1, { level: "info", line: "resolving depot" })) +
				frame(logEvent(2, { level: "warn", line: "checksum retry 1/3" })) +
				frame(logEvent(3, { level: "error", line: "esxi-02.example.internal unreachable" })),
		);

		await waitFor(() => expect(body.querySelectorAll(".job-log-drawer__line")).toHaveLength(3));

		const messages = Array.from(body.querySelectorAll(".job-log-drawer__msg")).map((el) => el.textContent);
		expect(messages).toEqual(["resolving depot", "checksum retry 1/3", "esxi-02.example.internal unreachable"]);

		const levels = Array.from(body.querySelectorAll(".job-log-drawer__level")).map((el) => el.className);
		expect(levels[0]).toContain("job-log-drawer__level--INFO");
		expect(levels[1]).toContain("job-log-drawer__level--WARN");
		expect(levels[2]).toContain("job-log-drawer__level--ERROR");

		// The bar's line counter is driven by the same buffer.
		expect(screen.getByText("3 lines")).toBeInTheDocument();
	});

	it("falls back to INFO for an unknown level, and to data.message when data.line is absent", async () => {
		const { body } = await mountOpenDrawer();

		await deliver(
			frame(logEvent(1, { level: "chatty", line: "unknown level" })) +
				frame(logEvent(2, { message: "no line field" })) +
				frame({
					seq: 3,
					ts: "2026-08-02T14:08:00Z",
					type: "system.notice",
					data: { level: "ok", message: "bundle verified" },
				}),
		);

		await waitFor(() => expect(body.querySelectorAll(".job-log-drawer__line")).toHaveLength(3));
		const levels = Array.from(body.querySelectorAll(".job-log-drawer__level")).map((el) => el.textContent);
		expect(levels).toEqual(["INFO", "INFO", "OK"]);
		expect(body.querySelectorAll(".job-log-drawer__msg")[1].textContent).toBe("no line field");
	});

	// --- The acceptance criterion the handoff spells out ---------------------

	it("follow-tail sets scrollTop = scrollHeight and never calls scrollIntoView", async () => {
		const { body } = await mountOpenDrawer();

		await deliver(frame(logEvent(1, { level: "info", line: "first" })));

		await waitFor(() => expect(body.scrollTop).toBe(FAKE_SCROLL_HEIGHT));
		// README: "set scrollTop = scrollHeight; never scrollIntoView". This
		// assertion is the point of the test — a refactor to scrollIntoView
		// would satisfy the visible behaviour and fail here.
		expect(scrollIntoView).not.toHaveBeenCalled();

		// And it keeps following as more lines arrive.
		body.scrollTop = 0;
		await deliver(frame(logEvent(2, { level: "info", line: "second" })));
		await waitFor(() => expect(body.scrollTop).toBe(FAKE_SCROLL_HEIGHT));
		expect(scrollIntoView).not.toHaveBeenCalled();
	});

	it("stops auto-scrolling once Follow tail is toggled off", async () => {
		const { body } = await mountOpenDrawer();

		await deliver(frame(logEvent(1, { level: "info", line: "first" })));
		await waitFor(() => expect(body.scrollTop).toBe(FAKE_SCROLL_HEIGHT));

		fireEvent.click(screen.getByRole("button", { name: "Follow tail" }));
		body.scrollTop = 0;

		await deliver(frame(logEvent(2, { level: "info", line: "second" })));
		await waitFor(() => expect(body.querySelectorAll(".job-log-drawer__line")).toHaveLength(2));
		// The user scrolled away deliberately; the new line must not yank them back.
		expect(body.scrollTop).toBe(0);
		expect(scrollIntoView).not.toHaveBeenCalled();
	});

	it("drag-resize clamps at both [96px, 40vh] bounds", async () => {
		const { body } = await mountOpenDrawer();
		const handle = screen.getByTitle("Drag to resize the log pane");

		// A drag inside the clamp moves 1:1 with the pointer (dragging up grows).
		fireEvent.mouseDown(handle, { clientY: 500 });
		fireEvent.mouseMove(window, { clientY: 400 });
		expect(body.style.height).toBe("296px"); // 196 default + 100px of drag
		fireEvent.mouseUp(window);

		// Far past the ceiling: 40% of a 1000px viewport.
		fireEvent.mouseDown(handle, { clientY: 500 });
		fireEvent.mouseMove(window, { clientY: -10000 });
		expect(body.style.height).toBe("400px");
		fireEvent.mouseUp(window);

		// Far past the floor.
		fireEvent.mouseDown(handle, { clientY: 500 });
		fireEvent.mouseMove(window, { clientY: 10000 });
		expect(body.style.height).toBe("96px");
		fireEvent.mouseUp(window);

		// mouseup detaches the listeners: further movement must not resize.
		fireEvent.mouseMove(window, { clientY: -10000 });
		expect(body.style.height).toBe("96px");
	});

	it("re-clamps the pane when the window shrinks below the current height", async () => {
		const { body } = await mountOpenDrawer();
		const handle = screen.getByTitle("Drag to resize the log pane");

		fireEvent.mouseDown(handle, { clientY: 500 });
		fireEvent.mouseMove(window, { clientY: 300 });
		fireEvent.mouseUp(window);
		expect(body.style.height).toBe("396px");

		// Shrink the viewport to 500px: the 40vh ceiling drops to 200px and the
		// drawer must give the height back rather than starve the screen above
		// it (layout rule #6).
		act(() => {
			Object.defineProperty(window, "innerHeight", { configurable: true, writable: true, value: 500 });
			window.dispatchEvent(new Event("resize"));
		});
		await waitFor(() => expect(body.style.height).toBe("200px"));
	});

	// --- Active-job tracking and buffer bound -------------------------------

	it("tracks active jobs from job.state and drops them on a terminal state", async () => {
		const { container } = await mountOpenDrawer();
		const bar = container.querySelector(".job-log-drawer__bar") as HTMLElement;

		await deliver(
			frame({
				seq: 1,
				ts: "2026-08-02T14:07:01Z",
				type: "job.state",
				job_id: "j-1",
				data: { target: "esxi-01.example.internal", to: "running" },
			}),
		);
		await waitFor(() => expect(within(bar).getByText("1")).toBeInTheDocument());
		expect(container.querySelector(".job-log-drawer__summary")?.textContent).toBe("esxi-01.example.internal");

		await deliver(
			frame({
				seq: 2,
				ts: "2026-08-02T14:07:02Z",
				type: "job.state",
				job_id: "j-1",
				data: { target: "esxi-01.example.internal", to: "done" },
			}),
		);
		await waitFor(() => expect(within(bar).getByText("0")).toBeInTheDocument());
		expect(container.querySelector(".job-log-drawer__summary")?.textContent).toBe("");
	});

	it("trims the buffer to MAX_BUFFERED_LINES, keeping the newest lines", async () => {
		const { body } = await mountOpenDrawer();

		let stream = "";
		for (let seq = 1; seq <= 1005; seq += 1) {
			stream += frame(logEvent(seq, { level: "info", line: `line ${seq}` }));
		}
		await deliver(stream);

		await waitFor(() => expect(screen.getByText("1000 lines")).toBeInTheDocument());
		const messages = body.querySelectorAll(".job-log-drawer__msg");
		expect(messages).toHaveLength(1000);
		// The oldest five were dropped, not the newest.
		expect(messages[0].textContent).toBe("line 6");
		expect(messages[999].textContent).toBe("line 1005");
	});

	// --- Accessibility (issue #71) ------------------------------------------

	it("does not fold the Follow-tail/Download buttons into the bar's accessible name", async () => {
		await mountOpenDrawer();

		// The regression this issue is about: before the fix, the bar itself
		// carried role="button" and wrapped the real buttons, so this query
		// matched both the bar and the real button.
		expect(screen.getAllByRole("button", { name: /follow tail/i })).toHaveLength(1);
		expect(screen.getAllByRole("button", { name: /download/i })).toHaveLength(1);
	});

	it("exposes drawer open/collapsed state via aria-expanded on a dedicated toggle", async () => {
		const { container } = render(
			<AuthProvider>
				<JobLogDrawer />
			</AuthProvider>,
		);
		await waitFor(() => expect(screen.getByText("JOB LOG")).toBeInTheDocument());

		const toggle = screen.getByRole("button", { name: /job log/i });
		expect(toggle).toHaveAttribute("aria-expanded", "false");

		fireEvent.click(toggle);
		await waitFor(() => expect(container.querySelector(".job-log-drawer__body")).not.toBeNull());
		expect(toggle).toHaveAttribute("aria-expanded", "true");

		fireEvent.click(toggle);
		await waitFor(() => expect(container.querySelector(".job-log-drawer__body")).toBeNull());
		expect(toggle).toHaveAttribute("aria-expanded", "false");
	});

	it("still opens/collapses when clicking anywhere on the bar (pointer affordance preserved)", async () => {
		const { container } = await mountOpenDrawer();
		const bar = container.querySelector(".job-log-drawer__bar") as HTMLElement;

		// Click on a non-interactive part of the bar (the summary/count area),
		// not the dedicated toggle button.
		fireEvent.click(bar);
		await waitFor(() => expect(container.querySelector(".job-log-drawer__body")).toBeNull());

		fireEvent.click(bar);
		await waitFor(() => expect(container.querySelector(".job-log-drawer__body")).not.toBeNull());
	});

	it("the toggle button is keyboard-operable independent of the bar div", async () => {
		render(
			<AuthProvider>
				<JobLogDrawer />
			</AuthProvider>,
		);
		await waitFor(() => expect(screen.getByText("JOB LOG")).toBeInTheDocument());

		const toggle = screen.getByRole("button", { name: /job log/i });
		toggle.focus();
		expect(toggle).toHaveFocus();

		// A real <button> activates on Enter/Space natively (jsdom fires the
		// click for us via fireEvent.click, which is how RTL simulates native
		// keyboard activation of a focused button).
		fireEvent.click(toggle);
		expect(toggle).toHaveAttribute("aria-expanded", "true");
	});

	it("renders nothing at all while signed out", async () => {
		window.sessionStorage.clear();
		const { container } = render(
			<AuthProvider>
				<JobLogDrawer />
			</AuthProvider>,
		);
		await waitFor(() => expect(container.querySelector(".job-log-drawer")).toBeNull());
		expect(globalThis.fetch).not.toHaveBeenCalled();
	});
});
