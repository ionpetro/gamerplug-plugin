/**
 * GamerPlug (Supabase) upload adapter.
 *
 * What is verified (and implemented against real contracts):
 *  - Backend is Supabase project `nuopwfehaekxrrmzfqyv` with Supabase Auth and
 *    a public `clips` table whose columns we read from the public PostgREST API:
 *    id, user_id, title, description, video_url, thumbnail_url, duration,
 *    file_size, file_type, is_public, width, height, display_order.
 *  - Clip media is served from CloudFront `d11fcxbq4rxmpu.cloudfront.net` at
 *    `users/<userId>/clips/<clipId>/video.mp4`, and a `video_processing` table
 *    tracks an AWS MediaConvert transcode of each upload.
 *  - This was cross-checked with REA passive web inspection of gamerplug.app
 *    (docs/rea/gamerplug-inspect-web-page.evidence.json) and the public REST API.
 *
 * What is NOT public (and therefore abstracted, not invented):
 *  - The exact endpoint that mints the S3 upload destination and kicks off the
 *    MediaConvert job is server-side and auth-gated; we could not observe it.
 *    Rather than fabricate it, this adapter supports two storage strategies:
 *      - `supabase-signed`: upload via a Supabase Storage signed upload URL
 *        (a documented Supabase mechanism) into a configurable bucket.
 *      - `backend-presigned`: POST a small request to a configurable GamerPlug
 *        endpoint that returns an {@link UploadTarget}; this is the clean seam
 *        where GamerPlug's real presign/MediaConvert endpoint plugs in.
 */

import type { GamerPlugClipRecord } from "@gamerplug/shared";
import { fetchHttpClient, type HttpClient } from "../http.js";
import type {
  CreateUploadRequest,
  CreateUploadResult,
  FinalizeRequest,
  FinalizeResult,
  UploadBackend,
  UploadIdentity,
  UploadTarget,
} from "../types.js";

export type StorageStrategy = "supabase-signed" | "backend-presigned";

export interface SupabaseSession {
  accessToken: string;
  refreshToken?: string;
  userId: string;
  displayName?: string;
}

export interface SupabaseBackendOptions {
  supabaseUrl: string;
  anonKey: string;
  http?: HttpClient;
  /** Table that holds clip rows. */
  clipsTable?: string;
  storageStrategy?: StorageStrategy;
  /** For `supabase-signed`: the storage bucket clips are uploaded to. */
  bucket?: string;
  /** For `backend-presigned`: absolute URL that returns an UploadTarget. */
  presignEndpoint?: string;
}

export class SupabaseAuthError extends Error {}
export class SupabaseBackendError extends Error {}

export class SupabaseUploadBackend implements UploadBackend {
  private readonly http: HttpClient;
  private session?: SupabaseSession;
  private readonly clipsTable: string;
  private readonly strategy: StorageStrategy;
  private readonly bucket: string;

  constructor(private readonly options: SupabaseBackendOptions) {
    this.http = options.http ?? fetchHttpClient();
    this.clipsTable = options.clipsTable ?? "clips";
    this.strategy = options.storageStrategy ?? "supabase-signed";
    this.bucket = options.bucket ?? "clips";
  }

  /** Restore a previously persisted session (e.g. from the desktop keychain). */
  setSession(session: SupabaseSession): void {
    this.session = session;
  }

  getSession(): SupabaseSession | undefined {
    return this.session;
  }

  /** Supabase Auth email/password sign-in. */
  async signInWithPassword(email: string, password: string): Promise<SupabaseSession> {
    const res = await this.http.send({
      method: "POST",
      url: `${this.base()}/auth/v1/token?grant_type=password`,
      headers: {
        apikey: this.options.anonKey,
        "content-type": "application/json",
      },
      body: JSON.stringify({ email, password }),
    });
    if (res.status !== 200) {
      throw new SupabaseAuthError(`sign-in failed (${res.status}): ${await res.text()}`);
    }
    const body = (await res.json()) as {
      access_token: string;
      refresh_token?: string;
      user?: { id: string; user_metadata?: { name?: string; username?: string } };
    };
    if (!body.access_token || !body.user?.id) {
      throw new SupabaseAuthError("sign-in response missing access_token/user");
    }
    this.session = {
      accessToken: body.access_token,
      refreshToken: body.refresh_token,
      userId: body.user.id,
      displayName: body.user.user_metadata?.username ?? body.user.user_metadata?.name,
    };
    return this.session;
  }

  async getIdentity(): Promise<UploadIdentity> {
    const session = this.requireSession();
    return { userId: session.userId, displayName: session.displayName };
  }

