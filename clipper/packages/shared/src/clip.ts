/**
 * Clip domain types. A clip starts life as a file the recorder remuxes out of
 * the replay buffer, is tracked locally in the desktop app's library, and is
 * eventually uploaded to GamerPlug (whose `clips` table shape is mirrored in
 * `GamerPlugClipRecord`).
 */

export type ClipSaveReason =
  | "noDataInBuffer"
  | "negativeBuffer"
  | "encoderError"
  | "diskFull"
  | "unknown";

/** Emitted by the recorder when a hotkey (or UI trigger) produces a clip file. */
export interface SavedClip {
  /** Client-generated UUID; becomes the GamerPlug clip id on upload. */
  id: string;
  filePath: string;
  thumbnailPath?: string;
  /** Duration in seconds (float). */
  durationSec: number;
  width: number;
  height: number;
  fileSizeBytes: number;
  fileType: "mp4";
  /** Epoch ms when the clip was saved. */
  savedAt: number;
  /** Window/exe title at save time, used as a default clip title. */
  gameTitle?: string;
  /** Which hotkey action produced it, e.g. "clip;30". */
  saveType: string;
}

export type ClipUploadStatus =
  | "local" // saved, not queued
  | "queued"
  | "uploading"
  | "processing" // remote transcode (MediaConvert) in progress
  | "published"
  | "failed";

/** A clip as tracked in the desktop app's local SQLite library. */
export interface LibraryClip extends SavedClip {
  title: string;
  description: string;
  isPublic: boolean;
  status: ClipUploadStatus;
  /** Set once uploaded. */
  remoteClipId?: string;
  remoteVideoUrl?: string;
  uploadError?: string;
  /** 0–100, best-effort during upload. */
  uploadProgress?: number;
  /** Whole-file CRC32C (base64) computed before upload. */
  crc32c?: string;
  /** True when the storage backend echoed the same CRC32C. */
  checksumVerified?: boolean;
}

/**
 * Shape of a row in GamerPlug's `clips` table, discovered via the public
 * Supabase REST API. Kept here so the upload adapter and the UI agree on the
 * contract. See README "GamerPlug backend".
 */
export interface GamerPlugClipRecord {
  id: string;
  user_id: string;
  title: string;
  description: string;
  video_url: string;
  thumbnail_url: string | null;
  duration: number;
  file_size: number;
  file_type: string;
  is_public: boolean;
  width: number;
  height: number;
  display_order: number;
  created_at?: string;
  updated_at?: string;
}
