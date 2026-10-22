import { afterEach, describe, expect, it } from "vitest";
import WebSocket from "ws";
import { createRecorderIpcServer, type RecorderIpcServer } from "./server.js";
import { connectRecorderIpcClient } from "./client.js";
import type { JsonRpcPeer } from "./jsonrpc.js";

let server: RecorderIpcServer | undefined;

afterEach(async () => {
  await server?.close();
  server = undefined;
});

describe("createRecorderIpcServer", () => {
  it("accepts a client with no Origin and round-trips a request", async () => {
    server = await createRecorderIpcServer({ startPort: 14600 });
    const serverPeerPromise = new Promise<JsonRpcPeer>((resolve) =>
      server!.onConnection((peer) => {
        peer.onRequest("ping", () => "pong");
        resolve(peer);
      }),
    );
    const client = await connectRecorderIpcClient(server.port, { defaultTimeoutMs: 2000 });
    await serverPeerPromise;
    await expect(client.request("ping")).resolves.toBe("pong");
    client.close();
  });

  it("rejects a browser-style connection that sends an Origin header", async () => {
    server = await createRecorderIpcServer({ startPort: 14620 });
    await expect(
      new Promise<void>((resolve, reject) => {
        const ws = new WebSocket(`ws://127.0.0.1:${server!.port}`, {
          headers: { Origin: "http://evil.example" },
        });
        ws.once("open", () => {
          ws.close();
          reject(new Error("connection should have been rejected"));
        });
        ws.once("error", () => resolve());
      }),
    ).resolves.toBeUndefined();
  });

  it("scans to the next port when the first is busy", async () => {
    const first = await createRecorderIpcServer({ startPort: 14640, portScanCount: 3 });
    const second = await createRecorderIpcServer({ startPort: 14640, portScanCount: 3 });
    expect(second.port).toBe(first.port + 1);
    await first.close();
    await second.close();
  });
});
