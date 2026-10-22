namespace GamerPlug.Recorder.Core;

/// <summary>
/// Why a clip save succeeded or failed. The failure values mirror the reasons
/// scope.dll's SaveVideoFile reports (REPORT.md §3): noDataInBuffer,
/// negativeBuffer, etc.
/// </summary>
public enum ClipSaveStatus
{
    Ok = 0,
    NoDataInBuffer,
    NegativeBuffer,
    NoKeyframe,
}

/// <summary>
/// The packets a clip is cut from: video starting at a keyframe, plus one list
/// per audio track, all on the shared microsecond clock. A muxer writes these
/// straight to MP4 with no re-encode.
/// </summary>
public sealed class ClipExtract
{
    public ClipSaveStatus Status { get; init; }
    public long StartPtsUs { get; init; }
    public long EndPtsUs { get; init; }
    public IReadOnlyList<MediaPacket> Video { get; init; } = Array.Empty<MediaPacket>();
    public IReadOnlyList<IReadOnlyList<MediaPacket>> AudioTracks { get; init; }
        = Array.Empty<IReadOnlyList<MediaPacket>>();

    public bool Success => Status == ClipSaveStatus.Ok;
    public long DurationUs => EndPtsUs - StartPtsUs;

    public static ClipExtract Failed(ClipSaveStatus status) => new() { Status = status };
}
