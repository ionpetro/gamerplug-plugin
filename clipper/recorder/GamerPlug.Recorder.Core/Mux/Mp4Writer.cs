using System.Buffers.Binary;
using System.Text;

namespace GamerPlug.Recorder.Core.Mux;

/// <summary>One sample (access unit / audio frame) ready to mux.</summary>
public readonly record struct Mp4Sample(byte[] Data, long PtsUs, long DtsUs, bool Keyframe);

/// <summary>A single elementary stream to write into the MP4.</summary>
public sealed class Mp4Track
{
    public required StreamKind Kind { get; init; }
    public List<Mp4Sample> Samples { get; } = new();

    // Video (H.264 / avc1)
    public byte[]? Sps { get; set; }
    public byte[]? Pps { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    // Audio (AAC / mp4a)
    public byte[]? AudioSpecificConfig { get; set; }
    public int SampleRate { get; set; }
    public int Channels { get; set; }

    public const uint Timescale = 1_000_000; // microseconds
}

/// <summary>
/// Minimal, dependency-free progressive MP4 writer for H.264 video + AAC audio
/// tracks. It remuxes already-encoded samples (no re-encode), which is exactly
/// what a replay-buffer clip save needs. Layout is ftyp + mdat + moov, one chunk
/// per track. Validated against ffprobe in the test suite.
/// </summary>
public static class Mp4Writer
{
    private const uint MovieTimescale = 1000;

    public static void Write(Stream output, IReadOnlyList<Mp4Track> tracks)
    {
        if (tracks.Count == 0) throw new ArgumentException("no tracks", nameof(tracks));

        byte[] ftyp = BuildFtyp();

        // Concatenate each track's samples into one chunk; record sizes + offset.
        var chunkBytes = new List<byte[]>();
        var withinMdatOffset = new long[tracks.Count];
        long running = 0;
        for (int t = 0; t < tracks.Count; t++)
        {
            withinMdatOffset[t] = running;
            using var ms = new MemoryStream();
            foreach (var s in tracks[t].Samples) ms.Write(s.Data, 0, s.Data.Length);
            byte[] chunk = ms.ToArray();
            chunkBytes.Add(chunk);
            running += chunk.Length;
        }
        long mdatPayloadSize = running;

        long mdatHeaderFileOffset = ftyp.Length;
        long mdatPayloadFileOffset = mdatHeaderFileOffset + 8; // 32-bit size + type
        var chunkFileOffset = new long[tracks.Count];
        for (int t = 0; t < tracks.Count; t++)
            chunkFileOffset[t] = mdatPayloadFileOffset + withinMdatOffset[t];

        byte[] moov = BuildMoov(tracks, chunkFileOffset);

        // ftyp
        output.Write(ftyp, 0, ftyp.Length);
        // mdat (size + type + payload)
        Span<byte> mdatHeader = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(mdatHeader, (uint)(8 + mdatPayloadSize));
        Encoding.ASCII.GetBytes("mdat").CopyTo(mdatHeader.Slice(4));
        output.Write(mdatHeader);
        foreach (var chunk in chunkBytes) output.Write(chunk, 0, chunk.Length);
        // moov
        output.Write(moov, 0, moov.Length);
    }

    private static byte[] BuildFtyp()
    {
        using var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("isom"));          // major brand
        WriteU32(ms, 0x200);                                 // minor version
        ms.Write(Encoding.ASCII.GetBytes("isom"));
        ms.Write(Encoding.ASCII.GetBytes("iso2"));
        ms.Write(Encoding.ASCII.GetBytes("avc1"));
        ms.Write(Encoding.ASCII.GetBytes("mp41"));
        return Box("ftyp", ms.ToArray());
    }

