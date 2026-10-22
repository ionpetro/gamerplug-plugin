import { contextBridge, ipcRenderer } from "electron";
import type { LibraryClip, Settings } from "@gamerplug/shared";

const api = {
  settings: {
    get: () => ipcRenderer.invoke("settings:get") as Promise<Settings>,
    set: (s: Settings) => ipcRenderer.invoke("settings:set", s) as Promise<Settings>,
  },
  library: {
    list: () => ipcRenderer.invoke("library:list") as Promise<LibraryClip[]>,
    patch: (id: string, patch: Partial<LibraryClip>) =>
      ipcRenderer.invoke("library:patch", id, patch) as Promise<LibraryClip | undefined>,
    remove: (id: string) => ipcRenderer.invoke("library:remove", id) as Promise<boolean>,
  },
  auth: {
    session: () => ipcRenderer.invoke("auth:session"),
    login: (email: string, password: string) => ipcRenderer.invoke("auth:login", email, password),
    logout: () => ipcRenderer.invoke("auth:logout"),
    uploadMode: () => ipcRenderer.invoke("auth:uploadMode") as Promise<"stub" | "supabase">,
    setUploadMode: (mode: "stub" | "supabase") => ipcRenderer.invoke("auth:setUploadMode", mode),
  },
  recorder: {
    state: () => ipcRenderer.invoke("recorder:state") as Promise<string>,
    start: () => ipcRenderer.invoke("recorder:start"),
    stop: () => ipcRenderer.invoke("recorder:stop"),
    action: (action: string) => ipcRenderer.invoke("recorder:action", action),
    displays: () => ipcRenderer.invoke("recorder:displays"),
    audioDevices: () => ipcRenderer.invoke("recorder:audioDevices"),
  },
  clip: {
    upload: (id: string) => ipcRenderer.invoke("clip:upload", id) as Promise<LibraryClip>,
    trim: (id: string, startSec: number, endSec: number) =>
      ipcRenderer.invoke("clip:trim", id, startSec, endSec) as Promise<LibraryClip>,
  },
  on: (channel: string, handler: (payload: unknown) => void) => {
    const listener = (_e: Electron.IpcRendererEvent, payload: unknown) => handler(payload);
    ipcRenderer.on(channel, listener);
    return () => ipcRenderer.removeListener(channel, listener);
  },
};

contextBridge.exposeInMainWorld("gamerplug", api);

export type GamerPlugApi = typeof api;
