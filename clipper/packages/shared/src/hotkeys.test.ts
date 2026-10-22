import { describe, expect, it } from "vitest";
import { formatHotkeyAction, parseHotkeyAction, HotkeyActionError } from "./hotkeys.js";

describe("parseHotkeyAction", () => {
  it("parses clip with seconds", () => {
    expect(parseHotkeyAction("clip;30")).toEqual({ kind: "clip", seconds: 30 });
  });

  it("parses fractional clip seconds", () => {
    expect(parseHotkeyAction("clip;7.5")).toEqual({ kind: "clip", seconds: 7.5 });
  });

  it("parses simple actions", () => {
    expect(parseHotkeyAction("bookmark")).toEqual({ kind: "bookmark" });
    expect(parseHotkeyAction("segment_toggle")).toEqual({ kind: "segment_toggle" });
    expect(parseHotkeyAction("screenshot")).toEqual({ kind: "screenshot" });
  });

  it("rejects clip without seconds", () => {
    expect(() => parseHotkeyAction("clip")).toThrow(HotkeyActionError);
    expect(() => parseHotkeyAction("clip;")).toThrow(HotkeyActionError);
  });

  it("rejects non-positive or non-numeric seconds", () => {
    expect(() => parseHotkeyAction("clip;0")).toThrow(HotkeyActionError);
    expect(() => parseHotkeyAction("clip;-5")).toThrow(HotkeyActionError);
    expect(() => parseHotkeyAction("clip;abc")).toThrow(HotkeyActionError);
  });

  it("rejects unknown and empty actions", () => {
    expect(() => parseHotkeyAction("explode")).toThrow(HotkeyActionError);
    expect(() => parseHotkeyAction("   ")).toThrow(HotkeyActionError);
  });

  it("round-trips through format", () => {
    for (const raw of ["clip;30", "bookmark", "screenshot", "segment_toggle"]) {
      expect(formatHotkeyAction(parseHotkeyAction(raw))).toBe(raw);
    }
  });
});
