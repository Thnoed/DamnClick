using System.Runtime.InteropServices;

namespace Clicker.Core;

/// <summary>
/// Global low-level keyboard + mouse hooks.
/// Used for: (1) capturing "press any key..." and (2) activation/hold detection.
///
/// Synthetic events produced by our own SendInput are filtered out via the
/// LLKHF_INJECTED / LLMHF_INJECTED flag in the hook struct (dwExtraInfo-based
/// detection is unreliable across threads). This fixes feedback loops where
/// the engine's own clicks were seen as physical releases (broke Combined
/// mode for keyboard click-keys after a single click).
/// </summary>
public sealed class GlobalHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYUP = 0x0105;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_MBUTTONUP = 0x0208;
    private const int WM_XBUTTONDOWN = 0x020B;
    private const int WM_XBUTTONUP = 0x020C;

    private const uint LLKHF_INJECTED = 0x10;
    private const uint LLMHF_INJECTED = 0x01;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public int x;
        public int y;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private IntPtr _kbHook = IntPtr.Zero;
    private IntPtr _mouseHook = IntPtr.Zero;
    private readonly HookProc _kbProc;
    private readonly HookProc _mouseProc;
    private readonly HashSet<string> _down = new();
    private readonly object _lock = new();

    /// <summary>Fired on physical press (key id, e.g. "Key:0x41" or "Mouse:LButton").</summary>
    public event Action<string>? Pressed;
    /// <summary>Fired on physical release.</summary>
    public event Action<string>? Released;

    /// <summary>When set, the next press is swallowed for capture and reported here instead.</summary>
    public event Action<string>? CaptureNext;

    private bool _capturing;

    public GlobalHook()
    {
        _kbProc = KbCallback;
        _mouseProc = MouseCallback;
    }

    public void Start()
    {
        var hMod = GetModuleHandle(null);
        _kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, hMod, 0);
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, hMod, 0);
    }

    public void Stop()
    {
        if (_kbHook != IntPtr.Zero) { UnhookWindowsHookEx(_kbHook); _kbHook = IntPtr.Zero; }
        if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
    }

    public void BeginCapture()
    {
        _capturing = true;
    }

    public void CancelCapture()
    {
        _capturing = false;
    }

    public bool IsDown(string keyId)
    {
        lock (_lock) return _down.Contains(keyId);
    }

    private IntPtr KbCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            // Ignore our own / any synthetic input (SendInput sets INJECTED).
            if ((kb.flags & LLKHF_INJECTED) != 0)
                return CallNextHookEx(_kbHook, nCode, wParam, lParam);

            string id = KeyCodes.ToId(false, (int)kb.vkCode);
            bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;
            if (isDown || isUp)
            {
                if (_capturing && isDown)
                {
                    _capturing = false;
                    if (kb.vkCode == 0x1B) // Esc cancels
                    {
                        CancelCapture();
                        return (IntPtr)1;
                    }
                    CaptureNext?.Invoke(id);
                    return (IntPtr)1; // swallow the capture press
                }
                bool isRepeat = false;
                lock (_lock)
                {
                    if (isDown)
                    {
                        // holding a key sends repeated DOWNs; only the first counts
                        if (!_down.Add(id)) isRepeat = true;
                    }
                    else _down.Remove(id);
                }
                if (isDown)
                {
                    if (!isRepeat) Pressed?.Invoke(id);
                }
                else Released?.Invoke(id);
            }
        }
        return CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var ms = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            // Ignore synthetic input (our own SendInput clicks).
            if ((ms.flags & LLMHF_INJECTED) != 0)
                return CallNextHookEx(_mouseHook, nCode, wParam, lParam);

            int msg = wParam.ToInt32();
            string? id = null;
            bool isDown = false, isUp = false;
            switch (msg)
            {
                case WM_LBUTTONDOWN: id = "Mouse:LButton"; isDown = true; break;
                case WM_LBUTTONUP: id = "Mouse:LButton"; isUp = true; break;
                case WM_RBUTTONDOWN: id = "Mouse:RButton"; isDown = true; break;
                case WM_RBUTTONUP: id = "Mouse:RButton"; isUp = true; break;
                case WM_MBUTTONDOWN: id = "Mouse:MButton"; isDown = true; break;
                case WM_MBUTTONUP: id = "Mouse:MButton"; isUp = true; break;
                case WM_XBUTTONDOWN:
                case WM_XBUTTONUP:
                    uint btn = (ms.mouseData >> 16) & 0xFFFF;
                    id = btn == 1 ? "Mouse:XButton1" : "Mouse:XButton2";
                    isDown = msg == WM_XBUTTONDOWN;
                    isUp = msg == WM_XBUTTONUP;
                    break;
            }
            if (id != null)
            {
                if (_capturing && isDown)
                {
                    _capturing = false;
                    CaptureNext?.Invoke(id);
                    return (IntPtr)1;
                }
                // Don't swallow normal clicks: game must still receive them.
                lock (_lock)
                {
                    if (isDown) _down.Add(id);
                    else _down.Remove(id);
                }
                if (isDown) Pressed?.Invoke(id);
                else Released?.Invoke(id);
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        Stop();
    }
}
