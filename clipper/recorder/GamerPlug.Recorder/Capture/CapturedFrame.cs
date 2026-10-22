namespace GamerPlug.Recorder.Capture;

/// <summary>One BGRA frame copied off the GPU. The encoder consumes these.</summary>
public sealed class CapturedFrame : IDisposable
{
    public required byte[] Bgra { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required long TimestampUs { get; init; }
    public int Stride => Width * 4;

    public void Dispose() { }
}

public interface IFrameSource : IDisposable
{
    int Width { get; }
    int Height { get; }
    string BackendName { get; }
    void Start(Action<CapturedFrame> onFrame);
    void Stop();
}

public sealed class CaptureStartException : Exception
{
    public CaptureStartException(string message) : base(message) { }
    public CaptureStartException(string message, Exception inner) : base(message, inner) { }
}
