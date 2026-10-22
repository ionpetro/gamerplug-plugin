import { app, BrowserWindow, globalShortcut, ipcMain, shell } from "electron";
import path from "node:path";
import { fileURLToPath } from "node:url";
import type { LibraryClip, Settings } from "@gamerplug/shared";
import { hasPersistedSettings, loadSettings, saveSettings } from "./settingsStore.js";
import { ClipLibrary } from "./library.js";
import { RecorderSupervisor } from "./recorderSupervisor.js";
import { createTray } from "./tray.js";
import { shouldShowMainWindow, windowIconPath } from "./trayIcon.js";
import { loadSession, saveSession } from "./auth.js";
import { loginWithPassword, uploadClip, type UploadMode } from "./uploadOrchestrator.js";
import { extractThumbnail, trimCopy } from "./trim.js";

if (process.env.GAMERPLUG_E2E === "1") {
  app.commandLine.appendSwitch("no-sandbox");
  app.commandLine.appendSwitch("disable-gpu");
  app.commandLine.appendSwitch("disable-dev-shm-usage");
}

const __dirname = path.dirname(fileURLToPath(import.meta.url));

let mainWindow: BrowserWindow | undefined;
let supervisor: RecorderSupervisor;
const library = new ClipLibrary();
let settings = loadSettings();
let uploadMode: UploadMode = (process.env.GAMERPLUG_UPLOAD_MODE as UploadMode) || "stub";

let isQuitting = false;

function persistedSettings(): boolean {
  try {
    return hasPersistedSettings();
  } catch {
    return false;
  }
}

function revealWindow(win: BrowserWindow, reason: "launch" | "activate" | "second-instance"): void {
  if (
    shouldShowMainWindow({
      startMinimized: settings.startMinimized,
      hasPersistedSettings: persistedSettings(),
      reason,
    })
  ) {
    win.show();
    win.focus();
  }
}

function showMainWindow(reason: "launch" | "activate" | "second-instance"): void {
  if (!mainWindow || mainWindow.isDestroyed()) {
    mainWindow = createWindow();
    mainWindow.once("ready-to-show", () => revealWindow(mainWindow!, reason));
    loadWindow(mainWindow);
    return;
  }
  revealWindow(mainWindow, reason);
}

function createWindow(): BrowserWindow {
  const win = new BrowserWindow({
    width: 1100,
    height: 740,
    minWidth: 880,
    minHeight: 560,
    title: "GamerPlug Clipper",
    backgroundColor: "#0b0d12",
    show: false,
    icon: windowIconPath(process.platform),
    webPreferences: {
      preload: path.join(__dirname, "../preload/index.cjs"),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: false,
    },
  });
  win.on("close", (e) => {
    if (!isQuitting) {
      e.preventDefault();
      win.hide();
    }
  });
  win.webContents.setWindowOpenHandler(({ url }) => {
    void shell.openExternal(url);
    return { action: "deny" };
  });
  return win;
}

function loadWindow(win: BrowserWindow): void {
  const devUrl = process.env.ELECTRON_RENDERER_URL;
  if (devUrl) void win.loadURL(devUrl);
  else void win.loadFile(path.join(__dirname, "../renderer/index.html"));
}

function send(channel: string, payload: unknown): void {
  mainWindow?.webContents.send(channel, payload);
}

