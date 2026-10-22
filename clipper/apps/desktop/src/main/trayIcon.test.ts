import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import {
  desktopPackageRoot,
  iconDir,
  iconFilesExist,
  macTemplateIconPaths,
  shouldShowMainWindow,
  trayIconPath,
  TRAY_ICON_FILES,
  windowIconPath,
} from "./trayIcon.js";

const here = path.dirname(fileURLToPath(import.meta.url));

function pngSize(file: string): { width: number; height: number } {
  const buf = fs.readFileSync(file);
  expect(buf.subarray(0, 8).equals(Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]))).toBe(true);
  return { width: buf.readUInt32BE(16), height: buf.readUInt32BE(20) };
}

describe("tray icons", () => {
  it("resolves the desktop package root from src/main", () => {
    const root = desktopPackageRoot(here);
    expect(path.basename(root)).toBe("desktop");
    expect(fs.existsSync(path.join(root, "package.json"))).toBe(true);
  });

  it("ships macOS template 16/32px at @1x and @2x plus Windows ico", () => {
    const root = desktopPackageRoot(here);
    expect(iconFilesExist(root)).toBe(true);
    const dir = iconDir(root);
    expect(pngSize(path.join(dir, TRAY_ICON_FILES.template16))).toEqual({ width: 16, height: 16 });
    expect(pngSize(path.join(dir, TRAY_ICON_FILES.template16x2))).toEqual({ width: 32, height: 32 });
    expect(pngSize(path.join(dir, TRAY_ICON_FILES.template32))).toEqual({ width: 32, height: 32 });
    expect(pngSize(path.join(dir, TRAY_ICON_FILES.template32x2))).toEqual({ width: 64, height: 64 });
    expect(pngSize(path.join(dir, TRAY_ICON_FILES.png))).toEqual({ width: 512, height: 512 });

    const ico = fs.readFileSync(path.join(dir, TRAY_ICON_FILES.ico));
    expect(ico.readUInt16LE(0)).toBe(0);
    expect(ico.readUInt16LE(2)).toBe(1); // ICO type
    expect(ico.readUInt16LE(4)).toBeGreaterThanOrEqual(1);
  });

  it("picks template / ico / png by platform", () => {
    const root = desktopPackageRoot(here);
    expect(trayIconPath("darwin", root)).toContain("trayTemplate.png");
    expect(trayIconPath("win32", root)).toContain("icon.ico");
    expect(trayIconPath("linux", root)).toContain("icon.png");
    expect(windowIconPath("win32", root)).toContain("icon.ico");
    expect(windowIconPath("darwin", root)).toContain("icon.png");
  });

  it("names the 16px file as a macOS Template image so Electron loads @2x", () => {
    const names = macTemplateIconPaths(desktopPackageRoot(here)).map((r) => path.basename(r.path));
    expect(names[0]).toMatch(/Template\.png$/);
    expect(names[1]).toMatch(/Template@2x\.png$/);
  });
});

describe("shouldShowMainWindow", () => {
  it("shows on first launch even if startMinimized defaults on", () => {
    expect(
      shouldShowMainWindow({ startMinimized: true, hasPersistedSettings: false, reason: "launch" }),
    ).toBe(true);
  });

  it("honors startMinimized after the user has settings", () => {
    expect(
      shouldShowMainWindow({ startMinimized: true, hasPersistedSettings: true, reason: "launch" }),
    ).toBe(false);
    expect(
      shouldShowMainWindow({ startMinimized: false, hasPersistedSettings: true, reason: "launch" }),
    ).toBe(true);
  });

  it("shows when activated from the dock or a second instance", () => {
    expect(
      shouldShowMainWindow({ startMinimized: true, hasPersistedSettings: true, reason: "activate" }),
    ).toBe(true);
    expect(
      shouldShowMainWindow({
        startMinimized: true,
        hasPersistedSettings: true,
        reason: "second-instance",
      }),
    ).toBe(true);
  });
});
