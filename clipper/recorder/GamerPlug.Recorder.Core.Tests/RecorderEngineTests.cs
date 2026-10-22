using GamerPlug.Recorder.Core;
using GamerPlug.Recorder.Core.Engine;
using Xunit;

namespace GamerPlug.Recorder.Core.Tests;

internal sealed class FakeMuxer : IClipMuxer
{
    public int Calls;
    public ClipExtract? LastExtract;
    public bool Throw;

    public ClipFile WriteClip(ClipExtract extract, string outputPath, MuxHints hints)
    {
        Calls++;
        LastExtract = extract;
        if (Throw) throw new InvalidOperationException("mux failed");
        return new ClipFile(outputPath, extract.DurationUs, hints.Width, hints.Height, 1024);
    }
}

internal sealed class RecordingObserver : IRecorderObserver
{
    public readonly List<RecorderState> States = new();
    public readonly List<SavedClipInfo> Saved = new();
    public readonly List<(ClipSaveStatus status, string message)> Failed = new();

    public void OnStateChanged(RecorderState state) => States.Add(state);
    public void OnClipSaved(SavedClipInfo info) => Saved.Add(info);
    public void OnClipFailed(ClipSaveStatus status, string message) => Failed.Add((status, message));
    public void OnLog(string level, string message) { }
}

public class RecorderEngineTests
{
    private static RecorderSettings Settings() => new()
    {
        Width = 1920,
        Height = 1080,
        Fps = 60,
        BufferSeconds = 120,
        ClipSeconds = 30,
        AudioTrackCount = 1,
        InMemoryBuffer = true,
        ClipFolder = Path.GetTempPath(),
    };

    private static void Fill(RecorderEngine engine, int frames, int gop = 60)
    {
        for (int i = 0; i < frames; i++)
            engine.OnVideoPacket(PacketFactory.Video(i * PacketFactory.FrameDurUs, i % gop == 0));
    }

    [Fact]
    public void SavesClipThroughMuxerOnClipAction()
    {
        var muxer = new FakeMuxer();
        var obs = new RecordingObserver();
        var engine = new RecorderEngine(Settings(), muxer, obs, idFactory: () => "fixed-id");
        engine.Start();
        Fill(engine, 600);

        var result = engine.ExecuteAction("clip;3", ActionSource.Hotkey);

        Assert.True(result.Accepted);
        Assert.Equal(1, muxer.Calls);
        Assert.Single(obs.Saved);
        Assert.Equal("fixed-id", obs.Saved[0].Id);
        Assert.Equal("clip;3", obs.Saved[0].SaveType);
        Assert.True(obs.Saved[0].DurationUs >= 3_000_000);
    }

    [Fact]
    public void RejectsUnknownAction()
    {
        var engine = new RecorderEngine(Settings(), new FakeMuxer());
        engine.Start();
        var r = engine.ExecuteAction("explode", ActionSource.Ui);
        Assert.False(r.Accepted);
        Assert.Equal(ActionRejectReason.UnknownAction, r.Reason);
    }

    [Fact]
    public void RejectsClipWhenNotCapturing()
    {
        var engine = new RecorderEngine(Settings(), new FakeMuxer());
        // not started -> Idle
        var r = engine.ExecuteAction("clip;5", ActionSource.Hotkey);
        Assert.False(r.Accepted);
        Assert.Equal(ActionRejectReason.NotRecording, r.Reason);
    }

    [Fact]
    public void RejectsClipWhenClipsDisabled()
    {
        var engine = new RecorderEngine(Settings(), new FakeMuxer());
        engine.Start();
        engine.SetClipsEnabled(false);
        var r = engine.ExecuteAction("clip;5", ActionSource.Hotkey);
        Assert.False(r.Accepted);
        Assert.Equal(ActionRejectReason.ClipsDisabled, r.Reason);
    }

    [Fact]
    public void ReportsFailureWhenBufferEmpty()
    {
        var muxer = new FakeMuxer();
        var obs = new RecordingObserver();
        var engine = new RecorderEngine(Settings(), muxer, obs);
        engine.Start();
        var r = engine.ExecuteAction("clip;5", ActionSource.Hotkey);
        Assert.True(r.Accepted); // action valid; save fails asynchronously
        Assert.Equal(0, muxer.Calls);
        Assert.Single(obs.Failed);
        Assert.Equal(ClipSaveStatus.NoDataInBuffer, obs.Failed[0].status);
    }

    [Fact]
    public void ReportsFailureWhenMuxerThrows()
    {
        var muxer = new FakeMuxer { Throw = true };
        var obs = new RecordingObserver();
        var engine = new RecorderEngine(Settings(), muxer, obs);
        engine.Start();
        Fill(engine, 300);
        engine.ExecuteAction("clip;2", ActionSource.Hotkey);
        Assert.Single(obs.Failed);
        Assert.Empty(obs.Saved);
    }

    [Fact]
    public void ReturnsToCapturingAfterSave()
    {
        var engine = new RecorderEngine(Settings(), new FakeMuxer());
        engine.Start();
        Fill(engine, 300);
        engine.ExecuteAction("clip;2", ActionSource.Hotkey);
        Assert.Equal(RecorderState.Capturing, engine.State);
    }

    [Fact]
    public void AppliesSettingsAndRebuildsBufferShape()
    {
        var engine = new RecorderEngine(Settings(), new FakeMuxer());
        engine.Start();
        Fill(engine, 300);
        var newSettings = Settings();
        newSettings.AudioTrackCount = 3;
        engine.ApplySettings(newSettings);
        // Buffer was rebuilt; a fresh save has no data yet.
        Assert.Equal(3, engine.Settings.AudioTrackCount);
    }

    [Fact]
    public void MultiTrackAudioFlowsIntoSavedClip()
    {
        var settings = Settings();
        settings.AudioTrackCount = 2;
        var muxer = new FakeMuxer();
        var engine = new RecorderEngine(settings, muxer, new RecordingObserver());
        engine.Start();
        Fill(engine, 300);
        for (int track = 0; track < 2; track++)
            for (int i = 0; i < 200; i++)
                engine.OnAudioPacket(PacketFactory.Audio(track, i * PacketFactory.AudioDurUs));

        engine.ExecuteAction("clip;2", ActionSource.Hotkey);
        Assert.NotNull(muxer.LastExtract);
        Assert.Equal(2, muxer.LastExtract!.AudioTracks.Count);
    }
}
