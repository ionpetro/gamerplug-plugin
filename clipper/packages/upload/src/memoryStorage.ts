/**
 * In-memory stand-in for a GCS/S3-style resumable object store, implemented as
 * an {@link HttpClient}. It honours the same resumable protocol the real
 * uploader speaks (initiate -> chunked PUT with Content-Range -> 308 resume ->
 * CRC32C in `x-goog-hash`). Used by the stub backend for a fully local dev loop
 * and by the uploader's tests, including fault injection.
 */

import { Crc32c } from "./crc32c.js";
import type { HttpClient, HttpRequest, HttpResponse } from "./http.js";

interface Session {
  objectKey: string;
  contentType: string;
  received: Uint8Array;
}

export interface MemoryStorageOptions {
  /**
   * Fault injector: return an HTTP status to force on the Nth matching call, or
   * undefined to proceed normally. Lets tests exercise resume/retry paths.
   */
  faultInjector?: (req: HttpRequest, callIndex: number) => number | undefined;
  baseUrl?: string;
}

export class MemoryStorage {
  readonly objects = new Map<string, { bytes: Uint8Array; contentType: string }>();
  private readonly sessions = new Map<string, Session>();
  private callIndex = 0;
  private sessionSeq = 0;
  private readonly baseUrl: string;

  constructor(private readonly options: MemoryStorageOptions = {}) {
    this.baseUrl = options.baseUrl ?? "https://memstore.local";
  }

  /** A signed URL the uploader can initiate a `gcs-resumable` upload against. */
  signedUrlFor(objectKey: string): string {
    return `${this.baseUrl}/o/${encodeURIComponent(objectKey)}?signed=1`;
  }

  singlePutUrlFor(objectKey: string): string {
    return `${this.baseUrl}/put/${encodeURIComponent(objectKey)}`;
  }

  asHttpClient(): HttpClient {
    return { send: (req) => this.handle(req) };
  }

  private async handle(req: HttpRequest): Promise<HttpResponse> {
    const idx = this.callIndex++;
    const forced = this.options.faultInjector?.(req, idx);
    if (forced !== undefined) {
      return makeResponse(forced, {}, "");
    }

    const url = new URL(req.url);

    // Initiate resumable session: POST signed URL with x-goog-resumable: start
    if (req.method === "POST" && req.headers?.["x-goog-resumable"] === "start") {
      const objectKey = decodeURIComponent(url.pathname.replace(/^\/o\//, ""));
      const sessionId = `sess-${this.sessionSeq++}`;
      this.sessions.set(sessionId, {
        objectKey,
        contentType: req.headers?.["content-type"] ?? "application/octet-stream",
        received: new Uint8Array(0),
      });
      const location = `${this.baseUrl}/session/${sessionId}`;
      return makeResponse(201, { location }, "");
    }

    // Chunked PUT to a session URL
    if (req.method === "PUT" && url.pathname.startsWith("/session/")) {
      return this.handleSessionPut(req, url);
    }

    // Single PUT
    if (req.method === "PUT" && url.pathname.startsWith("/put/")) {
      const objectKey = decodeURIComponent(url.pathname.replace(/^\/put\//, ""));
      const body = toBytes(req.body);
      this.objects.set(objectKey, {
        bytes: body,
        contentType: req.headers?.["content-type"] ?? "application/octet-stream",
      });
      const crc = new Crc32c().update(body).digestBase64();
      return makeResponse(200, { "x-goog-hash": `crc32c=${crc}` }, "");
    }

    return makeResponse(404, {}, "not found");
  }

  private handleSessionPut(req: HttpRequest, url: URL): HttpResponse {
    const sessionId = url.pathname.replace(/^\/session\//, "");
    const session = this.sessions.get(sessionId);
    if (!session) return makeResponse(404, {}, "no session");

    const range = req.headers?.["content-range"];
    const parsed = parseContentRange(range);
    if (!parsed) return makeResponse(400, {}, "bad content-range");

    const body = toBytes(req.body);

    // Only accept writes that start at the current committed offset.
    if (parsed.start !== session.received.length) {
      // Report what we actually have so the client can resync.
      if (session.received.length === 0) {
        return makeResponse(308, {}, "");
      }
      return makeResponse(308, { range: `bytes=0-${session.received.length - 1}` }, "");
    }

    const merged = new Uint8Array(session.received.length + body.length);
    merged.set(session.received, 0);
    merged.set(body, session.received.length);
    session.received = merged;

    const isComplete = session.received.length >= parsed.total;
    if (isComplete) {
      const crc = new Crc32c().update(session.received).digestBase64();
      this.objects.set(session.objectKey, {
        bytes: session.received,
        contentType: session.contentType,
      });
      this.sessions.delete(sessionId);
      return makeResponse(
        200,
        { "x-goog-hash": `crc32c=${crc}`, location: this.signedUrlFor(session.objectKey) },
        "",
      );
    }
    return makeResponse(308, { range: `bytes=0-${session.received.length - 1}` }, "");
  }
}

function parseContentRange(
  header: string | undefined,
): { start: number; end: number; total: number } | undefined {
  if (!header) return undefined;
  const match = /bytes (\d+)-(\d+)\/(\d+|\*)/.exec(header);
  if (!match) return undefined;
  return {
    start: Number(match[1]),
    end: Number(match[2]),
    total: match[3] === "*" ? Number.POSITIVE_INFINITY : Number(match[3]),
  };
}

function toBytes(body: Uint8Array | string | undefined): Uint8Array {
  if (body === undefined) return new Uint8Array(0);
  if (typeof body === "string") return new TextEncoder().encode(body);
  return body;
}

function makeResponse(
  status: number,
  headers: Record<string, string>,
  text: string,
): HttpResponse {
  return {
    status,
    headers,
    text: async () => text,
    json: async () => (text ? JSON.parse(text) : null),
    bytes: async () => new TextEncoder().encode(text),
  };
}
