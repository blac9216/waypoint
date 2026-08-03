/**
 * MANUAL browser verification for the route guard's never-decide-while-unknown
 * and never-mount properties (issues #78 and #82) and for the bounded startup
 * fetch (PR #88 round-1 review, finding 2). Not part of `npm test`: it drives a
 * real Chromium against a running `vite dev`, so it needs a browser and a dev
 * server that a plain unit run does not have. The jsdom equivalents live in
 * `src/App.test.tsx`; this is what proves the same properties in a real engine.
 *
 * WHY IT IS A COMMITTED SCRIPT AND NOT A SNIPPET IN A PR BODY. The round-1
 * version of this was pasted into the PR's Suggested Test Steps, and its
 * MutationObserver callback was empty, with the mount check being two
 * point-in-time samples. That is a two-sample poll: it cannot see a screen that
 * mounts and unmounts between the samples, which is exactly the class of bug
 * issue #82's triage ruled out as unprovable by a final-URL assertion. Here the
 * observer's callback RECORDS — it latches a boolean the first time a screen's
 * marker text is present during any mutation, and every assertion reads the
 * latch, never a snapshot. Keeping it in the repo also means the evidence a
 * reviewer runs is the evidence under review, rather than a block of shell that
 * GitHub may have mangled on its way into a stored body (docs/testing.md,
 * "Honest verification" rule 3).
 *
 * WHAT THE LATCH CAN AND CANNOT SEE — stated so no scenario here implies
 * coverage it does not have.
 *
 *   CAN: a screen that mounts and unmounts between two samples, including
 *   within a single synchronous task. The callback reads the delivered
 *   `MutationRecord`s (`addedNodes`, and `characterData` targets), which hold
 *   references to the nodes themselves, so a node already detached from the
 *   DOM is still inspected. Round 2 of the PR #88 review confirmed by control
 *   that the previous version — which re-read `document.body.innerText`
 *   inside the callback — reported `false` for exactly that case. Scenario 0
 *   is that control, kept in the harness so the property is re-proved on every
 *   run rather than trusted.
 *
 *   CANNOT: (a) anything rendered inside a shadow root or an iframe, which a
 *   document-level observer does not traverse; (b) a screen that mounts
 *   without ever putting its marker TEXT in the DOM — this harness detects a
 *   mount by rendered text, not by React internals. Both are why the
 *   authoritative evidence for the #78/#82 never-mount property is the
 *   component-level mount spy in `src/App.test.tsx` (a function component's
 *   body runs exactly when React mounts it, so "the spy was never called" is
 *   the stronger claim). This harness is corroboration in a real engine, not
 *   the primary proof.
 *
 * Requires `playwright-core` (deliberately NOT a dependency of this air-gapped
 * app — install it transiently) and a Chromium:
 *
 *   npm install --no-save playwright-core
 *   npm run dev -- --port 5411 --strictPort
 *   BASE_URL=http://localhost:5411 node scripts/verify-mode-guard-browser.mjs
 *
 * CHROME_PATH overrides the browser (defaults to the sandbox-provisioned
 * /opt/pw-browsers/chromium, see docs/testing.md). ONLY runs one scenario by
 * its leading number. Point BASE_URL at a second dev server running another
 * revision for a before/after comparison: every scenario prints raw
 * observations and asserts nothing, so the output stays meaningful on a
 * revision that fails.
 */
import { chromium } from "playwright-core";

const BASE = process.env.BASE_URL ?? "http://localhost:5411";
const EXE = process.env.CHROME_PATH ?? "/opt/pw-browsers/chromium";
const DELAY_MS = 2000;

const SYSTEM_BODY = (mode) =>
	JSON.stringify({ version: "0.1.0-dev", build: "local", mode, update_available: null });

