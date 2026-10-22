using System.Buffers.Binary;
using System.Text;

namespace GamerPlug.Recorder.Core.Tests;

/// <summary>Minimal MP4 box reader used to assert the muxer's output structure.</summary>
internal static class Mp4BoxWalker
{
    public readonly record struct BoxRef(string Type, int Start, int Size)
    {
        public int PayloadStart => Start + 8;
        public int PayloadEnd => Start + Size;
    }

    public static List<BoxRef> TopLevel(byte[] data) => ChildrenIn(data, 0, data.Length);

    public static List<BoxRef> ChildrenIn(byte[] data, int start, int end)
    {
        var boxes = new List<BoxRef>();
        int o = start;
        while (o + 8 <= end)
        {
            uint size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(o));
            string type = Encoding.ASCII.GetString(data, o + 4, 4);
            if (size < 8 || o + (int)size > end) break;
            boxes.Add(new BoxRef(type, o, (int)size));
            o += (int)size;
        }
        return boxes;
    }

    /// <summary>Resolve a slash path like "moov/trak/mdia/minf/stbl/stsz".</summary>
    public static BoxRef? Find(byte[] data, string path)
    {
        var parts = path.Split('/');
        int start = 0, end = data.Length;
        BoxRef? found = null;
        foreach (var part in parts)
        {
            var children = ChildrenIn(data, start, end);
            BoxRef? match = null;
            foreach (var c in children)
                if (c.Type == part) { match = c; break; }
            if (match is null) return null;
            found = match;
            start = match.Value.PayloadStart;
            end = match.Value.PayloadEnd;
        }
        return found;
    }

    public static List<BoxRef> FindAll(byte[] data, string containerPath, string childType)
    {
        var container = Find(data, containerPath);
        if (container is null) return new();
        return ChildrenIn(data, container.Value.PayloadStart, container.Value.PayloadEnd)
            .FindAll(b => b.Type == childType);
    }
}
