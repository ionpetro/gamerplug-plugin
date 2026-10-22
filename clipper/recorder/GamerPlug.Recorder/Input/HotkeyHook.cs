using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using GamerPlug.Recorder.Native;

namespace GamerPlug.Recorder.Input;

/// <summary>
/// Low-level keyboard hook that matches configured accelerators (e.g. "F9",
/// "Ctrl+Shift+C") and invokes the bound hotkey action. Runs on a dedicated
/// thread with a message pump so it works while the recorder is in the
/// background (no visible window required).
/// </summary>
public sealed class HotkeyHook : IDisposable
{
    private readonly object _gate = new();
    private List<(ParsedAccel accel, string action)> _bindings = new();
    private NativeMethods.LowLevelKeyboardProc? _proc;
    private IntPtr _hook;
    private Thread? _thread;
    private volatile bool _running;
    private Action<string>? _onAction;

    public void SetBindings(IEnumerable<(string accelerator, string action)> bindings)
    {
        var parsed = new List<(ParsedAccel, string)>();
        foreach (var (acc, action) in bindings)
        {
            if (ParsedAccel.TryParse(acc, out var p)) parsed.Add((p, action));
        }
        lock (_gate) { _bindings = parsed; }
    }

    public void Start(Action<string> onAction)
    {
        _onAction = onAction;
        if (_running) return;
        _running = true;
        _thread = new Thread(MessageLoop) { IsBackground = true, Name = "gamerplug-hotkeys" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    public void Dispose() => Stop();

    private void MessageLoop()
    {
        _proc = HookCallback;
        using var cur = Process.GetCurrentProcess();
        using var mod = cur.MainModule;
        IntPtr hMod = NativeMethods.GetModuleHandle(mod?.ModuleName);
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, hMod, 0);
        while (_running)
        {
            Application.DoEvents();
            Thread.Sleep(15);
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)NativeMethods.WM_KEYDOWN || wParam == (IntPtr)NativeMethods.WM_SYSKEYDOWN))
        {
            var info = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            int vk = (int)info.vkCode;
            bool ctrl = (NativeMethods.GetAsyncKeyState(0x11) & 0x8000) != 0;
            bool alt = (NativeMethods.GetAsyncKeyState(0x12) & 0x8000) != 0;
            bool shift = (NativeMethods.GetAsyncKeyState(0x10) & 0x8000) != 0;
            string? action = null;
            lock (_gate)
            {
                foreach (var (accel, act) in _bindings)
                {
                    if (accel.Matches(vk, ctrl, alt, shift)) { action = act; break; }
                }
            }
            if (action != null)
            {
                try { _onAction?.Invoke(action); } catch { /* observer errors must not unhook */ }
            }
        }
        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}

internal readonly struct ParsedAccel
{
    public readonly int Vk;
    public readonly bool Ctrl, Alt, Shift;

    public ParsedAccel(int vk, bool ctrl, bool alt, bool shift)
    {
        Vk = vk; Ctrl = ctrl; Alt = alt; Shift = shift;
    }

    public bool Matches(int vk, bool ctrl, bool alt, bool shift)
        => vk == Vk && ctrl == Ctrl && alt == Alt && shift == Shift;

    public static bool TryParse(string raw, out ParsedAccel accel)
    {
        accel = default;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var parts = raw.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        bool ctrl = false, alt = false, shift = false;
        int vk = 0;
        foreach (var p in parts)
        {
            switch (p.ToLowerInvariant())
            {
                case "ctrl":
                case "control": ctrl = true; break;
                case "alt": alt = true; break;
                case "shift": shift = true; break;
                case "win":
                case "meta": break; // ignored
                default:
                    vk = VkFromToken(p);
                    break;
            }
        }
        if (vk == 0) return false;
        accel = new ParsedAccel(vk, ctrl, alt, shift);
        return true;
    }

    private static int VkFromToken(string token)
    {
        if (token.Length == 1)
        {
            char c = char.ToUpperInvariant(token[0]);
            if (c is >= 'A' and <= 'Z') return c;
            if (c is >= '0' and <= '9') return c;
        }
        if (token.StartsWith("F", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(token.AsSpan(1), out int n) && n is >= 1 and <= 24)
            return 0x70 + (n - 1); // VK_F1
        return token.ToUpperInvariant() switch
        {
            "SPACE" => 0x20,
            "TAB" => 0x09,
            "ESC" or "ESCAPE" => 0x1B,
            "ENTER" or "RETURN" => 0x0D,
            _ => 0,
        };
    }
}
