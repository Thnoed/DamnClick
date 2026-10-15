namespace Clicker.Core;

/// <summary>
/// One-shot physical key state reads. Used only for seeding checks
/// (e.g. "click key already held when arming Combo"), never for edge
/// detection — GetAsyncKeyState reflects our own synthetic clicks,
/// so edges must come from GlobalHook (INJECTED-filtered).
/// </summary>
public static class KeyState
{
    public static bool IsDown(string keyId)
    {
        int vk = KeyCodes.ToVirtualKey(keyId);
        if (vk < 0) return false;
        return (Win32.GetAsyncKeyState(vk) & 0x8000) != 0;
    }
}
