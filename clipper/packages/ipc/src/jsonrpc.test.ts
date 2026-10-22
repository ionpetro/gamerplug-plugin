import { describe, expect, it, vi } from "vitest";
import {
  JsonRpcError,
  JsonRpcErrorCode,
  JsonRpcPeer,
  type Transport,
} from "./jsonrpc.js";

/** Build two transports wired directly to each other, with controllable delivery. */
function makeTransportPair() {
  const aHandlers: Array<(d: string) => void> = [];
  const bHandlers: Array<(d: string) => void> = [];
  const aClose: Array<() => void> = [];
  const bClose: Array<() => void> = [];
  let aOpen = true;
  let bOpen = true;

  const a: Transport = {
    send(data) {
      if (!bOpen) return;
      queueMicrotask(() => bHandlers.forEach((h) => h(data)));
    },
    onMessage(h) {
      aHandlers.push(h);
      return () => void aHandlers.splice(aHandlers.indexOf(h), 1);
    },
    onClose(h) {
      aClose.push(h);
      return () => void aClose.splice(aClose.indexOf(h), 1);
    },
    close() {
      if (!aOpen) return;
      aOpen = false;
      aClose.forEach((h) => h());
      bOpen = false;
      bClose.forEach((h) => h());
    },
  };

  const b: Transport = {
    send(data) {
      if (!aOpen) return;
      queueMicrotask(() => aHandlers.forEach((h) => h(data)));
    },
    onMessage(h) {
      bHandlers.push(h);
      return () => void bHandlers.splice(bHandlers.indexOf(h), 1);
    },
    onClose(h) {
      bClose.push(h);
      return () => void bClose.splice(bClose.indexOf(h), 1);
    },
    close() {
      if (!bOpen) return;
      bOpen = false;
      bClose.forEach((h) => h());
      aOpen = false;
      aClose.forEach((h) => h());
    },
  };

  return { a, b };
}

describe("JsonRpcPeer", () => {
  it("round-trips a request and result", async () => {
    const { a, b } = makeTransportPair();
    const server = new JsonRpcPeer(a);
    const client = new JsonRpcPeer(b);
    server.onRequest("add", (params) => {
      const [x, y] = params as [number, number];
      return x + y;
    });
    await expect(client.request<number>("add", [2, 3])).resolves.toBe(5);
  });

  it("supports bidirectional requests", async () => {
    const { a, b } = makeTransportPair();
    const peerA = new JsonRpcPeer(a);
    const peerB = new JsonRpcPeer(b);
    peerA.onRequest("fromB", () => "a-handled");
    peerB.onRequest("fromA", () => "b-handled");
    await expect(peerB.request("fromB")).resolves.toBe("a-handled");
    await expect(peerA.request("fromA")).resolves.toBe("b-handled");
  });

  it("delivers notifications without a response", async () => {
    const { a, b } = makeTransportPair();
    const server = new JsonRpcPeer(a);
    const client = new JsonRpcPeer(b);
    const handler = vi.fn();
    server.onNotification("clipSaved", handler);
    client.notify("clipSaved", { id: "abc" });
    await new Promise((r) => setTimeout(r, 10));
    expect(handler).toHaveBeenCalledWith({ id: "abc" });
  });

  it("propagates handler errors as JSON-RPC errors", async () => {
    const { a, b } = makeTransportPair();
    const server = new JsonRpcPeer(a);
    const client = new JsonRpcPeer(b);
    server.onRequest("boom", () => {
      throw new JsonRpcError(1234, "custom failure", { detail: 1 });
    });
    await expect(client.request("boom")).rejects.toMatchObject({
      code: 1234,
      message: "custom failure",
      data: { detail: 1 },
    });
  });

  it("returns MethodNotFound for unknown methods", async () => {
    const { a, b } = makeTransportPair();
    new JsonRpcPeer(a);
    const client = new JsonRpcPeer(b);
    await expect(client.request("nope")).rejects.toMatchObject({
      code: JsonRpcErrorCode.MethodNotFound,
    });
  });

  it("times out when no response arrives", async () => {
    const { a, b } = makeTransportPair();
    const serverPeer = new JsonRpcPeer(a);
    serverPeer.onRequest("hang", () => new Promise(() => {}));
    const client = new JsonRpcPeer(b, { defaultTimeoutMs: 20 });
    await expect(client.request("hang")).rejects.toMatchObject({
      code: JsonRpcErrorCode.Timeout,
    });
  });

  it("rejects pending requests when the connection closes", async () => {
    const { a, b } = makeTransportPair();
    const server = new JsonRpcPeer(a);
    const client = new JsonRpcPeer(b);
    server.onRequest("hang", () => new Promise(() => {}));
    const p = client.request("hang");
    client.close();
    await expect(p).rejects.toMatchObject({
      code: JsonRpcErrorCode.ConnectionClosed,
    });
  });

  it("ignores malformed frames via onError", async () => {
    const onError = vi.fn();
    const handlers: Array<(d: string) => void> = [];
    const transport: Transport = {
      send() {},
      onMessage(h) {
        handlers.push(h);
        return () => {};
      },
      onClose() {
        return () => {};
      },
      close() {},
    };
    new JsonRpcPeer(transport, { onError });
    handlers[0]?.("this is not json");
    expect(onError).toHaveBeenCalled();
  });
});
