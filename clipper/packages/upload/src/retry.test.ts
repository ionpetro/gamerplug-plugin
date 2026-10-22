import { describe, expect, it, vi } from "vitest";
import {
  computeBackoffDelay,
  withRetry,
  DEFAULT_BACKOFF,
  type BackoffOptions,
} from "./retry.js";

const noJitter: BackoffOptions = {
  maxAttempts: 5,
  baseDelayMs: 100,
  maxDelayMs: 10_000,
  jitter: 0,
};

describe("computeBackoffDelay", () => {
  it("doubles each attempt without jitter", () => {
    expect(computeBackoffDelay(1, noJitter)).toBe(100);
    expect(computeBackoffDelay(2, noJitter)).toBe(200);
    expect(computeBackoffDelay(3, noJitter)).toBe(400);
  });

  it("caps at maxDelayMs", () => {
    expect(computeBackoffDelay(20, noJitter)).toBe(10_000);
  });

  it("applies jitter within bounds", () => {
    const opts: BackoffOptions = { ...noJitter, jitter: 0.5 };
    const d = computeBackoffDelay(2, opts, () => 1); // max jitter
    expect(d).toBe(100); // 200 * (1 - 0.5*1)
  });
});

describe("withRetry", () => {
  const instantSleep = () => Promise.resolve();

  it("returns on first success", async () => {
    const fn = vi.fn().mockResolvedValue("ok");
    await expect(withRetry(fn, noJitter, { sleep: instantSleep })).resolves.toBe("ok");
    expect(fn).toHaveBeenCalledTimes(1);
  });

  it("retries then succeeds", async () => {
    let n = 0;
    const fn = vi.fn(async () => {
      n++;
      if (n < 3) throw new Error("transient");
      return "ok";
    });
    const onRetry = vi.fn();
    await expect(
      withRetry(fn, noJitter, { sleep: instantSleep, onRetry }),
    ).resolves.toBe("ok");
    expect(fn).toHaveBeenCalledTimes(3);
    expect(onRetry).toHaveBeenCalledTimes(2);
  });

  it("gives up after maxAttempts", async () => {
    const fn = vi.fn(async () => {
      throw new Error("always");
    });
    await expect(withRetry(fn, noJitter, { sleep: instantSleep })).rejects.toThrow("always");
    expect(fn).toHaveBeenCalledTimes(noJitter.maxAttempts);
  });

  it("does not retry a non-retryable error", async () => {
    const fn = vi.fn(async () => {
      const err = Object.assign(new Error("fatal"), { retryable: false });
      throw err;
    });
    await expect(withRetry(fn, noJitter, { sleep: instantSleep })).rejects.toThrow("fatal");
    expect(fn).toHaveBeenCalledTimes(1);
  });

  it("stops when the signal is aborted", async () => {
    const controller = new AbortController();
    controller.abort();
    const fn = vi.fn(async () => "ok");
    await expect(
      withRetry(fn, noJitter, { sleep: instantSleep, signal: controller.signal }),
    ).rejects.toMatchObject({ name: "AbortError" });
    expect(fn).not.toHaveBeenCalled();
  });

  it("DEFAULT_BACKOFF is sane", () => {
    expect(DEFAULT_BACKOFF.maxAttempts).toBeGreaterThanOrEqual(5);
  });
});
