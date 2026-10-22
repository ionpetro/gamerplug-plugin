import fs from "node:fs";
import type { LibraryClip, Settings } from "@gamerplug/shared";
import {
  fileDataSource,
  runUpload,
  StubUploadBackend,
  SupabaseUploadBackend,
  type UploadBackend,
  type UploadEvent,
} from "@gamerplug/upload";
import { GAMERPLUG_ANON_KEY, GAMERPLUG_URL, loadSession } from "./auth.js";
import { userData } from "./paths.js";

export type UploadMode = "stub" | "supabase";

export function createBackend(mode: UploadMode): UploadBackend {
  if (mode === "stub") return new StubUploadBackend();
  const backend = new SupabaseUploadBackend({
    supabaseUrl: GAMERPLUG_URL,
    anonKey: GAMERPLUG_ANON_KEY,
    storageStrategy: process.env.GAMERPLUG_PRESIGN_URL ? "backend-presigned" : "supabase-signed",
    presignEndpoint: process.env.GAMERPLUG_PRESIGN_URL,
  });
  const session = loadSession();
  if (session) backend.setSession(session);
  return backend;
}

export async function uploadClip(
  clip: LibraryClip,
  settings: Settings,
  onEvent: (e: UploadEvent) => void,
  mode: UploadMode,
): Promise<LibraryClip> {
  void settings;
  const source = await fileDataSource(clip.filePath);
  const backend = createBackend(mode);
  const storageHttp = backend instanceof StubUploadBackend ? backend.storage.asHttpClient() : undefined;
  const result = await runUpload({
    clip,
    source,
    backend,
    storageHttp,
    onEvent,
  });
  persistLastUpload(result);
  return result;
}

function persistLastUpload(clip: LibraryClip): void {
  try {
    fs.writeFileSync(
      userData("last-upload.json"),
      JSON.stringify(
        {
          clipId: clip.id,
          remoteClipId: clip.remoteClipId,
          status: clip.status,
          crc32c: clip.crc32c,
          checksumVerified: clip.checksumVerified === true,
          remoteVideoUrl: clip.remoteVideoUrl,
          bytes: clip.fileSizeBytes,
        },
        null,
        2,
      ),
    );
  } catch {
    /* best-effort for the E2E harness */
  }
}

export async function loginWithPassword(email: string, password: string) {
  const backend = new SupabaseUploadBackend({
    supabaseUrl: GAMERPLUG_URL,
    anonKey: GAMERPLUG_ANON_KEY,
  });
  return backend.signInWithPassword(email, password);
}
