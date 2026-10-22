import { describe, expect, it, vi } from "vitest";
import { bufferDataSource } from "./dataSource.js";
import { MemoryStorage } from "./memoryStorage.js";
import {
  resumableUpload,
  parseCommittedEnd,
  extractCrc32c,
  UploadError,
} from "./resumableUploader.js";
import { crc32cBase64 } from "./crc32c.js";
import type { HttpClient } from "./http.js";

function randomBytes(n: number): Uint8Array {
  const b = new Uint8Array(n);
  for (let i = 0; i < n; i++) b[i] = (i * 31 + 7) & 0xff;
  return b;
}

const instantRetry = { sleep: () => Promise.resolve() };

describe("parseCommittedEnd / extractCrc32c", () => {
  it("parses a GCS range header", () => {
    expect(parseCommittedEnd("bytes=0-262143")).toBe(262143);
    expect(parseCommittedEnd(undefined)).toBeUndefined();
    expect(parseCommittedEnd("garbage")).toBeUndefined();
  });

  it("extracts crc32c from x-goog-hash", () => {
    expect(extractCrc32c("crc32c=4waSgw==,md5=abc==")).toBe("4waSgw==");
    expect(extractCrc32c(undefined)).toBeUndefined();
  });
});

describe("resumableUpload (gcs-resumable)", () => {
  it("uploads a multi-chunk object and verifies checksum", async () => {
    const storage = new MemoryStorage();
    const data = randomBytes(20_000);
    const key = "users/u1/clips/c1/video.mp4";
    const progress: number[] = [];

    const result = await resumableUpload({
      http: storage.asHttpClient(),
      target: { kind: "gcs-resumable", signedUrl: storage.signedUrlFor(key) },
      source: bufferDataSource(data),
      contentType: "video/mp4",
      chunkSize: 4096, // -> 5 chunks
      expectedCrc32c: crc32cBase64(data),
      retryHooks: instantRetry,
      onProgress: (sent) => progress.push(sent),
    });

    expect(result.bytesSent).toBe(data.length);
    expect(storage.objects.get(key)?.bytes).toEqual(data);
    expect(result.serverCrc32c).toBe(crc32cBase64(data));
    expect(progress.at(-1)).toBe(data.length);
  });

  it("resumes after a transient failure mid-upload", async () => {
    // Force one 503 on the 3rd PUT; the uploader should retry and resume.
    let putCount = 0;
    const storage = new MemoryStorage({
      faultInjector: (req) => {
        if (req.method === "PUT") {
          putCount++;
          if (putCount === 3) return 503;
        }
        return undefined;
      },
    });
    const data = randomBytes(12_000);
    const key = "users/u1/clips/resume/video.mp4";

    const result = await resumableUpload({
      http: storage.asHttpClient(),
      target: { kind: "gcs-resumable", signedUrl: storage.signedUrlFor(key) },
      source: bufferDataSource(data),
      contentType: "video/mp4",
      chunkSize: 4096,
      retryHooks: instantRetry,
    });

    expect(result.bytesSent).toBe(data.length);
    expect(storage.objects.get(key)?.bytes).toEqual(data);
  });

  it("fails fast on a non-retryable status", async () => {
    const http: HttpClient = {
      send: async (req) => {
        if (req.method === "POST") {
          return {
            status: 201,
            headers: { location: "https://memstore.local/session/x" },
            text: async () => "",
            json: async () => null,
            bytes: async () => new Uint8Array(),
          };
        }
        return {
          status: 403,
          headers: {} as Record<string, string>,
          text: async () => "denied",
          json: async () => null,
          bytes: async () => new Uint8Array(),
        };
      },
    };
    await expect(
      resumableUpload({
        http,
        target: { kind: "gcs-resumable", signedUrl: "https://x/o/key" },
        source: bufferDataSource(randomBytes(1000)),
        contentType: "video/mp4",
        retryHooks: instantRetry,
      }),
    ).rejects.toBeInstanceOf(UploadError);
  });

  it("rejects a server checksum mismatch", async () => {
    const storage = new MemoryStorage();
    const data = randomBytes(8_000);
    const key = "users/u1/clips/bad/video.mp4";
    await expect(
      resumableUpload({
        http: storage.asHttpClient(),
        target: { kind: "gcs-resumable", signedUrl: storage.signedUrlFor(key) },
        source: bufferDataSource(data),
        contentType: "video/mp4",
        expectedCrc32c: "AAAAAA==", // wrong on purpose
        retryHooks: instantRetry,
      }),
    ).rejects.toThrow(/checksum mismatch/);
  });
});

describe("resumableUpload (single-put)", () => {
  it("uploads the whole object in one PUT", async () => {
    const storage = new MemoryStorage();
    const data = randomBytes(8_000);
    const key = "users/u1/clips/single/video.mp4";
    const result = await resumableUpload({
      http: storage.asHttpClient(),
      target: { kind: "single-put", url: storage.singlePutUrlFor(key) },
      source: bufferDataSource(data),
      contentType: "video/mp4",
      retryHooks: instantRetry,
    });
    expect(result.bytesSent).toBe(data.length);
    expect(storage.objects.get(key)?.bytes).toEqual(data);
  });
});
