import { spawn, type ChildProcess } from "node:child_process";
import type { JsonRpcPeer } from "@gamerplug/ipc";
import {
  createRecorderIpcServer,
  PROTOCOL_VERSION,
  type RecorderIpcServer,
} from "@gamerplug/ipc";
import type { SavedClip, Settings } from "@gamerplug/shared";
import { EventEmitter } from "node:events";
import { recorderExecutable } from "./paths.js";

export type RecorderUiState = "disconnected" | "starting" | "idle" | "capturing" | "saving" | "error";

/**
 * Hosts the localhost JSON-RPC server and spawns the recorder process. Mirrors
 * Medal's supervisor (REPORT.md §1): port scan from 10603, Origin rejection,
 * auto-restart with a crash-loop guard (5 exits of the same code in 120s).
 */
export class RecorderSupervisor extends EventEmitter {
  private server?: RecorderIpcServer;
  private peer?: JsonRpcPeer;
  private child?: ChildProcess;
  private port = 0;
  private exits: { code: number | null; at: number }[] = [];
  private restarting = true;
  private uiState: RecorderUiState = "disconnected";

  get state(): RecorderUiState {
    return this.uiState;
  }

  get portNumber(): number {
    return this.port;
  }

  async start(): Promise<void> {
    this.restarting = true;
    this.server = await createRecorderIpcServer({ startPort: 10603, defaultTimeoutMs: 15_000 });
    this.port = this.server.port;
    this.server.onConnection((peer) => this.bindPeer(peer));
    this.spawnRecorder();
  }

  async stop(): Promise<void> {
    this.restarting = false;
    try {
      await this.peer?.request("shutdown", undefined, 2000);
    } catch {
      /* already gone */
    }
    this.child?.kill();
    this.child = undefined;
    await this.server?.close();
    this.setState("disconnected");
  }

  async applySettings(settings: Settings): Promise<void> {
    await this.peer?.request("applySettings", { settings });
  }

  async startCapture(): Promise<void> {
    await this.peer?.request("startCapture");
  }

  async stopCapture(): Promise<void> {
    await this.peer?.request("stopCapture");
  }

  async executeAction(action: string, source: "hotkey" | "ui" | "api" = "ui"): Promise<unknown> {
    return this.peer?.request("executeAction", { action, source });
  }

  async startHotkeys(): Promise<void> {
    await this.peer?.request("startHotkeys");
  }

  async listDisplays(): Promise<unknown> {
    return this.peer?.request("listDisplays");
  }

  async listAudioDevices(): Promise<unknown> {
    return this.peer?.request("listAudioDevices");
  }

  private bindPeer(peer: JsonRpcPeer): void {
    this.peer = peer;
    peer.onRequest("handshake", (params) => {
      const p = params as { protocolVersion?: number };
      if (p.protocolVersion && p.protocolVersion !== PROTOCOL_VERSION) {
        throw new Error(`protocol mismatch: recorder ${p.protocolVersion} vs ui ${PROTOCOL_VERSION}`);
      }
      return { protocolVersion: PROTOCOL_VERSION };
    });
    peer.onRequest("ping", () => "pong");
    peer.onNotification("ready", () => {
      this.setState("idle");
    });
    peer.onNotification("state", (params) => {
      const state = (params as { state?: RecorderUiState }).state;
      if (state) this.setState(state);
    });
    peer.onNotification("clipSaved", (params) => {
      this.emit("clipSaved", params as SavedClip);
    });
    peer.onNotification("clipFailed", (params) => {
      this.emit("clipFailed", params as { reason: string; message: string });
    });
    peer.onNotification("log", (params) => {
      this.emit("log", params as { level: string; message: string });
    });
    peer.onNotification("fallbackEncoder", (params) => {
      this.emit("fallbackEncoder", params as { from: string; to: string; reason: string });
    });
    peer.onNotification("captureStarted", () => {
      this.setState("capturing");
    });
    peer.onNotification("captureStopped", () => {
      this.setState("idle");
    });
  }

  private spawnRecorder(): void {
    const exe = recorderExecutable();
    if (!exe) {
      this.setState("error");
      this.emit("log", {
        level: "warn",
        message: "Recorder executable not found (Windows build required). UI will run without capture.",
      });
      return;
    }
    this.setState("starting");
    const child = spawn(exe, ["--electronPort", String(this.port), "--wsComms"], {
      windowsHide: true,
      stdio: ["ignore", "pipe", "pipe"],
    });
    this.child = child;
    child.stderr?.on("data", (d: Buffer) =>
      this.emit("log", { level: "warn", message: d.toString().trimEnd() }),
    );
    child.on("exit", (code) => this.onExit(code));
  }

  private onExit(code: number | null): void {
    this.peer = undefined;
    const now = Date.now();
    this.exits.push({ code, at: now });
    this.exits = this.exits.filter((e) => now - e.at < 120_000);
    const same = this.exits.filter((e) => e.code === code);
    if (!this.restarting) return;
    if (same.length >= 5) {
      this.setState("error");
      this.emit("log", {
        level: "error",
        message: `recorder crash-loop: exit ${code} ×${same.length} in 120s, giving up`,
      });
      return;
    }
    setTimeout(() => {
      if (this.restarting) this.spawnRecorder();
    }, 500);
  }

  private setState(state: RecorderUiState): void {
    this.uiState = state;
    this.emit("state", state);
  }
}
