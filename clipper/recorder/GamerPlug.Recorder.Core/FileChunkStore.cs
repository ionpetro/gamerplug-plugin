namespace GamerPlug.Recorder.Core;

/// <summary>
/// Disk-backed chunk store with crash recovery. Each sealed chunk is written to
/// <c>buffer-{name}-{id:D12}.bin</c> under a recording-buffer directory (the
/// managed names in REPORT.md §5 are the same idea). On restart, <see
/// cref="Recover"/> validates each file's magic + CRCs via
/// <see cref="TimeDataChunk.TryDeserialize"/> and keeps only intact chunks.
/// </summary>
public sealed class FileChunkStore : IChunkStore
{
    private readonly string _directory;
    private readonly string _name;

    public FileChunkStore(string directory, string name)
    {
        _directory = directory;
        _name = name;
        Directory.CreateDirectory(directory);
    }

    private string PathFor(ulong id) => Path.Combine(_directory, $"buffer-{_name}-{id:D12}.bin");

    public void Persist(TimeDataChunk chunk)
    {
        byte[] bytes = chunk.Serialize();
        string finalPath = PathFor(chunk.Id);
        // Write to a temp file then move, so a crash mid-write never leaves a
        // half-written file at the real path.
        string tmp = finalPath + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, finalPath, overwrite: true);
    }

    public void Remove(ulong chunkId)
    {
        string path = PathFor(chunkId);
        if (File.Exists(path)) File.Delete(path);
    }

    public IReadOnlyList<TimeDataChunk> Recover()
    {
        var result = new List<TimeDataChunk>();
        if (!Directory.Exists(_directory)) return result;

        var files = Directory.GetFiles(_directory, $"buffer-{_name}-*.bin");
        // Recover in id order so the ring is reconstructed chronologically.
        Array.Sort(files, StringComparer.Ordinal);
        foreach (var file in files)
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(file);
            }
            catch (IOException)
            {
                continue;
            }
            var chunk = TimeDataChunk.TryDeserialize(bytes);
            if (chunk is not null)
            {
                result.Add(chunk);
            }
            // Corrupt/partial chunk: leave the file for diagnostics, skip it.
        }
        return result;
    }

    public void Clear()
    {
        if (!Directory.Exists(_directory)) return;
        foreach (var file in Directory.GetFiles(_directory, $"buffer-{_name}-*.bin*"))
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }
}
