import { describe, expect, it } from "vitest";
import { verifyConnection } from "./verifyOrigin.js";

describe("verifyConnection", () => {
  it("allows connections with no Origin header", () => {
    expect(verifyConnection({ host: "127.0.0.1:10603" })).toEqual({ allowed: true });
  });

  it("rejects connections with an Origin header", () => {
    expect(verifyConnection({ origin: "http://evil.example" })).toMatchObject({
      allowed: false,
      reason: "origin-header-present",
    });
  });

  it("rejects regardless of header casing", () => {
    expect(verifyConnection({ Origin: "https://app.example" } as never).allowed).toBe(false);
    expect(verifyConnection({ ORIGIN: "null" } as never).allowed).toBe(false);
  });

  it("allows an empty Origin header value", () => {
    expect(verifyConnection({ origin: "" }).allowed).toBe(true);
  });
});
