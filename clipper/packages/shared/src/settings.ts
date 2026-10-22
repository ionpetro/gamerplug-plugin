/**
 * Recorder + app settings. These mirror the capability surface described in the
 * Medal reverse-engineering report (REPORT.md §1 "Settings passed"), trimmed to
 * what GamerPlug Clipper actually implements. The desktop UI owns these values,
 * persists them, and pushes the recorder-relevant subset to the recorder process
 * over IPC via the `applySettings` method.
 */

export type EncoderBackend =
  | "auto"
  | "nvenc" // NVIDIA NVENC
  | "amf" // AMD AMF
  | "qsv" // Intel QuickSync (oneVPL)
  | "software"; // libx264 / libopenh264 (FFmpeg LGPL)

export type VideoCodec = "h264" | "hevc" | "av1";

export type CaptureMode =
  | "auto"
  /** Windows.Graphics.Capture — preferred, works for window + monitor. */
  | "wgc"
  /** DXGI Desktop Duplication — monitor-only fallback. */
  | "dxgi";

/**
 * How game/system/mic audio is laid down. `single` = one mixed track.
 * `split` = separate tracks (game / chat / mic), enabled by per-process
 * loopback (WASAPI `ActivateAudioInterfaceAsync` process loopback).
 */
export type AudioMode = "single" | "split";

export interface AudioDeviceSelection {
  /** WASAPI endpoint id, or "default". */
  deviceId: string;
  /** 0.0 – 2.0 linear gain applied before mux. */
  gain: number;
  enabled: boolean;
}

export interface MicSettings extends AudioDeviceSelection {
  /** RNNoise denoise via FFmpeg `arnndn`. */
  noiseSuppression: boolean;
  /** Noise gate threshold in dBFS; frames quieter than this are muted. */
  noiseGateDb: number;
}

export interface CaptureSettings {
  mode: CaptureMode;
  /** Monitor index for monitor capture; ignored when a game window is targeted. */
  monitorIndex: number;
  fps: number;
  /** Encoded video bitrate in kbps. */
  bitrateKbps: number;
  codec: VideoCodec;
  encoder: EncoderBackend;
  width: number;
  height: number;
  showCursor: boolean;
  /**
   * When true the replay buffer lives entirely in RAM (encoded packets).
   * When false it is backed by preallocated disk segments. See REPORT.md §3
   * "Replay buffer".
   */
  inMemoryBuffer: boolean;
}

export interface AudioSettings {
  mode: AudioMode;
  /** System/game loopback capture. */
  system: AudioDeviceSelection;
  mic: MicSettings;
}

export interface ReplaySettings {
  /** Rolling buffer length in seconds — the maximum a single hotkey save can produce. */
  bufferSeconds: number;
  /** Default clip length produced by the `clip` hotkey, in seconds. */
  clipSeconds: number;
  /** Where saved clips are written before upload. */
  clipFolder: string;
  /** Soft cap on local clip storage; oldest clips are pruned past this. */
  storageLimitGb: number;
}

export interface HotkeyBinding {
  /** e.g. "clip;30", "clip;15", "segment_toggle", "bookmark", "screenshot". */
  action: string;
  /** Electron/accelerator-style combo, e.g. "F9", "Ctrl+Shift+C". */
  accelerator: string;
}

export interface Settings {
  capture: CaptureSettings;
  audio: AudioSettings;
  replay: ReplaySettings;
  hotkeys: HotkeyBinding[];
  /** Automatically upload saved clips to GamerPlug. */
  autoUpload: boolean;
  /** Launch the recorder when the app starts. */
  recordOnStartup: boolean;
  /** Start the desktop app minimized to the system tray. */
  startMinimized: boolean;
}

export const DEFAULT_SETTINGS: Settings = {
  capture: {
    mode: "auto",
    monitorIndex: 0,
    fps: 60,
    bitrateKbps: 30_000,
    codec: "h264",
    encoder: "auto",
    width: 1920,
    height: 1080,
    showCursor: true,
    inMemoryBuffer: true,
  },
  audio: {
    mode: "single",
    system: { deviceId: "default", gain: 1.0, enabled: true },
    mic: {
      deviceId: "default",
      gain: 1.0,
      enabled: false,
      noiseSuppression: true,
      noiseGateDb: -45,
    },
  },
  replay: {
    bufferSeconds: 120,
    clipSeconds: 30,
    clipFolder: "",
    storageLimitGb: 20,
  },
  hotkeys: [
    { action: "clip;30", accelerator: "F9" },
    { action: "clip;15", accelerator: "F8" },
    { action: "bookmark", accelerator: "F7" },
    { action: "screenshot", accelerator: "F6" },
  ],
  autoUpload: false,
  recordOnStartup: true,
  startMinimized: false,
};
