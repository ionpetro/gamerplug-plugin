using GamerPlug.Recorder.Core.Mux;
using Xunit;

namespace GamerPlug.Recorder.Core.Tests;

public class AnnexBSplitterTests
{
    private static byte[] Nal(int type, params byte[] rest)
    {
        var payload = new byte[4 + 1 + rest.Length];
        payload[0] = 0; payload[1] = 0; payload[2] = 0; payload[3] = 1;
        payload[4] = (byte)type;
        rest.CopyTo(payload, 5);
        return payload;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        using var ms = new MemoryStream();
        foreach (var p in parts) ms.Write(p);
        return ms.ToArray();
    }

    [Fact]
    public void GroupsNalsUntilNextVcl()
    {
        // AU1: SPS + PPS + IDR; AU2: P-slice
        var sps = Nal(7, 0x42, 0x00, 0x1e);
        var pps = Nal(8, 0xce);
        var idr = Nal(5, 0x88);
        var p = Nal(1, 0x99);
        var splitter = new AnnexBAccessUnitSplitter();
        var aus = splitter.Push(Concat(sps, pps, idr, p));
        aus.AddRange(splitter.Flush());

        Assert.True(aus.Count >= 2);
        Assert.Contains(aus[0], b => b == 5); // IDR type in first AU
        Assert.Contains(aus[^1], b => b == 1); // P-slice in last AU
    }

    [Fact]
    public void AudStartsANewAccessUnit()
    {
        var aud = Nal(9, 0x10);
        var vcl = Nal(1, 0x11);
        var aud2 = Nal(9, 0x10);
        var vcl2 = Nal(1, 0x22);
        var splitter = new AnnexBAccessUnitSplitter();
        var aus = splitter.Push(Concat(aud, vcl, aud2, vcl2));
        aus.AddRange(splitter.Flush());
        Assert.Equal(2, aus.Count);
    }

    [Fact]
    public void BuffersIncompleteTailAcrossPushes()
    {
        var idr = Nal(5, 0x88, 0x89, 0x8a);
        var splitter = new AnnexBAccessUnitSplitter();
        var first = splitter.Push(idr.AsSpan(0, 4)); // only the start code
        Assert.Empty(first);
        var rest = splitter.Push(idr.AsSpan(4));
        rest.AddRange(splitter.Flush());
        Assert.NotEmpty(rest);
    }
}

public class AdtsSplitterTests
{
    private static byte[] FakeAdts(int payloadLen)
    {
        int header = 7;
        int frameLen = header + payloadLen;
        var buf = new byte[frameLen];
        buf[0] = 0xFF;
        buf[1] = 0xF1; // MPEG-4, no CRC
        buf[2] = 0x50; // AAC-LC, 44.1k? we'll set 48k: srIndex=3 -> 0x4C-ish
        // profile=1 (LC) srIndex=3 chan=2:
        // buf[2] = (1 << 6) | (3 << 2) | 0 = 0x4C
        buf[2] = 0x4C;
        buf[3] = (byte)(0x80 | ((frameLen >> 11) & 0x03));
        buf[4] = (byte)((frameLen >> 3) & 0xFF);
        buf[5] = (byte)(((frameLen & 0x07) << 5) | 0x1F);
        buf[6] = 0xFC;
        for (int i = 0; i < payloadLen; i++) buf[header + i] = (byte)i;
        return buf;
    }

    [Fact]
    public void SplitsConcatenatedFrames()
    {
        var a = FakeAdts(8);
        var b = FakeAdts(12);
        var splitter = new AdtsFrameSplitter();
        var frames = splitter.Push(a.Concat(b).ToArray());
        Assert.Equal(2, frames.Count);
        Assert.Equal(a.Length, frames[0].Length);
        Assert.Equal(b.Length, frames[1].Length);
    }

    [Fact]
    public void BuffersPartialFrame()
    {
        var a = FakeAdts(16);
        var splitter = new AdtsFrameSplitter();
        Assert.Empty(splitter.Push(a.AsSpan(0, 5)));
        var frames = splitter.Push(a.AsSpan(5));
        Assert.Single(frames);
        Assert.Equal(a.Length, frames[0].Length);
    }
}
