/**
 * Hotkey action grammar, mirroring the string actions Medal's encoder uses
 * (REPORT.md §1 "Clip-save flow"): `clip;<seconds>`, `segment_toggle`,
 * `bookmark`, `screenshot`. Parsing lives here so the recorder and UI agree.
 */

export type HotkeyActionKind =
  | "clip"
  | "segment_toggle"
  | "bookmark"
  | "screenshot";

export interface ParsedHotkeyAction {
  kind: HotkeyActionKind;
  /** Only present for `clip`: the number of seconds to save. */
  seconds?: number;
}

export class HotkeyActionError extends Error {}

export function parseHotkeyAction(raw: string): ParsedHotkeyAction {
  const trimmed = raw.trim();
  if (trimmed.length === 0) {
    throw new HotkeyActionError("empty hotkey action");
  }
  const [kind, ...rest] = trimmed.split(";");
  switch (kind) {
    case "clip": {
      const arg = rest[0];
      if (arg === undefined || arg === "") {
        throw new HotkeyActionError("clip action requires seconds, e.g. clip;30");
      }
      const seconds = Number(arg);
      if (!Number.isFinite(seconds) || seconds <= 0) {
        throw new HotkeyActionError(`invalid clip seconds: ${arg}`);
      }
      return { kind: "clip", seconds };
    }
    case "segment_toggle":
    case "bookmark":
    case "screenshot":
      return { kind };
    default:
      throw new HotkeyActionError(`unknown hotkey action: ${kind}`);
  }
}

export function formatHotkeyAction(action: ParsedHotkeyAction): string {
  if (action.kind === "clip") {
    if (action.seconds === undefined) {
      throw new HotkeyActionError("clip action requires seconds");
    }
    return `clip;${action.seconds}`;
  }
  return action.kind;
}
