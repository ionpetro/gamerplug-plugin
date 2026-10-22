import fs from "node:fs";
import { DEFAULT_SETTINGS, type Settings } from "@gamerplug/shared";
import { defaultClipFolder, userData } from "./paths.js";

export function hasPersistedSettings(): boolean {
  try {
    return fs.existsSync(userData("settings.json"));
  } catch {
    return false;
  }
}

export function loadSettings(): Settings {
  const file = userData("settings.json");
  let loaded: Settings = {
    ...DEFAULT_SETTINGS,
    replay: { ...DEFAULT_SETTINGS.replay, clipFolder: defaultClipFolder() },
  };
  try {
    if (fs.existsSync(file)) {
      const parsed = JSON.parse(fs.readFileSync(file, "utf8")) as Partial<Settings>;
      loaded = {
        ...loaded,
        ...parsed,
        capture: { ...loaded.capture, ...parsed.capture },
        audio: { ...loaded.audio, ...parsed.audio, mic: { ...loaded.audio.mic, ...parsed.audio?.mic } },
        replay: { ...loaded.replay, ...parsed.replay },
        hotkeys: parsed.hotkeys ?? loaded.hotkeys,
      };
    }
  } catch {
    /* keep defaults */
  }
  if (!loaded.replay.clipFolder) loaded.replay.clipFolder = defaultClipFolder();
  return loaded;
}

export function saveSettings(settings: Settings): void {
  fs.writeFileSync(userData("settings.json"), JSON.stringify(settings, null, 2));
}
