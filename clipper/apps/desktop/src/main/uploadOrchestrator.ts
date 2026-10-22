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
  const source = await fileDataSource(clip.filePath);
  return runUpload({
    clip,
    source,
    backend: createBackend(mode),
    onEvent,
  });
}

export async function loginWithPassword(email: string, password: string) {
  const backend = new SupabaseUploadBackend({
    supabaseUrl: GAMERPLUG_URL,
    anonKey: GAMERPLUG_ANON_KEY,
  });
  return backend.signInWithPassword(email, password);
}
