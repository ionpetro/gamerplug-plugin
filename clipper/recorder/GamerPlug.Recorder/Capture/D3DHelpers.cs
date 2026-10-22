using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace GamerPlug.Recorder.Capture;

/// <summary>
/// Shared Vortice 3.6 D3D11 helpers. 3.x exposes friendly overloads (adapter +
/// <see cref="DeviceCreationFlags"/>), not the raw 10-argument P/Invoke.
/// </summary>
internal static class D3DHelpers
{
    private static readonly FeatureLevel[] FeatureLevels =
    {
        FeatureLevel.Level_11_1,
        FeatureLevel.Level_11_0,
    };

    public static void CreateHardwareDevice(
        IDXGIAdapter? adapter,
        out ID3D11Device device,
        out ID3D11DeviceContext context)
    {
        Result result = adapter is null
            ? D3D11.D3D11CreateDevice(
                IntPtr.Zero,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                FeatureLevels,
                out device!,
                out context!)
            : D3D11.D3D11CreateDevice(
                adapter,
                DriverType.Unknown,
                DeviceCreationFlags.BgraSupport,
                FeatureLevels,
                out device!,
                out context!);

        if (result.Failure || device is null || context is null)
            throw new CaptureStartException($"D3D11CreateDevice failed: {result}");
    }

    public static byte[] CopyTextureBgra(ID3D11Device device, ID3D11DeviceContext context, ID3D11Texture2D texture, out int width, out int height)
    {
        var desc = texture.Description;
        var stagingDesc = desc;
        stagingDesc.Usage = ResourceUsage.Staging;
        stagingDesc.BindFlags = BindFlags.None;
        stagingDesc.CPUAccessFlags = CpuAccessFlags.Read;
        stagingDesc.MiscFlags = ResourceOptionFlags.None;
        using var staging = device.CreateTexture2D(in stagingDesc);
        context.CopyResource(staging, texture);
        MappedSubresource mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
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