async function withPage(fn) {
	const browser = await chromium.launch({ executablePath: EXE });
	const page = await browser.newPage();
	// The recording observer. `seen` latches true the first time the marker
	// text is in the DOM during ANY mutation -- so a screen that mounts for a
	// single frame and is torn down before the next sample is still recorded.
	await page.addInitScript(() => {
		const MARKERS = {
			catalog: "GET /api/v1/catalog/artifacts",
			config: "GET /api/v1/credentials",
		};
		const seen = { catalog: false, config: false };
		const latch = (text) => {
			if (!text) return;
			for (const [key, marker] of Object.entries(MARKERS)) {
				if (text.includes(marker)) {
					seen[key] = true;
				}
			}
		};
		// Latch on the DELIVERED MutationRecords, not on a re-read of the
		// current DOM. PR #88 round-2 review, finding 5: re-reading
		// `document.body.innerText` inside the callback means a node that was
		// added and removed again before the callback ran is already gone from
		// the DOM by the time it is looked for — the reviewer confirmed that
		// with a control (added + removed in the same synchronous task latched
		// `false`). A `MutationRecord` holds a reference to the added node
		// itself, so `addedNodes` still sees it after it has been detached.
		const onMutations = (records) => {
			for (const record of records) {
				for (const node of record.addedNodes) {
					latch(node.textContent);
				}
				if (record.type === "characterData") {
					latch(record.target.textContent);
				}
			}
		};
		new MutationObserver(onMutations).observe(document, { childList: true, subtree: true, characterData: true });
		latch(document.body ? document.body.innerText : "");
		window.__seen = seen;
		window.__bodyTextLen = () => (document.body ? document.body.innerText.length : 0);
		// Test hook for the positive control below — lets the harness verify the
		// latch really records, instead of asserting a `false` that could just
		// as easily mean the observer is inert.
		window.__latchControl = (marker) => {
			const node = document.createElement("div");
			node.textContent = marker;
			document.body.appendChild(node);
			node.remove(); // same synchronous task: gone before the callback runs
		};
	});
	try {
		return await fn(page);
	} finally {
		await browser.close();
	}
}

async function signIn(page) {
	await page.goto(BASE + "/");
	await page.getByLabel("Username").fill("admin");
	await page.getByLabel("Password").fill("waypoint-dev");
	await page.getByRole("button", { name: /sign in/i }).click();
}

async function probe(page) {
	return page.evaluate(() => ({
		path: location.pathname,
		bodyTextLen: window.__bodyTextLen(),
		catalogEverMounted: window.__seen.catalog,
		configEverMounted: window.__seen.config,
	}));
}

/** #82: a connected appliance, /system delayed. The deep link must survive and
 * the screen must mount once mode is known. */
async function delayedConnectedDeepLink() {
	return withPage(async (page) => {
		await page.route("**/api/v1/system", async (route) => {
			await new Promise((r) => setTimeout(r, DELAY_MS));
			await route.fulfill({ status: 200, contentType: "application/json", body: SYSTEM_BODY("connected") });
		});
		await signIn(page);
		await page.waitForSelector("text=WAYPOINT");
		await page.goto(BASE + "/catalog");
		await page.waitForTimeout(DELAY_MS / 2);
		const midFlight = await probe(page);
		await page.waitForTimeout(DELAY_MS + 500);
		return { midFlight, settled: await probe(page) };
	});
}

/** #82 + #78: a DISCONNECTED appliance deep-linking /catalog. `pending` must
 * not become a silent allow -- the screen must never mount, not for a frame. */
async function delayedDisconnectedDeepLink() {
	return withPage(async (page) => {
		await page.route("**/api/v1/system", async (route) => {
			await new Promise((r) => setTimeout(r, DELAY_MS));
			await route.fulfill({ status: 200, contentType: "application/json", body: SYSTEM_BODY("disconnected") });
		});
		await signIn(page);
		await page.waitForSelector("text=WAYPOINT");
		await page.goto(BASE + "/catalog");
		await page.waitForTimeout(DELAY_MS / 2);
		const midFlight = await probe(page);
		await page.waitForTimeout(DELAY_MS + 500);
		return { midFlight, settled: await probe(page) };
	});
}