    private static byte[] BuildMoov(IReadOnlyList<Mp4Track> tracks, long[] chunkFileOffset)
    {
        long movieDurationMovieTs = 0;
        foreach (var track in tracks)
        {
            long durUs = TrackDurationUs(track);
            long movieTs = durUs * MovieTimescale / Mp4Track.Timescale;
            if (movieTs > movieDurationMovieTs) movieDurationMovieTs = movieTs;
        }

        using var ms = new MemoryStream();
        ms.Write(BuildMvhd(movieDurationMovieTs, tracks.Count + 1));
        for (int t = 0; t < tracks.Count; t++)
            ms.Write(BuildTrak(tracks[t], t + 1, chunkFileOffset[t], movieDurationMovieTs));
        return Box("moov", ms.ToArray());
    }

    private static long TrackDurationUs(Mp4Track track)
    {
        if (track.Samples.Count == 0) return 0;
        long first = track.Samples[0].DtsUs;
        var last = track.Samples[^1];
        long lastDelta = track.Samples.Count >= 2
            ? last.DtsUs - track.Samples[^2].DtsUs
            : Math.Max(1, last.PtsUs - last.DtsUs);
        return last.DtsUs - first + Math.Max(lastDelta, 1);
    }

    private static byte[] BuildMvhd(long durationMovieTs, int nextTrackId)
    {
        using var ms = new MemoryStream();
        WriteU32(ms, 0);                 // version + flags
        WriteU32(ms, 0);                 // creation time
        WriteU32(ms, 0);                 // modification time
        WriteU32(ms, MovieTimescale);
        WriteU32(ms, (uint)durationMovieTs);
        WriteU32(ms, 0x00010000);        // rate 1.0
        WriteU16(ms, 0x0100);            // volume 1.0
        WriteU16(ms, 0);                 // reserved
        WriteU32(ms, 0); WriteU32(ms, 0); // reserved
        foreach (uint v in IdentityMatrix) WriteU32(ms, v);
        for (int i = 0; i < 6; i++) WriteU32(ms, 0); // pre_defined
        WriteU32(ms, (uint)nextTrackId);
        return Box("mvhd", ms.ToArray());
    }

    private static byte[] BuildTrak(Mp4Track track, int trackId, long chunkOffset, long movieDurationMovieTs)
    {
        using var ms = new MemoryStream();
        ms.Write(BuildTkhd(track, trackId, movieDurationMovieTs));
        ms.Write(BuildMdia(track, chunkOffset));
        return Box("trak", ms.ToArray());
    }

    private static byte[] BuildTkhd(Mp4Track track, int trackId, long durationMovieTs)
    {
        using var ms = new MemoryStream();
        WriteU32(ms, 0x00000007);        // version 0, flags: enabled|in movie|in preview
        WriteU32(ms, 0);                 // creation
        WriteU32(ms, 0);                 // modification
        WriteU32(ms, (uint)trackId);
        WriteU32(ms, 0);                 // reserved
        WriteU32(ms, (uint)durationMovieTs);
        WriteU32(ms, 0); WriteU32(ms, 0); // reserved
        WriteU16(ms, 0);                 // layer
        WriteU16(ms, 0);                 // alternate group
        WriteU16(ms, track.Kind == StreamKind.Audio ? (ushort)0x0100 : (ushort)0); // volume
        WriteU16(ms, 0);                 // reserved
        foreach (uint v in IdentityMatrix) WriteU32(ms, v);
        WriteU32(ms, (uint)track.Width << 16);  // width 16.16
        WriteU32(ms, (uint)track.Height << 16); // height 16.16
        return Box("tkhd", ms.ToArray());
    }

    private static byte[] BuildMdia(Mp4Track track, long chunkOffset)
    {
        using var ms = new MemoryStream();
        ms.Write(BuildMdhd(track));
        ms.Write(BuildHdlr(track));
        ms.Write(BuildMinf(track, chunkOffset));
        return Box("mdia", ms.ToArray());
    }