  async createUpload(req: CreateUploadRequest): Promise<CreateUploadResult> {
    const session = this.requireSession();
    const remoteClipId = req.clip.id;
    const objectKey = `users/${session.userId}/clips/${remoteClipId}/video.mp4`;

    const video =
      this.strategy === "supabase-signed"
        ? await this.signedUploadTarget(objectKey, session)
        : await this.presignTarget(req, remoteClipId, session);

    return { remoteClipId, video };
  }

  async finalizeUpload(req: FinalizeRequest): Promise<FinalizeResult> {
    const session = this.requireSession();
    const videoUrl = req.videoUrl ?? this.publicUrlFor(session.userId, req.remoteClipId);
    // Upsert the clip row via PostgREST so the clip shows on the profile.
    const res = await this.http.send({
      method: "POST",
      url: `${this.base()}/rest/v1/${this.clipsTable}`,
      headers: {
        apikey: this.options.anonKey,
        authorization: `Bearer ${session.accessToken}`,
        "content-type": "application/json",
        prefer: "return=representation,resolution=merge-duplicates",
      },
      body: JSON.stringify(this.clipRow(req, videoUrl, session.userId)),
    });
    if (res.status >= 300) {
      throw new SupabaseBackendError(
        `clip row upsert failed (${res.status}): ${await res.text()}`,
      );
    }
    return {
      remoteClipId: req.remoteClipId,
      videoUrl,
      // Processing (MediaConvert) is triggered server-side; we report best-effort.
      processing: true,
    };
  }

  private clipRow(
    req: FinalizeRequest,
    videoUrl: string,
    userId: string,
  ): Partial<GamerPlugClipRecord> {
    return {
      id: req.remoteClipId,
      user_id: userId,
      video_url: videoUrl,
      thumbnail_url: req.thumbnailUrl ?? null,
      file_type: "mp4",
    };
  }

  private async signedUploadTarget(
    objectKey: string,
    session: SupabaseSession,
  ): Promise<UploadTarget> {
    const res = await this.http.send({
      method: "POST",
      url: `${this.base()}/storage/v1/object/upload/sign/${this.bucket}/${objectKey}`,
      headers: {
        apikey: this.options.anonKey,
        authorization: `Bearer ${session.accessToken}`,
        "content-type": "application/json",
      },
      body: JSON.stringify({}),
    });
    if (res.status >= 300) {
      throw new SupabaseBackendError(
        `signed upload URL request failed (${res.status}): ${await res.text()}`,
      );
    }
    const body = (await res.json()) as { url?: string; token?: string };
    if (!body.url) {
      throw new SupabaseBackendError("signed upload response missing url");
    }
    // Supabase returns a relative `/object/upload/sign/...?token=...` URL.
    const url = body.url.startsWith("http")
      ? body.url
      : `${this.base()}/storage/v1${body.url.startsWith("/") ? "" : "/"}${body.url}`;
    return {
      kind: "single-put",
      url,
      headers: body.token ? { "x-upsert": "true" } : undefined,
    };
  }

  private async presignTarget(
    req: CreateUploadRequest,
    remoteClipId: string,
    session: SupabaseSession,
  ): Promise<UploadTarget> {
    if (!this.options.presignEndpoint) {
      throw new SupabaseBackendError(
        "storageStrategy 'backend-presigned' requires presignEndpoint to be configured",
      );
    }
    const res = await this.http.send({
      method: "POST",
      url: this.options.presignEndpoint,
      headers: {
        apikey: this.options.anonKey,
        authorization: `Bearer ${session.accessToken}`,
        "content-type": "application/json",
      },
      body: JSON.stringify({
        clipId: remoteClipId,
        contentType: req.contentType,
        crc32c: req.crc32c,
        sizeBytes: req.clip.fileSizeBytes,
        width: req.clip.width,
        height: req.clip.height,
        durationSec: req.clip.durationSec,
      }),
    });
    if (res.status >= 300) {
      throw new SupabaseBackendError(
        `presign endpoint failed (${res.status}): ${await res.text()}`,
      );
    }
    return (await res.json()) as UploadTarget;
  }

  private publicUrlFor(userId: string, clipId: string): string {
    if (this.strategy === "supabase-signed") {
      return `${this.base()}/storage/v1/object/public/${this.bucket}/users/${userId}/clips/${clipId}/video.mp4`;
    }
    // Matches the observed CloudFront layout for the production S3 path.
    return `https://d11fcxbq4rxmpu.cloudfront.net/users/${userId}/clips/${clipId}/video.mp4`;
  }

  private base(): string {
    return this.options.supabaseUrl.replace(/\/$/, "");
  }

  private requireSession(): SupabaseSession {
    if (!this.session) {
      throw new SupabaseAuthError("not authenticated: call signInWithPassword or setSession");
    }
    return this.session;
  }
}

/** The public GamerPlug Supabase project, from the web app. */
export const GAMERPLUG_SUPABASE = {
  url: "https://nuopwfehaekxrrmzfqyv.supabase.co",
} as const;
