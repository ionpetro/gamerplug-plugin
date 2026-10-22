import { describe, expect, it, vi } from "vitest";
import type { LibraryClip } from "@gamerplug/shared";
import { bufferDataSource } from "./dataSource.js";
import { MemoryStorage } from "./memoryStorage.js";
import { StubUploadBackend } from "./backends/stub.js";
import { hashSource, runUpload, UploadQueue } from "./uploadManager.js";
import type { UploadEvent } from "./types.js";

function makeClip(id: string): LibraryClip {
  return {
    id,
    filePath: `/clips/${id}.mp4`,
    durationSec: 30,
    width: 1920,
    height: 1080,
    fileSizeBytes: 1_234_567,
    fileType: "mp4",
    savedAt: Date.now(),
    saveType: "clip;30",
    title: `Clip ${id}`,
    description: "",
    isPublic: true,
    status: "queued",
  };
}

const bytes = (n: number) => {
  const b = new Uint8Array(n);
  for (let i = 0; i < n; i++) b[i] = (i * 13 + 5) & 0xff;
  return b;
};

describe("hashSource", () => {
  it("streams the same crc as hashing the whole buffer", async () => {
    const data = bytes(50_000);
    const { crc32cBase64 } = await import("./crc32c.js");
    expect(await hashSource(bufferDataSource(data), 4096)).toBe(crc32cBase64(data));
  });
});

describe("runUpload", () => {
  it("runs create -> upload -> finalize and emits events", async () => {
    const storage = new MemoryStorage();
    const backend = new StubUploadBackend({ storage });
    const data = bytes(20_000);
    const events: UploadEvent[] = [];

    const result = await runUpload({
      clip: makeClip("a"),
      source: bufferDataSource(data),
      backend,
      storageHttp: storage.asHttpClient(),
      chunkSize: 4096,
      retryHooks: { sleep: () => Promise.resolve() },
      onEvent: (e) => events.push(e),
    });

    expect(result.status === "published" || result.status === "processing").toBe(true);
    expect(result.checksumVerified).toBe(true);
    expect(result.crc32c).toBeTruthy();
    expect(result.remoteClipId).toBeDefined();
    expect(backend.created).toHaveLength(1);
    expect(backend.finalized).toHaveLength(1);
    expect(events.some((e) => e.type === "progress")).toBe(true);
    // The object actually landed in storage.
    expect([...storage.objects.values()][0]?.bytes).toEqual(data);
  });

  it("emits an error event and marks failed when the backend rejects", async () => {
    const backend = new StubUploadBackend();
    vi.spyOn(backend, "createUpload").mockRejectedValueOnce(new Error("backend down"));
    const events: UploadEvent[] = [];
    await expect(
      runUpload({
        clip: makeClip("b"),
        source: bufferDataSource(bytes(1000)),
        backend,
        onEvent: (e) => events.push(e),
        retryHooks: { sleep: () => Promise.resolve() },
      }),
    ).rejects.toThrow("backend down");
    expect(events.some((e) => e.type === "error")).toBe(true);
  });
});

describe("UploadQueue", () => {
  it("processes queued clips sequentially", async () => {
    const storage = new MemoryStorage();
    const backend = new StubUploadBackend({ storage });
    const done: string[] = [];
    const queue = new UploadQueue({
      backend,
      storageHttp: storage.asHttpClient(),
      retryHooks: { sleep: () => Promise.resolve() },
      onEvent: (e) => {
        if (e.type === "status" && (e.clip.status === "published" || e.clip.status === "processing")) {
          if (!done.includes(e.clip.id)) done.push(e.clip.id);
        }
      },
    });

    queue.enqueue(makeClip("c1"), bufferDataSource(bytes(8_000)));
    queue.enqueue(makeClip("c2"), bufferDataSource(bytes(8_000)));

    await vi.waitFor(() => expect(done).toEqual(["c1", "c2"]), { timeout: 2000 });
    expect(backend.finalized).toHaveLength(2);
  });
});
