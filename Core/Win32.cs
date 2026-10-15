using System.Runtime.InteropServices;

namespace Clicker.Core;

/// <summary>
/// Win32 SendInput wrapper, same technique family as the original
/// SpeedAutoClicker (SendInput + mouse_event compat + GetAsyncKeyState).
/// </summary>
public static class Win32
{
    public const int INPUT_MOUSE = 0;
    public const int INPUT_KEYBOARD = 1;

    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    public const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    public const uint MOUSEEVENTF_XDOWN = 0x0080;
    public const uint MOUSEEVENTF_XUP = 0x0100;

    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    // Classic click injection, same call the original SpeedAutoClicker uses.
    [DllImport("user32.dll")]
    public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    public static extern uint MapVirtualKey(uint uCode, uint uMapType);

    // 1ms system timer resolution (like games/clickers do). Call once at startup.
    [DllImport("winmm.dll")]
    public static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll")]
    public static extern uint timeEndPeriod(uint uPeriod);

    /// <summary>
    /// Precise cancellable wait. Thread.Sleep/Task.Delay have ~15ms granularity
    /// by default, which destroys high CPS rates; spin for short spans.
    /// Returns false if cancelled.
    /// </summary>
    public static bool PreciseWait(int ms, WaitHandle? cancel)
    {
        if (ms <= 0) return cancel == null || !cancel.WaitOne(0);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            if (cancel != null && cancel.WaitOne(0)) return false;
            long left = ms - sw.ElapsedMilliseconds;
            if (left <= 0) return true;
            if (left > 12)
            {
                if (cancel != null)
                {
                    if (cancel.WaitOne(1)) return false;
                }
                else Thread.Sleep(1);
            }
            else Thread.SpinWait(500);
        }
    }

    public static bool IsPhysicallyDown(string keyId)
    {
        int vk = KeyCodes.ToVirtualKey(keyId);
        if (vk < 0) return false;
        // high bit = currently down
        return (GetAsyncKeyState(vk) & 0x8000) != 0;
    }

    public static void EmitDown(string keyId)
    {
        if (keyId.StartsWith("Mouse:", StringComparison.OrdinalIgnoreCase))
        {
            var (flag, data) = MouseFlag(keyId.Substring(6), true);
            mouse_event(flag, 0, 0, data, UIntPtr.Zero);
            return;
        }
        foreach (var inp in Build(keyId, down: true))
            SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }

    public static void EmitUp(string keyId)
    {
        if (keyId.StartsWith("Mouse:", StringComparison.OrdinalIgnoreCase))
        {
            var (flag, data) = MouseFlag(keyId.Substring(6), false);
            mouse_event(flag, 0, 0, data, UIntPtr.Zero);
            return;
        }
        foreach (var inp in Build(keyId, down: false))
            SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }

    public static void EmitClick(string keyId, int holdMs, WaitHandle? cancel)
    {
        EmitDown(keyId);
        if (holdMs > 0) PreciseWait(holdMs, cancel);
        EmitUp(keyId);
    }

    public static void EmitClick(string keyId, int holdMs) => EmitClick(keyId, holdMs, null);

    private static (uint flag, uint data) MouseFlag(string name, bool down)
    {
        switch (name.ToLowerInvariant())
        {
            case "lbutton": return (down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP, 0);
            case "rbutton": return (down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP, 0);
            case "mbutton": return (down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP, 0);
            case "xbutton1": return (down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, 1);
            case "xbutton2": return (down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, 2);
            default: return (down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP, 0);
        }
    }

    private static INPUT[] Build(string keyId, bool down)
    {
        if (keyId.StartsWith("Mouse:", StringComparison.OrdinalIgnoreCase))
        {
            var name = keyId.Substring(6);
            uint flag = 0;
            uint data = 0;
            switch (name.ToLowerInvariant())
            {
                case "lbutton": flag = down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP; break;
                case "rbutton": flag = down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP; break;
                case "mbutton": flag = down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP; break;
                case "xbutton1": flag = down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP; data = 1; break;
                case "xbutton2": flag = down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP; data = 2; break;
                default: flag = down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP; break;
            }
            return new[]
            {
                new INPUT
                {
                    type = INPUT_MOUSE,
                    u = new INPUTUNION
                    {
                        mi = new MOUSEINPUT { dwFlags = flag, mouseData = data }
                    }
                }
            };
        }
        // keyboard
        int vk = KeyCodes.ToVirtualKey(keyId);
        if (vk < 0) vk = 0x41;
        uint kflags = down ? 0 : KEYEVENTF_KEYUP;
        if (KeyCodes.IsExtended(vk)) kflags |= KEYEVENTF_EXTENDEDKEY;
        ushort scan = (ushort)MapVirtualKey((uint)vk, 0);
        return new[]
        {
            new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new INPUTUNION
                {
                    ki = new KEYBDINPUT { wVk = (ushort)vk, wScan = scan, dwFlags = kflags }
                }
            }
        };
    }
}
