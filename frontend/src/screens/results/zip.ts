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

/** General-purpose bit flag 11 (APPNOTE 6.3.2 §4.4.4): filename and comment
 * fields are UTF-8. Set whenever an entry's name is written as UTF-8 bytes,
 * so readers know not to fall back to legacy CP437 decoding. */
const UTF8_FLAG = 0x0800;

/** Max value of a 32-bit ZIP field (size, offset). This writer has no Zip64
 * support, so anything that would need more than 32 bits must fail loudly
 * rather than silently wrap and produce a corrupt archive. */
const MAX_UINT32 = 0xffffffff;

/** Max value of the 16-bit ZIP entry-count fields in the classic EOCD record. */
const MAX_UINT16 = 0xffff;

/**
 * Throws if `value` cannot be represented in the given ZIP field width. This
 * writer has no Zip64 extension, so a value that overflows must abort the
 * export rather than silently truncate/wrap into a corrupt archive — a
 * corrupt bundle discovered only at an air-gapped enclave is the worst
 * failure mode for this feature.
 */
function assertFits(value: number, max: number, description: string): void {
	if (value > max) {
		const limit = max === MAX_UINT16 ? "65535 entries" : "4GB STORED-zip limit (Zip64 not supported)";
		throw new Error(`ZIP ${description} exceeds the ${limit}`);
	}
}

function writeUint32LE(view: DataView, offset: number, value: number, description: string): void {
	assertFits(value, MAX_UINT32, description);
	view.setUint32(offset, value, true);
}

function writeUint16LE(view: DataView, offset: number, value: number): void {
	view.setUint16(offset, value, true);
}

/** Returns true if `name` contains any byte outside the 7-bit ASCII range
 * once encoded as UTF-8 — i.e. whether the UTF-8 filename flag is needed. */
function isNonAscii(encodedName: Uint8Array): boolean {
	for (let i = 0; i < encodedName.length; i++) {
		if (encodedName[i] > 0x7f) return true;
	}
	return false;
}

/**
 * Builds a STORED-method ZIP archive from `entries` and returns it as a
 * `Blob` (`application/zip`), ready for `URL.createObjectURL`. Duplicate
 * names are not de-duplicated — callers (see `results.ts` artifact naming)
 * are responsible for unique names, the same responsibility any zip tool
 * places on its caller.
 */
export function buildZip(entries: ZipEntry[]): Blob {
	assertFits(entries.length, MAX_UINT16, "entry count");

	for (let i = 0; i < entries.length; i++) {
		assertFits(entries[i].data.length, MAX_UINT32, `entry '${entries[i].name}' size`);
	}

	const encoder = new TextEncoder();
	const encodedNames = entries.map((e) => encoder.encode(e.name));
	const crcs = entries.map((e) => crc32(e.data));
	const flags = encodedNames.map((name) => (isNonAscii(name) ? UTF8_FLAG : 0));

	let localSize = 0;
	for (let i = 0; i < entries.length; i++) {
		localSize += 30 + encodedNames[i].length + entries[i].data.length;
	}
	let centralSize = 0;
	for (let i = 0; i < entries.length; i++) {
		centralSize += 46 + encodedNames[i].length;
	}
	const endSize = 22;

	const totalSize = localSize + centralSize + endSize;
	assertFits(totalSize, MAX_UINT32, "total archive size");

	const buffer = new ArrayBuffer(totalSize);
	const view = new DataView(buffer);
	const bytes = new Uint8Array(buffer);

	const localOffsets: number[] = [];
	let cursor = 0;
	for (let i = 0; i < entries.length; i++) {
		assertFits(cursor, MAX_UINT32, `entry '${entries[i].name}' local header offset`);
		localOffsets.push(cursor);
		const name = encodedNames[i];
		const data = entries[i].data;
		const crc = crcs[i];
		const flag = flags[i];
		const desc = `entry '${entries[i].name}'`;

		writeUint32LE(view, cursor, 0x04034b50, `${desc} local header signature`); // local file header signature
		writeUint16LE(view, cursor + 4, 20); // version needed
		writeUint16LE(view, cursor + 6, flag); // flags (bit 11: UTF-8 filename)
		writeUint16LE(view, cursor + 8, 0); // method: STORED
		writeUint16LE(view, cursor + 10, DOS_TIME);
		writeUint16LE(view, cursor + 12, DOS_DATE);
		writeUint32LE(view, cursor + 14, crc, `${desc} CRC`);
		writeUint32LE(view, cursor + 18, data.length, `${desc} compressed size`); // compressed size
		writeUint32LE(view, cursor + 22, data.length, `${desc} uncompressed size`); // uncompressed size
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
		const flag = flags[i];
		const desc = `entry '${entries[i].name}'`;

		writeUint32LE(view, cursor, 0x02014b50, `${desc} central directory signature`); // central directory header signature
		writeUint16LE(view, cursor + 4, 20); // version made by
		writeUint16LE(view, cursor + 6, 20); // version needed
		writeUint16LE(view, cursor + 8, flag); // flags (bit 11: UTF-8 filename)
		writeUint16LE(view, cursor + 10, 0); // method: STORED
		writeUint16LE(view, cursor + 12, DOS_TIME);
		writeUint16LE(view, cursor + 14, DOS_DATE);
		writeUint32LE(view, cursor + 16, crc, `${desc} CRC`);
		writeUint32LE(view, cursor + 20, data.length, `${desc} compressed size`);
		writeUint32LE(view, cursor + 24, data.length, `${desc} uncompressed size`);
		writeUint16LE(view, cursor + 28, name.length);
		writeUint16LE(view, cursor + 30, 0); // extra field length
		writeUint16LE(view, cursor + 32, 0); // comment length
		writeUint16LE(view, cursor + 34, 0); // disk number start
		writeUint16LE(view, cursor + 36, 0); // internal attributes
		writeUint32LE(view, cursor + 38, 0, `${desc} external attributes`); // external attributes
		writeUint32LE(view, cursor + 42, localOffsets[i], `${desc} local header offset`);
		bytes.set(name, cursor + 46);
		cursor += 46 + name.length;
	}
	const centralDirectoryLength = cursor - centralStart;

	writeUint32LE(view, cursor, 0x06054b50, "end of central directory signature"); // end of central directory signature
	writeUint16LE(view, cursor + 4, 0); // disk number
	writeUint16LE(view, cursor + 6, 0); // disk with central directory
	writeUint16LE(view, cursor + 8, entries.length); // entries on this disk
	writeUint16LE(view, cursor + 10, entries.length); // total entries
	writeUint32LE(view, cursor + 12, centralDirectoryLength, "central directory size");
	writeUint32LE(view, cursor + 16, centralStart, "central directory offset");
	writeUint16LE(view, cursor + 20, 0); // comment length

	return new Blob([bytes], { type: "application/zip" });
}
