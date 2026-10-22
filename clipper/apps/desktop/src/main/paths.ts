import { app } from "electron";
import fs from "node:fs";
import path from "node:path";
import { resolveRecorderLaunch, type RecorderLaunch } from "./recorderLaunch.js";

export type { RecorderLaunch } from "./recorderLaunch.js";
export { resolveRecorderLaunch, useDevRecorder } from "./recorderLaunch.js";

export function userData(...parts: string[]): string {
  const dir = app.getPath("userData");
  fs.mkdirSync(dir, { recursive: true });
  return path.join(dir, ...parts);
}

export function defaultClipFolder(): string {
  const env = process.env.GAMERPLUG_CLIP_FOLDER;
  if (env) {
    fs.mkdirSync(env, { recursive: true });
    return env;
  }
  const videos = app.getPath("videos");
  const dir = path.join(videos, "GamerPlug Clips");
  fs.mkdirSync(dir, { recursive: true });
  return dir;
}

export function recorderLaunch(): RecorderLaunch | undefined {
  return resolveRecorderLaunch({
    platform: process.platform,
    env: process.env,
    resourcesPath: process.resourcesPath,
    appPath: app.getAppPath(),
  });
}

/** @deprecated Use {@link recorderLaunch}; kept for call-sites that only need a path. */
export function recorderExecutable(): string | undefined {
  const launch = recorderLaunch();
  if (!launch) return undefined;
  if (launch.command === "dotnet") return launch.args[0];
  return launch.command;
}

export function ffmpegExecutable(): string | undefined {
  const env = process.env.GAMERPLUG_FFMPEG;
  if (env && fs.existsSync(env)) return env;
  const extraWin = path.join(process.resourcesPath, "recorder", "ffmpeg.exe");
  if (fs.existsSync(extraWin)) return extraWin;
  const extraUnix = path.join(process.resourcesPath, "recorder", "ffmpeg");
  if (fs.existsSync(extraUnix)) return extraUnix;
  return findOnPath(process.platform === "win32" ? "ffmpeg.exe" : "ffmpeg")
    ?? findOnPath("ffmpeg")
    ?? findOnPath("ffmpeg.exe");
}

function findOnPath(name: string): string | undefined {
  const parts = (process.env.PATH ?? "").split(path.delimiter);
  for (const dir of parts) {
    if (!dir) continue;
    const candidate = path.join(dir, name);
    if (fs.existsSync(candidate)) return candidate;
  }
  return undefined;
}
