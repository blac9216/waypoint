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
		const sample = () => {
			const text = document.body ? document.body.innerText : "";
			for (const [key, marker] of Object.entries(MARKERS)) {
				if (text.includes(marker)) {
					seen[key] = true;
				}
			}
		};
		new MutationObserver(sample).observe(document, { childList: true, subtree: true, characterData: true });
		sample();
		window.__seen = seen;
		window.__bodyTextLen = () => (document.body ? document.body.innerText.length : 0);
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
		await page.route("**/api/v1/auth/login", async (route) => {
			const response = await route.fetch();
			const body = await response.json();
			body.user.role = "Viewer";
			await route.fulfill({ response, body: JSON.stringify(body) });
		});
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

const scenarios = {
	"1 connected deep link (#82)": delayedConnectedDeepLink,
	"2 disconnected deep link (#82/#78)": delayedDisconnectedDeepLink,
	"3 Viewer role deep link (#78)": viewerRoleDeepLink,
	"4 /system HANGS (finding 2)": hangingSystem,
};

const only = process.env.ONLY;
for (const [name, fn] of Object.entries(scenarios)) {
	if (only && !name.startsWith(only)) continue;
	console.log(`\n--- ${BASE}  ${name}`);
	console.log(JSON.stringify(await fn(), null, 1));
}
