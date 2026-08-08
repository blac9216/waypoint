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
});
