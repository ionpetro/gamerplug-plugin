using Xunit;

namespace GamerPlug.Recorder.Core.Tests;

public class FfToolsTests
{
    [Fact]
    public void ParsePacketsJsonAcceptsStringOrNumberFields()
    {
        const string json = """
            {"packets":[
              {"flags":"K_","pos":"0","size":"12"},
              {"flags":"_","pos":20,"size":8}
            ]}
            """;
        var packets = FfTools.ParsePacketsJson(json);
        Assert.Equal(2, packets.Count);
        Assert.Equal(new FfTools.ProbedPacket(0, 12, true), packets[0]);
        Assert.Equal(new FfTools.ProbedPacket(20, 8, false), packets[1]);
    }

    [Fact]
    public void ParsePacketsJsonSkipsMalformedEntries()
    {
        const string json = """{"packets":[{"flags":"K_"},{"pos":"-1","size":"4"},{"pos":"2","size":"0"}]}""";
        Assert.Empty(FfTools.ParsePacketsJson(json));
    }
}
