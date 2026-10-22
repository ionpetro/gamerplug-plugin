/**
 * Exponential backoff with jitter, used for both resumable chunk PUTs and
 * backend metadata calls. Medal retries 5–32 times with exponential backoff and
 * stall detection depending on payload size (REPORT.md §4 step 6); this is the
 * platform-independent core of that behaviour, kept injectable (sleep/random) so
 * it is deterministic under test.
 */

export interface BackoffOptions {
  /** Max attempts including the first. */
  maxAttempts: number;
  /** Base delay in ms for the first retry. */
  baseDelayMs: number;
  /** Cap on any single delay. */
  maxDelayMs: number;
  /** Jitter factor 0..1; delay is multiplied by (1 - f*rand .. 1). */
  jitter: number;
}

export const DEFAULT_BACKOFF: BackoffOptions = {
  maxAttempts: 8,
  baseDelayMs: 500,
  maxDelayMs: 32_000,
  jitter: 0.5,
};

export interface RetryHooks {
  sleep?: (ms: number, signal?: AbortSignal) => Promise<void>;
  random?: () => number;
  /** Called before each retry delay. */
  onRetry?: (info: { attempt: number; delayMs: number; error: unknown }) => void;
  signal?: AbortSignal;
}

/** A thrown error can opt out of retries by setting `retryable = false`. */
export interface RetryableError {
  retryable?: boolean;
}

export function computeBackoffDelay(
  attempt: number,
  opts: BackoffOptions,
  random: () => number = Math.random,
): number {
  const exp = Math.min(opts.maxDelayMs, opts.baseDelayMs * 2 ** (attempt - 1));
  const jitterFactor = 1 - opts.jitter * random();
  return Math.max(0, Math.round(exp * jitterFactor));
}

export function defaultSleep(ms: number, signal?: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    if (signal?.aborted) {
      reject(new DOMExceptionLike("aborted", "AbortError"));
      return;
    }
    const timer = setTimeout(() => {
      signal?.removeEventListener("abort", onAbort);
      resolve();
    }, ms);
    const onAbort = () => {
      clearTimeout(timer);
      reject(new DOMExceptionLike("aborted", "AbortError"));
    };
    signal?.addEventListener("abort", onAbort, { once: true });
  });
}

/** Minimal AbortError stand-in so this works outside browsers too. */
export class DOMExceptionLike extends Error {
  constructor(message: string, name: string) {
    super(message);
    this.name = name;
  }
}

export function isAbortError(err: unknown): boolean {
  return err instanceof Error && err.name === "AbortError";
}

/**
 * Runs `fn` with retries. `fn` receives the 1-based attempt number. Stops early
 * when the signal aborts or the thrown error has `retryable === false`.
 */
export async function withRetry<T>(
  fn: (attempt: number) => Promise<T>,
  opts: BackoffOptions = DEFAULT_BACKOFF,
  hooks: RetryHooks = {},
): Promise<T> {
  const sleep = hooks.sleep ?? defaultSleep;
  const random = hooks.random ?? Math.random;
  let lastError: unknown;

  for (let attempt = 1; attempt <= opts.maxAttempts; attempt++) {
    if (hooks.signal?.aborted) {
      throw new DOMExceptionLike("aborted", "AbortError");
    }
    try {
      return await fn(attempt);
    } catch (err) {
      lastError = err;
      if (isAbortError(err)) throw err;
      const retryable = (err as RetryableError)?.retryable;
      if (retryable === false) throw err;
      if (attempt >= opts.maxAttempts) break;
      const delayMs = computeBackoffDelay(attempt, opts, random);
      hooks.onRetry?.({ attempt, delayMs, error: err });
      await sleep(delayMs, hooks.signal);
    }
  }
  throw lastError;
}
