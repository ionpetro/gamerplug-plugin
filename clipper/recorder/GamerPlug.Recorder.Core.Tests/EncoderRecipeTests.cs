using GamerPlug.Recorder.Core;
using GamerPlug.Recorder.Core.Encode;
using Xunit;

namespace GamerPlug.Recorder.Core.Tests;

public class EncoderRecipeTests
{
    [Fact]
    public void AutoTriesHardwareThenSoftware()
    {
        var chain = EncoderRecipe.FallbackChain(EncoderBackend.Auto, VideoCodec.H264);
        Assert.Equal(EncoderBackend.Nvenc, chain[0].Backend);
        Assert.Equal("h264_nvenc", chain[0].FfmpegVideoCodec);
        Assert.Contains(chain, a => a.Backend == EncoderBackend.Amf);
        Assert.Contains(chain, a => a.Backend == EncoderBackend.Qsv);
        Assert.Contains(chain, a => a.FfmpegVideoCodec == "libx264");
        Assert.Contains(chain, a => a.FfmpegVideoCodec == "libopenh264");
    }

    [Fact]
    public void ExplicitBackendDoesNotFallBackToOthersFirst()
    {
        var chain = EncoderRecipe.FallbackChain(EncoderBackend.Amf, VideoCodec.Hevc);
        Assert.Equal("hevc_amf", chain[0].FfmpegVideoCodec);
        Assert.DoesNotContain(chain, a => a.Backend == EncoderBackend.Nvenc);
    }

    [Fact]
    public void VideoArgsContainDumpExtraAndAnnexBOutput()
    {
        var attempt = EncoderRecipe.FallbackChain(EncoderBackend.Nvenc, VideoCodec.H264)[0];
        var args = EncoderRecipe.BuildVideoArgs(attempt, 1920, 1080, 60, 30_000).ToList();
        Assert.Contains("h264_nvenc", args);
        Assert.Contains("dump_extra=freq=keyframe", args);
        int dumpAt = args.IndexOf("dump_extra=freq=keyframe");
        Assert.Equal("h264", args[args.IndexOf("-f", dumpAt) + 1]);
        Assert.Contains("pipe:1", args);
        Assert.DoesNotContain("libx264", args);
    }

    [Fact]
    public void AudioArgsAreAacAdts()
    {
        var args = EncoderRecipe.BuildAudioArgs();
        Assert.Contains("aac", args);
        Assert.Contains("adts", args);
        Assert.Contains("s16le", args);
    }
}
