/**
 * Minimal, transport-agnostic JSON-RPC 2.0 peer. Both sides (the Electron main
 * process and the recorder) can send requests and notifications, so this is a
 * symmetric "peer" rather than a split client/server. Transport is injected so
 * the dispatch logic is unit-testable without real sockets.
 */

export type JsonRpcId = number | string;

export interface JsonRpcRequest<P = unknown> {
  jsonrpc: "2.0";
  id: JsonRpcId;
  method: string;
  params?: P;
}

export interface JsonRpcNotification<P = unknown> {
  jsonrpc: "2.0";
  method: string;
  params?: P;
}

export interface JsonRpcSuccess<R = unknown> {
  jsonrpc: "2.0";
  id: JsonRpcId;
  result: R;
}

export interface JsonRpcErrorObject {
  code: number;
  message: string;
  data?: unknown;
}

export interface JsonRpcErrorResponse {
  jsonrpc: "2.0";
  id: JsonRpcId | null;
  error: JsonRpcErrorObject;
}

export type JsonRpcMessage =
  | JsonRpcRequest
  | JsonRpcNotification
  | JsonRpcSuccess
  | JsonRpcErrorResponse;

/** Standard JSON-RPC 2.0 error codes plus app-specific ranges. */
export const JsonRpcErrorCode = {
  ParseError: -32700,
  InvalidRequest: -32600,
  MethodNotFound: -32601,
  InvalidParams: -32602,
  InternalError: -32603,
  /** Request timed out waiting for a response. */
  Timeout: -32000,
  /** The peer transport closed before the response arrived. */
  ConnectionClosed: -32001,
} as const;

export class JsonRpcError extends Error {
  constructor(
    public readonly code: number,
    message: string,
    public readonly data?: unknown,
  ) {
    super(message);
    this.name = "JsonRpcError";
  }

  toResponse(id: JsonRpcId | null): JsonRpcErrorResponse {
    return {
      jsonrpc: "2.0",
      id,
      error: { code: this.code, message: this.message, data: this.data },
    };
  }
}

/** Abstract bidirectional text transport. */
export interface Transport {
  send(data: string): void;
  /** Register a message handler. Returns an unsubscribe fn. */
  onMessage(handler: (data: string) => void): () => void;
  /** Register a close handler. Returns an unsubscribe fn. */
  onClose(handler: () => void): () => void;
  close(): void;
}

export type RequestHandler = (params: unknown) => unknown | Promise<unknown>;
export type NotificationHandler = (params: unknown) => void | Promise<void>;

interface Pending {
  resolve: (value: unknown) => void;
  reject: (err: Error) => void;
  timer?: ReturnType<typeof setTimeout>;
}

export interface JsonRpcPeerOptions {
  /** Default per-request timeout in ms. 0 disables. */
  defaultTimeoutMs?: number;
  /** Called for logging/telemetry when a malformed frame is received. */
  onError?: (err: Error) => void;
}

export function isJsonRpcRequest(m: JsonRpcMessage): m is JsonRpcRequest {
  return "method" in m && "id" in m && m.id !== undefined && m.id !== null;
}

export function isJsonRpcNotification(m: JsonRpcMessage): m is JsonRpcNotification {
  return "method" in m && !("id" in m);
}

export function isJsonRpcResponse(
  m: JsonRpcMessage,
): m is JsonRpcSuccess | JsonRpcErrorResponse {
  return !("method" in m) && "id" in m;
}

export class JsonRpcPeer {
  private readonly requestHandlers = new Map<string, RequestHandler>();
  private readonly notificationHandlers = new Map<string, NotificationHandler>();
  private readonly pending = new Map<JsonRpcId, Pending>();
  private nextId = 1;
  private closed = false;
  private readonly unsubscribers: Array<() => void> = [];

  constructor(
    private readonly transport: Transport,
    private readonly options: JsonRpcPeerOptions = {},
  ) {
    this.unsubscribers.push(transport.onMessage((d) => this.handleRaw(d)));
    this.unsubscribers.push(transport.onClose(() => this.handleClose()));
  }

  onRequest(method: string, handler: RequestHandler): void {
    this.requestHandlers.set(method, handler);
  }

  onNotification(method: string, handler: NotificationHandler): void {
    this.notificationHandlers.set(method, handler);
  }

  notify(method: string, params?: unknown): void {
    if (this.closed) throw new JsonRpcError(JsonRpcErrorCode.ConnectionClosed, "peer closed");
    const msg: JsonRpcNotification = { jsonrpc: "2.0", method };
    if (params !== undefined) msg.params = params;
    this.transport.send(JSON.stringify(msg));
  }

