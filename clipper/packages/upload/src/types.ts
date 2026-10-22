import type { LibraryClip, SavedClip } from "@gamerplug/shared";

/**
 * How bytes should be pushed to storage. The desktop app's uploader understands
 * each variant; a backend returns whichever its storage provider needs.
 *
 * - `gcs-resumable`: POST the signed URL with `x-goog-resumable: start` to get a
 *   session URI, then chunked PUT (the Medal/GCS path, REPORT.md §4).
 * - `session-resumable`: a ready-to-use resumable session URI (chunked PUT only).
 * - `single-put`: one PUT of the whole object (small clips / simple storage).
 */
export type UploadTarget =
  | {
      kind: "gcs-resumable";
      signedUrl: string;
      headers?: Record<string, string>;
    }
  | {
      kind: "session-resumable";
      sessionUrl: string;
      headers?: Record<string, string>;
    }
  | {
      kind: "single-put";
      url: string;
      headers?: Record<string, string>;
    };

export interface UploadIdentity {
  userId: string;
  displayName?: string;
}

export interface CreateUploadRequest {
  clip: SavedClip;
  title: string;
  description: string;
  isPublic: boolean;
  /** crc32c of the whole file, base64 (GCS wire format). */
  crc32c: string;
  contentType: string;
}

export interface CreateUploadResult {
  /** The remote clip id the metadata row was created under. */
  remoteClipId: string;
  video: UploadTarget;
  thumbnail?: UploadTarget;
}

export interface FinalizeRequest {
  remoteClipId: string;
  crc32c: string;
  /** Final resolved object URL, if the backend needs the client to report it. */
  videoUrl?: string;
  thumbnailUrl?: string;
}

export interface FinalizeResult {
  remoteClipId: string;
  videoUrl: string;
  /** Whether the backend kicked off async processing (e.g. transcode). */
  processing: boolean;
}

/**
 * A storage/metadata backend. Keeping GamerPlug's specifics behind this
 * interface means the uploader is backend-agnostic and we can ship a real
 * Supabase adapter plus a stub without the uploader knowing the difference.
 */
export interface UploadBackend {
  getIdentity(): Promise<UploadIdentity>;
  createUpload(req: CreateUploadRequest): Promise<CreateUploadResult>;
  finalizeUpload(req: FinalizeRequest): Promise<FinalizeResult>;
}

export interface UploadProgress {
  clipId: string;
  bytesSent: number;
  totalBytes: number;
  /** 0..1 */
  fraction: number;
}

export type UploadEvent =
  | { type: "status"; clip: LibraryClip }
  | { type: "progress"; progress: UploadProgress }
  | { type: "error"; clipId: string; error: string };
