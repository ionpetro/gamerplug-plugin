/**
 * A fully local {@link UploadBackend} used for development and tests. It pairs
 * with {@link MemoryStorage} so the whole create -> upload -> finalize flow runs
 * without any network. It is also the reference implementation of the backend
 * contract: a real backend only has to produce the same three results.
 */

import { randomUUID } from "node:crypto";
import { MemoryStorage } from "../memoryStorage.js";
import type {
  CreateUploadRequest,
  CreateUploadResult,
  FinalizeRequest,
  FinalizeResult,
  UploadBackend,
  UploadIdentity,
} from "../types.js";

export interface StubBackendOptions {
  userId?: string;
  storage?: MemoryStorage;
  /** Use single-PUT instead of resumable targets. */
  singlePut?: boolean;
}

export class StubUploadBackend implements UploadBackend {
  readonly storage: MemoryStorage;
  readonly created: CreateUploadRequest[] = [];
  readonly finalized: FinalizeRequest[] = [];
  private readonly userId: string;

  constructor(private readonly options: StubBackendOptions = {}) {
    this.userId = options.userId ?? "stub-user";
    this.storage = options.storage ?? new MemoryStorage();
  }

  async getIdentity(): Promise<UploadIdentity> {
    return { userId: this.userId, displayName: "Stub User" };
  }

  async createUpload(req: CreateUploadRequest): Promise<CreateUploadResult> {
    this.created.push(req);
    const remoteClipId = randomUUID();
    const objectKey = `users/${this.userId}/clips/${remoteClipId}/video.mp4`;
    const video = this.options.singlePut
      ? ({ kind: "single-put", url: this.storage.singlePutUrlFor(objectKey) } as const)
      : ({ kind: "gcs-resumable", signedUrl: this.storage.signedUrlFor(objectKey) } as const);
    return { remoteClipId, video };
  }

  async finalizeUpload(req: FinalizeRequest): Promise<FinalizeResult> {
    this.finalized.push(req);
    return {
      remoteClipId: req.remoteClipId,
      videoUrl:
        req.videoUrl ??
        `https://cdn.example/users/${this.userId}/clips/${req.remoteClipId}/video.mp4`,
      processing: false,
    };
  }
}
