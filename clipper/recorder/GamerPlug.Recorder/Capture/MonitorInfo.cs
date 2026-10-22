using GamerPlug.Recorder.Native;

namespace GamerPlug.Recorder.Capture;

public sealed record MonitorInfo(int Index, IntPtr Handle, string Name, int Width, int Height, int RefreshHz, bool IsPrimary);

public static class MonitorEnumeration
{
    public static List<MonitorInfo> Enumerate()
    {
        var list = new List<MonitorInfo>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hmon, IntPtr hdc, ref NativeMethods.RECT rect, IntPtr data) =>
        {
            var info = new NativeMethods.MONITORINFOEX { cbSize = MarshalSize() };
            if (NativeMethods.GetMonitorInfo(hmon, ref info))
            {
                int w = info.rcMonitor.Right - info.rcMonitor.Left;
                int h = info.rcMonitor.Bottom - info.rcMonitor.Top;
                bool primary = (info.dwFlags & 1) != 0;
                list.Add(new MonitorInfo(list.Count, hmon, info.szDevice ?? $"Display {list.Count + 1}", w, h, 60, primary));
            }
            return true;
        }, IntPtr.Zero);
        return list;

        static int MarshalSize() => System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>();
    }
}
