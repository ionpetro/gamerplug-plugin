/**
 * A seekable byte source for a clip being uploaded. Abstracted so the resumable
 * uploader can stream a file from disk in production while tests feed it an
 * in-memory buffer.
 */
export interface DataSource {
  readonly size: number;
  /** Read `length` bytes starting at `offset`. May return fewer at EOF. */
  read(offset: number, length: number): Promise<Uint8Array>;
}

export function bufferDataSource(buffer: Uint8Array): DataSource {
  return {
    size: buffer.length,
    async read(offset, length) {
      return buffer.subarray(offset, Math.min(buffer.length, offset + length));
    },
  };
}

/**
 * Node file-backed data source. Imported lazily so this module stays usable in
 * environments without `node:fs` (e.g. a browser-side unit test of other code).
 */
export async function fileDataSource(path: string): Promise<DataSource> {
  const fs = await import("node:fs/promises");
  const stat = await fs.stat(path);
  return {
    size: stat.size,
    async read(offset, length) {
      const handle = await fs.open(path, "r");
      try {
        const buf = Buffer.alloc(length);
        const { bytesRead } = await handle.read(buf, 0, length, offset);
        return new Uint8Array(buf.subarray(0, bytesRead));
      } finally {
        await handle.close();
      }
    },
  };
}
