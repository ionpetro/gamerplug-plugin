using System.Runtime.InteropServices;

namespace GamerPlug.Recorder.Native;

internal static class NativeMethods
{
    public const int WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const uint PM_REMOVE = 0x0001;

    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    public const uint MONITOR_DEFAULTTOPRIMARY = 1;
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    public const int SM_CXSCREEN = 0;
    public const int SM_CYSCREEN = 1;

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", PreserveSig = true)]
    public static extern uint CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    public static readonly Guid IGraphicsCaptureItemIID = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    public static readonly Guid IGraphicsCaptureItemInteropIID = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    [DllImport("ole32.dll")]
    public static extern int CoCreateInstance(
        [In] ref Guid clsid,
        IntPtr inner,
        uint context,
        [In] ref Guid iid,
        out IntPtr ppv);

    public static IntPtr GetActivationFactory(string classId)
    {
        // Windows.Graphics.Capture.GraphicsCaptureItem
        var inspectable = WinRtActivation.GetActivationFactory(classId);
        return inspectable;
    }
}

/// <summary>Tiny WinRT activation helper (RoGetActivationFactory).</summary>
internal static class WinRtActivation
{
    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int RoGetActivationFactory(
        [MarshalAs(UnmanagedType.HString)] string activatableClassId,
        [In] ref Guid iid,
        out IntPtr factory);

    private static readonly Guid IActivationFactory = new("00000035-0000-0000-C000-000000000046");
    private static readonly Guid IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");

    public static IntPtr GetActivationFactory(string classId)
    {
        var iid = IGraphicsCaptureItemInterop;
        int hr = RoGetActivationFactory(classId, ref iid, out var factory);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        return factory;
    }

    public static NativeMethods.IGraphicsCaptureItemInterop GetCaptureInterop()
    {
        IntPtr unk = GetActivationFactory("Windows.Graphics.Capture.GraphicsCaptureItem");
        var com = (NativeMethods.IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(unk);
        Marshal.Release(unk);
        return com;
    }
}
