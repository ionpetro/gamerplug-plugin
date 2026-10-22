import { app } from "electron";
import fs from "node:fs";
import path from "node:path";

export function userData(...parts: string[]): string {
  const dir = app.getPath("userData");
  fs.mkdirSync(dir, { recursive: true });
  return path.join(dir, ...parts);
}

export function defaultClipFolder(): string {
  const videos = app.getPath("videos");
  const dir = path.join(videos, "GamerPlug Clips");
  fs.mkdirSync(dir, { recursive: true });
  return dir;
}

/** Recorder exe: env override, then extraResources, then a local build output. */
export function recorderExecutable(): string | undefined {
  const env = process.env.GAMERPLUG_RECORDER_EXE;
  if (env && fs.existsSync(env)) return env;

  const extra = path.join(process.resourcesPath, "recorder", "GamerPlug.Recorder.exe");
  if (fs.existsSync(extra)) return extra;

  const local = path.resolve(
    app.getAppPath(),
    "..",
    "..",
    "recorder",
    "GamerPlug.Recorder",
    "bin",
    "Release",
    "net8.0-windows10.0.19041.0",
    "GamerPlug.Recorder.exe",
  );
  if (fs.existsSync(local)) return local;
  return undefined;
}

export function ffmpegExecutable(): string | undefined {
  const env = process.env.GAMERPLUG_FFMPEG;
  if (env && fs.existsSync(env)) return env;
  const extra = path.join(process.resourcesPath, "recorder", "ffmpeg.exe");
  if (fs.existsSync(extra)) return extra;
  return undefined;
}
