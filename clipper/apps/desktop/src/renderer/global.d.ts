import type { GamerPlugApi } from "../preload/index.ts";

declare global {
  interface Window {
    gamerplug: GamerPlugApi;
  }
}

export {};
