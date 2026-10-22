import { useEffect, useMemo, useState } from "react";
import type { LibraryClip, Settings } from "@gamerplug/shared";

type Tab = "library" | "settings" | "account";

export function App() {
  const api = window.gamerplug;
  if (!api) {
    return (
      <div className="empty" data-testid="preload-missing">
        Desktop bridge failed to load. The preload script did not expose the GamerPlug API.
      </div>
    );
  }
  return <AppShell api={api} />;
}

function AppShell({ api }: { api: Window["gamerplug"] }) {
  const [tab, setTab] = useState<Tab>("library");
  const [settings, setSettings] = useState<Settings | null>(null);
  const [clips, setClips] = useState<LibraryClip[]>([]);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [recState, setRecState] = useState("disconnected");
  const [session, setSession] = useState<{ userId: string; displayName?: string } | null>(null);
  const [uploadMode, setUploadMode] = useState<"stub" | "supabase">("stub");

  const selected = useMemo(() => clips.find((c) => c.id === selectedId) ?? clips[0], [clips, selectedId]);

  async function refresh() {
    const [s, lib, st, sess, mode] = await Promise.all([
      api.settings.get(),
      api.library.list(),
      api.recorder.state(),
      api.auth.session(),
      api.auth.uploadMode(),
    ]);
    setSettings(s);
    setClips(lib);
    setRecState(st);
    setSession(sess);
    setUploadMode(mode);
    if (!selectedId && lib[0]) setSelectedId(lib[0].id);
  }

  useEffect(() => {
    void refresh();
    const offs = [
      api.on("library:changed", (p) => setClips(p as LibraryClip[])),
      api.on("recorder:state", (p) => setRecState(p as string)),
      api.on("upload:event", () => {
        void api.library.list().then(setClips);
      }),
      api.on("clip:saved", (p) => {
        const clip = p as LibraryClip;
        setSelectedId(clip.id);
        void api.library.list().then(setClips);
      }),
    ];
    return () => offs.forEach((off) => off());
  }, [api]);

  if (!settings) return <div className="empty" data-testid="app-loading">Loading…</div>;

  return (
    <div className="app" data-testid="app">
      <header className="topbar">
        <div className="brand">
          GamerPlug <span>Clipper</span>
        </div>
        <nav className="tabs">
          <button className={tab === "library" ? "active" : ""} onClick={() => setTab("library")}>
            Library
          </button>
          <button className={tab === "settings" ? "active" : ""} onClick={() => setTab("settings")}>
            Settings
          </button>
          <button className={tab === "account" ? "active" : ""} onClick={() => setTab("account")}>
            Account
          </button>
        </nav>
        <div className="status">
          <span className={`dot ${recState === "capturing" ? "on" : recState === "error" ? "err" : ""}`} />
          <span data-testid="recorder-state">{recState}</span>
          <button data-testid="recorder-start" onClick={() => void api.recorder.start()}>
            Start
          </button>
          <button data-testid="recorder-stop" onClick={() => void api.recorder.stop()}>
            Stop
          </button>
          <button
            className="primary"
            data-testid="clip-save"
            onClick={() => void api.recorder.action(`clip;${settings.replay.clipSeconds}`)}
          >
            Clip {settings.replay.clipSeconds}s
          </button>
        </div>
      </header>
      {tab === "library" && (
        <LibraryView clips={clips} selected={selected} onSelect={setSelectedId} onChange={refresh} />
      )}
      {tab === "settings" && (
        <SettingsView
          settings={settings}
          onSave={async (next) => {
            const saved = await api.settings.set(next);
            setSettings(saved);
          }}
        />
      )}
      {tab === "account" && (
        <AccountView
          session={session}
          uploadMode={uploadMode}
          onChange={async () => {
            await refresh();
          }}
        />
      )}
    </div>
  );
}

function LibraryView({
  clips,
  selected,
  onSelect,
  onChange,
}: {
  clips: LibraryClip[];
  selected?: LibraryClip;
  onSelect: (id: string) => void;
  onChange: () => void;
}) {
  if (clips.length === 0) {
    return (
      <div className="empty" data-testid="library-empty">
        No clips yet. Start capture and press the clip hotkey (default F9) while a game is on screen.
      </div>
    );
  }
  return (
    <div className="layout">
      <aside className="sidebar">
        {clips.map((c) => (
          <div
            key={c.id}
            className={`clip-row ${selected?.id === c.id ? "selected" : ""}`}
            data-testid="clip-row"
            data-clip-id={c.id}
            data-filepath={c.filePath}
            data-status={c.status}
            data-checksum-verified={c.checksumVerified ? "true" : "false"}
            data-crc32c={c.crc32c ?? ""}
            data-duration={String(c.durationSec)}
            onClick={() => onSelect(c.id)}
          >
            {c.thumbnailPath ? (
              <img className="thumb" src={fileUrl(c.thumbnailPath)} alt="" />
            ) : (
              <div className="thumb placeholder" />
            )}
            <div className="clip-copy">
              <div className="title" data-testid="clip-title">{c.title}</div>
              <div className="meta">
                {Math.round(c.durationSec)}s · {c.width}×{c.height}{" "}
                <span className={`badge ${c.status}`} data-testid="clip-status">
                  {c.status}
                </span>
              </div>
            </div>
          </div>
        ))}
      </aside>
      <section className="main">{selected && <ClipPreview clip={selected} onChange={onChange} />}</section>
    </div>
  );
}

