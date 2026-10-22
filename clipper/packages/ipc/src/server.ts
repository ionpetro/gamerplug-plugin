import { WebSocketServer, type WebSocket as WsWebSocket } from "ws";
import type { IncomingMessage } from "node:http";
import { JsonRpcPeer, type JsonRpcPeerOptions } from "./jsonrpc.js";
import { verifyConnection } from "./verifyOrigin.js";
import { wsTransport } from "./wsTransport.js";

export interface RecorderIpcServerOptions extends JsonRpcPeerOptions {
  /** First port to try. Medal uses 10603; we default to the same. */
  startPort?: number;
  /** How many sequential ports to try if the first is busy. */
  portScanCount?: number;
  host?: string;
}

export interface RecorderIpcServer {
  readonly port: number;
  /** Fires when the recorder connects and a peer is established. */
  onConnection(handler: (peer: JsonRpcPeer) => void): void;
  close(): Promise<void>;
}

const DEFAULT_START_PORT = 10603;
const DEFAULT_PORT_SCAN = 10;

/**
 * Starts the localhost JSON-RPC control server the recorder connects to. Binds
 * 127.0.0.1 only, scans forward from {@link DEFAULT_START_PORT} if busy, and
 * rejects any upgrade that carries an Origin header.
 */
export async function createRecorderIpcServer(
  options: RecorderIpcServerOptions = {},
): Promise<RecorderIpcServer> {
  const host = options.host ?? "127.0.0.1";
  const startPort = options.startPort ?? DEFAULT_START_PORT;
  const scan = options.portScanCount ?? DEFAULT_PORT_SCAN;

  const connectionHandlers: Array<(peer: JsonRpcPeer) => void> = [];

  const wss = await listenWithScan(host, startPort, scan);

  wss.on("connection", (socket: WsWebSocket) => {
    const peer = new JsonRpcPeer(wsTransport(socket), options);
    for (const handler of connectionHandlers) handler(peer);
  });

  const addr = wss.address();
  const port = typeof addr === "object" && addr ? addr.port : startPort;

  return {
    port,
    onConnection(handler) {
      connectionHandlers.push(handler);
    },
    close() {
      return new Promise<void>((resolve) => wss.close(() => resolve()));
    },
  };
}

function listenWithScan(
  host: string,
  startPort: number,
  scan: number,
): Promise<WebSocketServer> {
  return new Promise((resolve, reject) => {
    let attempt = 0;

    const tryPort = (port: number) => {
      const wss = new WebSocketServer({
        host,
        port,
        verifyClient: (info: { req: IncomingMessage }, cb) => {
          const result = verifyConnection(info.req.headers);
          if (result.allowed) {
            cb(true);
          } else {
            cb(false, 403, "Forbidden");
          }
        },
      });

      const onError = (err: NodeJS.ErrnoException) => {
        wss.removeAllListeners();
        if (err.code === "EADDRINUSE" && attempt < scan - 1) {
          attempt += 1;
          tryPort(port + 1);
        } else {
          reject(err);
        }
      };

      wss.once("error", onError);
      wss.once("listening", () => {
        wss.removeListener("error", onError);
        resolve(wss);
      });
    };

    tryPort(startPort);
  });
}
