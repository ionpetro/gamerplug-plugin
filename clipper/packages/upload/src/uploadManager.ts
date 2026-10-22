/**
 * Orchestrates one clip upload end-to-end: hash -> create remote clip + upload
 * target -> resumable byte upload -> finalize metadata. Mirrors the ordering in
 * REPORT.md §4 (create content, upload, verify checksum, finalize). Emits
 * progress/status events the desktop UI renders, and a small sequential queue
 * drives multiple clips with job-level retry.
 */

import type { LibraryClip } from "@gamerplug/shared";
import { Crc32c } from "./crc32c.js";
import type { DataSource } from "./dataSource.js";
import { fetchHttpClient, type HttpClient } from "./http.js";
import { resumableUpload } from "./resumableUploader.js";
import {
  DEFAULT_BACKOFF,
  withRetry,
  type BackoffOptions,
  type RetryHooks,
} from "./retry.js";
import type { UploadBackend, UploadEvent } from "./types.js";

export interface RunUploadOptions {
  clip: LibraryClip;
  source: DataSource;
  backend: UploadBackend;
  /** HTTP client for the storage PUTs. Defaults to fetch. */
  storageHttp?: HttpClient;
  contentType?: string;
  chunkSize?: number;
  backoff?: BackoffOptions;
  retryHooks?: RetryHooks;
  onEvent?: (event: UploadEvent) => void;
  signal?: AbortSignal;
}

/** Compute the whole-file CRC32C by streaming the source in chunks. */
export async function hashSource(source: DataSource, chunkSize = 4 * 1024 * 1024): Promise<string> {
  const hasher = new Crc32c();
  let offset = 0;
  while (offset < source.size) {
    const chunk = await source.read(offset, Math.min(chunkSize, source.size - offset));
    if (chunk.length === 0) break;
    hasher.update(chunk);
    offset += chunk.length;
  }
  return hasher.digestBase64();
}

export async function runUpload(opts: RunUploadOptions): Promise<LibraryClip> {
  const http = opts.storageHttp ?? fetchHttpClient();
  const contentType = opts.contentType ?? "video/mp4";
  let clip: LibraryClip = { ...opts.clip, status: "uploading", uploadProgress: 0 };

  const emit = (next: Partial<LibraryClip>) => {
    clip = { ...clip, ...next };
    opts.onEvent?.({ type: "status", clip });
  };

  try {
    emit({ status: "uploading" });
    const crc32c = await hashSource(opts.source);

    const created = await opts.backend.createUpload({
      clip: opts.clip,
      title: opts.clip.title,
      description: opts.clip.description,
      isPublic: opts.clip.isPublic,
      crc32c,
      contentType,
    });
    emit({ remoteClipId: created.remoteClipId });

    const result = await resumableUpload({
      http,
      target: created.video,
      source: opts.source,
      contentType,
      expectedCrc32c: crc32c,
      chunkSize: opts.chunkSize,
      backoff: opts.backoff ?? DEFAULT_BACKOFF,
      retryHooks: opts.retryHooks,
      signal: opts.signal,
      onProgress: (sent, total) => {
        const fraction = total > 0 ? sent / total : 1;
        opts.onEvent?.({
          type: "progress",
          progress: { clipId: clip.id, bytesSent: sent, totalBytes: total, fraction },
        });
        clip = { ...clip, uploadProgress: Math.round(fraction * 100) };
      },
    });

    emit({ status: "processing" });
    const finalized = await opts.backend.finalizeUpload({
      remoteClipId: created.remoteClipId,
      crc32c,
      videoUrl: result.location,
    });

    emit({
      status: finalized.processing ? "processing" : "published",
      remoteVideoUrl: finalized.videoUrl,
      uploadProgress: 100,
    });
    return clip;
  } catch (err) {
    const message = err instanceof Error ? err.message : String(err);
    opts.onEvent?.({ type: "error", clipId: clip.id, error: message });
    emit({ status: "failed", uploadError: message });
    throw err;
  }
}

interface QueueJob {
  clip: LibraryClip;
  source: DataSource;
}

export interface UploadQueueOptions {
  backend: UploadBackend;
  storageHttp?: HttpClient;
  backoff?: BackoffOptions;
  retryHooks?: RetryHooks;
  onEvent?: (event: UploadEvent) => void;
}

/**
 * Sequential upload queue with job-level retry. Keeps a single upload in flight
 * so a background recorder saving clips never saturates the uplink.
 */
export class UploadQueue {
  private readonly jobs: QueueJob[] = [];
  private running = false;
  private aborter = new AbortController();

  constructor(private readonly options: UploadQueueOptions) {}

  get size(): number {
    return this.jobs.length;
  }

  enqueue(clip: LibraryClip, source: DataSource): void {
    this.jobs.push({ clip, source });
    void this.pump();
  }

  abort(): void {
    this.aborter.abort();
    this.aborter = new AbortController();
  }

  private async pump(): Promise<void> {
    if (this.running) return;
    this.running = true;
    try {
      while (this.jobs.length > 0) {
        const job = this.jobs.shift()!;
        try {
          await withRetry(
            () =>
              runUpload({
                clip: job.clip,
                source: job.source,
                backend: this.options.backend,
                storageHttp: this.options.storageHttp,
                backoff: this.options.backoff,
                retryHooks: this.options.retryHooks,
                onEvent: this.options.onEvent,
                signal: this.aborter.signal,
              }),
            this.options.backoff ?? DEFAULT_BACKOFF,
            this.options.retryHooks,
          );
        } catch {
          // runUpload already emitted a failure event; keep draining the queue.
        }
      }
    } finally {
      this.running = false;
    }
  }
}
