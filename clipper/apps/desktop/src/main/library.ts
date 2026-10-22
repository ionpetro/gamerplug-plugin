import fs from "node:fs";
import type { LibraryClip, SavedClip } from "@gamerplug/shared";
import { userData } from "./paths.js";

const FILE = () => userData("library.json");

export class ClipLibrary {
  private clips: LibraryClip[] = [];

  load(): LibraryClip[] {
    try {
      if (fs.existsSync(FILE())) {
        this.clips = JSON.parse(fs.readFileSync(FILE(), "utf8")) as LibraryClip[];
      }
    } catch {
      this.clips = [];
    }
    return this.list();
  }

  list(): LibraryClip[] {
    return [...this.clips].sort((a, b) => b.savedAt - a.savedAt);
  }

  get(id: string): LibraryClip | undefined {
    return this.clips.find((c) => c.id === id);
  }

  upsert(clip: LibraryClip): LibraryClip {
    const idx = this.clips.findIndex((c) => c.id === clip.id);
    if (idx >= 0) this.clips[idx] = clip;
    else this.clips.unshift(clip);
    this.persist();
    return clip;
  }

  addFromSaved(saved: SavedClip): LibraryClip {
    const clip: LibraryClip = {
      ...saved,
      title: saved.gameTitle || `Clip ${new Date(saved.savedAt).toLocaleString()}`,
      description: "",
      isPublic: true,
      status: "local",
    };
    return this.upsert(clip);
  }

  patch(id: string, patch: Partial<LibraryClip>): LibraryClip | undefined {
    const existing = this.get(id);
    if (!existing) return undefined;
    return this.upsert({ ...existing, ...patch, id });
  }

  remove(id: string): boolean {
    const before = this.clips.length;
    this.clips = this.clips.filter((c) => c.id !== id);
    if (this.clips.length !== before) this.persist();
    return this.clips.length !== before;
  }

  private persist(): void {
    fs.writeFileSync(FILE(), JSON.stringify(this.clips, null, 2));
  }
}
