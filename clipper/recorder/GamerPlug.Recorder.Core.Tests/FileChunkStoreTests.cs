using GamerPlug.Recorder.Core;
using Xunit;

namespace GamerPlug.Recorder.Core.Tests;

public class FileChunkStoreTests : IDisposable
{
    private readonly string _dir;

    public FileChunkStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "gpclip-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void PersistsAndRecoversIntactChunks()
    {
        var store = new FileChunkStore(_dir, "vbuf-0");
        var c1 = new TimeDataChunk(0, TimeDataChunk.ChunkTypeVideo, 4096);
        c1.TryAppend(PacketFactory.Video(0, true));
        var c2 = new TimeDataChunk(1, TimeDataChunk.ChunkTypeVideo, 4096);
        c2.TryAppend(PacketFactory.Video(16_666, false));
        store.Persist(c1);
        store.Persist(c2);

        var recovered = store.Recover();
        Assert.Equal(2, recovered.Count);
        Assert.Equal(0u, recovered[0].Id);
        Assert.Equal(1u, recovered[1].Id);
    }

    [Fact]
    public void SkipsCorruptChunkFileOnRecovery()
    {
        var store = new FileChunkStore(_dir, "vbuf-0");
        var good = new TimeDataChunk(0, TimeDataChunk.ChunkTypeVideo, 4096);
        good.TryAppend(PacketFactory.Video(0, true));
        store.Persist(good);

        // Simulate a torn write: a second chunk file with garbage bytes.
        File.WriteAllBytes(Path.Combine(_dir, "buffer-vbuf-0-000000000001.bin"),
            new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        var recovered = store.Recover();
        Assert.Single(recovered);
        Assert.Equal(0u, recovered[0].Id);
    }

    [Fact]
    public void RecoveryRebuildsRingBuffer()
    {
        // Write a buffer to disk via one ring, then recover into a fresh ring.
        var storeFactory = (StreamKind k, int i) => (IChunkStore)new FileChunkStore(_dir, $"{k}-{i}");
        var writer = new ReplayBuffer(retentionUs: 120_000_000, audioTracks: 0,
            storeFactory: storeFactory, videoChunkBytes: 2048);
        PacketFactory.FillVideo(writer, frameCount: 200, gop: 60);

        var reader = new ReplayBuffer(retentionUs: 120_000_000, audioTracks: 0,
            storeFactory: storeFactory, videoChunkBytes: 2048);
        reader.Recover();

        var clip = reader.SaveLastSeconds(1.0);
        Assert.True(clip.Success);
        Assert.True(clip.Video[0].IsKeyframe);
    }

    [Fact]
    public void ClearRemovesPersistedChunks()
    {
        var store = new FileChunkStore(_dir, "vbuf-0");
        var c = new TimeDataChunk(0, TimeDataChunk.ChunkTypeVideo, 4096);
        c.TryAppend(PacketFactory.Video(0, true));
        store.Persist(c);
        store.Clear();
        Assert.Empty(store.Recover());
    }
}
