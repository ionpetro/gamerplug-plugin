import type { WebSocket as WsWebSocket } from "ws";
import type { Transport } from "./jsonrpc.js";

/** Wraps a `ws` WebSocket as a JSON-RPC {@link Transport}. */
export function wsTransport(socket: WsWebSocket): Transport {
  return {
    send(data: string) {
      socket.send(data);
    },
    onMessage(handler) {
      const listener = (data: unknown) => handler(data?.toString() ?? "");
      socket.on("message", listener);
      return () => socket.off("message", listener);
    },
    onClose(handler) {
      socket.on("close", handler);
      return () => socket.off("close", handler);
    },
    close() {
      socket.close();
    },
  };
}