function ClipPreview({ clip, onChange }: { clip: LibraryClip; onChange: () => void }) {
  const [start, setStart] = useState(0);
  const [end, setEnd] = useState(clip.durationSec);
  const [title, setTitle] = useState(clip.title);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    setStart(0);
    setEnd(clip.durationSec);
    setTitle(clip.title);
    setError(null);
  }, [clip.id, clip.durationSec, clip.title]);

  return (
    <div className="panel">
      <h1 data-testid="clip-preview-title">{clip.title}</h1>
      <video data-testid="clip-video" src={fileUrl(clip.filePath)} controls />
      <div className="row">
        <label>Title</label>
        <input
          value={title}
          onChange={(e) => setTitle(e.target.value)}
          onBlur={() => void window.gamerplug.library.patch(clip.id, { title })}
          data-testid="clip-title-input"
        />
      </div>
      <div className="trim">
        <h2>Trim (lossless remux)</h2>
        <div className="row">
          <label>Start (s)</label>
          <input
            type="number"
            min={0}
            max={end}
            step={0.1}
            value={start}
            data-testid="trim-start"
            onChange={(e) => setStart(Number(e.target.value))}
          />
        </div>
        <div className="row">
          <label>End (s)</label>
          <input
            type="number"
            min={start}
            max={clip.durationSec}
            step={0.1}
            value={end}
            data-testid="trim-end"
            onChange={(e) => setEnd(Number(e.target.value))}
          />
        </div>
        <p className="hint">
          Applies ffmpeg <code>-c copy</code> from the in/out points. Needs ffmpeg next to the recorder or on PATH.
        </p>
      </div>
      {error && <div className="err">{error}</div>}
      {clip.status === "uploading" && <div className="hint">Uploading… {clip.uploadProgress ?? 0}%</div>}
      <div className="actions">
        <button
          disabled={busy}
          data-testid="trim-apply"
          onClick={async () => {
            setBusy(true);
            setError(null);
            try {
              await window.gamerplug.clip.trim(clip.id, start, end);
              onChange();
            } catch (e) {
              setError(e instanceof Error ? e.message : String(e));
            } finally {
              setBusy(false);
            }
          }}
        >
          Apply trim
        </button>
        <button
          className="primary"
          disabled={busy}
          data-testid="upload-clip"
          onClick={async () => {
            setBusy(true);
            setError(null);
            try {
              await window.gamerplug.clip.upload(clip.id);
              onChange();
            } catch (e) {
              setError(e instanceof Error ? e.message : String(e));
            } finally {
              setBusy(false);
            }
          }}
        >
          Upload to GamerPlug
        </button>
        <button
          className="danger"
          onClick={async () => {
            await window.gamerplug.library.remove(clip.id);
            onChange();
          }}
        >
          Delete
        </button>
      </div>
    </div>
  );
}

