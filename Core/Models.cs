using System.Runtime.Serialization;

namespace Clicker.Core;

public enum ClickMode
{
    Hold = 0,      // задержать: кликает пока зажата клавиша активации
    Toggle = 1,    // переключить: вкл/выкл по нажатию активации
    Combined = 2   // комбинированный: активация = armed вкл/выкл, кликает пока зажата клавиша клика
}

[DataContract]
public class ClickBlock
{
    [DataMember] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [DataMember] public string Name { get; set; } = "Block";
    [DataMember] public bool Enabled { get; set; } = true;
    [DataMember] public string ActivationKey { get; set; } = "Mouse:XButton1";
    [DataMember] public string ClickKey { get; set; } = "Mouse:LButton";
    [DataMember] public ClickMode Mode { get; set; } = ClickMode.Hold;
    [DataMember] public double Cps { get; set; } = 12.0;
    [DataMember] public bool Unlimited { get; set; } = true;
    [DataMember] public double RandomPercent { get; set; } = 0.0;   // 0..100 jitter
    [DataMember] public double DutyCycle { get; set; } = 10.0;      // % of each interval held down
    [DataMember] public int ClickLimit { get; set; } = 0;           // 0 = нет лимита
    [DataMember] public int DelayMs { get; set; } = 0;              // ms to wait after press before clicking
}

[DataContract]
public class Profile
{
    [DataMember] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [DataMember] public string Name { get; set; } = "Default";
    [DataMember] public List<ClickBlock> Blocks { get; set; } = new();
}

[DataContract]
public class AppSettings
{
    [DataMember] public string Lang { get; set; } = "en";           // en | ru, default en
    [DataMember] public string Accent { get; set; } = "#7C5CFF";
    [DataMember] public bool MinimizeToTrayOnClose { get; set; } = false;  // off by default
    [DataMember] public string DataPath { get; set; } = "";         // "" = next to exe

    // NOTE: DataContract deserialization skips constructors/property initializers,
    // so new value-type members would read as 0/false from old files.
    // Nullable backings preserve real defaults (50 / true) for missing members.
    [DataMember(Name = "ShowStatusBar")] private bool? _showStatusBarSer;
    [IgnoreDataMember]
    public bool ShowStatusBar { get => _showStatusBarSer ?? true; set => _showStatusBarSer = value; }

    [DataMember(Name = "SoundVolume")] private int? _soundVolumeSer;
    [IgnoreDataMember]
    public int SoundVolume { get => _soundVolumeSer ?? 50; set => _soundVolumeSer = value; }
}

[DataContract]
public class AppData
{
    [DataMember] public List<Profile> Profiles { get; set; } = new();
    [DataMember] public AppSettings Settings { get; set; } = new();
    [DataMember] public string? SelectedProfileId { get; set; }
}

/// <summary>Math.Clamp нет в .NET Framework — свой хелпер.</summary>
public static class Num
{
    public static double Clamp(double v, double min, double max) =>
        v < min ? min : v > max ? max : v;
    public static int Clamp(int v, int min, int max) =>
        v < min ? min : v > max ? max : v;
}
