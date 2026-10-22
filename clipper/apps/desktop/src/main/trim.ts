import { spawn } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { ffmpegExecutable } from "./paths.js";

export interface TrimRequest {
  inputPath: string;
  startSec: number;
  endSec: number;
}

/**
 * Lossless trim via ffmpeg `-c copy`. Returns the new file path. Requires
 * ffmpeg on PATH or bundled next to the recorder.
 */
export function trimCopy(req: TrimRequest): Promise<string> {
  const ffmpeg = ffmpegExecutable() ?? "ffmpeg";
  if (req.endSec <= req.startSec) {
    return Promise.reject(new Error("end must be after start"));
  }
  const parsed = path.parse(req.inputPath);
  const out = path.join(parsed.dir, `${parsed.name}.trim${parsed.ext}`);
  return new Promise((resolve, reject) => {
    const args = [
      "-hide_banner",
      "-y",
      "-ss",
      String(req.startSec),
      "-to",
      String(req.endSec),
      "-i",
      req.inputPath,
      "-c",
      "copy",
      out,
    ];
    const proc = spawn(ffmpeg, args, { windowsHide: true });
    let err = "";
    proc.stderr.on("data", (d: Buffer) => {
      err += d.toString();
    });
    proc.on("error", (e) => reject(e));
    proc.on("close", (code) => {
      if (code === 0 && fs.existsSync(out)) resolve(out);
      else reject(new Error(`ffmpeg trim failed (${code}): ${err.slice(-400)}`));
    });
  });
}
