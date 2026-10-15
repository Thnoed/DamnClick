using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Clicker.Core;
using Forms = System.Windows.Forms;
using WButton = System.Windows.Controls.Button;
using WLabel = System.Windows.Controls.Label;
using WTextBox = System.Windows.Controls.TextBox;
using WBrush = System.Windows.Media.Brush;
using WColor = System.Windows.Media.Color;
using WColorConverter = System.Windows.Media.ColorConverter;

namespace Clicker;

public partial class MainWindow : Window
{
    private AppData _data = new();
    private readonly GlobalHook _hook = new();
    private readonly Dictionary<string, BlockRunner> _runners = new();
    private readonly List<BlockCard> _cards = new();
    private Forms.NotifyIcon? _tray;
    private static System.Drawing.Icon? _appIcon;
    private TaskCompletionSource<string?>? _captureTcs;
    private bool _loadingList;
    private bool _loadingPath;
    private WButton? _armedDeleteBtn;
    private readonly List<WButton> _accentButtons = new();
    private SoundBank? _sounds;
    private bool _loadingVolume;

    private static readonly string[] AccentChoices =
        { "#7C5CFF", "#2F81F7", "#46A758", "#E5484D", "#F5A524", "#12A594", "#FF6B9D" };

    public MainWindow()
    {
        InitializeComponent();
        _data = ProfileStore.Load();
        I18n.Lang = _data.Settings.Lang;

        // 1ms timer resolution for precise CPS waits (like the original).
        try { Win32.timeBeginPeriod(1); } catch { }
        _hook.CaptureNext += OnCaptureNext;
        _hook.Start();
        // Click elsewhere disarms a pending delete button.
        PreviewMouseLeftButtonDown += (_, e) => DisarmDeleteUnless(e.OriginalSource as DependencyObject);

        // Window icon from the exe itself (WPF can't decode PNG-compressed
        // .ico entries from XAML, so load via WinForms handle instead).
        _appIcon = LoadAppIcon();
        try
        {
            Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                _appIcon.Handle, Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
        }
        catch { }

        BuildAccentPicker();
        ApplyAccent(_data.Settings.Accent, save: false);
        InitSound();
        BuildTray();
        SetActiveRunners(CurrentProfile);
        RefreshProfileList();
        ShowProfiles();
        ApplyLang();
        UpdateStatus();

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        timer.Tick += (_, _) =>
        {
            foreach (var c in _cards) c.UpdateState();
            UpdateStatus();
        };
        timer.Start();

        // Plain minimize = plain minimize. Tray only on close and only if enabled.
        Closing += (_, e) =>
        {
            if (_data.Settings.MinimizeToTrayOnClose)
            {
                e.Cancel = true;
                HideToTray();
            }
        };
    }

    // ---------- views ----------

    private void ShowProfiles()
    {
        ProfilesView.Visibility = Visibility.Visible;
        BlocksView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;
    }

    private void ShowBlocks()
    {
        ProfilesView.Visibility = Visibility.Collapsed;
        BlocksView.Visibility = Visibility.Visible;
        SettingsView.Visibility = Visibility.Collapsed;
    }

    private void ShowSettings()
    {
        ProfilesView.Visibility = Visibility.Collapsed;
        BlocksView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Visible;
    }

    // ---------- profiles ----------

    private Profile? CurrentProfile =>
        _data.Profiles.FirstOrDefault(p => p.Id == _data.SelectedProfileId)
        ?? _data.Profiles.FirstOrDefault();

    private string? _viewedProfileId;

    /// <summary>
    /// Make profile active: stop old runners, start headless runners for it.
    /// The viewed blocks (if any) are rebuilt by RefreshBlocks on open.
    /// </summary>
    private void SetActiveRunners(Profile? p)
    {
        foreach (var r in _runners.Values) r.Dispose();
        _runners.Clear();
        _cards.Clear();
        BlocksPanel.Children.Clear();
        if (p == null) return;
        _data.SelectedProfileId = p.Id;
        foreach (var b in p.Blocks)
        {
            var r = new BlockRunner(b, _hook);
            AttachRunner(r);
            _runners[b.Id] = r;
        }
    }

