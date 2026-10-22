using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace GamerPlug.Recorder.Capture;

/// <summary>
/// DXGI Desktop Duplication fallback. Monitor-only (cannot target a single
/// window). Used when Windows.Graphics.Capture is unavailable or fails to start.
/// </summary>
public sealed class DxgiFrameSource : IFrameSource
{
    private readonly int _monitorIndex;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGIOutputDuplication? _duplication;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _width;
    private int _height;

    public int Width => _width;
    public int Height => _height;
    public string BackendName => "dxgi";

    public DxgiFrameSource(int monitorIndex) => _monitorIndex = monitorIndex;

    public void Start(Action<CapturedFrame> onFrame)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        using var adapter = factory.GetAdapter1(0);
        D3D11.D3D11CreateDevice(
            adapter,
            DriverType.Unknown,
            DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
            out _device);
        _context = _device.ImmediateContext;

        using var output = adapter.GetOutput(Math.Max(0, _monitorIndex));
        var bounds = output.Description.DesktopCoordinates;
        _width = Math.Abs(bounds.Right - bounds.Left);
        _height = Math.Abs(bounds.Bottom - bounds.Top);

        using var output1 = output.QueryInterface<IDXGIOutput1>();
        _duplication = output1.DuplicateOutput(_device);

        _cts = new CancellationTokenSource();
        _loop = Task.Factory.StartNew(() => Pump(onFrame, _cts.Token), _cts.Token,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _loop?.Wait(500); } catch { /* ignore */ }
        _duplication?.Dispose();
        _duplication = null;
        _context?.Dispose();
        _context = null;
        _device?.Dispose();
        _device = null;
    }

    public void Dispose() => Stop();

    private void Pump(Action<CapturedFrame> onFrame, CancellationToken token)
    {
        while (!token.IsCancellationRequested && _duplication is not null && _device is not null && _context is not null)
        {
            var result = _duplication.AcquireNextFrame(500, out _, out IDXGIResource? resource);
            if (result.Failure)
            {
                continue;
            }
            try
            {
                using var texture = resource!.QueryInterface<ID3D11Texture2D>();
                var desc = texture.Description;
                var stagingDesc = desc;
                stagingDesc.Usage = ResourceUsage.Staging;
                stagingDesc.BindFlags = BindFlags.None;
                stagingDesc.CPUAccessFlags = CpuAccessFlags.Read;
                stagingDesc.MiscFlags = ResourceOptionFlags.None;
                using var staging = _device.CreateTexture2D(stagingDesc);
                _context.CopyResource(staging, texture);
                var mapped = _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                int w = (int)desc.Width;
                int h = (int)desc.Height;
                var bgra = new byte[w * h * 4];
                unsafe
                {
                    byte* src = (byte*)mapped.DataPointer;
                    int srcStride = mapped.RowPitch;
                    for (int y = 0; y < h; y++)
                        Marshal.Copy((IntPtr)(src + y * srcStride), bgra, y * w * 4, w * 4);
                }
                _context.Unmap(staging, 0);
                onFrame(new CapturedFrame
                {
                    Bgra = bgra,
                    Width = w,
                    Height = h,
                    TimestampUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000,
                });
            }
            finally
            {
                resource?.Dispose();
                _duplication.ReleaseFrame();
            }
        }
    }
}