function SettingsView({ settings, onSave }: { settings: Settings; onSave: (s: Settings) => void }) {
  const [draft, setDraft] = useState(settings);
  useEffect(() => setDraft(settings), [settings]);

  function patchCapture(p: Partial<Settings["capture"]>) {
    setDraft({ ...draft, capture: { ...draft.capture, ...p } });
  }

  return (
    <div className="main">
      <div className="panel">
        <h1>Capture &amp; encoder</h1>
        <div className="row">
          <label>Mode</label>
          <select value={draft.capture.mode} onChange={(e) => patchCapture({ mode: e.target.value as Settings["capture"]["mode"] })}>
            <option value="auto">Auto (WGC then DXGI)</option>
            <option value="wgc">Windows.Graphics.Capture</option>
            <option value="dxgi">DXGI Desktop Duplication</option>
          </select>
        </div>
        <div className="row">
          <label>Encoder</label>
          <select
            value={draft.capture.encoder}
            onChange={(e) => patchCapture({ encoder: e.target.value as Settings["capture"]["encoder"] })}
          >
            <option value="auto">Auto (NVENC → AMF → QSV → software)</option>
            <option value="nvenc">NVIDIA NVENC</option>
            <option value="amf">AMD AMF</option>
            <option value="qsv">Intel QuickSync</option>
            <option value="software">Software (libx264 / OpenH264)</option>
          </select>
        </div>
        <div className="row">
          <label>Codec</label>
          <select value={draft.capture.codec} onChange={(e) => patchCapture({ codec: e.target.value as Settings["capture"]["codec"] })}>
            <option value="h264">H.264</option>
            <option value="hevc">HEVC</option>
            <option value="av1">AV1</option>
          </select>
        </div>
        <div className="row">
          <label>FPS</label>
          <input type="number" value={draft.capture.fps} onChange={(e) => patchCapture({ fps: Number(e.target.value) })} />
        </div>
        <div className="row">
          <label>Bitrate (kbps)</label>
          <input
            type="number"
            value={draft.capture.bitrateKbps}
            onChange={(e) => patchCapture({ bitrateKbps: Number(e.target.value) })}
          />
        </div>
        <h1>Replay buffer</h1>
        <div className="row">
          <label>Buffer seconds</label>
          <input
            type="number"
            value={draft.replay.bufferSeconds}
            onChange={(e) => setDraft({ ...draft, replay: { ...draft.replay, bufferSeconds: Number(e.target.value) } })}
          />
        </div>
        <div className="row">
          <label>Clip seconds</label>
          <input
            type="number"
            value={draft.replay.clipSeconds}
            onChange={(e) => setDraft({ ...draft, replay: { ...draft.replay, clipSeconds: Number(e.target.value) } })}
          />
        </div>
        <div className="row">
          <label>Clip folder</label>
          <input
            value={draft.replay.clipFolder}
            onChange={(e) => setDraft({ ...draft, replay: { ...draft.replay, clipFolder: e.target.value } })}
          />
        </div>
        <div className="row">
          <label>In-memory buffer</label>
          <input
            type="checkbox"
            checked={draft.capture.inMemoryBuffer}
            onChange={(e) => patchCapture({ inMemoryBuffer: e.target.checked })}
          />
        </div>
        <h1>Hotkeys</h1>
        {draft.hotkeys.map((h, i) => (
          <div className="row" key={i}>
            <label>{h.action}</label>
            <input
              value={h.accelerator}
              onChange={(e) => {
                const hotkeys = draft.hotkeys.slice();
                hotkeys[i] = { ...h, accelerator: e.target.value };
                setDraft({ ...draft, hotkeys });
              }}
            />
          </div>
        ))}
        <div className="row">
          <label>Auto-upload</label>
          <input
            type="checkbox"
            checked={draft.autoUpload}
            onChange={(e) => setDraft({ ...draft, autoUpload: e.target.checked })}
          />
        </div>
        <div className="row">
          <label>Record on startup</label>
          <input
            type="checkbox"
            checked={draft.recordOnStartup}
            onChange={(e) => setDraft({ ...draft, recordOnStartup: e.target.checked })}
          />
        </div>
        <div className="actions">
          <button className="primary" onClick={() => onSave(draft)}>
            Save settings
          </button>
        </div>
      </div>
    </div>
  );
}

function AccountView({
  session,
  uploadMode,
  onChange,
}: {
  session: { userId: string; displayName?: string } | null;
  uploadMode: "stub" | "supabase";
  onChange: () => void;
}) {
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(null);

  return (
    <div className="main">
      <div className="panel">
        <h1>GamerPlug account</h1>
        <p className="hint">
          Sign in with the same email/password as the GamerPlug app. Uploads go to the public{" "}
          <code>clips</code> table via Supabase. If GamerPlug has not opened a clip-upload endpoint yet, keep
          “Local stub” to exercise the resumable pipeline against in-memory storage.
        </p>
        {session ? (
          <>
            <div className="hint">Signed in as {session.displayName || session.userId}</div>
            <button
              onClick={async () => {
                await window.gamerplug.auth.logout();
                onChange();
              }}
            >
              Sign out
            </button>
          </>
        ) : (
          <>
            <div className="row">
              <label>Email</label>
              <input value={email} onChange={(e) => setEmail(e.target.value)} />
            </div>
            <div className="row">
              <label>Password</label>
              <input type="password" value={password} onChange={(e) => setPassword(e.target.value)} />
            </div>
            {error && <div className="err">{error}</div>}
            <button
              className="primary"
              onClick={async () => {
                setError(null);
                try {
                  await window.gamerplug.auth.login(email, password);
                  onChange();
                } catch (e) {
                  setError(e instanceof Error ? e.message : String(e));
                }
              }}
            >
              Sign in
            </button>
          </>
        )}
        <h2>Upload backend</h2>
        <div className="row">
          <label>Mode</label>
          <select
            value={uploadMode}
            onChange={(e) => {
              void window.gamerplug.auth.setUploadMode(e.target.value as "stub" | "supabase").then(onChange);
            }}
          >
            <option value="stub">Local stub (no cloud)</option>
            <option value="supabase">GamerPlug Supabase</option>
          </select>
        </div>
      </div>
    </div>
  );
}

function fileUrl(p: string): string {
  const normalized = p.replaceAll("\\", "/");
  return normalized.startsWith("file:") ? normalized : `file://${normalized}`;
}
