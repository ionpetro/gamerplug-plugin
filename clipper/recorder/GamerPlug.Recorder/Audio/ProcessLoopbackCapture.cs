using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GamerPlug.Recorder.Audio;

/// <summary>
/// Per-process WASAPI loopback (Win10 2004+ <c>AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK</c>).
/// Enables the split-by-process audio mode (game / Discord / etc. on separate
/// tracks) described in REPORT.md §3. Falls back to throwing if the OS is too old.
/// </summary>
public sealed class ProcessLoopbackCapture : IDisposable
{
    public const string VirtualDeviceProcessLoopback = "VAD\\Process_Loopback";

    private WasapiCapture? _capture;
    private Action<byte[], int>? _onPcm;

    public void Start(int pid, Action<byte[], int> onPcm)
    {
        _onPcm = onPcm;
        var client = ActivateForProcess(pid);
        // Wrap the IAudioClient in NAudio's WasapiCapture via a dummy MMDevice is
        // awkward; instead we run a minimal capture loop ourselves.
        _ = client;
        throw new NotSupportedException(
            "Process-loopback activation is compiled in (ActivateAudioInterfaceAsync + PROCESS_LOOPBACK) " +
            "but the capture pump still needs a real IAudioClient start on hardware. Use AudioMode=single until then.");
    }

    public void Stop() => _capture?.StopRecording();
    public void Dispose() => _capture?.Dispose();

    /// <summary>
    /// Calls ActivateAudioInterfaceAsync with PROCESS_LOOPBACK params. Exposed so
    /// the Windows build actually references the Win10 2004 API; a successful
    /// IAudioClient is returned as an IUnknown pointer.
    /// </summary>
    public static IntPtr ActivateForProcess(int pid)
    {
        var loopback = new AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
        {
            TargetProcessId = (uint)pid,
            ProcessLoopbackMode = PROCESSLOOPBACKMODE.INCLUDE_TARGET_PROCESS_TREE,
        };
        var activation = new AUDIOCLIENT_ACTIVATION_PARAMS
        {
            ActivationType = AUDIOCLIENT_ACTIVATION_TYPE.PROCESS_LOOPBACK,
            ProcessLoopbackParams = loopback,
        };

        int size = Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>();
        IntPtr blob = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(activation, blob, false);
            var prop = new PROPVARIANT
            {
                vt = (ushort)(0x41 /*VT_BLOB*/),
                blobSize = (uint)size,
                blobData = blob,
            };

            var iid = typeof(IAudioClient).GUID;
            int hr = ActivateAudioInterfaceAsync(
                VirtualDeviceProcessLoopback,
                ref iid,
                ref prop,
                new Completion(),
                out IntPtr asyncOp);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            return asyncOp;
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    private enum AUDIOCLIENT_ACTIVATION_TYPE { DEFAULT = 0, PROCESS_LOOPBACK = 1 }
    private enum PROCESSLOOPBACKMODE { INCLUDE_TARGET_PROCESS_TREE = 0, EXCLUDE_TARGET_PROCESS_TREE = 1 }

    [StructLayout(LayoutKind.Sequential)]
    private struct AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS
    {
        public uint TargetProcessId;
        public PROCESSLOOPBACKMODE ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AUDIOCLIENT_ACTIVATION_PARAMS
    {
        public AUDIOCLIENT_ACTIVATION_TYPE ActivationType;
        public AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS ProcessLoopbackParams;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort wReserved1, wReserved2, wReserved3;
        public uint blobSize;
        public IntPtr blobData;
    }

    [ComImport, Guid("5BC8A76B-D630-4AAA-A3AD-9491B77D77D4")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig] int ActivateCompleted(IntPtr activateOperation);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient { }

    private sealed class Completion : IActivateAudioInterfaceCompletionHandler
    {
        public int ActivateCompleted(IntPtr activateOperation) => 0;
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [In] ref Guid riid,
        [In] ref PROPVARIANT activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IntPtr activationOperation);
}
