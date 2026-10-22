import WebSocket from "ws";
import { JsonRpcPeer, type JsonRpcPeerOptions } from "./jsonrpc.js";
import { wsTransport } from "./wsTransport.js";

export interface RecorderIpcClientOptions extends JsonRpcPeerOptions {
  host?: string;
}

/**
 * Connects to the desktop control server as the recorder would. Provided so the
 * protocol can be exercised end-to-end from Node (tests, tooling); the shipping
 * recorder is a .NET process using its own JSON-RPC client.
 */
export function connectRecorderIpcClient(
  port: number,
  options: RecorderIpcClientOptions = {},
): Promise<JsonRpcPeer> {
  const host = options.host ?? "127.0.0.1";
  return new Promise((resolve, reject) => {
    // Deliberately omit the Origin header: the server rejects any connection
    // that sends one.
    const socket = new WebSocket(`ws://${host}:${port}`);
    socket.once("open", () => {
      resolve(new JsonRpcPeer(wsTransport(socket), options));
    });
    socket.once("error", (err: Error) => reject(err));
  });
}
