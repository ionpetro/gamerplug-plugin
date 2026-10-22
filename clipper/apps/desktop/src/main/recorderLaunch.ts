import fs from "node:fs";
import path from "node:path";

export interface RecorderLaunch {
  command: string;
  args: string[];
  cwd?: string;
  kind: "windows" | "dev";
}

function exists(file: string, probe: (p: string) => boolean): boolean {
  try {
    return probe(file);
  } catch {
    return false;
  }
}

export function useDevRecorder(platform: string, env: NodeJS.Dict<string | undefined>): boolean {
  const flag = (env.GAMERPLUG_DEV_RECORDER ?? "").toLowerCase();
  if (flag === "1" || flag === "true" || flag === "yes") return true;
  if (flag === "0" || flag === "false" || flag === "no") return false;
  return platform !== "win32";
}

function clipperRootFromAppPath(appPath: string): string {
  // Unpackaged: clipper/apps/desktop. Packaged: resources/app or app.asar.
  return path.resolve(appPath, "..", "..");
}

function launchFromPath(file: string, kind: RecorderLaunch["kind"]): RecorderLaunch {
  if (file.endsWith(".dll")) {
    return { command: "dotnet", args: [file], cwd: path.dirname(file), kind };
  }
  return { command: file, args: [], cwd: path.dirname(file), kind };
}

function firstExisting(candidates: string[], probe: (p: string) => boolean): string | undefined {
  return candidates.find((c) => exists(c, probe));
}

/**
 * Resolve which recorder process to spawn. Pure so unit tests can drive it
 * without booting Electron.
 *
 * Priority:
 * 1. GAMERPLUG_RECORDER_EXE (dll → `dotnet <dll>`, otherwise the file itself)
 * 2. Dev recorder on macOS/Linux, or when GAMERPLUG_DEV_RECORDER=1
 * 3. Windows GamerPlug.Recorder.exe (extraResources, then local publish)
 */
export function resolveRecorderLaunch(input: {
  platform: string;
  env: NodeJS.Dict<string | undefined>;
  resourcesPath: string;
  appPath: string;
  existsSync?: (p: string) => boolean;
}): RecorderLaunch | undefined {
  const probe = input.existsSync ?? ((p: string) => fs.existsSync(p));
  const envExe = input.env.GAMERPLUG_RECORDER_EXE;
  if (envExe && exists(envExe, probe)) {
    const kind = envExe.includes("Recorder.Dev") || envExe.endsWith(".dll") ? "dev" : "windows";
    return launchFromPath(envExe, kind);
  }

  const root = clipperRootFromAppPath(input.appPath);
  const wantDev = useDevRecorder(input.platform, input.env);

  if (wantDev) {
    const devDll = firstExisting(
      [
        path.join(input.resourcesPath, "recorder", "GamerPlug.Recorder.Dev.dll"),
        path.join(root, "recorder", "GamerPlug.Recorder.Dev", "bin", "Release", "net8.0", "GamerPlug.Recorder.Dev.dll"),
        path.join(root, "recorder", "GamerPlug.Recorder.Dev", "bin", "Debug", "net8.0", "GamerPlug.Recorder.Dev.dll"),
      ],
      probe,
    );
    if (devDll) return launchFromPath(devDll, "dev");

    const devExeName = input.platform === "win32" ? "GamerPlug.Recorder.Dev.exe" : "GamerPlug.Recorder.Dev";
    const devExe = firstExisting(
      [
        path.join(input.resourcesPath, "recorder", devExeName),
        path.join(root, "recorder", "GamerPlug.Recorder.Dev", "bin", "Release", "net8.0", devExeName),
        path.join(root, "recorder", "GamerPlug.Recorder.Dev", "bin", "Debug", "net8.0", devExeName),
      ],
      probe,
    );
    if (devExe) return launchFromPath(devExe, "dev");
  }

  const winExe = firstExisting(
    [
      path.join(input.resourcesPath, "recorder", "GamerPlug.Recorder.exe"),
      path.join(
        root,
        "recorder",
        "GamerPlug.Recorder",
        "bin",
        "Release",
        "net8.0-windows10.0.19041.0",
        "GamerPlug.Recorder.exe",
      ),
    ],
    probe,
  );
  if (winExe) return launchFromPath(winExe, "windows");
  return undefined;
}
