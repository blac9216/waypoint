import { describe, expect, it } from "vitest";
import { decodeJwtPayload } from "./jwt";

/**
 * `decodeJwtPayload` (issue #534) reads the display/step-up claims out of the
 * access token's middle segment. It deliberately does not verify the
 * signature (the backend re-validates every request against Keycloak's JWKS),
 * so the header and signature segments are irrelevant here — only the base64url
 * middle segment is decoded. Every token below is an invented fixture: the
 * header is a fixed placeholder and the signature is a literal `sig`, so
 * nothing here resembles a real credential (CLAUDE.md sanitization).
 */

const FAKE_HEADER = "eyJhbGciOiJub25lIn0"; // {"alg":"none"} — placeholder, never inspected
const FAKE_SIG = "sig"; // never verified

/** base64url-encode a JSON object into a token payload segment. */
function encodePayload(payload: unknown): string {
	const json = JSON.stringify(payload);
	// Encode UTF-8 → base64 → base64url (mirror of the decode in jwt.ts).
	const utf8 = encodeURIComponent(json).replace(/%([0-9A-F]{2})/g, (_m, hex: string) =>
		String.fromCharCode(parseInt(hex, 16)),
	);
	return btoa(utf8).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

/** Assemble a three-segment token around an already-encoded payload segment. */
function tokenWithPayloadSegment(segment: string): string {
	return `${FAKE_HEADER}.${segment}.${FAKE_SIG}`;
}

function tokenFor(payload: unknown): string {
	return tokenWithPayloadSegment(encodePayload(payload));
}

describe("decodeJwtPayload (issue #534)", () => {
	it("decodes a well-formed payload into its claims object", () => {
		const claims = decodeJwtPayload(
			tokenFor({ role: "Admin", preferred_username: "opuser", sub: "abc-123", exp: 1893456000 }),
		);
		expect(claims).toEqual({ role: "Admin", preferred_username: "opuser", sub: "abc-123", exp: 1893456000 });
	});

	it("decodes a payload whose base64url encoding uses the URL-safe `-`/`_` alphabet", () => {
		// Choose a payload whose base64 contains both `+` and `/` in standard
		// alphabet so the `-`/`_` substitution path is actually exercised. The
		// bytes 0xFB (…111111 11) reliably produce `+`/`/` in standard base64.
		const value = "ûÿþý"; // multi-byte UTF-8, forces `+`/`/`
		const payload = { note: value };
		const segment = encodePayload(payload);
		// Guard: the fixture is only meaningful if the segment really carries
		// URL-safe substitutions (otherwise this test would pass trivially).
		expect(segment).toMatch(/[-_]/);
		expect(decodeJwtPayload(tokenWithPayloadSegment(segment))).toEqual(payload);
	});

	it("returns null for a token that does not have exactly three segments", () => {
		expect(decodeJwtPayload("only.two")).toBeNull();
		expect(decodeJwtPayload("a.b.c.d")).toBeNull();
		expect(decodeJwtPayload("nodots")).toBeNull();
		expect(decodeJwtPayload("")).toBeNull();
	});

	it("returns null when the payload segment is not valid base64/JSON", () => {
		// `@@@@` is not decodable base64url, so atob throws and the guard returns
		// null rather than propagating.
		expect(decodeJwtPayload(tokenWithPayloadSegment("@@@@"))).toBeNull();
	});

	it("returns null when the decoded payload is valid base64 but not JSON", () => {
		// "not json at all" base64url-encoded — decodes cleanly, then JSON.parse throws.
		const segment = btoa("not json at all").replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
		expect(decodeJwtPayload(tokenWithPayloadSegment(segment))).toBeNull();
	});

	it.each([
		["a JSON string", '"just-a-string"'],
		["a JSON number", "42"],
		["a JSON boolean", "true"],
		["JSON null", "null"],
	])("returns null when the payload is %s (not an object)", (_label, rawJson) => {
		const segment = btoa(rawJson).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
		expect(decodeJwtPayload(tokenWithPayloadSegment(segment))).toBeNull();
	});

	it("returns the array as-is for a JSON array payload (arrays are objects in JS)", () => {
		// typeof [] === "object" and [] !== null, so the guard admits it — this
		// pins the actual documented behavior rather than an assumed one.
		const segment = btoa("[1,2,3]").replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
		expect(decodeJwtPayload(tokenWithPayloadSegment(segment))).toEqual([1, 2, 3]);
	});
});