    private void SetActiveProfile(Profile p)
    {
        bool same = _data.SelectedProfileId == p.Id && _runners.Count > 0;
        _data.SelectedProfileId = p.Id;
        SaveData();
        if (!same) SetActiveRunners(p);
        UpdateStatus();
    }

    private void RefreshProfileList()
    {
        ProfileList.Items.Clear();
        foreach (var p in _data.Profiles)
            ProfileList.Items.Add(p);
        var cur = CurrentProfile;
        if (cur != null)
        {
            int idx = _data.Profiles.IndexOf(cur);
            _loadingList = true;
            try
            {
                ProfileList.SelectionChanged -= ProfileList_SelectionChanged;
                ProfileList.SelectedIndex = idx;
            }
            finally
            {
                ProfileList.SelectionChanged += ProfileList_SelectionChanged;
                _loadingList = false;
            }
        }
    }

    private void RefreshBlocks()
    {
        foreach (var r in _runners.Values) r.Dispose();
        _runners.Clear();
        _cards.Clear();
        BlocksPanel.Children.Clear();

        var cur = CurrentProfile;
        if (cur == null) return;
        _viewedProfileId = cur.Id;
        CurrentProfileTitle.Text = cur.Name;

        foreach (var b in cur.Blocks)
        {
            var runner = new BlockRunner(b, _hook);
            AttachRunner(runner);
            _runners[b.Id] = runner;
            var card = new BlockCard(b, runner, SaveData, RemoveBlock, CaptureKeyAsync, ShowNotice);
            _cards.Add(card);
            BlocksPanel.Children.Add(card);
        }
    }

