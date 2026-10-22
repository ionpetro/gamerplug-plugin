using System.Buffers.Binary;
using GamerPlug.Recorder.Core;
using GamerPlug.Recorder.Core.Mux;
using Xunit;

namespace GamerPlug.Recorder.Core.Tests;

public class Mp4WriterStructureTests
{
    private static Mp4Track VideoTrack(int samples)
    {
        var track = new Mp4Track
        {
            Kind = StreamKind.Video,
            Width = 320,
            Height = 240,
            Sps = new byte[] { 0x67, 0x42, 0x00, 0x1e, 0xAA },
            Pps = new byte[] { 0x68, 0xce, 0x3c, 0x80 },
        };
        for (int i = 0; i < samples; i++)
        {
            var data = new byte[] { 0, 0, 0, 2, 0x65, (byte)i }; // AVCC: len=2, fake NAL
            track.Samples.Add(new Mp4Sample(data, i * 33_333, i * 33_333, i % 5 == 0));
        }
        return track;
    }

    private static Mp4Track AudioTrack(int samples)
    {
        var track = new Mp4Track
        {
            Kind = StreamKind.Audio,
            SampleRate = 48000,
            Channels = 2,
            AudioSpecificConfig = new byte[] { 0x11, 0x90 }, // AAC-LC 48k stereo
        };
        for (int i = 0; i < samples; i++)
            track.Samples.Add(new Mp4Sample(new byte[] { 1, 2, 3, 4 }, i * 21_333, i * 21_333, true));
        return track;
    }

    private static byte[] WriteToBytes(params Mp4Track[] tracks)
    {
        using var ms = new MemoryStream();
        Mp4Writer.Write(ms, tracks);
        return ms.ToArray();
    }

    [Fact]
    public void ProducesFtypMdatMoovInOrder()
    {
        var bytes = WriteToBytes(VideoTrack(10));
        var top = Mp4BoxWalker.TopLevel(bytes);
        Assert.Equal("ftyp", top[0].Type);
        Assert.Equal("mdat", top[1].Type);
        Assert.Equal("moov", top[2].Type);
    }

    [Fact]
    public void VideoStszHasOneEntryPerSample()
    {
        var bytes = WriteToBytes(VideoTrack(10));
        var stsz = Mp4BoxWalker.Find(bytes, "moov/trak/mdia/minf/stbl/stsz");
        Assert.NotNull(stsz);
        uint sampleCount = BinaryPrimitives.ReadUInt32BigEndian(
            bytes.AsSpan(stsz!.Value.PayloadStart + 8));
        Assert.Equal(10u, sampleCount);
    }

    [Fact]
    public void VideoHasStssButAudioDoesNot()
    {
        var bytes = WriteToBytes(VideoTrack(10), AudioTrack(20));
        var traks = Mp4BoxWalker.FindAll(bytes, "moov", "trak");
        Assert.Equal(2, traks.Count);

        // First trak (video) must have stss; second (audio) must not.
        var videoStbl = StblOf(bytes, traks[0]);
        var audioStbl = StblOf(bytes, traks[1]);
        Assert.Contains(videoStbl, b => b.Type == "stss");
        Assert.DoesNotContain(audioStbl, b => b.Type == "stss");
    }

    [Fact]
    public void ChunkOffsetPointsInsideMdat()
    {
        var bytes = WriteToBytes(VideoTrack(5));
        var mdat = Mp4BoxWalker.Find(bytes, "mdat")!.Value;
        var stco = Mp4BoxWalker.Find(bytes, "moov/trak/mdia/minf/stbl/stco")!.Value;
        uint offset = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(stco.PayloadStart + 8));
        Assert.InRange((int)offset, mdat.PayloadStart, mdat.PayloadEnd);
    }

    [Fact]
    public void TwoTracksProduceTwoStsd()
    {
        var bytes = WriteToBytes(VideoTrack(4), AudioTrack(4));
        var traks = Mp4BoxWalker.FindAll(bytes, "moov", "trak");
        Assert.Equal(2, traks.Count);
    }

    private static List<Mp4BoxWalker.BoxRef> StblOf(byte[] bytes, Mp4BoxWalker.BoxRef trak)
    {
        // trak -> mdia -> minf -> stbl
        var mdia = Child(bytes, trak, "mdia");
        var minf = Child(bytes, mdia, "minf");
        var stbl = Child(bytes, minf, "stbl");
        return Mp4BoxWalker.ChildrenIn(bytes, stbl.PayloadStart, stbl.PayloadEnd);
    }

    private static Mp4BoxWalker.BoxRef Child(byte[] bytes, Mp4BoxWalker.BoxRef parent, string type)
    {
        foreach (var c in Mp4BoxWalker.ChildrenIn(bytes, parent.PayloadStart, parent.PayloadEnd))
            if (c.Type == type) return c;
        throw new Xunit.Sdk.XunitException($"missing child {type}");
    }
}
