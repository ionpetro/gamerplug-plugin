namespace GamerPlug.Recorder.Core.Mux;

/// <summary>
/// Helpers for H.264 Annex B byte streams and AAC ADTS frames. The replay buffer
/// stores encoded packets; to remux them into MP4 we need to split NAL units and
/// read codec configuration out of them, with no re-encode.
/// </summary>
public static class AnnexB
{
    /// <summary>Split an Annex B buffer into NAL units (payloads without start codes).</summary>
    public static List<ReadOnlyMemory<byte>> SplitNalUnits(ReadOnlyMemory<byte> data)
    {
        var result = new List<ReadOnlyMemory<byte>>();
        var span = data.Span;
        int i = 0;
        int n = span.Length;

        // Find first start code.
        int start = NextStartCode(span, 0, out int scLen);
        if (start < 0) return result;
        i = start + scLen;

        while (i <= n)
        {
            int next = NextStartCode(span, i, out int nextScLen);
            int end = next < 0 ? n : next;
            if (end > i)
                result.Add(data.Slice(i, end - i));
            if (next < 0) break;
            i = next + nextScLen;
        }
        return result;
    }

    private static int NextStartCode(ReadOnlySpan<byte> span, int from, out int scLen)
    {
        for (int i = from; i + 3 <= span.Length; i++)
        {
            if (span[i] == 0 && span[i + 1] == 0)
            {
                if (span[i + 2] == 1)
                {
                    scLen = 3;
                    return i;
                }
                if (i + 4 <= span.Length && span[i + 2] == 0 && span[i + 3] == 1)
                {
                    scLen = 4;
                    return i;
                }
            }
        }
        scLen = 0;
        return -1;
    }

    public static int NalType(ReadOnlySpan<byte> nal) => nal.Length == 0 ? -1 : nal[0] & 0x1F;

    public const int NalSps = 7;
    public const int NalPps = 8;
    public const int NalIdr = 5;
    public const int NalNonIdr = 1;

    public static bool IsVcl(int nalType) => nalType is >= 1 and <= 5;

    /// <summary>
    /// Convert an Annex B access unit into length-prefixed AVCC (4-byte lengths),
    /// dropping SPS/PPS/AUD NALs (those live in avcC, not the sample data).
    /// </summary>
    public static byte[] AnnexBToAvcc(ReadOnlyMemory<byte> accessUnit)
    {
        var nals = SplitNalUnits(accessUnit);
        using var ms = new MemoryStream();
        foreach (var nal in nals)
        {
            int t = NalType(nal.Span);
            if (t is NalSps or NalPps or 9 /*AUD*/) continue;
            int len = nal.Length;
            ms.WriteByte((byte)((len >> 24) & 0xFF));
            ms.WriteByte((byte)((len >> 16) & 0xFF));
            ms.WriteByte((byte)((len >> 8) & 0xFF));
            ms.WriteByte((byte)(len & 0xFF));
            ms.Write(nal.Span);
        }
        return ms.ToArray();
    }

    /// <summary>Decoded ADTS header fields plus the offset/length of the raw AAC payload.</summary>
    public readonly record struct AdtsFrame(
        int Profile, int SampleRateIndex, int ChannelConfig, int HeaderLength, int FrameLength);

    /// <summary>Parse the ADTS header at the start of <paramref name="data"/>.</summary>
    public static bool TryParseAdts(ReadOnlySpan<byte> data, out AdtsFrame frame)
    {
        frame = default;
        if (data.Length < 7) return false;
        if (data[0] != 0xFF || (data[1] & 0xF0) != 0xF0) return false;
        bool protectionAbsent = (data[1] & 0x01) != 0;
        int profile = (data[2] >> 6) & 0x03;
        int srIndex = (data[2] >> 2) & 0x0F;
        int chanCfg = ((data[2] & 0x01) << 2) | (data[3] >> 6);
        int frameLength = ((data[3] & 0x03) << 11) | (data[4] << 3) | (data[5] >> 5);
        int headerLen = protectionAbsent ? 7 : 9;
        if (frameLength < headerLen) return false;
        frame = new AdtsFrame(profile, srIndex, chanCfg, headerLen, frameLength);
        return true;
    }

    public static readonly int[] AacSampleRates =
    {
        96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050,
        16000, 12000, 11025, 8000, 7350, 0, 0, 0,
    };

    /// <summary>Build a 2-byte AAC AudioSpecificConfig from ADTS fields.</summary>
    public static byte[] BuildAudioSpecificConfig(AdtsFrame f)
    {
        int audioObjectType = f.Profile + 1; // ADTS profile is AOT-1
        int b0 = (audioObjectType << 3) | ((f.SampleRateIndex >> 1) & 0x07);
        int b1 = ((f.SampleRateIndex & 0x01) << 7) | ((f.ChannelConfig & 0x0F) << 3);
        return new[] { (byte)b0, (byte)b1 };
    }
}
