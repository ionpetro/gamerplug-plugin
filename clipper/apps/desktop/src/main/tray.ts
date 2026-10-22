import { Menu, Tray, nativeImage, type BrowserWindow, app } from "electron";
import path from "node:path";
import type { RecorderSupervisor } from "./recorderSupervisor.js";

export function createTray(window: BrowserWindow, supervisor: RecorderSupervisor): Tray {
  const icon = nativeImage.createEmpty();
  const tray = new Tray(icon);
  tray.setToolTip("GamerPlug Clipper");

  const rebuild = () => {
    const menu = Menu.buildFromTemplate([
      { label: `Recorder: ${supervisor.state}`, enabled: false },
      { type: "separator" },
      {
        label: "Start capture",
        click: () => void supervisor.startCapture(),
      },
      {
        label: "Stop capture",
        click: () => void supervisor.stopCapture(),
      },
      {
        label: "Save 30s clip",
        click: () => void supervisor.executeAction("clip;30", "ui"),
      },
      { type: "separator" },
      {
        label: "Open GamerPlug Clipper",
        click: () => {
          window.show();
          window.focus();
        },
      },
      { type: "separator" },
      { label: "Quit", click: () => app.quit() },
    ]);
    tray.setContextMenu(menu);
  };

  supervisor.on("state", rebuild);
  rebuild();
  tray.on("click", () => {
    window.show();
    window.focus();
  });
  void path; // keep import for a future real icon at extraResources/icon.png
  return tray;
}
