using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace GamerPlug.Recorder.Capture;

/// <summary>Shared Vortice 3.6 D3D11 helpers (the 3.x bindings are the raw COM signatures).</summary>
internal static class D3DHelpers
{
    public static void CreateHardwareDevice(
        IDXGIAdapter? adapter,
        out ID3D11Device device,
        out ID3D11DeviceContext context)
    {
        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
        D3D11.D3D11CreateDevice(
            adapter?.NativePointer ?? IntPtr.Zero,
            adapter is null ? DriverType.Hardware : DriverType.Unknown,
            IntPtr.Zero,
            (uint)DeviceCreationFlags.BgraSupport,
            levels,
            (uint)levels.Length,
            D3D11.SdkVersion,
            out device!,
            out _,
            out context!);
        if (device is null || context is null)
            throw new CaptureStartException("D3D11CreateDevice returned a null device");
    }

    public static byte[] CopyTextureBgra(ID3D11Device device, ID3D11DeviceContext context, ID3D11Texture2D texture, out int width, out int height)
    {
        var desc = texture.Description;
        var stagingDesc = desc;
        stagingDesc.Usage = ResourceUsage.Staging;
        stagingDesc.BindFlags = BindFlags.None;
        stagingDesc.CPUAccessFlags = CpuAccessFlags.Read;
        stagingDesc.MiscFlags = ResourceOptionFlags.None;
        using var staging = device.CreateTexture2D(stagingDesc, null);
        context.CopyResource(staging, texture);
        context.Map(staging, 0, MapMode.Read, MapFlags.None, out MappedSubresource mapped);
        try
        {
            width = (int)desc.Width;
            height = (int)desc.Height;
            var bgra = new byte[width * height * 4];
            unsafe
            {
                byte* src = (byte*)mapped.DataPointer;
                int srcStride = (int)mapped.RowPitch;
                for (int y = 0; y < height; y++)
                    Marshal.Copy((IntPtr)(src + y * srcStride), bgra, y * width * 4, width * 4);
            }
            return bgra;
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }
}