    private void SaveData()
    {
        try 
        { 
            ProfileStore.Save(_data); 
        } 
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SaveData error: {ex.Message}");
        }
    }

    private void RemoveBlock(BlockCard card)
    {
        var cur = CurrentProfile;
        if (cur == null) return;
        if (_runners.TryGetValue(card.Block.Id, out var r)) r.Dispose();
        _runners.Remove(card.Block.Id);
        cur.Blocks.Remove(card.Block);
        _cards.Remove(card);
        BlocksPanel.Children.Remove(card);
        SaveData();
    }

    private void AddProfileBtn_Click(object sender, RoutedEventArgs e)
    {
        var name = Prompt(I18n.T("profile_name"), I18n.T("new_profile"));
        if (string.IsNullOrWhiteSpace(name)) return;
        var p = new Profile { Name = name.Trim() };
        _data.Profiles.Add(p);
        SetActiveProfile(p);
        RefreshProfileList();
        ApplyLang();
    }

    private void RenameProfileBtn_Click(object sender, RoutedEventArgs e)
    {
        var target = (ProfileList.SelectedItem as Profile) ?? CurrentProfile;
        if (target == null) return;
        var name = Prompt(I18n.T("profile_name"), target.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        target.Name = name.Trim();
        SaveData();
        RefreshProfileList();
        RefreshBlocks();
        ApplyLang();
    }

    private void DeleteProfile(Profile? target)
    {
        if (target == null || _data.Profiles.Count <= 1) return;
        bool viewedGone = _viewedProfileId == target.Id;
        _data.Profiles.Remove(target);
        if (_data.SelectedProfileId == target.Id)
            _data.SelectedProfileId = _data.Profiles[0].Id;
        SaveData();
        SetActiveRunners(CurrentProfile);
        RefreshProfileList();
        if (viewedGone) ShowProfiles();
        else RefreshBlocks();
        ApplyLang();
    }

    private void ArmDelete(WButton btn)
    {
        DisarmDelete();
        _armedDeleteBtn = btn;
        btn.Tag = "armed";
        var app = System.Windows.Application.Current;
        btn.Foreground = app?.TryFindResource("DangerBrush") as WBrush
            ?? new SolidColorBrush(WColor.FromRgb(0xE5, 0x48, 0x4D));
        btn.Background = new SolidColorBrush(WColor.FromRgb(0x3D, 0x22, 0x26));
        btn.BorderBrush = btn.Foreground;
    }

    private void DisarmDelete()
    {
        if (_armedDeleteBtn == null) return;
        _armedDeleteBtn.Tag = null;
        _armedDeleteBtn.ClearValue(System.Windows.Controls.Button.ForegroundProperty);
        _armedDeleteBtn.ClearValue(System.Windows.Controls.Button.BackgroundProperty);
        _armedDeleteBtn.ClearValue(System.Windows.Controls.Button.BorderBrushProperty);
        _armedDeleteBtn = null;
    }

    private static WButton? FindRowButton(DependencyObject? src)
    {
        while (src != null)
        {
            if (src is WButton b && (b.Name == "DeleteRowBtn" || b.Name == "EnterProfileBtn"))
                return b;
            src = System.Windows.Media.VisualTreeHelper.GetParent(src);
        }
        return null;
    }

    private void DisarmDeleteUnless(DependencyObject? src)
    {
        if (_armedDeleteBtn == null) return;
        if (FindRowButton(src) == _armedDeleteBtn) return; // second click on it
        DisarmDelete();
    }

    private void DeleteRowBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WButton btn || btn.DataContext is not Profile profile) return;
        if ((btn.Tag as string) == "armed")
        {
            DisarmDelete();
            DeleteProfile(profile);
        }
        else ArmDelete(btn);
    }

    private void RowBtn_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not WButton btn) return;
        btn.ToolTip = btn.Name switch
        {
            "EnterProfileBtn" => I18n.T("enter_tip"),
            "ExportRowBtn" => I18n.T("export_tip"),
            _ => I18n.T("profile_delete_tip"),
        };
    }

    private void OpenProfile(Profile profile)
    {
        DisarmDelete();
        SetActiveProfile(profile);
        RefreshBlocks();
        ShowBlocks();
    }

    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Click = select profile for use (highlight + live runners), → = open it.
        if (_loadingList) return;
        DisarmDelete();
        if (ProfileList.SelectedItem is Profile profile)
            SetActiveProfile(profile);
    }

    private void EnterProfileBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WButton btn && btn.DataContext is Profile profile)
            OpenProfile(profile);
    }

    private void BackBtn_Click(object sender, RoutedEventArgs e) => ShowProfiles();

    private void SettingsBtn_Click(object sender, RoutedEventArgs e) => ShowSettings();

    private void SettingsBackBtn_Click(object sender, RoutedEventArgs e) => ShowProfiles();

    private void AddBlockBtn_Click(object sender, RoutedEventArgs e)
    {
        var cur = CurrentProfile;
        if (cur == null) return;
        var b = new ClickBlock { Name = "Block " + (cur.Blocks.Count + 1) };
        cur.Blocks.Add(b);
        var runner = new BlockRunner(b, _hook);
        AttachRunner(runner);
        _runners[b.Id] = runner;
        var card = new BlockCard(b, runner, SaveData, RemoveBlock, CaptureKeyAsync, ShowNotice);
        _cards.Add(card);
        BlocksPanel.Children.Add(card);
        SaveData();
    }

    // ---------- toggle sounds (on.mp3 / off.mp3 next to the exe) ----------

    private void AttachRunner(BlockRunner runner)
    {
        runner.Toggled += PlayToggleSound;
    }

    private void InitSound()
    {
        try
        {
            _sounds = new SoundBank();
            ApplySoundVolume();
        }
        catch { _sounds = null; }
    }

    private void ApplySoundVolume()
    {
        try { _sounds?.SetVolume(_data.Settings.SoundVolume); } catch { }
    }

    private void PlayToggleSound(bool on)
    {
        // Sounds intentionally swapped: enabling a block plays off.mp3,
        // disabling plays on.mp3.
        try { _sounds?.Play(!on); } catch { }
    }

    private void SoundVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingVolume) return;
        _data.Settings.SoundVolume = Num.Clamp((int)Math.Round(e.NewValue), 0, 100);
        SoundVolumeBox.Text = _data.Settings.SoundVolume.ToString();
        ApplySoundVolume();
        SaveData();
    }

    private void SoundVolumeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingVolume) return;
        if (int.TryParse(SoundVolumeBox.Text, out var v))
        {
            v = Num.Clamp(v, 0, 100);
            _data.Settings.SoundVolume = v;
            if (Math.Abs(SoundVolumeSlider.Value - v) > 0.01)
                SoundVolumeSlider.Value = v;
            ApplySoundVolume();
            SaveData();
        }
    }

    // ---------- key capture (hook stays installed, capture window only) ----------

    private async Task<string?> CaptureKeyAsync(Action<string> preview)
    {
        _captureTcs = new TaskCompletionSource<string?>();
        _hook.BeginCapture();
        CaptureHint.Text = I18n.T("press_any");
        CaptureHint.Visibility = Visibility.Visible;
        preview(I18n.T("press_any"));
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var _ = cts.Token.Register(() => _captureTcs.TrySetResult(null));
            return await _captureTcs.Task;
        }
        finally
        {
            CaptureHint.Visibility = Visibility.Collapsed;
            _captureTcs = null;
        }
    }

    private void OnCaptureNext(string id)
    {
        // Hook callbacks run on the installing (UI) thread.
        _captureTcs?.TrySetResult(id);
    }

    // ---------- settings ----------

    private void LangBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LangBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            if (I18n.Lang == tag) return;
            I18n.Lang = tag;
            _data.Settings.Lang = tag;
            SaveData();
            RefreshBlocks();
            ApplyLang();
        }
    }

    private void ApplyLang()
    {
        Title = I18n.T("app_title");
        ProfilesTitle.Text = I18n.T("profiles");
        AddProfileBtn.Content = I18n.T("new_profile");
        RenameProfileBtn.Content = I18n.T("rename");
        AddBlockBtn.Content = I18n.T("add");
        SettingsTitle.Text = I18n.T("settings_title");
        LangLabel.Content = I18n.T("language");
        AccentLabel.Content = I18n.T("accent");
        CustomColorBtn.ToolTip = I18n.T("custom");
        TrayOnCloseBox.Content = I18n.T("tray_on_close");
        SettingsBtn.ToolTip = I18n.T("settings_title");
        InfoBtn.ToolTip = I18n.T("info_tip");
        ImportBtn.ToolTip = I18n.T("import_tip");
        SoundVolumeLabel.Content = I18n.T("sound_volume");
        _loadingVolume = true;
        try
        {
            SoundVolumeSlider.Value = Num.Clamp(_data.Settings.SoundVolume, 0, 100);
            SoundVolumeBox.Text = _data.Settings.SoundVolume.ToString();
        }
        finally { _loadingVolume = false; }
        LangBox.SelectionChanged -= LangBox_SelectionChanged;
        foreach (ComboBoxItem it in LangBox.Items)
            if ((string)it.Tag == I18n.Lang) LangBox.SelectedItem = it;
        LangBox.SelectionChanged += LangBox_SelectionChanged;
        TrayOnCloseBox.Checked -= TrayOnCloseBox_Changed;
        TrayOnCloseBox.Unchecked -= TrayOnCloseBox_Changed;
        TrayOnCloseBox.IsChecked = _data.Settings.MinimizeToTrayOnClose;
        TrayOnCloseBox.Checked += TrayOnCloseBox_Changed;
        TrayOnCloseBox.Unchecked += TrayOnCloseBox_Changed;
        ShowStatusBox.Checked -= ShowStatusBox_Changed;
        ShowStatusBox.Unchecked -= ShowStatusBox_Changed;
        ShowStatusBox.Content = I18n.T("show_status");
        ShowStatusBox.IsChecked = _data.Settings.ShowStatusBar;
        ShowStatusBox.Checked += ShowStatusBox_Changed;
        ShowStatusBox.Unchecked += ShowStatusBox_Changed;
        ApplyStatusBarVisibility();
        DataPathLabel.Content = I18n.T("data_path");
        DataPathBox.ToolTip = I18n.T("data_path_hint");
        BrowseBtn.ToolTip = I18n.T("browse");
        _loadingPath = true;
        try { DataPathBox.Text = _data.Settings.DataPath ?? ""; }
        finally { _loadingPath = false; }
        foreach (var c in _cards) c.ApplyLang();
        if (_tray != null) _tray.Text = I18n.T("tray_hint");
        RefreshProfileList(); // rebuild rows so row-button tooltips follow language
        UpdateStatus();
    }

    private void TrayOnCloseBox_Changed(object sender, RoutedEventArgs e)
    {
        _data.Settings.MinimizeToTrayOnClose = TrayOnCloseBox.IsChecked == true;
        SaveData();
    }

    private void ShowStatusBox_Changed(object sender, RoutedEventArgs e)
    {
        _data.Settings.ShowStatusBar = ShowStatusBox.IsChecked == true;
        SaveData();
        ApplyStatusBarVisibility();
    }

    private void DataPathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingPath) return;
        var t = (DataPathBox.Text ?? "").Trim();
        if (t.Length == 0 || ProfileStore.IsValidDir(t))
        {
            _data.Settings.DataPath = t;
            SaveData(); // persists into the (possibly new) location
        }
    }

    private void BrowseBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Forms.FolderBrowserDialog
        {
            Description = I18n.T("data_path"),
            SelectedPath = ProfileStore.BaseDir,
        };
        if (dlg.ShowDialog() == Forms.DialogResult.OK)
            DataPathBox.Text = dlg.SelectedPath;
    }

    private void BuildAccentPicker()
    {
        AccentPresets.Children.Clear();
        _accentButtons.Clear();
        foreach (var hex in AccentChoices)
        {
            var b = new WButton
            {
                Width = 26,
                Height = 26,
                Margin = new Thickness(0, 0, 6, 0),
                Background = new SolidColorBrush((WColor)WColorConverter.ConvertFromString(hex)!),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                ToolTip = hex,
                Tag = hex,
                Style = (Style)FindResource("AccentPreset"),
            };
            b.Click += (_, _) => ApplyAccent((string)((WButton)b).Tag, save: true);
            AccentPresets.Children.Add(b);
            _accentButtons.Add(b);
        }
    }

    private void RefreshAccentMarks(string currentHex)
    {
        foreach (var b in _accentButtons)
        {
            bool sel = string.Equals((string)b.Tag, currentHex, StringComparison.OrdinalIgnoreCase);
            b.BorderThickness = sel ? new Thickness(2) : new Thickness(0);
            b.BorderBrush = sel ? new SolidColorBrush(Colors.White) : b.BorderBrush;
        }
        bool custom = true;
        foreach (var hex in AccentChoices)
            if (string.Equals(hex, currentHex, StringComparison.OrdinalIgnoreCase)) { custom = false; break; }
        CustomColorBtn.BorderThickness = custom ? new Thickness(2) : new Thickness(1);
        CustomColorBtn.BorderBrush = custom ? new SolidColorBrush(Colors.White) : CustomColorBtn.BorderBrush;
    }

    private void ApplyAccent(string hex, bool save)
    {
        try
        {
            var color = (WColor)WColorConverter.ConvertFromString(hex)!;
            var brush = new SolidColorBrush(color);
            // Dark tint of the accent for selection highlights, follows accent.
            var dim = new SolidColorBrush(WColor.FromRgb(
                (byte)(color.R * 0.35), (byte)(color.G * 0.35), (byte)(color.B * 0.35)));
            dim.Freeze();
            brush.Freeze();
            Resources["AccentBrush"] = brush;
            Resources["AccentDimBrush"] = dim;
            if (System.Windows.Application.Current != null)
            {
                System.Windows.Application.Current.Resources["AccentBrush"] = brush;
                System.Windows.Application.Current.Resources["AccentDimBrush"] = dim;
            }
            RefreshAccentMarks(hex);
            if (save)
            {
                _data.Settings.Accent = hex;
                SaveData();
            }
        }
        catch { }
    }

    private void CustomColorBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Forms.ColorDialog { FullOpen = true };
        if (dlg.ShowDialog() == Forms.DialogResult.OK)
        {
            var c = dlg.Color;
            ApplyAccent($"#{c.R:X2}{c.G:X2}{c.B:X2}", save: true);
        }
    }

    // ---------- info window ----------

    private void InfoBtn_Click(object sender, RoutedEventArgs e) => ShowInfoWindow();

    private void ShowInfoWindow()
    {
        var app = System.Windows.Application.Current;
        WBrush bg = app?.TryFindResource("BgBrush") as WBrush
            ?? new SolidColorBrush(WColor.FromRgb(0x1F, 0x22, 0x26));
        WBrush card = app?.TryFindResource("CardBrush") as WBrush
            ?? new SolidColorBrush(WColor.FromRgb(0x2A, 0x2E, 0x34));
        var win = new Window
        {
            Title = I18n.T("info_title"),
            Width = 380,
            Height = 540,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = bg,
        };
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var panel = new StackPanel { Margin = new Thickness(16) };
        void Section(string hKey, string bKey)
        {
            panel.Children.Add(new TextBlock
            {
                Text = I18n.T(hKey), FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 4)
            });
            panel.Children.Add(new Border
            {
                Background = card, CornerRadius = new CornerRadius(8), Padding = new Thickness(10),
                Child = new TextBlock { Text = I18n.T(bKey), TextWrapping = TextWrapping.Wrap, FontSize = 12 }
            });
        }
        Section("info_profiles_h", "info_profiles");
        Section("info_blocks_h", "info_blocks");
        Section("info_modes_h", "info_modes");
        Section("info_data_h", "info_data");
        scroll.Content = panel;
        win.Content = scroll;
        win.ShowDialog();
    }

    // ---------- profile export / import (copy, source stays) ----------

    private void ExportRowBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WButton btn || btn.DataContext is not Profile profile) return;
        var dlg = new Forms.SaveFileDialog
        {
            Filter = I18n.T("profile_file_filter"),
            FileName = profile.Name + ".dat",
            InitialDirectory = ProfileStore.BaseDir,
        };
        if (dlg.ShowDialog() == Forms.DialogResult.OK)
        {
            try { ProfileStore.ExportProfile(dlg.FileName, profile); }
            catch { System.Windows.MessageBox.Show(dlg.FileName, I18n.T("import_error")); }
        }
    }

    private void ImportBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Forms.OpenFileDialog
        {
            Filter = I18n.T("profile_file_filter"),
            InitialDirectory = ProfileStore.BaseDir,
        };
        if (dlg.ShowDialog() != Forms.DialogResult.OK) return;
        var p = ProfileStore.ImportProfile(dlg.FileName);
        if (p == null)
        {
            System.Windows.MessageBox.Show(dlg.FileName, I18n.T("import_error"));
            return;
        }
        p.Id = Guid.NewGuid().ToString("N");
        string baseName = p.Name.Trim();
        string name = baseName;
        int n = 2;
        while (_data.Profiles.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            name = $"{baseName} ({n++})";
        p.Name = name;
        _data.Profiles.Add(p);
        SetActiveProfile(p);
        RefreshProfileList();
        ApplyLang();
    }

    // ---------- tray (only when enabled in settings) ----------

    private void BuildTray()
    {
        _tray = new Forms.NotifyIcon
        {
            Icon = _appIcon ?? LoadAppIcon(),
            Visible = false,
            Text = I18n.T("tray_hint"),
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(I18n.T("tray_show"), null, (_, _) => ShowFromTray());
        menu.Items.Add(I18n.T("tray_exit"), null, (_, _) =>
        {
            _data.Settings.MinimizeToTrayOnClose = false; // let it really close
            if (_tray != null) _tray.Visible = false;
            Close();
        });
        _tray.ContextMenuStrip = menu;
    }

    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (exe != null)
            {
                var icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (icon != null) return icon;
            }
        }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
        if (_tray != null) _tray.Visible = true;
    }

    private void ShowFromTray()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();
        if (_tray != null && !_data.Settings.MinimizeToTrayOnClose)
            _tray.Visible = false;
    }

    private void UpdateStatus()
    {
        var cur = CurrentProfile;
        if (cur == null) { StatusText.Text = ""; return; }
        var running = _runners.Values
            .Where(r => r.IsRunning)
            .Select(r => KeyCodes.DisplayName(r.Block.ClickKey))
            .Distinct()
            .ToList();
        StatusText.Text = running.Count > 0
            ? $"{cur.Name} ({running.Count}): {string.Join(", ", running)}"
            : $"{cur.Name} (0)";
    }

    private void ApplyStatusBarVisibility()
    {
        StatusBar.Visibility = _data.Settings.ShowStatusBar ? Visibility.Visible : Visibility.Collapsed;
    }

    private System.Windows.Threading.DispatcherTimer? _noticeTimer;

    private void ShowNotice(string text)
    {
        if (_captureTcs != null) return; // don't fight the capture hint
        CaptureHint.Text = text;
        CaptureHint.Visibility = Visibility.Visible;
        _noticeTimer?.Stop();
        _noticeTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2.5)
        };
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer?.Stop();
            if (_captureTcs == null) CaptureHint.Visibility = Visibility.Collapsed;
        };
        _noticeTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        SaveData(); // insurance: persist everything on exit
        foreach (var r in _runners.Values) r.Dispose();
        _hook.Dispose();
        try { _sounds?.Dispose(); } catch { }
        try { Win32.timeEndPeriod(1); } catch { }
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        base.OnClosed(e);
    }

    private string? Prompt(string label, string initial)
    {
        var app = System.Windows.Application.Current;
        WBrush bg = app?.TryFindResource("BgBrush") as WBrush
            ?? new SolidColorBrush(WColor.FromRgb(0x1F, 0x22, 0x26));
        // Accent-colored OK button with guaranteed white text (built explicitly
        // so it never depends on implicit style resolution).
        WBrush accent = app?.TryFindResource("AccentBrush") as WBrush
            ?? new SolidColorBrush(WColor.FromRgb(0x7C, 0x5C, 0xFF));
        var win = new Window
        {
            Title = label,
            Width = 340,
            Height = 190,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = bg,
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new WLabel { Content = label, FontSize = 13 });
        var box = new WTextBox { Text = initial, Margin = new Thickness(0, 6, 0, 0), FontSize = 13, Padding = new Thickness(8, 5, 8, 5) };
        panel.Children.Add(box);
        var okBorder = new Border
        {
            Background = accent,
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 14, 0, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        var okText = new TextBlock
        {
            Text = "OK",
            Foreground = new SolidColorBrush(Colors.White),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Padding = new Thickness(20, 6, 20, 6),
        };
        okBorder.Child = okText;
        okBorder.MouseLeftButtonUp += (_, _) => { win.DialogResult = true; win.Close(); };
        panel.Children.Add(okBorder);
        win.Content = panel;
        box.Focus();
        box.SelectAll();
        return win.ShowDialog() == true ? box.Text : null;
    }
}
