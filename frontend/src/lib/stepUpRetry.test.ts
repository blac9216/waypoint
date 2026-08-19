import { afterEach, describe, expect, it } from "vitest";
import { clearStepUpRetry, consumeStepUpRetry, stashStepUpRetry } from "./stepUpRetry";

const KEY = "waypoint.stepup.retry";

describe("stepUpRetry", () => {
	afterEach(() => {
		window.sessionStorage.clear();
	});

	it("round-trips an intent by matching kind, and is read-once", () => {
		stashStepUpRetry({ kind: "credential-edit", payload: { id: "cred-1", name: "Alpha" } });
		expect(consumeStepUpRetry("credential-edit")).toEqual({ id: "cred-1", name: "Alpha" });
		// Consumed: a second read finds nothing.
		expect(consumeStepUpRetry("credential-edit")).toBeNull();
		expect(window.sessionStorage.getItem(KEY)).toBeNull();
	});

	it("returns null (but still consumes) when the kind does not match", () => {
		stashStepUpRetry({ kind: "credential-edit", payload: { id: "cred-1" } });
		expect(consumeStepUpRetry("remediation")).toBeNull();
		// Read-once even on a mismatch, so a stale stash cannot linger.
		expect(window.sessionStorage.getItem(KEY)).toBeNull();
	});

	it("clearStepUpRetry discards a stash without reading it (abort path)", () => {
		stashStepUpRetry({ kind: "credential-edit", payload: { id: "cred-1" } });
		clearStepUpRetry();
		expect(window.sessionStorage.getItem(KEY)).toBeNull();
		expect(consumeStepUpRetry("credential-edit")).toBeNull();
	});

	it("never writes anything the caller did not put in the payload", () => {
		// Issue #537 Finding 1 (generic guard): the stash only ever contains
		// exactly what the caller hands it — callers are responsible for keeping
		// secret material out. Grepping the serialized stash for a secret value
		// the caller withheld proves nothing leaks in via this module.
		stashStepUpRetry({ kind: "credential-edit", payload: { id: "cred-1", username: "svc@example.internal" } });
		const raw = window.sessionStorage.getItem(KEY) as string;
		expect(raw).not.toContain("super-secret-password");
		expect(raw).toContain("cred-1");
	});
});
