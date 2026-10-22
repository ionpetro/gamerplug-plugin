import { describe, expect, it } from "vitest";
import path from "node:path";

describe("thumbnail output path", () => {
  it("places the jpeg next to the source mp4", () => {
    const input = path.join("clips", "clip-abc.mp4");
    const parsed = path.parse(input);
    const out = path.join(parsed.dir, `${parsed.name}.jpg`);
    expect(out).toBe(path.join("clips", "clip-abc.jpg"));
  });
});
