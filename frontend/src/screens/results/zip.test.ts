import { describe, expect, it } from "vitest";
import { buildZip } from "./zip";

async function toBytes(blob: Blob): Promise<Uint8Array> {
	return new Uint8Array(await blob.arrayBuffer());
}

function readUint32LE(bytes: Uint8Array, offset: number): number {
	return bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24);
}

function readUint16LE(bytes: Uint8Array, offset: number): number {
	return bytes[offset] | (bytes[offset + 1] << 8);
}

describe("buildZip", () => {
	it("produces a blob with the ZIP MIME type", () => {
		const blob = buildZip([{ name: "a.ckl", data: new TextEncoder().encode("<CHECKLIST/>") }]);
		expect(blob.type).toBe("application/zip");
	});

	it("writes a valid local file header signature and STORED method for each entry", async () => {
		const entries = [
			{ name: "esxi-01.example.internal.ckl", data: new TextEncoder().encode("<CHECKLIST>one</CHECKLIST>") },
			{ name: "esxi-02.example.internal.ckl", data: new TextEncoder().encode("<CHECKLIST>two</CHECKLIST>") },
		];
		const bytes = await toBytes(buildZip(entries));

		let cursor = 0;
		for (const entry of entries) {
			expect(readUint32LE(bytes, cursor)).toBe(0x04034b50); // local file header signature
			const method = readUint16LE(bytes, cursor + 8);
			expect(method).toBe(0); // STORED, no compression
			const compressedSize = readUint32LE(bytes, cursor + 18);
			const uncompressedSize = readUint32LE(bytes, cursor + 22);
			expect(compressedSize).toBe(entry.data.length);
			expect(uncompressedSize).toBe(entry.data.length);
			const nameLength = readUint16LE(bytes, cursor + 26);
			expect(nameLength).toBe(entry.name.length);
			const nameBytes = bytes.slice(cursor + 30, cursor + 30 + nameLength);
			expect(new TextDecoder().decode(nameBytes)).toBe(entry.name);
			const dataBytes = bytes.slice(cursor + 30 + nameLength, cursor + 30 + nameLength + entry.data.length);
			expect(Array.from(dataBytes)).toEqual(Array.from(entry.data));
			cursor += 30 + nameLength + entry.data.length;
		}
	});

	it("terminates with a valid end-of-central-directory record naming every entry", async () => {
		const entries = [
			{ name: "one.ckl", data: new TextEncoder().encode("x") },
			{ name: "two.ckl", data: new TextEncoder().encode("yy") },
			{ name: "three.ckl", data: new TextEncoder().encode("zzz") },
		];
		const bytes = await toBytes(buildZip(entries));

		// Locate the EOCD by its signature (fixed 22-byte record, no comment
		// written by this module, so it is always the last 22 bytes).
		const eocdOffset = bytes.length - 22;
		expect(readUint32LE(bytes, eocdOffset)).toBe(0x06054b50);
		const totalEntries = readUint16LE(bytes, eocdOffset + 10);
		expect(totalEntries).toBe(entries.length);

		const centralDirSize = readUint32LE(bytes, eocdOffset + 12);
		const centralDirOffset = readUint32LE(bytes, eocdOffset + 16);
		expect(centralDirOffset + centralDirSize).toBe(eocdOffset);

		let cursor = centralDirOffset;
		for (const entry of entries) {
			expect(readUint32LE(bytes, cursor)).toBe(0x02014b50); // central directory header signature
			const nameLength = readUint16LE(bytes, cursor + 28);
			expect(nameLength).toBe(entry.name.length);
			const nameBytes = bytes.slice(cursor + 46, cursor + 46 + nameLength);
			expect(new TextDecoder().decode(nameBytes)).toBe(entry.name);
			cursor += 46 + nameLength;
		}
	});

	it("produces distinct CRCs for entries with different contents", async () => {
		const bytes = await toBytes(
			buildZip([
				{ name: "a.ckl", data: new TextEncoder().encode("alpha") },
				{ name: "b.ckl", data: new TextEncoder().encode("bravo") },
			]),
		);
		const crcA = readUint32LE(bytes, 14);
		const secondEntryOffset = 30 + "a.ckl".length + "alpha".length;
		const crcB = readUint32LE(bytes, secondEntryOffset + 14);
		expect(crcA).not.toBe(crcB);
		expect(crcA).not.toBe(0);
		expect(crcB).not.toBe(0);
	});

	it("handles an empty entry list", async () => {
		const bytes = await toBytes(buildZip([]));
		expect(bytes.length).toBe(22);
		expect(readUint32LE(bytes, 0)).toBe(0x06054b50);
		expect(readUint16LE(bytes, 10)).toBe(0);
	});

	describe("4GB overflow guard (no Zip64 support)", () => {
		it("throws a clear error when a single entry's declared size exceeds the 32-bit field", () => {
			// Constructing a real >4GB Uint8Array would blow up test memory/time;
			// instead fake a `data.length` past the limit without allocating the
			// backing bytes, since buildZip only reads `.length` and iterates
			// `data` during the byte-copy step, which we never reach — the range
			// check on `data.length` throws first.
			const oversized: Uint8Array = { length: 0x100000000 } as unknown as Uint8Array;
			expect(() => buildZip([{ name: "huge.ckl", data: oversized }])).toThrow(
				/ZIP entry 'huge\.ckl' size exceeds the 4GB STORED-zip limit/,
			);
		});

		it("throws a clear error when more than 65535 entries are supplied", () => {
			const manyEntries = Array.from({ length: 0x10000 }, (_, i) => ({
				name: `f${i}.ckl`,
				data: new Uint8Array(0),
			}));
			expect(() => buildZip(manyEntries)).toThrow(/ZIP entry count exceeds the 65535 entries/);
		});

		it("does not throw for entries comfortably under the 4GB limit", () => {
			expect(() =>
				buildZip([{ name: "small.ckl", data: new TextEncoder().encode("<CHECKLIST/>") }]),
			).not.toThrow();
		});
	});

	describe("UTF-8 filename flag (general-purpose bit 11)", () => {
		it("does not set the UTF-8 flag for an ASCII-only filename", async () => {
			const bytes = await toBytes(
				buildZip([{ name: "esxi-01.example.internal.ckl", data: new TextEncoder().encode("x") }]),
			);
			const localFlags = readUint16LE(bytes, 6);
			expect(localFlags & 0x0800).toBe(0);
		});

		it("sets the UTF-8 flag (bit 11) in the local file header for a non-ASCII filename", async () => {
			const bytes = await toBytes(
				buildZip([{ name: "ésxi-01.ckl", data: new TextEncoder().encode("x") }]), // "ésxi-01.ckl"
			);
			const localFlags = readUint16LE(bytes, 6);
			expect(localFlags & 0x0800).toBe(0x0800);
		});

		it("sets the UTF-8 flag (bit 11) in the central directory entry for a non-ASCII filename", async () => {
			const name = "ésxi-01.ckl";
			const data = new TextEncoder().encode("x");
			const bytes = await toBytes(buildZip([{ name, data }]));

			const nameLength = new TextEncoder().encode(name).length;
			const centralStart = 30 + nameLength + data.length;
			const centralFlags = readUint16LE(bytes, centralStart + 8);
			expect(centralFlags & 0x0800).toBe(0x0800);
		});

		it("round-trips a non-ASCII filename as UTF-8 bytes in both headers", async () => {
			const name = "ésxi-01.ckl"; // "ésxi-01.ckl"
			const data = new TextEncoder().encode("<CHECKLIST/>");
			const bytes = await toBytes(buildZip([{ name, data }]));
			const encodedName = new TextEncoder().encode(name);

			// Local header: name length is a UTF-8 byte count, not a JS string length.
			const localNameLength = readUint16LE(bytes, 26);
			expect(localNameLength).toBe(encodedName.length);
			const localNameBytes = bytes.slice(30, 30 + localNameLength);
			expect(new TextDecoder("utf-8").decode(localNameBytes)).toBe(name);

			// Central directory: same name, same length, immediately after the
			// local header + data (only entry in the archive).
			const centralStart = 30 + localNameLength + data.length;
			const centralNameLength = readUint16LE(bytes, centralStart + 28);
			expect(centralNameLength).toBe(encodedName.length);
			const centralNameBytes = bytes.slice(centralStart + 46, centralStart + 46 + centralNameLength);
			expect(new TextDecoder("utf-8").decode(centralNameBytes)).toBe(name);
		});
	});
});