function bindIpc(): void {
  ipcMain.handle("settings:get", () => settings);
  ipcMain.handle("settings:set", async (_e, next: Settings) => {
    settings = next;
    saveSettings(settings);
    try {
      await supervisor.applySettings(settings);
      bindShellHotkeys();
    } catch {
      /* recorder may be disconnected */
    }
    return settings;
  });

  ipcMain.handle("library:list", () => library.list());
  ipcMain.handle("library:patch", (_e, id: string, patch: Partial<LibraryClip>) => library.patch(id, patch));
  ipcMain.handle("library:remove", (_e, id: string) => library.remove(id));

  ipcMain.handle("auth:session", () => loadSession() ?? null);
  ipcMain.handle("auth:login", async (_e, email: string, password: string) => {
    const session = await loginWithPassword(email, password);
    saveSession(session);
    uploadMode = "supabase";
    return session;
  });
  ipcMain.handle("auth:logout", () => {
    saveSession(undefined);
    uploadMode = "stub";
    return null;
  });
  ipcMain.handle("auth:setUploadMode", (_e, mode: UploadMode) => {
    uploadMode = mode;
    return uploadMode;
  });
  ipcMain.handle("auth:uploadMode", () => uploadMode);

  ipcMain.handle("recorder:state", () => supervisor.state);
  ipcMain.handle("recorder:start", () => supervisor.startCapture());
  ipcMain.handle("recorder:stop", () => supervisor.stopCapture());
  ipcMain.handle("recorder:action", (_e, action: string) => supervisor.executeAction(action, "ui"));
  ipcMain.handle("recorder:displays", () => supervisor.listDisplays());
  ipcMain.handle("recorder:audioDevices", () => supervisor.listAudioDevices());

  ipcMain.handle("clip:upload", async (_e, id: string) => {
    const clip = library.get(id);
    if (!clip) throw new Error("clip not found");
    library.patch(id, { status: "queued" });
    const result = await uploadClip(
      clip,
      settings,
      (event) => {
        if (event.type === "status") library.upsert(event.clip);
        send("upload:event", event);
      },
      uploadMode,
    );
    library.upsert(result);
    return result;
  });

  ipcMain.handle("clip:trim", async (_e, id: string, startSec: number, endSec: number) => {
    const clip = library.get(id);
    if (!clip) throw new Error("clip not found");
    const out = await trimCopy({ inputPath: clip.filePath, startSec, endSec });
    return library.patch(id, {
      filePath: out,
      durationSec: endSec - startSec,
    });
  });
}

async function main(): Promise<void> {
  // Single instance so hotkeys and the recorder port stay unique.
  if (!app.requestSingleInstanceLock()) {
    app.quit();
    return;
  }
  await app.whenReady();
  library.load();
  supervisor = new RecorderSupervisor();
  supervisor.on("clipSaved", (saved) => {
    const clip = library.addFromSaved(saved);
    send("library:changed", library.list());
    send("clip:saved", clip);
    void extractThumbnail(clip.filePath, clip.durationSec).then((thumbnailPath) => {
      if (!thumbnailPath) return;
      library.patch(clip.id, { thumbnailPath });
      send("library:changed", library.list());
    });
    if (settings.autoUpload) {
      void uploadClip(clip, settings, (e) => send("upload:event", e), uploadMode)
        .then((updated) => {
          library.upsert(updated);
          send("library:changed", library.list());
        })
        .catch(() => {
          library.patch(clip.id, { status: "failed" });
          send("library:changed", library.list());
        });
    }
  });
  supervisor.on("state", (s) => send("recorder:state", s));
  supervisor.on("clipFailed", (f) => send("clip:failed", f));
  supervisor.on("log", (l) => send("recorder:log", l));
  supervisor.on("fallbackEncoder", (f) => send("recorder:fallback", f));

  bindIpc();
  mainWindow = createWindow();
  mainWindow.once("ready-to-show", () => revealWindow(mainWindow!, "launch"));
  loadWindow(mainWindow);
  try {
    createTray(mainWindow, supervisor);
  } catch (err) {
    console.warn("tray unavailable", err);
  }

  try {
    await supervisor.start();
    await supervisor.applySettings(settings);
    await supervisor.startHotkeys();
    bindShellHotkeys();
    if (settings.recordOnStartup) await supervisor.startCapture();
  } catch (err) {
    console.warn("recorder missing or failed to start", err);
  }

  app.on("second-instance", () => showMainWindow("second-instance"));
  // macOS: clicking the dock icon while the window is hidden (close-to-tray).
  app.on("activate", () => showMainWindow("activate"));
}

function bindShellHotkeys(): void {
  try {
    globalShortcut.unregisterAll();
  } catch {
    /* none registered */
  }
  // Windows capture host owns the low-level hook. Dev recorder (macOS/Linux, or
  // GAMERPLUG_DEV_RECORDER=1) relies on Electron globalShortcut instead.
  if (supervisor.recorderKind === "windows") return;
  for (const binding of settings.hotkeys) {
    try {
      globalShortcut.register(binding.accelerator, () => {
        void supervisor.executeAction(binding.action, "hotkey");
      });
    } catch (err) {
      console.warn(`hotkey ${binding.accelerator} not registered`, err);
    }
  }
}

app.on("before-quit", () => {
  isQuitting = true;
  try {
    globalShortcut.unregisterAll();
  } catch {
    /* none */
  }
  void supervisor?.stop();
});

void main();
