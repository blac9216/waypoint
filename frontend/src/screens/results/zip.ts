/**
 * Minimal client-side ZIP writer for the CKL bundle export (issue #27 AC3:
 * "Export produces a zip of the run's CKLs"). STORED method only (no
 * compression) — CKL files are already-small XML, so the compression ratio
 * isn't worth the code, and STORED keeps this to local file headers + a
 * central directory + CRC32, no deflate implementation needed.
 *
 * Air-gap rule (CLAUDE.md "Key Constraints"): no CDN assets, and the repo
 * had no zip-capable dependency in `frontend/package.json` or its lock file
 * (direct or transitive — checked before writing this) when this landed.
 * Adding a new npm dependency for ~100 LOC of well-understood, stable format
 * (the ZIP local/central-directory layout hasn't changed since the 1990s)
 * was judged higher-maintenance than hand-rolling it: one more
 * `npm audit`/license-review/transitive-dependency surface for a repo that
 * already ships zero non-React runtime dependencies, for a feature this
 * narrow. Revisit if a future screen needs actual compression or nested-zip
 * features this module doesn't cover.
 *
 * Not a general-purpose zip library: no streaming, no Zip64, no compression,
 * no directory entries. Sufficient for "N small XML files, one flat bundle."
 */

export interface ZipEntry {
	/** Forward-slash path inside the archive, e.g. "esxi-01.example.internal.ckl". */
	name: string;
	data: Uint8Array;
}

const CRC_TABLE = buildCrcTable();

function buildCrcTable(): Uint32Array {
	const table = new Uint32Array(256);
	for (let n = 0; n < 256; n++) {
		let c = n;
		for (let k = 0; k < 8; k++) {
			c = c & 1 ? (0xedb88320 ^ (c >>> 1)) : c >>> 1;
		}
		table[n] = c >>> 0;
	}
	return table;
}

function crc32(data: Uint8Array): number {
	let crc = 0xffffffff;
	for (let i = 0; i < data.length; i++) {
		crc = CRC_TABLE[(crc ^ data[i]) & 0xff] ^ (crc >>> 8);
	}
	return (crc ^ 0xffffffff) >>> 0;
}

/** MS-DOS date/time packed fields, fixed to a stable value rather than
 * `Date.now()` — the bundle is a derived artifact keyed by its contents, and
 * reproducible output makes hash-comparing two exports meaningful. */
const DOS_TIME = 0;
const DOS_DATE = (1 << 9) | (1 << 5) | 1; // 2026-01-01, the epoch DOS_DATE=0 would predate

function writeUint32LE(view: DataView, offset: number, value: number): void {
	view.setUint32(offset, value, true);
}

function writeUint16LE(view: DataView, offset: number, value: number): void {
	view.setUint16(offset, value, true);
}

/**
 * Builds a STORED-method ZIP archive from `entries` and returns it as a
 * `Blob` (`application/zip`), ready for `URL.createObjectURL`. Duplicate
 * names are not de-duplicated — callers (see `results.ts` artifact naming)
 * are responsible for unique names, the same responsibility any zip tool
 * places on its caller.
 */
export function buildZip(entries: ZipEntry[]): Blob {
	const encoder = new TextEncoder();
	const encodedNames = entries.map((e) => encoder.encode(e.name));
	const crcs = entries.map((e) => crc32(e.data));

	let localSize = 0;
	for (let i = 0; i < entries.length; i++) {
		localSize += 30 + encodedNames[i].length + entries[i].data.length;
	}
	let centralSize = 0;
	for (let i = 0; i < entries.length; i++) {
		centralSize += 46 + encodedNames[i].length;
	}
	const endSize = 22;

	const buffer = new ArrayBuffer(localSize + centralSize + endSize);
	const view = new DataView(buffer);
	const bytes = new Uint8Array(buffer);

	const localOffsets: number[] = [];
	let cursor = 0;
	for (let i = 0; i < entries.length; i++) {
		localOffsets.push(cursor);
		const name = encodedNames[i];
		const data = entries[i].data;
		const crc = crcs[i];

		writeUint32LE(view, cursor, 0x04034b50); // local file header signature
		writeUint16LE(view, cursor + 4, 20); // version needed
		writeUint16LE(view, cursor + 6, 0); // flags
		writeUint16LE(view, cursor + 8, 0); // method: STORED
		writeUint16LE(view, cursor + 10, DOS_TIME);
		writeUint16LE(view, cursor + 12, DOS_DATE);
		writeUint32LE(view, cursor + 14, crc);
		writeUint32LE(view, cursor + 18, data.length); // compressed size
		writeUint32LE(view, cursor + 22, data.length); // uncompressed size
		writeUint16LE(view, cursor + 26, name.length);
		writeUint16LE(view, cursor + 28, 0); // extra field length
		bytes.set(name, cursor + 30);
		bytes.set(data, cursor + 30 + name.length);
		cursor += 30 + name.length + data.length;
	}

	const centralStart = cursor;
	for (let i = 0; i < entries.length; i++) {
		const name = encodedNames[i];
		const crc = crcs[i];
		const data = entries[i].data;

		writeUint32LE(view, cursor, 0x02014b50); // central directory header signature
		writeUint16LE(view, cursor + 4, 20); // version made by
		writeUint16LE(view, cursor + 6, 20); // version needed
		writeUint16LE(view, cursor + 8, 0); // flags
		writeUint16LE(view, cursor + 10, 0); // method: STORED
		writeUint16LE(view, cursor + 12, DOS_TIME);
		writeUint16LE(view, cursor + 14, DOS_DATE);
		writeUint32LE(view, cursor + 16, crc);
		writeUint32LE(view, cursor + 20, data.length);
		writeUint32LE(view, cursor + 24, data.length);
		writeUint16LE(view, cursor + 28, name.length);
		writeUint16LE(view, cursor + 30, 0); // extra field length
		writeUint16LE(view, cursor + 32, 0); // comment length
		writeUint16LE(view, cursor + 34, 0); // disk number start
		writeUint16LE(view, cursor + 36, 0); // internal attributes
		writeUint32LE(view, cursor + 38, 0); // external attributes
		writeUint32LE(view, cursor + 42, localOffsets[i]);
		bytes.set(name, cursor + 46);
		cursor += 46 + name.length;
	}
	const centralDirectoryLength = cursor - centralStart;

	writeUint32LE(view, cursor, 0x06054b50); // end of central directory signature
	writeUint16LE(view, cursor + 4, 0); // disk number
	writeUint16LE(view, cursor + 6, 0); // disk with central directory
	writeUint16LE(view, cursor + 8, entries.length); // entries on this disk
	writeUint16LE(view, cursor + 10, entries.length); // total entries
	writeUint32LE(view, cursor + 12, centralDirectoryLength);
	writeUint32LE(view, cursor + 16, centralStart);
	writeUint16LE(view, cursor + 20, 0); // comment length

	return new Blob([bytes], { type: "application/zip" });
}
