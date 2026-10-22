import path from "node:path";
import { describe, expect, it } from "vitest";
import { resolveRecorderLaunch } from "./recorderLaunch.js";

describe("resolveRecorderLaunch", () => {
  const files = new Set<string>();
  const existsSync = (p: string) => files.has(path.normalize(p));

  it("uses GAMERPLUG_RECORDER_EXE when the file exists", () => {
    files.clear();
    files.add("/tmp/GamerPlug.Recorder.exe");
    const launch = resolveRecorderLaunch({
      platform: "linux",
      env: { GAMERPLUG_RECORDER_EXE: "/tmp/GamerPlug.Recorder.exe" },
      resourcesPath: "/resources",
      appPath: "/clipper/apps/desktop",
      existsSync,
    });
    expect(launch).toEqual({
      command: "/tmp/GamerPlug.Recorder.exe",
      args: [],
      cwd: "/tmp",
      kind: "windows",
    });
  });

  it("auto-selects the Dev dll on non-Windows", () => {
    files.clear();
    const dll = path.normalize(
      "/clipper/recorder/GamerPlug.Recorder.Dev/bin/Release/net8.0/GamerPlug.Recorder.Dev.dll",
    );
    files.add(dll);
    const launch = resolveRecorderLaunch({
      platform: "darwin",
      env: {},
      resourcesPath: "/resources",
      appPath: "/clipper/apps/desktop",
      existsSync,
    });
    expect(launch?.kind).toBe("dev");
    expect(launch?.command).toBe("dotnet");
    expect(launch?.args[0]).toBe(dll);
  });

  it("forces the Dev recorder on Windows via GAMERPLUG_DEV_RECORDER", () => {
    files.clear();
    const dll = path.normalize(
      "/clipper/recorder/GamerPlug.Recorder.Dev/bin/Debug/net8.0/GamerPlug.Recorder.Dev.dll",
    );
    const win = path.normalize("/resources/recorder/GamerPlug.Recorder.exe");
    files.add(dll);
    files.add(win);
    const launch = resolveRecorderLaunch({
      platform: "win32",
      env: { GAMERPLUG_DEV_RECORDER: "1" },
      resourcesPath: "/resources",
      appPath: "/clipper/apps/desktop",
      existsSync,
    });
    expect(launch?.kind).toBe("dev");
    expect(launch?.command).toBe("dotnet");
  });

  it("keeps the Windows exe as default on win32", () => {
    files.clear();
    const win = path.normalize("/resources/recorder/GamerPlug.Recorder.exe");
    files.add(win);
    const launch = resolveRecorderLaunch({
      platform: "win32",
      env: {},
      resourcesPath: "/resources",
      appPath: "/clipper/apps/desktop",
      existsSync,
    });
    expect(launch).toEqual({
      command: win,
      args: [],
      cwd: path.normalize("/resources/recorder"),
      kind: "windows",
    });
  });
});
