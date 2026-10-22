namespace GamerPlug.Recorder.Core.Engine;

/// <summary>
/// The platform-independent recorder brain. It owns the <see cref="ReplayBuffer"/>,
/// receives encoded packets (pushed from a platform <see cref="ICaptureSource"/>),
/// and turns hotkey/UI actions into saved clips by extracting from the buffer and
/// handing the packets to an <see cref="IClipMuxer"/> — no re-encode. All the
/// decision logic lives here so it is unit-testable off-Windows; the Windows
/// project only supplies capture, encode, mux, hotkey and transport adapters.
/// </summary>
public sealed class RecorderEngine
{
    private readonly object _gate = new();
    private readonly IClipMuxer _muxer;
    private readonly IRecorderObserver _observer;
    private readonly Func<string, string> _clipPathFactory;
    private readonly Func<string> _idFactory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<StreamKind, int, IChunkStore>? _storeFactory;

    private RecorderSettings _settings;
    private ReplayBuffer _buffer;
    private RecorderState _state = RecorderState.Idle;
    private bool _clipsEnabled = true;

    public RecorderState State => _state;
    public RecorderSettings Settings => _settings;

    public RecorderEngine(
        RecorderSettings settings,
        IClipMuxer muxer,
        IRecorderObserver? observer = null,
        Func<string, string>? clipPathFactory = null,
        Func<string>? idFactory = null,
        Func<DateTimeOffset>? clock = null,
        Func<StreamKind, int, IChunkStore>? storeFactory = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _muxer = muxer ?? throw new ArgumentNullException(nameof(muxer));
        _observer = observer ?? NullRecorderObserver.Instance;
        _idFactory = idFactory ?? (() => Guid.NewGuid().ToString("D"));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _storeFactory = storeFactory;
        _clipPathFactory = clipPathFactory ?? DefaultClipPath;
        _buffer = BuildBuffer(settings);
    }

    private ReplayBuffer BuildBuffer(RecorderSettings s)
    {
        Func<StreamKind, int, IChunkStore>? factory = _storeFactory;
        if (factory is null && !s.InMemoryBuffer && !string.IsNullOrEmpty(s.ClipFolder))
        {
            string dir = Path.Combine(s.ClipFolder, "RecordingBuffer");
            factory = (kind, index) => new FileChunkStore(dir, $"{(kind == StreamKind.Video ? "vbuf" : "abuf")}-{index}");
        }
        return new ReplayBuffer(s.BufferRetentionUs, Math.Max(1, s.AudioTrackCount), factory);
    }

    private string DefaultClipPath(string id)
    {
        string folder = string.IsNullOrEmpty(_settings.ClipFolder)
            ? Directory.GetCurrentDirectory()
            : _settings.ClipFolder;
        return Path.Combine(folder, $"clip-{id}.mp4");
    }

    public void Start()
    {
        lock (_gate)
        {
            SetState(RecorderState.Capturing);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            SetState(RecorderState.Stopping);
            SetState(RecorderState.Idle);
        }
    }

    /// <summary>Reconfigure the buffer when settings change (keeps capturing).</summary>
    public void ApplySettings(RecorderSettings settings)
    {
        lock (_gate)
        {
            bool bufferShapeChanged =
                settings.BufferRetentionUs != _settings.BufferRetentionUs ||
                settings.AudioTrackCount != _settings.AudioTrackCount ||
                settings.InMemoryBuffer != _settings.InMemoryBuffer ||
                settings.ClipFolder != _settings.ClipFolder;
            _settings = settings;
            if (bufferShapeChanged)
            {
                _buffer.Clear();
                _buffer = BuildBuffer(settings);
            }
        }
    }

    public void SetClipsEnabled(bool enabled)
    {
        lock (_gate) { _clipsEnabled = enabled; }
    }

    /// <summary>Attempt to recover a crash-persisted buffer before capture starts.</summary>
    public void RecoverBuffer()
    {
        lock (_gate) { _buffer.Recover(); }
    }

