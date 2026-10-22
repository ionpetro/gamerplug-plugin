import fs from "node:fs";
import { GAMERPLUG_SUPABASE, type SupabaseSession } from "@gamerplug/upload";
import { userData } from "./paths.js";

const FILE = () => userData("session.json");

/** Public GamerPlug anon key (shipped in the web app; not a secret). */
export const GAMERPLUG_ANON_KEY =
  process.env.GAMERPLUG_ANON_KEY ??
  "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6Im51b3B3ZmVoYWVreHJybXpmcXl2Iiwicm9sZSI6ImFub24iLCJpYXQiOjE3NjAxMzQ1MDUsImV4cCI6MjA3NTcxMDUwNX0.vYZlByFys43Y4J1dgyeInH3rpjZiNZYAdbaMDdNFNaA";

export const GAMERPLUG_URL = process.env.GAMERPLUG_SUPABASE_URL ?? GAMERPLUG_SUPABASE.url;

export function loadSession(): SupabaseSession | undefined {
  try {
    if (!fs.existsSync(FILE())) return undefined;
    return JSON.parse(fs.readFileSync(FILE(), "utf8")) as SupabaseSession;
  } catch {
    return undefined;
  }
}

export function saveSession(session: SupabaseSession | undefined): void {
  if (!session) {
    if (fs.existsSync(FILE())) fs.unlinkSync(FILE());
    return;
  }
  fs.writeFileSync(FILE(), JSON.stringify(session, null, 2), { mode: 0o600 });
}
