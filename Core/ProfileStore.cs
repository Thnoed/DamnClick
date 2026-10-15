using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using Clicker.Core;

namespace Clicker.Core;

/// <summary>
/// Single portable data file next to the exe. No folders created.
/// File: <exe_dir>/profiles.dat (JSON inside, .dat to look neutral).
/// Uses DataContractJsonSerializer (in-box in .NET Framework, no packages).
/// </summary>
public static class ProfileStore
{
    private static readonly DataContractJsonSerializer Serializer = new(typeof(AppData));
    private static readonly DataContractJsonSerializer ProfileSerializer = new(typeof(Profile));

    public static string ExeDir => AppContext.BaseDirectory;

    /// <summary>Directory actually used for profiles.dat (custom or exe dir).</summary>
    public static string BaseDir { get; private set; } = AppContext.BaseDirectory;

    public static string DataFilePath => Path.Combine(BaseDir, "profiles.dat");

    /// <summary>Resolve storage dir from settings. Creates nothing.</summary>
    public static void ResolveBaseDir(string? customPath)
    {
        BaseDir = IsValidDir(customPath) ? customPath! : ExeDir;
    }

    public static bool IsValidDir(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { Path.GetFullPath(path); return true; }
        catch { return false; }
    }

    private static readonly Encoding NoBomUtf8 = new UTF8Encoding(false);

    public static AppData Load()
    {
        try
        {
            // 1) Always read exe-dir file first: it holds DataPath setting.
            var exePath = Path.Combine(ExeDir, "profiles.dat");
            AppData? data = null;
            if (File.Exists(exePath))
                data = ReadFile(exePath);
            // 2) If a custom path is set and has its own file, it wins.
            var custom = data?.Settings?.DataPath;
            if (IsValidDir(custom))
            {
                var customFile = Path.Combine(custom!, "profiles.dat");
                if (!string.Equals(customFile, exePath, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(customFile))
                {
                    var cdata = ReadFile(customFile);
                    if (cdata != null) data = cdata;
                }
                BaseDir = custom!;
            }
            else BaseDir = ExeDir;
            if (data != null)
            {
                Normalize(data);
                return data;
            }
        }
        catch (Exception ex)
        {
            // corrupted file -> fall through to defaults (keep a .bak)
            System.Diagnostics.Debug.WriteLine($"ProfileStore.Load error: {ex.Message}");
            try
            {
                var path = DataFilePath;
                if (File.Exists(path))
                    File.Copy(path, path + ".bak", overwrite: true);
            }
            catch { }
        }
        var fresh = new AppData();
        Normalize(fresh);
        return fresh;
    }

    private static AppData? ReadFile(string path)
    {
        var raw = File.ReadAllBytes(path);
        int start = (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF) ? 3 : 0;
        using var ms = new MemoryStream(raw, start, raw.Length - start);
        return Serializer.ReadObject(ms) as AppData;
    }

    /// <summary>Export single profile to a file (copy, source stays).</summary>
    public static void ExportProfile(string path, Profile profile)
    {
        using var ms = new MemoryStream();
        ProfileSerializer.WriteObject(ms, profile);
        File.WriteAllText(path, NoBomUtf8.GetString(ms.ToArray()), NoBomUtf8);
    }

    /// <summary>Read single profile from a file. Returns null when invalid.</summary>
    public static Profile? ImportProfile(string path)
    {
        try
        {
            var raw = File.ReadAllBytes(path);
            int start = (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF) ? 3 : 0;
            using var ms = new MemoryStream(raw, start, raw.Length - start);
            var p = ProfileSerializer.ReadObject(ms) as Profile;
            if (p == null || string.IsNullOrWhiteSpace(p.Name)) return null;
            p.Blocks ??= new();
            return p;
        }
        catch { return null; }
    }

    public static void Save(AppData data)
    {
        ResolveBaseDir(data.Settings?.DataPath);
        WriteFile(DataFilePath, data);
        // If storage lives elsewhere, keep a pointer in the exe dir so the
        // next launch knows where to look (settings + DataPath, no profiles).
        var exePath = Path.Combine(ExeDir, "profiles.dat");
        if (!string.Equals(DataFilePath, exePath, StringComparison.OrdinalIgnoreCase))
        {
            WriteFile(exePath, new AppData
            {
                Settings = data.Settings ?? new AppSettings(),
                Profiles = new List<Profile>(),
                SelectedProfileId = null,
            });
        }
    }

    private static void WriteFile(string path, AppData data)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var ms = new MemoryStream();
        Serializer.WriteObject(ms, data);
        // NOTE: Encoding.UTF8 emits a BOM which DataContractJsonSerializer
        // cannot read back — always write without preamble.
        var json = NoBomUtf8.GetString(ms.ToArray());
        // atomic-ish: write tmp then replace, still same folder, no extra dirs
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json, NoBomUtf8);
        File.Copy(tmp, path, overwrite: true);
        File.Delete(tmp);
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            throw new IOException("Failed to write " + path);
    }

    private static void Normalize(AppData data)
    {
        data.Profiles ??= new();
        data.Settings ??= new();
        if (string.IsNullOrWhiteSpace(data.Settings.Lang) ||
            (data.Settings.Lang != "en" && data.Settings.Lang != "ru"))
            data.Settings.Lang = "en";
        if (string.IsNullOrWhiteSpace(data.Settings.Accent))
            data.Settings.Accent = "#7C5CFF";
        data.Settings.SoundVolume = Num.Clamp(data.Settings.SoundVolume, 0, 100);
        if (data.Profiles.Count == 0)
        {
            data.Profiles.Add(new Profile { Id = "default", Name = "Default" });
        }
        foreach (var p in data.Profiles)
        {
            p.Blocks ??= new();
            if (string.IsNullOrWhiteSpace(p.Id)) p.Id = Guid.NewGuid().ToString("N");
            if (string.IsNullOrWhiteSpace(p.Name)) p.Name = "Default";
            int bi = 0;
            foreach (var b in p.Blocks)
            {
                bi++;
                if (string.IsNullOrWhiteSpace(b.Id)) b.Id = Guid.NewGuid().ToString("N");
                if (string.IsNullOrWhiteSpace(b.Name)) b.Name = "Block " + bi;
                if (string.IsNullOrWhiteSpace(b.ActivationKey)) b.ActivationKey = "Mouse:XButton1";
                if (string.IsNullOrWhiteSpace(b.ClickKey)) b.ClickKey = "Mouse:LButton";
                if (b.Cps < 0.5) b.Cps = 0.5;
                if (b.Cps > 1000) b.Cps = 1000;
                b.RandomPercent = Num.Clamp(b.RandomPercent, 0, 100);
                b.DutyCycle = Num.Clamp(b.DutyCycle, 5, 95);
                if (b.ClickLimit < 0) b.ClickLimit = 0;
                b.DelayMs = Num.Clamp(b.DelayMs, 0, 1000);
            }
        }
        if (string.IsNullOrWhiteSpace(data.SelectedProfileId) ||
            data.Profiles.All(p => p.Id != data.SelectedProfileId))
            data.SelectedProfileId = data.Profiles[0].Id;
    }
}
