/**
 * CRC32C (Castagnoli, polynomial 0x1EDC6F41) — the checksum Google Cloud Storage
 * and the Medal upload pipeline use for integrity verification (REPORT.md §4
 * steps 4/6/7). Pure, dependency-free, and streamable so a large clip can be
 * hashed chunk-by-chunk without loading it all into memory.
 */

const POLY = 0x82f63b78; // reversed 0x1EDC6F41

const TABLE: Uint32Array = (() => {
  const t = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) {
      c = c & 1 ? POLY ^ (c >>> 1) : c >>> 1;
    }
    t[n] = c >>> 0;
  }
  return t;
})();

/** Incremental CRC32C hasher. */
export class Crc32c {
  private crc = 0xffffffff;

  update(data: Uint8Array): this {
    let crc = this.crc;
    for (let i = 0; i < data.length; i++) {
      crc = TABLE[(crc ^ data[i]!) & 0xff]! ^ (crc >>> 8);
    }
    this.crc = crc >>> 0;
    return this;
  }

  /** The final checksum as an unsigned 32-bit integer. */
  digest(): number {
    return (this.crc ^ 0xffffffff) >>> 0;
  }

  /** Big-endian 4-byte representation (GCS `x-goog-hash` wire format). */
  digestBytes(): Uint8Array {
    const value = this.digest();
    return new Uint8Array([
      (value >>> 24) & 0xff,
      (value >>> 16) & 0xff,
      (value >>> 8) & 0xff,
      value & 0xff,
    ]);
  }

  /** Base64 of the big-endian digest — the value GCS expects/returns. */
  digestBase64(): string {
    return base64Encode(this.digestBytes());
  }
}

export function crc32c(data: Uint8Array): number {
  return new Crc32c().update(data).digest();
}

export function crc32cBase64(data: Uint8Array): string {
  return new Crc32c().update(data).digestBase64();
}

function base64Encode(bytes: Uint8Array): string {
  if (typeof Buffer !== "undefined") {
    return Buffer.from(bytes).toString("base64");
  }
  let binary = "";
  for (const b of bytes) binary += String.fromCharCode(b);
  // eslint-disable-next-line no-undef
  return btoa(binary);
}
