import { describe, expect, it } from "vitest";
import { Crc32c, crc32c, crc32cBase64 } from "./crc32c.js";

const enc = (s: string) => new TextEncoder().encode(s);

describe("crc32c", () => {
  // Reference vectors for CRC32C (Castagnoli).
  it("matches known vectors", () => {
    expect(crc32c(enc(""))).toBe(0x00000000);
    // Canonical CRC32C (Castagnoli) check value for the ASCII "123456789".
    expect(crc32c(enc("123456789"))).toBe(0xe3069283);
  });

  it("is order-dependent and streamable", () => {
    const whole = new Crc32c().update(enc("hello world")).digest();
    const streamed = new Crc32c().update(enc("hello ")).update(enc("world")).digest();
    expect(streamed).toBe(whole);
  });

  it("produces base64 of the big-endian digest", () => {
    // crc32c("123456789") = 0xe3069283 -> bytes e3 06 92 83 -> base64 4waSgw==
    expect(crc32cBase64(enc("123456789"))).toBe("4waSgw==");
  });
});
