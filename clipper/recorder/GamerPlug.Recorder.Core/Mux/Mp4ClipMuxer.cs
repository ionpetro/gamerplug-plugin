using GamerPlug.Recorder.Core.Engine;

namespace GamerPlug.Recorder.Core.Mux;

/// <summary>
/// <see cref="IClipMuxer"/> backed by the pure-C# <see cref="Mp4Writer"/>. It
/// remuxes the encoded packets from the replay buffer into an MP4 with no
/// re-encode. Assumptions (what the Windows encoders must emit):
///   - H.264 video as Annex B access units; SPS/PPS may be in-band on keyframes.
///   - AAC audio as ADTS frames (header is parsed for codec config and stripped).
/// This keeps the whole save path cross-platform and testable.
/// </summary>
public sealed class Mp4ClipMuxer : IClipMuxer
{
    public ClipFile WriteClip(ClipExtract extract, string outputPath, MuxHints hints)
    {
        if (!extract.Success) throw new InvalidOperationException("cannot mux a failed extract");

        long clipStart = extract.StartPtsUs;
        var tracks = new List<Mp4Track>();

        var video = BuildVideoTrack(extract, hints, clipStart);
        tracks.Add(video);

        foreach (var audioPackets in extract.AudioTracks)
        {
            var audio = BuildAudioTrackOrNull(audioPackets, clipStart);
            if (audio != null) tracks.Add(audio);
        }

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using (var fs = File.Create(outputPath))
        {
            Mp4Writer.Write(fs, tracks);
        }

        long size = new FileInfo(outputPath).Length;
        return new ClipFile(outputPath, extract.DurationUs, hints.Width, hints.Height, size);
    }

    private static Mp4Track BuildVideoTrack(ClipExtract extract, MuxHints hints, long clipStart)
    {
        var track = new Mp4Track
        {
            Kind = StreamKind.Video,
            Width = hints.Width,
            Height = hints.Height,
        };

        foreach (var packet in extract.Video)
        {
            var nals = AnnexB.SplitNalUnits(packet.Data);
            foreach (var nal in nals)
            {
                int t = AnnexB.NalType(nal.Span);
                if (t == AnnexB.NalSps && track.Sps == null) track.Sps = nal.ToArray();
                else if (t == AnnexB.NalPps && track.Pps == null) track.Pps = nal.ToArray();
            }

            byte[] avcc = AnnexB.AnnexBToAvcc(packet.Data);
            if (avcc.Length == 0) continue; // SPS/PPS-only packet, not a sample
            track.Samples.Add(new Mp4Sample(
                avcc,
                packet.PtsUs - clipStart,
                packet.DtsUs - clipStart,
                packet.IsKeyframe));
        }

        if (track.Sps == null || track.Pps == null)
            throw new InvalidOperationException(
                "no SPS/PPS found in clip; encoder must emit parameter sets in-band on keyframes");
        return track;
    }

    private static Mp4Track? BuildAudioTrackOrNull(IReadOnlyList<MediaPacket> packets, long clipStart)
    {
        if (packets.Count == 0) return null;
        var track = new Mp4Track { Kind = StreamKind.Audio };
        bool configured = false;

        foreach (var packet in packets)
        {
            byte[] raw;
            if (AnnexB.TryParseAdts(packet.Data, out var adts))
            {
                if (!configured)
                {
                    track.AudioSpecificConfig = AnnexB.BuildAudioSpecificConfig(adts);
                    track.SampleRate = AnnexB.AacSampleRates[adts.SampleRateIndex];
                    track.Channels = adts.ChannelConfig == 0 ? 2 : adts.ChannelConfig;
                    configured = true;
                }
                int len = Math.Min(adts.FrameLength, packet.Data.Length) - adts.HeaderLength;
                if (len <= 0) continue;
                raw = new byte[len];
                Array.Copy(packet.Data, adts.HeaderLength, raw, 0, len);
            }
            else
            {
                // Raw AAC without ADTS requires a pre-set config; skip if we have none.
                if (!configured) return null;
                raw = packet.Data;
            }
            track.Samples.Add(new Mp4Sample(raw, packet.PtsUs - clipStart, packet.DtsUs - clipStart, true));
        }

        return track.Samples.Count > 0 ? track : null;
    }
}
