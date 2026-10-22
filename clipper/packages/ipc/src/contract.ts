/**
 * The recorder <-> desktop IPC contract. The Electron main process hosts the
 * JSON-RPC server; the recorder connects as a client (mirroring Medal, see
 * REPORT.md §1 "Transport"/"Methods"). Method names and payloads are defined
 * once here and shared by both sides.
 */

import type { SavedClip } from "@gamerplug/shared";

/** Methods the desktop app invokes on the recorder (UI -> recorder). */
export const RecorderMethods = {
  Ping: "ping",
  ApplySettings: "applySettings",
  StartCapture: "startCapture",
  StopCapture: "stopCapture",
  /** Trigger a save from the UI, equivalent to a hotkey (Medal `executeInputAction`). */
  ExecuteAction: "executeAction",
  StartHotkeys: "startHotkeys",
  StopHotkeys: "stopHotkeys",
  ListDisplays: "listDisplays",
  ListAudioDevices: "listAudioDevices",
  Shutdown: "shutdown",
} as const;

/** Methods/notifications the recorder sends to the desktop app (recorder -> UI). */
export const UiMethods = {
  Handshake: "handshake",
  Ready: "ready",
  State: "state",
  ClipSaved: "clipSaved",
  ClipFailed: "clipFailed",
  GameStarted: "gameStarted",
  GameEnded: "gameEnded",
  HotkeyCaptured: "hotkeyCaptured",
  CaptureStarted: "captureStarted",
  CaptureStopped: "captureStopped",
  FallbackEncoder: "fallbackEncoder",
  RecorderError: "recorderError",
  Log: "log",
} as const;

export type RecorderState =
  | "starting"
  | "idle"
  | "capturing"
  | "saving"
  | "error"
  | "stopping";

export interface HandshakeParams {
  recorderVersion: string;
  protocolVersion: number;
  pid: number;
}

export interface HandshakeResult {
  /** Server acknowledges and returns the negotiated protocol version. */
  protocolVersion: number;
}

export interface ExecuteActionParams {
  /** e.g. "clip;30", "bookmark", "screenshot". */
  action: string;
  source: "hotkey" | "ui" | "api";
}

export interface ExecuteActionResult {
  accepted: boolean;
  /** Error code when not accepted (mirrors Medal 1001-1004). */
  reason?: "unknownAction" | "notRecording" | "clipsDisabled" | "noSession";
}

export interface DisplayInfo {
  index: number;
  name: string;
  width: number;
  height: number;
  refreshHz: number;
  isPrimary: boolean;
}

export interface AudioDeviceInfo {
  id: string;
  name: string;
  kind: "render" | "capture";
  isDefault: boolean;
}

export interface GameInfo {
  processName: string;
  windowTitle: string;
  pid: number;
}

export interface StateParams {
  state: RecorderState;
}

export interface ClipFailedParams {
  reason: SavedClip extends never ? never : string;
  message: string;
}

export interface FallbackEncoderParams {
  from: string;
  to: string;
  reason: string;
}

export interface RecorderErrorParams {
  code: string;
  message: string;
  fatal: boolean;
}

export interface LogParams {
  level: "debug" | "info" | "warn" | "error";
  message: string;
}

/** The protocol version both peers must agree on. Bump on breaking changes. */
export const PROTOCOL_VERSION = 1;
