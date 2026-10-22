/**
 * Resumable chunked uploader. Implements the storage half of the Medal-style
 * pipeline (REPORT.md §4 steps 5–7): initiate a resumable session, PUT the
 * object in chunks with `Content-Range`, resume from the server's committed
 * offset after a transient failure, and verify the stored CRC32C.
 */

import { Crc32c } from "./crc32c.js";
import type { DataSource } from "./dataSource.js";
import type { HttpClient } from "./http.js";
import {
  DEFAULT_BACKOFF,
  withRetry,
  type BackoffOptions,
  type RetryHooks,
} from "./retry.js";
import type { UploadTarget } from "./types.js";

export interface ResumableUploadOptions {
  http: HttpClient;
  target: UploadTarget;
  source: DataSource;
  contentType: string;
  /** Bytes per PUT. GCS requires a multiple of 256 KiB except the final chunk. */
  chunkSize?: number;
  /** Expected whole-object CRC32C (base64). When set, the result is verified. */
  expectedCrc32c?: string;
  backoff?: BackoffOptions;
  retryHooks?: RetryHooks;
  onProgress?: (bytesSent: number, total: number) => void;
  signal?: AbortSignal;
}

export interface ResumableUploadResult {
  bytesSent: number;
  /** The CRC32C the server reported, if any (GCS `x-goog-hash`). */
  serverCrc32c?: string;
  /** Final object URL/location when the server provides one. */
  location?: string;
}

const DEFAULT_CHUNK_SIZE = 8 * 1024 * 1024; // 8 MiB, a 256 KiB multiple

export class UploadError extends Error {
  retryable: boolean;
  constructor(message: string, retryable = true) {
    super(message);
    this.name = "UploadError";
    this.retryable = retryable;
  }
}

export async function resumableUpload(
  opts: ResumableUploadOptions,
): Promise<ResumableUploadResult> {
  const { target } = opts;
  if (target.kind === "single-put") {
    return singlePut(opts, target.url, target.headers);
  }
  const sessionUrl =
    target.kind === "session-resumable"
      ? target.sessionUrl
      : await initiateGcsSession(opts, target.signedUrl, target.headers);
  return putInChunks(opts, sessionUrl);
}

async function initiateGcsSession(
  opts: ResumableUploadOptions,
  signedUrl: string,
  headers: Record<string, string> | undefined,
): Promise<string> {
  const res = await withRetry(
    () =>
      opts.http.send({
        method: "POST",
        url: signedUrl,
        headers: {
          "x-goog-resumable": "start",
          "content-type": opts.contentType,
          "content-length": "0",
          ...headers,
        },
        signal: opts.signal,
      }),
    opts.backoff ?? DEFAULT_BACKOFF,
    opts.retryHooks,
  );
  if (res.status !== 201 && res.status !== 200) {
    throw new UploadError(`failed to initiate resumable session: ${res.status}`);
  }
  const location = res.headers["location"];
  if (!location) {
    throw new UploadError("resumable session response missing Location header", false);
  }
  return location;
}

async function putInChunks(
  opts: ResumableUploadOptions,
  sessionUrl: string,
): Promise<ResumableUploadResult> {
  const total = opts.source.size;
  const chunkSize = opts.chunkSize ?? DEFAULT_CHUNK_SIZE;
  let offset = 0;
  let serverCrc32c: string | undefined;
  let location: string | undefined;

  while (offset < total) {
    const end = Math.min(offset + chunkSize, total);
    const chunk = await opts.source.read(offset, end - offset);
    const rangeStart = offset;
    const rangeEnd = offset + chunk.length - 1;

    const res = await withRetry(
      async () => {
        const r = await opts.http.send({
          method: "PUT",
          url: sessionUrl,
          headers: {
            "content-type": opts.contentType,
            "content-length": String(chunk.length),
            "content-range": `bytes ${rangeStart}-${rangeEnd}/${total}`,
          },
          body: chunk,
          signal: opts.signal,
        });
        // 308 = resume incomplete (expected for non-final chunks).
        // 200/201 = complete (final chunk).
        if (r.status === 308 || r.status === 200 || r.status === 201) {
          return r;
        }
        if (r.status === 503 || r.status === 500 || r.status === 429) {
          throw new UploadError(`transient storage error ${r.status}`, true);
        }
        throw new UploadError(`unexpected upload status ${r.status}`, false);
      },
      opts.backoff ?? DEFAULT_BACKOFF,
      opts.retryHooks,
    );

    if (res.status === 308) {
      // Trust the server's committed range when present, to avoid re-sending.
      const committed = parseCommittedEnd(res.headers["range"]);
      offset = committed !== undefined ? committed + 1 : end;
    } else {
      offset = total;
      serverCrc32c = extractCrc32c(res.headers["x-goog-hash"]);
      location = res.headers["location"] ?? sessionUrl;
    }
    opts.onProgress?.(Math.min(offset, total), total);
  }

  if (opts.expectedCrc32c && serverCrc32c && serverCrc32c !== opts.expectedCrc32c) {
    throw new UploadError(
      `checksum mismatch: local ${opts.expectedCrc32c} vs server ${serverCrc32c}`,
      false,
    );
  }

  return { bytesSent: total, serverCrc32c, location };
}

async function singlePut(
  opts: ResumableUploadOptions,
  url: string,
  headers: Record<string, string> | undefined,
): Promise<ResumableUploadResult> {
  const total = opts.source.size;
  const body = await opts.source.read(0, total);
  const crc = new Crc32c().update(body).digestBase64();
  const res = await withRetry(
    async () => {
      const r = await opts.http.send({
        method: "PUT",
        url,
        headers: {
          "content-type": opts.contentType,
          "content-length": String(total),
          "x-goog-hash": `crc32c=${crc}`,
          ...headers,
        },
        body,
        signal: opts.signal,
      });
      if (r.status >= 200 && r.status < 300) return r;
      if (r.status === 503 || r.status === 500 || r.status === 429) {
        throw new UploadError(`transient storage error ${r.status}`, true);
      }
      throw new UploadError(`unexpected upload status ${r.status}`, false);
    },
    opts.backoff ?? DEFAULT_BACKOFF,
    opts.retryHooks,
  );
  opts.onProgress?.(total, total);
  return {
    bytesSent: total,
    serverCrc32c: extractCrc32c(res.headers["x-goog-hash"]) ?? crc,
    location: res.headers["location"] ?? url,
  };
}

/** Parse the end byte from a GCS `Range: bytes=0-N` resume header. */
export function parseCommittedEnd(rangeHeader: string | undefined): number | undefined {
  if (!rangeHeader) return undefined;
  const match = /bytes=(\d+)-(\d+)/.exec(rangeHeader);
  if (!match) return undefined;
  return Number(match[2]);
}

/** Extract the crc32c value from a GCS `x-goog-hash: crc32c=...,md5=...` header. */
export function extractCrc32c(hashHeader: string | undefined): string | undefined {
  if (!hashHeader) return undefined;
  const match = /crc32c=([^,\s]+)/.exec(hashHeader);
  return match ? match[1] : undefined;
}
