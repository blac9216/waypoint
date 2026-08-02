import type { Connect, Plugin } from "vite";
import type { IncomingMessage, ServerResponse } from "node:http";

/**
 * Dev-only mock of the M1 backend's local-auth + system + event-stream
 * surface. NOT part of the production bundle — this is a Vite dev-server
 * middleware (`configureServer`), which only runs under `vite dev`; it
 * contributes nothing to `vite build` output, so it cannot trip the
 * zero-external-assets check and ships nothing to the appliance image.
 *
 * Why this exists: the real ASP.NET Core backend (issue #3) has landed in
 * `backend/` and the compose stack's `backend` service builds it directly —
 * but `npm run dev` runs the Vite dev server standalone, with no proxy to
 * that (or any) backend container. This mock is what "develop against a
 * local mock" (issue #11's brief) means in practice: it's the thing that
 * makes login, the STIG Manager pill, the mode badge, and the job log
 * drawer's live stream demonstrable today, in the shape the documented
 * contract (`docs/api-contract.md`'s Auth section) describes — `POST
 * /auth/login` returns `{token, role, expires_at}` (no `user` object) and
 * `GET /auth/me` returns `{username, role}`, matching the backend's
 * `Contracts/AuthContracts.cs` field-for-field.
 *
 * DEV_USERNAME/DEV_PASSWORD are throwaway, fictional dev-loop credentials —
 * not a real secret, not lab data (CLAUDE.md sanitization policy).
 */
const DEV_USERNAME = "admin";
const DEV_PASSWORD = "waypoint-dev";
const DEV_TOKEN = "dev-mock-token";
const DEV_ROLE = "Admin";

function json(res: ServerResponse, status: number, body: unknown): void {
	res.statusCode = status;
	res.setHeader("Content-Type", "application/json");
	res.end(JSON.stringify(body));
}

function errorEnvelope(code: string, message: string) {
	return { error: { code, message } };
}

async function readJsonBody(req: IncomingMessage): Promise<Record<string, unknown>> {
	const chunks: Buffer[] = [];
	for await (const chunk of req) {
		chunks.push(chunk as Buffer);
	}
	if (chunks.length === 0) {
		return {};
	}
	try {
		return JSON.parse(Buffer.concat(chunks).toString("utf-8"));
	} catch {
		return {};
	}
}

function isAuthorized(req: IncomingMessage): boolean {
	const header = req.headers.authorization;
	return header === `Bearer ${DEV_TOKEN}`;
}

const SYSTEM_INFO = {
	version: "0.1.0-dev",
	build: "local",
	mode: "connected" as const,
	update_available: null as string | null,
};

const STIGMAN_STATUS = {
	connected: true,
	// Fictional per CLAUDE.md sanitization policy (RFC 2606 reserved domain).
	endpoint: "stigman.example.internal",
	collection: "17",
};

let eventSeq = 0;
const JOB_TARGETS = ["vcsa-01.example.internal", "esxi-02.example.internal", "esxi-03.example.internal"];

function nextEvent(): { seq: number; ts: string; type: string; job_id?: string; data: unknown } {
	eventSeq += 1;
	const target = JOB_TARGETS[eventSeq % JOB_TARGETS.length];
	const kind = eventSeq % 4;
	const ts = new Date().toISOString();
	if (kind === 0) {
		return {
			seq: eventSeq,
			ts,
			type: "job.state",
			job_id: `j-mock-${eventSeq % JOB_TARGETS.length}`,
			data: { target, from: "running", to: "attesting" },
		};
	}
	if (kind === 1) {
		return {
			seq: eventSeq,
			ts,
			type: "job.log",
			job_id: `j-mock-${eventSeq % JOB_TARGETS.length}`,
			data: { level: "info", line: `${target}: inspec exec — control batch ${eventSeq} of 171` },
		};
	}
	if (kind === 2) {
		return { seq: eventSeq, ts, type: "run.progress", data: { percent: (eventSeq * 3) % 100 } };
	}
	return { seq: eventSeq, ts, type: "system.notice", data: { level: "ok", message: `mock heartbeat #${eventSeq}` } };
}

export function mockBackendPlugin(): Plugin {
	return {
		name: "waypoint-mock-backend",
		apply: "serve", // dev-server only — never runs during `vite build`.
		configureServer(server) {
			const handler: Connect.NextHandleFunction = async (req, res, next) => {
				const url = req.url ?? "";

				if (req.method === "POST" && url === "/api/v1/auth/login") {
					const body = await readJsonBody(req);
					if (body.username === DEV_USERNAME && body.password === DEV_PASSWORD) {
						// Matches `LoginResponse` (AuthContracts.cs): token + role +
						// expires_at, no `user` object. 8h lifetime mirrors
						// `LocalAuthOptions.SessionLifetime`'s default.
						const expiresAt = new Date(Date.now() + 8 * 60 * 60 * 1000).toISOString();
						json(res, 200, { token: DEV_TOKEN, role: DEV_ROLE, expires_at: expiresAt });
					} else {
						json(res, 401, errorEnvelope("invalid_credentials", "Invalid username or password."));
					}
					return;
				}

				if (req.method === "GET" && url === "/api/v1/auth/me") {
					if (!isAuthorized(req)) {
						json(res, 401, errorEnvelope("unauthorized", "Missing or invalid bearer token."));
						return;
					}
					// Matches `CurrentUserResponse` (AuthContracts.cs).
					json(res, 200, { username: DEV_USERNAME, role: DEV_ROLE });
					return;
				}

				if (req.method === "GET" && url === "/api/v1/system") {
					if (!isAuthorized(req)) {
						json(res, 401, errorEnvelope("unauthorized", "Missing or invalid bearer token."));
						return;
					}
					json(res, 200, SYSTEM_INFO);
					return;
				}

				if (req.method === "GET" && url === "/api/v1/stigman") {
					if (!isAuthorized(req)) {
						json(res, 401, errorEnvelope("unauthorized", "Missing or invalid bearer token."));
						return;
					}
					json(res, 200, STIGMAN_STATUS);
					return;
				}

				if (req.method === "GET" && url === "/api/v1/events") {
					if (!isAuthorized(req)) {
						json(res, 401, errorEnvelope("unauthorized", "Missing or invalid bearer token."));
						return;
					}
					res.statusCode = 200;
					res.setHeader("Content-Type", "text/event-stream");
					res.setHeader("Cache-Control", "no-cache");
					res.setHeader("Connection", "keep-alive");
					res.flushHeaders?.();

					const interval = setInterval(() => {
						const event = nextEvent();
						res.write(`id: ${event.seq}\n`);
						res.write(`data: ${JSON.stringify(event)}\n\n`);
					}, 1500);

					req.on("close", () => clearInterval(interval));
					return;
				}

				next();
			};

			server.middlewares.use(handler);
		},
	};
}
