/**
 * Tiny HTTP abstraction so the resumable uploader and backends can be tested
 * without real network access. A `fetch`-based default is provided for runtime.
 */

export interface HttpRequest {
  method: string;
  url: string;
  headers?: Record<string, string>;
  body?: Uint8Array | string;
  signal?: AbortSignal;
}

export interface HttpResponse {
  status: number;
  headers: Record<string, string>;
  text(): Promise<string>;
  json(): Promise<unknown>;
  bytes(): Promise<Uint8Array>;
}

export interface HttpClient {
  send(req: HttpRequest): Promise<HttpResponse>;
}

/** HttpClient backed by the global `fetch`. */
export function fetchHttpClient(): HttpClient {
  return {
    async send(req) {
      const res = await fetch(req.url, {
        method: req.method,
        headers: req.headers,
        body: req.body as BodyInit | undefined,
        signal: req.signal,
      });
      const headers: Record<string, string> = {};
      res.headers.forEach((value, key) => {
        headers[key.toLowerCase()] = value;
      });
      return {
        status: res.status,
        headers,
        text: () => res.text(),
        json: () => res.json(),
        async bytes() {
          return new Uint8Array(await res.arrayBuffer());
        },
      };
    },
  };
}
