using System.Runtime.InteropServices;
using GamerPlug.Recorder.Native;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace GamerPlug.Recorder.Capture;

/// <summary>
/// Windows.Graphics.Capture (WinRT) frame source. Preferred path: works for a
/// monitor or a window, including most borderless-fullscreen games. Frames are
/// copied to a CPU staging texture as BGRA so the FFmpeg encoder can consume
/// them. GPU zero-copy into NVENC is a documented follow-up.
/// </summary>
public sealed class WgcFrameSource : IFrameSource
{
    private readonly int _monitorIndex;
    private readonly bool _showCursor;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private GraphicsCaptureItem? _item;
    private Action<CapturedFrame>? _onFrame;
    private int _width;
    private int _height;
    private bool _running;

    public int Width => _width;
    public int Height => _height;
    public string BackendName => "wgc";

    public WgcFrameSource(int monitorIndex, bool showCursor)
    {
        _monitorIndex = monitorIndex;
        _showCursor = showCursor;
    }

    public void Start(Action<CapturedFrame> onFrame)
    {
        _onFrame = onFrame;
        var monitors = MonitorEnumeration.Enumerate();
        if (monitors.Count == 0) throw new CaptureStartException("no monitors found");
        var mon = monitors[Math.Clamp(_monitorIndex, 0, monitors.Count - 1)];
        _width = mon.Width;
        _height = mon.Height;

        D3DHelpers.CreateHardwareDevice(null, out _device, out _context);

        _item = CreateItemForMonitor(mon.Handle);
        var winrtDevice = CreateWinrtDevice(_device);
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            winrtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2,
            _item.Size);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = _showCursor;
        _session.StartCapture();
        _running = true;
    }

    public void Stop()
    {
        _running = false;
        if (_pool != null) _pool.FrameArrived -= OnFrameArrived;
        _session?.Dispose();
        _session = null;
        _pool?.Dispose();
        _pool = null;
        _item = null;
        _context?.Dispose();
        _context = null;
        _device?.Dispose();
        _device = null;
    }

    public void Dispose() => Stop();

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (!_running || _device is null || _context is null) return;
        using var frame = sender.TryGetNextFrame();
        if (frame is null) return;

        try
        {
            var texture = GetTextureFromSurface(frame.Surface);
            var bgra = D3DHelpers.CopyTextureBgra(_device, _context, texture, out int w, out int h);
            texture.Dispose();

            long ts = frame.SystemRelativeTime.Ticks / 10; // 100ns -> us
            _onFrame?.Invoke(new CapturedFrame
            {
                Bgra = bgra,
                Width = w,
                Height = h,
                TimestampUs = ts,
            });
        }
        catch
        {
            // Drop a bad frame rather than tearing down capture.
        }
    }

    private static GraphicsCaptureItem CreateItemForMonitor(IntPtr hmon)
    {
        var interop = WinRtActivation.GetCaptureInterop();
        var iid = NativeMethods.IGraphicsCaptureItemIID;
        IntPtr abi = interop.CreateForMonitor(hmon, ref iid);
        return GraphicsCaptureItem.FromAbi(abi);
    }

    private static IDirect3DDevice CreateWinrtDevice(ID3D11Device device)
    {
        // Vortice's generic CreateDirect3D11DeviceFromDXGIDevice<T> constructs a
        // SharpGen ComObject. IDirect3DDevice is a CsWinRT interface, so wrap the
        // native IInspectable ourselves.
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        uint hr = NativeMethods.CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out IntPtr inspectable);
        if (hr != 0 || inspectable == IntPtr.Zero)
            Marshal.ThrowExceptionForHR(hr == 0 ? unchecked((int)0x80004005) : unchecked((int)hr));
        try
        {
            return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }

    private static ID3D11Texture2D GetTextureFromSurface(IDirect3DSurface surface)
    {
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        var iid = typeof(ID3D11Texture2D).GUID;
        IntPtr tex = access.GetInterface(ref iid);
        return new ID3D11Texture2D(tex);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface([In] ref Guid iid);
    }
}