    public void OnVideoPacket(in MediaPacket packet)
    {
        lock (_gate) { _buffer.AddVideo(packet); }
    }

    public void OnAudioPacket(in MediaPacket packet)
    {
        lock (_gate) { _buffer.AddAudio(packet); }
    }

    /// <summary>
    /// Handle an action string (<c>clip;30</c>, <c>bookmark</c>, ...). Returns
    /// whether it was accepted; clip saves additionally emit OnClipSaved/Failed.
    /// </summary>
    public ActionResult ExecuteAction(string action, ActionSource source)
    {
        if (!HotkeyAction.TryParse(action, out var parsed))
            return ActionResult.Rejected(ActionRejectReason.UnknownAction);

        lock (_gate)
        {
            if (!_clipsEnabled && parsed.Kind == HotkeyActionKind.Clip)
                return ActionResult.Rejected(ActionRejectReason.ClipsDisabled);
            if (_state != RecorderState.Capturing && _state != RecorderState.Saving)
                return ActionResult.Rejected(ActionRejectReason.NotRecording);

            switch (parsed.Kind)
            {
                case HotkeyActionKind.Clip:
                    SaveClipLocked(parsed.Seconds, action);
                    return ActionResult.Ok();
                case HotkeyActionKind.Bookmark:
                    _observer.OnLog("info", $"bookmark at {_buffer.NewestPtsUs() ?? 0}us");
                    return ActionResult.Ok();
                case HotkeyActionKind.Screenshot:
                    // Screenshot capture is a platform concern; accepted as a no-op here.
                    _observer.OnLog("info", "screenshot requested");
                    return ActionResult.Ok();
                case HotkeyActionKind.SegmentToggle:
                    _observer.OnLog("info", "segment toggle requested");
                    return ActionResult.Ok();
                default:
                    return ActionResult.Rejected(ActionRejectReason.UnknownAction);
            }
        }
    }

    /// <summary>Save the last <paramref name="seconds"/> seconds as a clip.</summary>
    public SavedClipInfo? SaveClip(double seconds, string saveType)
    {
        lock (_gate)
        {
            if (_state != RecorderState.Capturing && _state != RecorderState.Saving)
            {
                _observer.OnClipFailed(ClipSaveStatus.NoDataInBuffer, "recorder not capturing");
                return null;
            }
            return SaveClipLocked(seconds, saveType);
        }
    }

    private SavedClipInfo? SaveClipLocked(double seconds, string saveType)
    {
        var prev = _state;
        SetState(RecorderState.Saving);
        try
        {
            var extract = _buffer.SaveLastSeconds(seconds);
            if (!extract.Success)
            {
                _observer.OnClipFailed(extract.Status, $"clip save failed: {extract.Status}");
                return null;
            }

            string id = _idFactory();
            string outPath = _clipPathFactory(id);
            var hints = new MuxHints(
                _settings.Width,
                _settings.Height,
                _settings.Fps,
                _settings.Codec.ToString().ToLowerInvariant(),
                BuildAudioCodecList());

            ClipFile file;
            try
            {
                file = _muxer.WriteClip(extract, outPath, hints);
            }
            catch (Exception ex)
            {
                _observer.OnClipFailed(ClipSaveStatus.NoDataInBuffer, $"mux error: {ex.Message}");
                return null;
            }

            var info = new SavedClipInfo(
                id,
                file.Path,
                file.DurationUs,
                file.Width,
                file.Height,
                file.SizeBytes,
                saveType,
                _clock().ToUnixTimeMilliseconds());
            _observer.OnClipSaved(info);
            return info;
        }
        finally
        {
            SetState(prev == RecorderState.Saving ? RecorderState.Capturing : prev);
        }
    }

    private IReadOnlyList<string> BuildAudioCodecList()
    {
        var list = new List<string>();
        for (int i = 0; i < Math.Max(1, _settings.AudioTrackCount); i++) list.Add("aac");
        return list;
    }

    private void SetState(RecorderState state)
    {
        if (_state == state) return;
        _state = state;
        _observer.OnStateChanged(state);
    }
}