    private static byte[] BuildMdhd(Mp4Track track)
    {
        using var ms = new MemoryStream();
        WriteU32(ms, 0);                 // version + flags
        WriteU32(ms, 0);                 // creation
        WriteU32(ms, 0);                 // modification
        WriteU32(ms, Mp4Track.Timescale);
        WriteU32(ms, (uint)TrackDurationUs(track));
        WriteU16(ms, 0x55C4);            // language 'und'
        WriteU16(ms, 0);                 // pre_defined
        return Box("mdhd", ms.ToArray());
    }

    private static byte[] BuildHdlr(Mp4Track track)
    {
        using var ms = new MemoryStream();
        WriteU32(ms, 0);                 // version + flags
        WriteU32(ms, 0);                 // pre_defined
        ms.Write(Encoding.ASCII.GetBytes(track.Kind == StreamKind.Video ? "vide" : "soun"));
        WriteU32(ms, 0); WriteU32(ms, 0); WriteU32(ms, 0); // reserved
        ms.Write(Encoding.ASCII.GetBytes(track.Kind == StreamKind.Video ? "VideoHandler\0" : "SoundHandler\0"));
        return Box("hdlr", ms.ToArray());
    }

    private static byte[] BuildMinf(Mp4Track track, long chunkOffset)
    {
        using var ms = new MemoryStream();
        if (track.Kind == StreamKind.Video)
        {
            // vmhd
            using var v = new MemoryStream();
            WriteU32(v, 0x00000001); // flags
            WriteU16(v, 0); WriteU16(v, 0); WriteU16(v, 0); WriteU16(v, 0);
            ms.Write(Box("vmhd", v.ToArray()));
        }
        else
        {
            using var s = new MemoryStream();
            WriteU32(s, 0); WriteU16(s, 0); WriteU16(s, 0); // balance + reserved
            ms.Write(Box("smhd", s.ToArray()));
        }
        ms.Write(BuildDinf());
        ms.Write(BuildStbl(track, chunkOffset));
        return Box("minf", ms.ToArray());
    }

    private static byte[] BuildDinf()
    {
        using var url = new MemoryStream();
        WriteU32(url, 0x00000001); // self-contained flag
        byte[] urlBox = Box("url ", url.ToArray());
        using var dref = new MemoryStream();
        WriteU32(dref, 0); // version + flags
        WriteU32(dref, 1); // entry count
        dref.Write(urlBox);
        return Box("dinf", Box("dref", dref.ToArray()));
    }

    private static byte[] BuildStbl(Mp4Track track, long chunkOffset)
    {
        using var ms = new MemoryStream();
        ms.Write(BuildStsd(track));
        ms.Write(BuildStts(track));
        if (track.Kind == StreamKind.Video) ms.Write(BuildStss(track));
        byte[]? ctts = BuildCttsOrNull(track);
        if (ctts != null) ms.Write(ctts);
        ms.Write(BuildStsc(track));
        ms.Write(BuildStsz(track));
        ms.Write(BuildStco(chunkOffset));
        return Box("stbl", ms.ToArray());
    }

    private static byte[] BuildStsd(Mp4Track track)
    {
        byte[] entry = track.Kind == StreamKind.Video ? BuildAvc1(track) : BuildMp4a(track);
        using var ms = new MemoryStream();
        WriteU32(ms, 0); // version + flags
        WriteU32(ms, 1); // entry count
        ms.Write(entry);
        return Box("stsd", ms.ToArray());
    }

