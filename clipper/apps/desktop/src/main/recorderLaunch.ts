import fs from "node:fs";
import path from "node:path";

export interface RecorderLaunch {
  command: string;
  args: string[];
  cwd?: string;
  kind: "windows" | "dev";
  env?: NodeJS.Dict<string | undefined>;
}

/** Well-known locations GUI-launched Electron will not have on PATH. */
export const DOTNET_WELL_KNOWN_UNIX = [
  "/opt/homebrew/bin/dotnet",
  "/usr/local/share/dotnet/dotnet",
  "/usr/local/bin/dotnet",
] as const;

/**
 * Locate a `dotnet` host. Finder/Dock launches on macOS often have a stripped
 * PATH, so Homebrew (`/opt/homebrew/bin`) and the Microsoft layout
 * (`/usr/local/share/dotnet`) are checked explicitly.
 */
export function resolveDotnetExecutable(input: {
  platform: string;
  env: NodeJS.Dict<string | undefined>;
  existsSync?: (p: string) => boolean;
}): string {
  const probe = input.existsSync ?? ((p: string) => fs.existsSync(p));
  const exe = input.platform === "win32" ? "dotnet.exe" : "dotnet";
  const candidates: string[] = [];
  if (input.env.GAMERPLUG_DOTNET) candidates.push(input.env.GAMERPLUG_DOTNET);
  if (input.env.DOTNET_ROOT) candidates.push(path.join(input.env.DOTNET_ROOT, exe));
  const pathValue = input.env.PATH ?? input.env.Path ?? "";
  for (const dir of pathValue.split(path.delimiter)) {
    if (dir) candidates.push(path.join(dir, exe));
  }
  if (input.platform !== "win32") candidates.push(...DOTNET_WELL_KNOWN_UNIX);
  const found = candidates.find((p) => exists(p, probe));
  return found ?? exe;
}

function extraEnvForDotnet(
  dotnetPath: string,
  inheritedPath: string | undefined,
): NodeJS.Dict<string | undefined> {
  const env: NodeJS.Dict<string | undefined> = { DOTNET_ROLL_FORWARD: "Major" };
  const dir = path.dirname(dotnetPath);
  if (dir.endsWith(`${path.sep}share${path.sep}dotnet`) || path.basename(dir) === "dotnet") {
    env.DOTNET_ROOT = dir;
  }
  // Finder/Dock PATH is often `/usr/bin:/bin`. Keep Homebrew + the host dir
  // in front so `dotnet` and `ffmpeg` resolve the same way as in a shell.
  const prefix = [
    path.isAbsolute(dotnetPath) ? dir : undefined,
    "/opt/homebrew/bin",
    "/usr/local/share/dotnet",
    "/usr/local/bin",
  ].filter((p): p is string => Boolean(p));
  const unique = [...new Set(prefix)];
  env.PATH = inheritedPath ? `${unique.join(path.delimiter)}${path.delimiter}${inheritedPath}` : unique.join(path.delimiter);
  return env;
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

function launchFromPath(
  file: string,
  kind: RecorderLaunch["kind"],
  input: {
    platform: string;
    env: NodeJS.Dict<string | undefined>;
    existsSync?: (p: string) => boolean;
  },
): RecorderLaunch {
  if (file.endsWith(".dll")) {
    const command = resolveDotnetExecutable(input);
    return {
      command,
      args: [file],
      cwd: path.dirname(file),
      kind,
      env: extraEnvForDotnet(command, input.env.PATH ?? input.env.Path),
    };
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
    return launchFromPath(envExe, kind, input);
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
    if (devDll) return launchFromPath(devDll, "dev", input);

    const devExeName = input.platform === "win32" ? "GamerPlug.Recorder.Dev.exe" : "GamerPlug.Recorder.Dev";
    const devExe = firstExisting(
      [
        path.join(input.resourcesPath, "recorder", devExeName),
        path.join(root, "recorder", "GamerPlug.Recorder.Dev", "bin", "Release", "net8.0", devExeName),
        path.join(root, "recorder", "GamerPlug.Recorder.Dev", "bin", "Debug", "net8.0", devExeName),
      ],
      probe,
    );
    if (devExe) return launchFromPath(devExe, "dev", input);
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
  if (winExe) return launchFromPath(winExe, "windows", input);
  return undefined;
}
