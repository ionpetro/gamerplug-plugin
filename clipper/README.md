# GamerPlug Clipper

Windows desktop game clipping for [GamerPlug](https://www.gamerplug.app/). A tray app keeps a rolling replay buffer while you play, a hotkey saves the last N seconds (no re-encode), and the clip can be uploaded to the same GamerPlug profile that already leads with gameplay clips.

This is an independent implementation. Medal.tv was used as a **design reference only** (architecture, not code or assets). The non-GPL capture path is Windows.Graphics.Capture with DXGI Desktop Duplication as fallback — there is no libobs, no OBS graphics-hook, and no Detours present-hook.

## Architecture

```
┌──────────────────────────────────────────────────────────────┐
│  Electron shell (tray, library, preview/trim, login, upload) │
│    JSON-RPC 2.0 WebSocket on 127.0.0.1:10603+                │
│    verifyClient rejects any Origin header                    │
└────────────────────────────┬─────────────────────────────────┘
                             │ spawn GamerPlug.Recorder.exe
                             │   --electronPort N --wsComms
┌────────────────────────────▼─────────────────────────────────┐
│  Recorder process (.NET 8, Windows)                          │
│    WGC → DXGI Desktop Duplication fallback                   │
│    FFmpeg LGPL: NVENC / AMF / QSV / libx264 / libopenh264    │
│    WASAPI loopback (+ PROCESS_LOOPBACK activation for split) │
│    encoded-packet TimeDataBuffer (16 MiB video / 1 MiB audio │
│    chunks, crash-recovery headers, memory or file backend)   │
│    hotkey hook → remux last N seconds to MP4                 │
└──────────────────────────────────────────────────────────────┘
                             │
                             ▼
              UploadBackend: stub  or  GamerPlug Supabase
              resumable chunked PUT + CRC32C verify
```

| Piece | Location |
| --- | --- |
| Shared types (settings, clips, hotkeys) | `packages/shared` |
| JSON-RPC peer + WS server (reject Origin) | `packages/ipc` |
| CRC32C, retry, resumable uploader, backends | `packages/upload` |
| Replay buffer, engine, MP4 remuxer | `recorder/GamerPlug.Recorder.Core` |
| Windows capture / encode / audio / IPC host | `recorder/GamerPlug.Recorder` |
| Cross-platform dev recorder (testsrc → real ring) | `recorder/GamerPlug.Recorder.Dev` |
| Electron tray app | `apps/desktop` |

### Replay buffer

Encoded packets (not raw frames) sit in a time-bounded ring. Storage is a list of fixed-size chunks — **16 MiB video, 1 MiB audio** — with a 64-byte header (`GPLUGCK1` magic, type, id, payload size, PTS range, CRC-32 of header and payload). Sealed chunks can be persisted as `buffer-{name}-{id}.bin` so a crash can reconstruct the ring (ClipRecovery). Saving a clip walks back to the nearest keyframe and remuxes to MP4 with a pure-C# muxer (no re-encode).

This follows the *shape* of Medal's `TimeDataBuffer` (memory / file / mmap backends, chunked records, crash-recovery checksums) without copying its layout, magic, or code.

### Capture and encode (Windows)

1. **Windows.Graphics.Capture** for a monitor (or, later, a window).
2. **DXGI Desktop Duplication** if WGC **Start** fails (monitor-only). Constructors never fail, so Auto fallback happens when capture actually starts.
3. Frames are BGRA-copied to an **FFmpeg** child process. Encoder fallback: `h264_nvenc` → `h264_amf` → `h264_qsv` → `libx264` → `libopenh264` (same idea for HEVC/AV1). ffmpeg must be an **LGPL** build; do not ship GPL libobs/x264-GPL binaries.
4. System audio: **WASAPI loopback** (NAudio). Per-process loopback (`ActivateAudioInterfaceAsync` + `AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK`) is compiled in for the split-track mode; the capture pump still needs a hardware pass.

Game-hook injection (Medal's Detours present hook passing DXGI shared textures) is **not** implemented. Exclusive-fullscreen titles that WGC/DXGI cannot see are a known gap; we will not take the GPL OBS-hook path without an explicit license decision.

### IPC

The UI hosts the WebSocket server; the recorder is the client (`ws://127.0.0.1:{port}`). Connections that send an `Origin` header are rejected so a web page cannot drive the recorder. Protocol version `1`; methods live in `packages/ipc/src/contract.ts`. Crash-loop guard: 5 exits with the same code inside 120s stops restarts.

### Upload / GamerPlug backend

GamerPlug's production backend is Supabase project `nuopwfehaekxrrmzfqyv`:

- Auth: Supabase email/password (`/auth/v1/token`).
- Public `clips` table: `id, user_id, title, description, video_url, thumbnail_url, duration, file_size, file_type, is_public, width, height, display_order`.
- Media CDN: CloudFront `d11fcxbq4rxmpu.cloudfront.net/users/<userId>/clips/<clipId>/video.mp4`.
- `video_processing` tracks an AWS MediaConvert job after upload.

The **presign / MediaConvert kickoff endpoint is not public**. Rather than invent it, `SupabaseUploadBackend` supports:

| `storageStrategy` | What it does |
| --- | --- |
| `supabase-signed` (default) | Documented Supabase Storage signed upload URL into a configurable `clips` bucket, then PostgREST upsert of the `clips` row. |
| `backend-presigned` | `POST` `GAMERPLUG_PRESIGN_URL` with clip metadata + CRC32C; the response is an `UploadTarget` (`gcs-resumable` / `session-resumable` / `single-put`). This is the seam for GamerPlug's real S3+MediaConvert path. |

`StubUploadBackend` + in-memory GCS-like storage exercises the full create → chunked PUT → checksum → finalize flow offline.

Set `GAMERPLUG_UPLOAD_MODE=supabase` (or sign in from the Account tab) to use the real project; the default is the stub.

## How to run

Requires Node 20+, pnpm 10, .NET 8 SDK. Windows is required for capture; Linux can build/test everything except the WinExe.

```bash
cd clipper
pnpm install
pnpm run build:packages
pnpm test                       # shared + ipc + upload + desktop
pnpm recorder:test              # 60+ xUnit tests (ring buffer, muxer, engine)
```

### Desktop UI + dev recorder (macOS / Linux)

Real WGC/DXGI capture is Windows-only. On macOS and Linux the Electron app auto-spawns **GamerPlug.Recorder.Dev**, which speaks the same JSON-RPC protocol (`ws://127.0.0.1:10603+`, Origin rejected) and feeds ffmpeg `testsrc2` + `sine` into the **real** Core replay ring. Clip save, lossless trim, and stub upload then work end-to-end.

```bash
# macOS
brew install ffmpeg
# Linux: ffmpeg + a working display (or xvfb-run for tests)

cd clipper
pnpm install
pnpm run build:packages
dotnet build recorder/GamerPlug.Recorder.Dev/GamerPlug.Recorder.Dev.csproj -c Release
pnpm --filter @gamerplug/desktop run build

# non-Windows auto-selects the dev recorder; force it on Windows with GAMERPLUG_DEV_RECORDER=1
pnpm --filter @gamerplug/desktop exec electron .
```

Hotkeys on macOS/Linux are registered by Electron (`F9` = last 30s, `F8` = 15s). The **Clip** button in the UI calls the same `executeAction` RPC.

To drive the protocol without Electron (the desktop app must already be listening):

```bash
dotnet run --project recorder/GamerPlug.Recorder.Dev -c Release -- --electronPort 10603 --wsComms
```

Override the binary with `GAMERPLUG_RECORDER_EXE=/path/to/GamerPlug.Recorder.Dev.dll` (launched via `dotnet`). Size/fps: `GAMERPLUG_DEV_WIDTH`, `GAMERPLUG_DEV_HEIGHT`, `GAMERPLUG_DEV_FPS`.

### End-to-end test (Playwright + Electron)

The E2E launches the real desktop app under xvfb on Linux, waits for the preload bridge and the dev recorder, saves a clip, checks the MP4 with ffprobe, trims it, and uploads through `StubUploadBackend` with CRC32C verified.

```bash
# Linux without a display: the script wraps xvfb-run automatically
cd clipper
pnpm run test:e2e
```

On macOS, with ffmpeg on PATH and a window server:

```bash
cd clipper
pnpm run recorder:dev
pnpm --filter @gamerplug/desktop run build
pnpm --filter @gamerplug/desktop run test:e2e
```

### Windows capture

1. Build the recorder:

```
dotnet publish recorder/GamerPlug.Recorder/GamerPlug.Recorder.csproj -c Release -r win-x64 --self-contained false
```

2. Place an LGPL `ffmpeg.exe` next to `GamerPlug.Recorder.exe`, or set `GAMERPLUG_FFMPEG`.
3. Point the UI at it: `GAMERPLUG_RECORDER_EXE=...GamerPlug.Recorder.exe`.
4. Run the Electron app. Start capture, press **F9** (30s) or **F8** (15s).

### Windows installer

```
dotnet publish recorder/GamerPlug.Recorder/GamerPlug.Recorder.csproj -c Release -r win-x64 -o apps/desktop/extra/recorder
# copy ffmpeg.exe into extra/recorder
pnpm --filter @gamerplug/desktop run pack:win
```

`electron-builder` writes an NSIS installer under `apps/desktop/release/`.

## Tests & CI

- **ubuntu-latest:** TypeScript package tests + desktop `tsc`/`vite` build; .NET Core xUnit (including an ffprobe-validated H.264+AAC remux when ffmpeg is present); Windows recorder cross-compile via `EnableWindowsTargeting`; Dev recorder compile; Playwright Electron E2E under xvfb (clip save → ffprobe → trim → stub upload + CRC32C).
- **windows-latest:** compile the WinExe (`GamerPlug.Recorder.csproj`). Capture/encode/audio still need a real GPU and a game window — see gaps.

## REA (Reverse Engineer Anything)

[REA](https://github.com/morluto/rea) (`rea-agents` 6.7.0) was used on this VM to ground the GamerPlug upload adapter in evidence, not guesswork:

1. `npx -y rea-agents setup` / `doctor` (SDK 2.3.1, 207 MCP tools). `procps` `ps` is on PATH.
2. Headless Chrome on `https://gamerplug.app/en/` with CDP at `127.0.0.1:9222`.
3. `rea inspect-web-page` (passive CDP, no JS evaluation) produced evidence id in `docs/rea/gamerplug-inspect-web-page.evidence.json`.
4. Confirmed: Supabase host `nuopwfehaekxrrmzfqyv.supabase.co`, CloudFront media, PostHog, strict CSP. Full `analyze-web-bundle` OOMed on this Next.js app (20 GB heap); passive inspection was the reliable path.

Medal itself was **not** on this VM. The attached REPORT.md v2 is a REA-verified analysis of Medal (Ghidra on `scope.dll` / `medal-hook64-v2.dll`) and was used only as a design reference: TimeDataBuffer chunk sizes, crash-recovery headers, WGC/DXGI/NVENC/AMF/QSV lists, Detours present-hook (which we deliberately do not copy), JSON-RPC Origin rejection, GCS resumable PUT + CRC32C.

## Known gaps (need real Windows hardware)

- WGC/DXGI/NVENC/WASAPI have not been run on a GPU; only compiled (CI `windows-latest`) and reviewed.
- Exclusive-fullscreen / anti-cheat games that block WGC may need a present hook; that is out of scope (non-GPL).
- GPU zero-copy into NVENC (shared DXGI texture) is not wired; frames currently go CPU BGRA → ffmpeg stdin.
- Per-process audio loopback activation is compiled but the IAudioClient pump is not finished (`AudioMode=split` will not capture yet).
- Webcam, overlay, bookmarks-as-session-recording, WHIP live output: not in v0.1.
- GamerPlug's private presign/MediaConvert endpoint is stubbed behind `GAMERPLUG_PRESIGN_URL`.
- Thumbnail generation is best-effort (`ffmpeg` JPEG poster frame next to the clip). `display_order` assignment is not implemented.
- Unpackaged WGC needs the included `app.manifest` `maxversiontested`; some Windows SKUs still prompt for capture permission.
- No auto-update (Velopack) yet.

## License

MIT, same as the rest of this repo. Bundle only LGPL/permissive ffmpeg and vendor encoder SDKs. Do not drop OBS graphics-hook or GPL ffmpeg into `extra/recorder`.
