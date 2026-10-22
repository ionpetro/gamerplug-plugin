namespace GamerPlug.Recorder.Core;

/// <summary>
/// Standard CRC-32 (IEEE 802.3, polynomial 0xEDB88320). Used to protect chunk
/// headers and payloads so a crash mid-write is detectable on recovery — the
/// same idea as the header checksum in scope.dll's on-disk chunk format
/// (REPORT.md §5).
/// </summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        const uint poly = 0xEDB88320u;
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? poly ^ (c >> 1) : c >> 1;
            }
            table[i] = c;
        }
        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }
        return crc ^ 0xFFFFFFFFu;
    }
}
