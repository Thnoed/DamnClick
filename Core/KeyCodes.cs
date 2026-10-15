using System.Windows.Forms;

namespace Clicker.Core;

/// <summary>
/// Key ids: "Mouse:LButton|RButton|MButton|XButton1|XButton2"
/// or "Key:0xNN" (virtual-key code in hex). Accepts anything from mouse or keyboard.
/// </summary>
public static class KeyCodes
{
    public static readonly string[] MouseIds =
    {
        "Mouse:LButton", "Mouse:RButton", "Mouse:MButton", "Mouse:XButton1", "Mouse:XButton2"
    };

    public static string ToId(bool isMouse, int vkOrMouseButton)
    {
        if (isMouse)
        {
            return vkOrMouseButton switch
            {
                0 => "Mouse:LButton",
                1 => "Mouse:RButton",
                2 => "Mouse:MButton",
                3 => "Mouse:XButton1",
                4 => "Mouse:XButton2",
                _ => "Mouse:LButton",
            };
        }
        return $"Key:0x{vkOrMouseButton:X2}";
    }

    /// <returns>VK code, or mouse VK (1,2,4,5,6), or -1 if unknown.</returns>
    public static int ToVirtualKey(string keyId)
    {
        if (string.IsNullOrWhiteSpace(keyId)) return -1;
        if (keyId.StartsWith("Mouse:", StringComparison.OrdinalIgnoreCase))
        {
            return keyId.Substring(6).ToLowerInvariant() switch
            {
                "lbutton" => 0x01,
                "rbutton" => 0x02,
                "mbutton" => 0x04,
                "xbutton1" => 0x05,
                "xbutton2" => 0x06,
                _ => -1,
            };
        }
        if (keyId.StartsWith("Key:", StringComparison.OrdinalIgnoreCase))
        {
            var hex = keyId.Substring(4).Trim();
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                hex = hex.Substring(2);
            if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int vk))
                return vk;
        }
        return -1;
    }

    public static bool IsExtended(int vk) => vk switch
    {
        0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28
            or 0x2D or 0x2E or 0x5B or 0x5C or 0x5D or 0x6F or 0x90 => true,
        _ => false,
    };

    public static string DisplayName(string keyId)
    {
        if (string.IsNullOrWhiteSpace(keyId)) return "—";
        if (keyId.StartsWith("Mouse:", StringComparison.OrdinalIgnoreCase))
        {
            return keyId.Substring(6) switch
            {
                var s when s.Equals("LButton", StringComparison.OrdinalIgnoreCase) => "Mouse Left",
                var s when s.Equals("RButton", StringComparison.OrdinalIgnoreCase) => "Mouse Right",
                var s when s.Equals("MButton", StringComparison.OrdinalIgnoreCase) => "Mouse Middle",
                var s when s.Equals("XButton1", StringComparison.OrdinalIgnoreCase) => "Mouse X1",
                var s when s.Equals("XButton2", StringComparison.OrdinalIgnoreCase) => "Mouse X2",
                var s => s,
            };
        }
        int vk = ToVirtualKey(keyId);
        if (vk < 0) return keyId;
        try
        {
            var k = (Keys)vk;
            return k.ToString();
        }
        catch { return $"VK 0x{vk:X2}"; }
    }

    public static string DisplayNameRu(string keyId)
    {
        // same names, fine for both languages (key names stay latin)
        return DisplayName(keyId);
    }
}