    private static byte[] BuildAvc1(Mp4Track track)
    {
        if (track.Sps == null || track.Pps == null)
            throw new InvalidOperationException("video track requires SPS and PPS");
        using var ms = new MemoryStream();
        for (int i = 0; i < 6; i++) ms.WriteByte(0); // reserved
        WriteU16(ms, 1);                              // data_reference_index
        WriteU16(ms, 0); WriteU16(ms, 0);             // pre_defined + reserved
        for (int i = 0; i < 3; i++) WriteU32(ms, 0);  // pre_defined[3]
        WriteU16(ms, (ushort)track.Width);
        WriteU16(ms, (ushort)track.Height);
        WriteU32(ms, 0x00480000);                     // horizresolution 72dpi
        WriteU32(ms, 0x00480000);                     // vertresolution
        WriteU32(ms, 0);                              // reserved
        WriteU16(ms, 1);                              // frame_count
        for (int i = 0; i < 32; i++) ms.WriteByte(0); // compressorname
        WriteU16(ms, 0x0018);                         // depth
        WriteU16(ms, 0xFFFF);                         // pre_defined
        ms.Write(BuildAvcC(track.Sps, track.Pps));
        return Box("avc1", ms.ToArray());
    }

    private static byte[] BuildAvcC(byte[] sps, byte[] pps)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(1);                 // configurationVersion
        ms.WriteByte(sps[1]);            // AVCProfileIndication
        ms.WriteByte(sps[2]);            // profile_compatibility
        ms.WriteByte(sps[3]);            // AVCLevelIndication
        ms.WriteByte(0xFF);              // 6 bits reserved + lengthSizeMinusOne (3)
        ms.WriteByte(0xE1);              // 3 bits reserved + numOfSPS (1)
        WriteU16(ms, (ushort)sps.Length);
        ms.Write(sps);
        ms.WriteByte(1);                 // numOfPPS
        WriteU16(ms, (ushort)pps.Length);
        ms.Write(pps);
        return Box("avcC", ms.ToArray());
    }

    private static byte[] BuildMp4a(Mp4Track track)
    {
        if (track.AudioSpecificConfig == null)
            throw new InvalidOperationException("audio track requires AudioSpecificConfig");
        using var ms = new MemoryStream();
        for (int i = 0; i < 6; i++) ms.WriteByte(0); // reserved
        WriteU16(ms, 1);                              // data_reference_index
        WriteU32(ms, 0); WriteU32(ms, 0);             // reserved (version/rev/vendor)
        WriteU16(ms, (ushort)track.Channels);
        WriteU16(ms, 16);                             // sample size
        WriteU16(ms, 0); WriteU16(ms, 0);             // pre_defined + reserved
        WriteU32(ms, (uint)track.SampleRate << 16);   // 16.16 sample rate
        ms.Write(BuildEsds(track.AudioSpecificConfig));
        return Box("mp4a", ms.ToArray());
    }

    private static byte[] BuildEsds(byte[] asc)
    {
        byte[] dsi = Descriptor(0x05, asc); // DecoderSpecificInfo
        using var dcd = new MemoryStream();
        dcd.WriteByte(0x40);             // objectTypeIndication = AAC
        dcd.WriteByte(0x15);             // streamType=audio(5)<<2 | upstream(0) | reserved(1)
        dcd.Write(new byte[] { 0, 0, 0 }); // bufferSizeDB
        WriteU32(dcd, 0);                // maxBitrate
        WriteU32(dcd, 0);                // avgBitrate
        dcd.Write(dsi);
        byte[] decoderConfig = Descriptor(0x04, dcd.ToArray());

        byte[] sl = Descriptor(0x06, new byte[] { 0x02 }); // SLConfigDescriptor

        using var es = new MemoryStream();
        WriteU16(es, 0);                 // ES_ID
        es.WriteByte(0);                 // flags
        es.Write(decoderConfig);
        es.Write(sl);
        byte[] esDescriptor = Descriptor(0x03, es.ToArray());

        using var ms = new MemoryStream();
        WriteU32(ms, 0);                 // version + flags
        ms.Write(esDescriptor);
        return Box("esds", ms.ToArray());
    }

    private static byte[] Descriptor(byte tag, byte[] payload)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(tag);
        // Length as 4-byte expandable (0x80-continued) form for simplicity.
        int len = payload.Length;
        ms.WriteByte((byte)(0x80 | ((len >> 21) & 0x7F)));
        ms.WriteByte((byte)(0x80 | ((len >> 14) & 0x7F)));
        ms.WriteByte((byte)(0x80 | ((len >> 7) & 0x7F)));
        ms.WriteByte((byte)(len & 0x7F));
        ms.Write(payload);
        return ms.ToArray();
    }

    private static byte[] BuildStts(Mp4Track track)
    {
        var entries = new List<(uint count, uint delta)>();
        var samples = track.Samples;
        for (int i = 0; i < samples.Count; i++)
        {
            uint delta;
            if (i + 1 < samples.Count)
                delta = (uint)Math.Max(1, samples[i + 1].DtsUs - samples[i].DtsUs);
            else
                delta = entries.Count > 0 ? entries[^1].delta : 1;
            if (entries.Count > 0 && entries[^1].delta == delta)
                entries[^1] = (entries[^1].count + 1, delta);
            else
                entries.Add((1, delta));
        }
        using var ms = new MemoryStream();
        WriteU32(ms, 0);
        WriteU32(ms, (uint)entries.Count);
        foreach (var (count, delta) in entries)
        {
            WriteU32(ms, count);
            WriteU32(ms, delta);
        }
        return Box("stts", ms.ToArray());
    }

    private static byte[] BuildStss(Mp4Track track)
    {
        var keyframes = new List<uint>();
        for (int i = 0; i < track.Samples.Count; i++)
            if (track.Samples[i].Keyframe) keyframes.Add((uint)(i + 1));
        using var ms = new MemoryStream();
        WriteU32(ms, 0);
        WriteU32(ms, (uint)keyframes.Count);
        foreach (var k in keyframes) WriteU32(ms, k);
        return Box("stss", ms.ToArray());
    }

    private static byte[]? BuildCttsOrNull(Mp4Track track)
    {
        bool any = track.Samples.Any(s => s.PtsUs != s.DtsUs);
        if (!any) return null;
        using var ms = new MemoryStream();
        WriteU32(ms, 0);
        WriteU32(ms, (uint)track.Samples.Count);
        foreach (var s in track.Samples)
        {
            WriteU32(ms, 1);
            WriteU32(ms, (uint)Math.Max(0, s.PtsUs - s.DtsUs));
        }
        return Box("ctts", ms.ToArray());
    }

    private static byte[] BuildStsc(Mp4Track track)
    {
        using var ms = new MemoryStream();
        WriteU32(ms, 0);
        WriteU32(ms, 1);                        // one entry: all samples in chunk 1
        WriteU32(ms, 1);                        // first_chunk
        WriteU32(ms, (uint)track.Samples.Count);// samples_per_chunk
        WriteU32(ms, 1);                        // sample_description_index
        return Box("stsc", ms.ToArray());
    }

    private static byte[] BuildStsz(Mp4Track track)
    {
        using var ms = new MemoryStream();
        WriteU32(ms, 0);
        WriteU32(ms, 0);                        // sample_size 0 => table follows
        WriteU32(ms, (uint)track.Samples.Count);
        foreach (var s in track.Samples) WriteU32(ms, (uint)s.Data.Length);
        return Box("stsz", ms.ToArray());
    }

    private static byte[] BuildStco(long chunkOffset)
    {
        using var ms = new MemoryStream();
        WriteU32(ms, 0);
        WriteU32(ms, 1);                        // one chunk
        WriteU32(ms, (uint)chunkOffset);
        return Box("stco", ms.ToArray());
    }

    private static readonly uint[] IdentityMatrix =
    {
        0x00010000, 0, 0,
        0, 0x00010000, 0,
        0, 0, 0x40000000,
    };

    private static byte[] Box(string type, byte[] payload)
    {
        byte[] result = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(result, (uint)result.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(result, 4);
        payload.CopyTo(result, 8);
        return result;
    }

    private static void WriteU32(Stream s, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        s.Write(b);
    }

    private static void WriteU16(Stream s, ushort v)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, v);
        s.Write(b);
    }
}