/** #78 role case: a Viewer deep-linking /config must never mount it. */
async function viewerRoleDeepLink() {
	return withPage(async (page) => {
		// Downgrade the role in the real login response rather than forging one:
		// the dev mock backend authorizes /system and /stigman against the token
		// it issued, and a forged token gets a 401 that signs the session
		// straight back out (which would silently make this scenario prove
		// nothing).
		// Both halves of the auth contract carry the role (issue #64/#93:
		// `POST /auth/login` returns `{token, role, expires_at}` and identity
		// comes from `GET /auth/me`), so both have to be downgraded or the
		// session re-reads Admin from the one that was left alone.
		for (const path of ["**/api/v1/auth/login", "**/api/v1/auth/me"]) {
			await page.route(path, async (route) => {
				const response = await route.fetch();
				const body = await response.json();
				body.role = "Viewer";
				await route.fulfill({ response, body: JSON.stringify(body) });
			});
		}
		await signIn(page);
		await page.waitForSelector("text=WAYPOINT");
		await page.goto(BASE + "/config");
		await page.waitForTimeout(DELAY_MS / 2);
		const midFlight = await probe(page);
		await page.waitForTimeout(DELAY_MS + 500);
		return { midFlight, settled: await probe(page) };
	});
}

/** Finding 2: /system HANGS (accepted, never answered). Samples the page at
 * 1s / 3.5s / 9.5s -- the timings the reviewer measured -- then once past the
 * frontend's own 8s bound. `bodyTextLen: 0` is the blank page. */
async function hangingSystem() {
	return withPage(async (page) => {
		await page.route("**/api/v1/system", async () => {
			await new Promise(() => {}); // accepted, never answered
		});
		await signIn(page);
		await page.waitForTimeout(1500); // let the login round trip land
		await page.goto(BASE + "/catalog");
		const samples = {};
		const marks = [1000, 3500, 9500, 12000];
		let elapsed = 0;
		for (const m of marks) {
			await page.waitForTimeout(m - elapsed);
			elapsed = m;
			samples["t" + m] = await probe(page);
		}
		return samples;
	});
}

/**
 * POSITIVE CONTROL for the latch itself. Every other scenario's headline
 * result is `catalogEverMounted: false` / `configEverMounted: false` — an
 * assertion that is equally satisfied by a correct guard and by a broken
 * observer. This scenario adds a marker node and removes it again inside the
 * SAME synchronous task, then samples after it is gone from the DOM. It must
 * report `true`.
 *
 * The round-1 version of this harness had an empty observer callback and the
 * round-2 version re-read the live DOM, both of which report `false` here.
 * Run this first: if it does not say `true`, nothing the other scenarios claim
 * about never-mounting means anything.
 */
async function latchPositiveControl() {
	return withPage(async (page) => {
		await signIn(page);
		await page.waitForSelector("text=WAYPOINT");
		return page.evaluate(async () => {
			window.__latchControl("GET /api/v1/catalog/artifacts");
			const inDomAfterwards = document.body.innerText.includes("GET /api/v1/catalog/artifacts");
			// MutationObserver callbacks are delivered at the microtask
			// checkpoint, so give them one before reading the latch.
			await new Promise((r) => setTimeout(r, 50));
			return {
				markerInDomAfterwards: inDomAfterwards,
				catalogEverMounted: window.__seen.catalog,
				note: "expected: markerInDomAfterwards false, catalogEverMounted true",
			};
		});
	});
}