  request<R = unknown>(method: string, params?: unknown, timeoutMs?: number): Promise<R> {
    if (this.closed) {
      return Promise.reject(
        new JsonRpcError(JsonRpcErrorCode.ConnectionClosed, "peer closed"),
      );
    }
    const id = this.nextId++;
    const msg: JsonRpcRequest = { jsonrpc: "2.0", id, method };
    if (params !== undefined) msg.params = params;

    return new Promise<R>((resolve, reject) => {
      const effectiveTimeout = timeoutMs ?? this.options.defaultTimeoutMs ?? 0;
      const pending: Pending = {
        resolve: resolve as (v: unknown) => void,
        reject,
      };
      if (effectiveTimeout > 0) {
        pending.timer = setTimeout(() => {
          this.pending.delete(id);
          reject(
            new JsonRpcError(
              JsonRpcErrorCode.Timeout,
              `request '${method}' timed out after ${effectiveTimeout}ms`,
            ),
          );
        }, effectiveTimeout);
      }
      this.pending.set(id, pending);
      try {
        this.transport.send(JSON.stringify(msg));
      } catch (err) {
        this.pending.delete(id);
        if (pending.timer) clearTimeout(pending.timer);
        reject(err instanceof Error ? err : new Error(String(err)));
      }
    });
  }

  close(): void {
    this.handleClose();
    this.transport.close();
  }

  private handleRaw(data: string): void {
    let parsed: unknown;
    try {
      parsed = JSON.parse(data);
    } catch {
      this.options.onError?.(new Error("received non-JSON frame"));
      return;
    }
    const messages = Array.isArray(parsed) ? parsed : [parsed];
    for (const m of messages) {
      void this.dispatch(m as JsonRpcMessage);
    }
  }

  private async dispatch(message: JsonRpcMessage): Promise<void> {
    if (!message || typeof message !== "object" || (message as { jsonrpc?: string }).jsonrpc !== "2.0") {
      this.options.onError?.(new Error("invalid JSON-RPC message"));
      return;
    }

    if (isJsonRpcResponse(message)) {
      this.resolvePending(message);
      return;
    }

    if (isJsonRpcNotification(message)) {
      const handler = this.notificationHandlers.get(message.method);
      if (!handler) {
        this.options.onError?.(new Error(`no handler for notification '${message.method}'`));
        return;
      }
      try {
        await handler(message.params);
      } catch (err) {
        this.options.onError?.(err instanceof Error ? err : new Error(String(err)));
      }
      return;
    }

    if (isJsonRpcRequest(message)) {
      await this.handleRequest(message);
    }
  }

  private async handleRequest(req: JsonRpcRequest): Promise<void> {
    const handler = this.requestHandlers.get(req.method);
    if (!handler) {
      this.send(
        new JsonRpcError(
          JsonRpcErrorCode.MethodNotFound,
          `method not found: ${req.method}`,
        ).toResponse(req.id),
      );
      return;
    }
    try {
      const result = await handler(req.params);
      const response: JsonRpcSuccess = {
        jsonrpc: "2.0",
        id: req.id,
        result: result === undefined ? null : result,
      };
      this.send(response);
    } catch (err) {
      const rpcErr =
        err instanceof JsonRpcError
          ? err
          : new JsonRpcError(
              JsonRpcErrorCode.InternalError,
              err instanceof Error ? err.message : String(err),
            );
      this.send(rpcErr.toResponse(req.id));
    }
  }

  private resolvePending(message: JsonRpcSuccess | JsonRpcErrorResponse): void {
    if (message.id === null) return;
    const pending = this.pending.get(message.id);
    if (!pending) return;
    this.pending.delete(message.id);
    if (pending.timer) clearTimeout(pending.timer);
    if ("error" in message) {
      pending.reject(
        new JsonRpcError(message.error.code, message.error.message, message.error.data),
      );
    } else {
      pending.resolve(message.result);
    }
  }

  private handleClose(): void {
    if (this.closed) return;
    this.closed = true;
    for (const unsub of this.unsubscribers) unsub();
    for (const [, pending] of this.pending) {
      if (pending.timer) clearTimeout(pending.timer);
      pending.reject(
        new JsonRpcError(JsonRpcErrorCode.ConnectionClosed, "peer connection closed"),
      );
    }
    this.pending.clear();
  }

  private send(message: JsonRpcMessage): void {
    if (this.closed) return;
    this.transport.send(JSON.stringify(message));
  }
}
