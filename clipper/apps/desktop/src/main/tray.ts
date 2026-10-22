import { Menu, Tray, nativeImage, type BrowserWindow, app, type NativeImage } from "electron";
import fs from "node:fs";
import { desktopPackageRoot, macTemplateIconPaths, trayIconPath } from "./trayIcon.js";
import type { RecorderSupervisor } from "./recorderSupervisor.js";

/** GamerPlug GP mark: macOS template (black+alpha) or Windows .ico / Linux png. */
export function loadTrayImage(root = desktopPackageRoot()): NativeImage {
  if (process.platform === "darwin") {
    const image = nativeImage.createEmpty();
    for (const rep of macTemplateIconPaths(root)) {
      if (!fs.existsSync(rep.path)) continue;
      const encoded = nativeImage.createFromPath(rep.path);
      image.addRepresentation({
        width: rep.width,
        height: rep.height,
        scaleFactor: rep.scaleFactor,
        buffer: encoded.toPNG(),
      });
    }
    image.setTemplateImage(true);
    return image;
  }
  return nativeImage.createFromPath(trayIconPath(process.platform, root));
}

export function createTray(window: BrowserWindow, supervisor: RecorderSupervisor): Tray {
  const tray = new Tray(loadTrayImage());
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
  return tray;
}
