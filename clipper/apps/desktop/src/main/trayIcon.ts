import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

/** Package root (`apps/desktop`) whether this file is compiled to dist/main or run from src/main. */
export function desktopPackageRoot(fromDir = path.dirname(fileURLToPath(import.meta.url))): string {
  return path.resolve(fromDir, "../..");
}

export function iconDir(root = desktopPackageRoot()): string {
  return path.join(root, "icons");
}

/** Filenames under `icons/`. macOS template names include "Template" so Electron loads @2x. */
export const TRAY_ICON_FILES = {
  template16: "trayTemplate.png",
  template16x2: "trayTemplate@2x.png",
  template32: "trayTemplate32.png",
  template32x2: "trayTemplate32@2x.png",
  ico: "icon.ico",
  png: "icon.png",
} as const;

export interface MacTemplateRep {
  width: number;
  height: number;
  scaleFactor: number;
  path: string;
}

/** 16px and 32px menu-bar templates at @1x and @2x. */
export function macTemplateIconPaths(root = desktopPackageRoot()): MacTemplateRep[] {
  const dir = iconDir(root);
  return [
    { width: 16, height: 16, scaleFactor: 1, path: path.join(dir, TRAY_ICON_FILES.template16) },
    { width: 32, height: 32, scaleFactor: 2, path: path.join(dir, TRAY_ICON_FILES.template16x2) },
    { width: 32, height: 32, scaleFactor: 1, path: path.join(dir, TRAY_ICON_FILES.template32) },
    { width: 64, height: 64, scaleFactor: 2, path: path.join(dir, TRAY_ICON_FILES.template32x2) },
  ];
}

export function trayIconPath(platform: NodeJS.Platform, root = desktopPackageRoot()): string {
  const dir = iconDir(root);
  if (platform === "darwin") return path.join(dir, TRAY_ICON_FILES.template16);
  if (platform === "win32") return path.join(dir, TRAY_ICON_FILES.ico);
  return path.join(dir, TRAY_ICON_FILES.png);
}

export function windowIconPath(platform: NodeJS.Platform, root = desktopPackageRoot()): string {
  const dir = iconDir(root);
  return path.join(dir, platform === "win32" ? TRAY_ICON_FILES.ico : TRAY_ICON_FILES.png);
}

export function iconFilesExist(root = desktopPackageRoot()): boolean {
  const dir = iconDir(root);
  return Object.values(TRAY_ICON_FILES).every((name) => fs.existsSync(path.join(dir, name)));
}

/**
 * First process start (no settings.json yet) always shows the window so the
 * user can find the app. Dock / second-instance activation always shows it.
 * After that, honor `startMinimized`.
 */
export function shouldShowMainWindow(opts: {
  startMinimized: boolean;
  hasPersistedSettings: boolean;
  reason: "launch" | "activate" | "second-instance";
}): boolean {
  if (opts.reason === "activate" || opts.reason === "second-instance") return true;
  if (!opts.hasPersistedSettings) return true;
  return !opts.startMinimized;
}