/**
 * PR #88 round-2 review, finding 1: `/system` answers with a **200 and
 * headers**, then its body never completes. `fetch` resolves as soon as
 * headers arrive, so a deadline cleared at that moment leaves
 * `response.json()` unbounded — and the page is blank forever, exactly as in
 * the round-1 finding. The reviewer measured `bodyTextLen: 0` at 25 s, three
 * times past the 8 s bound.
 *
 * This is NOT mocked, and deliberately not a `page.route` fulfilment —
 * Playwright buffers a fulfilled body, so it cannot express "headers arrived,
 * body never will". Instead the harness stands up a real `node:http` FRONT
 * DOOR on its own port and points the browser at that: every request is
 * reverse-proxied to `BASE` untouched, except `/api/v1/system`, for which the
 * front door writes the status line, the headers and a first body chunk,
 * flushes them, and then never writes again. Chromium therefore sees a
 * genuine same-origin 200 whose response stream is stuck — no CORS in play, no
 * interception layer, nothing synthesised.
 */
async function stallingSystemBody() {
	const { createServer, request: httpRequest } = await import("node:http");
	const upstream = new URL(BASE);
	const sockets = new Set();

	const server = createServer((req, res) => {
		if (req.url.startsWith("/api/v1/system")) {
			res.writeHead(200, { "Content-Type": "application/json", "Cache-Control": "no-store" });
			res.write('{"version":"0.1.0-dev",'); // ... and nothing more, ever.
			return;
		}
		const proxied = httpRequest(
			{
				hostname: upstream.hostname,
				port: upstream.port,
				path: req.url,
				method: req.method,
				headers: { ...req.headers, host: upstream.host },
				// No keep-alive pooling: a pooled socket to the upstream
				// outlives the scenario and keeps the event loop (and so the
				// whole harness) alive after the last sample.
				agent: false,
			},
			(up) => {
				res.writeHead(up.statusCode ?? 502, up.headers);
				up.pipe(res);
			},
		);
		proxied.on("error", () => {
			if (!res.headersSent) res.writeHead(502);
			res.end();
		});
		req.pipe(proxied);
	});
	server.on("connection", (socket) => {
		sockets.add(socket);
		socket.on("close", () => sockets.delete(socket));
	});
	await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
	const front = `http://127.0.0.1:${server.address().port}`;

	try {
		return await withPage(async (page) => {
			await page.goto(front + "/");
			await page.getByLabel("Username").fill("admin");
			await page.getByLabel("Password").fill("waypoint-dev");
			await page.getByRole("button", { name: /sign in/i }).click();
			await page.waitForTimeout(1500);
			await page.goto(front + "/catalog");
			const samples = { frontDoor: front, upstream: BASE };
			const marks = [1000, 3500, 9500, 15000, 25000];
			let elapsed = 0;
			for (const m of marks) {
				await page.waitForTimeout(m - elapsed);
				elapsed = m;
				samples["t" + m] = await probe(page);
			}
			return samples;
		});
	} finally {
		for (const socket of sockets) socket.destroy();
		server.close();
	}
}

const scenarios = {
	"0 latch positive control": latchPositiveControl,
	"1 connected deep link (#82)": delayedConnectedDeepLink,
	"2 disconnected deep link (#82/#78)": delayedDisconnectedDeepLink,
	"3 Viewer role deep link (#78)": viewerRoleDeepLink,
	"4 /system HANGS (round-1 finding 2)": hangingSystem,
	"5 /system 200 then BODY STALLS (round-2 finding 1)": stallingSystemBody,
};

const only = process.env.ONLY;
for (const [name, fn] of Object.entries(scenarios)) {
	if (only && !name.startsWith(only)) continue;
	console.log(`\n--- ${BASE}  ${name}`);
	console.log(JSON.stringify(await fn(), null, 1));
}

// Scenario 5 deliberately leaves an HTTP response that is never ended — that
// is the condition under test — and the sockets behind it keep Node's event
// loop alive even after the server is closed. Every result is already printed
// by this point, so exit explicitly rather than leaving a reviewer staring at
// a prompt that never returns.
process.exit(0);
