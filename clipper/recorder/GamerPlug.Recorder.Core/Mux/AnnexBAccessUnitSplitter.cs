namespace GamerPlug.Recorder.Core.Mux;

/// <summary>
/// Splits a continuous Annex B H.264 byte stream into access units (one encoded
/// picture each). An AU starts at an AUD NAL or at the first VCL NAL after the
/// previous AU already contained a VCL. Used to turn ffmpeg's <c>-f h264</c>
/// stdout into <see cref="MediaPacket"/>s for the replay buffer.
/// </summary>
public sealed class AnnexBAccessUnitSplitter
{
    private readonly MemoryStream _pending = new();
    private bool _currentHasVcl;

    /// <summary>
    /// Consume a chunk of the Annex B stream. Completed access units (without
    /// trailing start codes of the next AU) are returned.
    /// </summary>
    public List<byte[]> Push(ReadOnlySpan<byte> data)
    {
        _pending.Write(data);
        return Drain(flush: false);
    }

    /// <summary>Emit any leftover bytes as a final access unit.</summary>
    public List<byte[]> Flush() => Drain(flush: true);

    private List<byte[]> Drain(bool flush)
    {
        var result = new List<byte[]>();
        byte[] buf = _pending.ToArray();
        if (buf.Length == 0) return result;

        int auStart = -1;
        int searchFrom = 0;
        _currentHasVcl = false;

        while (true)
        {
            int sc = NextStartCode(buf, searchFrom, out int scLen);
            if (sc < 0) break;
            int nalStart = sc + scLen;
            if (nalStart >= buf.Length) break; // incomplete NAL header
            int nalType = buf[nalStart] & 0x1F;
            bool isVcl = AnnexB.IsVcl(nalType);
            bool isAud = nalType == 9;

            bool startNew = isAud || (isVcl && _currentHasVcl);
            if (startNew && auStart >= 0 && sc > auStart)
            {
                result.Add(buf.AsSpan(auStart, sc - auStart).ToArray());
                _currentHasVcl = false;
            }

            if (auStart < 0 || startNew) auStart = sc;
            if (isVcl) _currentHasVcl = true;
            searchFrom = nalStart;
        }

        int consumed;
        if (flush && auStart >= 0)
        {
            result.Add(buf.AsSpan(auStart).ToArray());
            consumed = buf.Length;
        }
        else
        {
            consumed = auStart >= 0 ? auStart : 0;
            // Keep the in-progress AU (from auStart) plus any trailing incomplete bytes.
            if (auStart < 0) consumed = 0;
        }

        _pending.SetLength(0);
        if (consumed < buf.Length)
            _pending.Write(buf, consumed, buf.Length - consumed);

        if (flush) _currentHasVcl = false;
        return result;
    }

    private static int NextStartCode(byte[] span, int from, out int scLen)
    {
        for (int i = from; i + 3 <= span.Length; i++)
        {
            if (span[i] == 0 && span[i + 1] == 0)
            {
                if (span[i + 2] == 1) { scLen = 3; return i; }
                if (i + 4 <= span.Length && span[i + 2] == 0 && span[i + 3] == 1)
                { scLen = 4; return i; }
            }
        }
        scLen = 0;
        return -1;
    }
}

/// <summary>Splits a continuous ADTS AAC stream into complete frames.</summary>
public sealed class AdtsFrameSplitter
{
    private readonly MemoryStream _pending = new();

    public List<byte[]> Push(ReadOnlySpan<byte> data)
    {
        _pending.Write(data);
        return Drain();
    }

    public List<byte[]> Flush() => Drain();

    private List<byte[]> Drain()
    {
        var result = new List<byte[]>();
        byte[] buf = _pending.ToArray();
        int o = 0;
        while (o < buf.Length)
        {
            int sync = FindSync(buf, o);
            if (sync < 0) { o = buf.Length; break; }
            if (sync + 7 > buf.Length)
            {
                o = sync;
                break; // wait for a full header
            }
            if (!AnnexB.TryParseAdts(buf.AsSpan(sync), out var frame))
            {
                o = sync + 1;
                continue;
            }
            if (sync + frame.FrameLength > buf.Length)
            {
                o = sync;
                break; // wait for the rest of the frame
            }
            result.Add(buf.AsSpan(sync, frame.FrameLength).ToArray());
            o = sync + frame.FrameLength;
        }
        _pending.SetLength(0);
        if (o < buf.Length) _pending.Write(buf, o, buf.Length - o);
        return result;
    }

    private static int FindSync(byte[] buf, int from)
    {
        for (int i = from; i + 1 < buf.Length; i++)
            if (buf[i] == 0xFF && (buf[i + 1] & 0xF0) == 0xF0) return i;
        return -1;
    }
}
