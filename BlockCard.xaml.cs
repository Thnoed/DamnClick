using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Clicker.Core;
using ClickMode = Clicker.Core.ClickMode;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace Clicker;

public partial class BlockCard : System.Windows.Controls.UserControl
{
    private readonly ClickBlock _block;
    private readonly BlockRunner _runner;
    private readonly Action _onChanged;
    private readonly Action<BlockCard> _onRemove;
    private readonly Func<Action<string>, Task<string?>> _captureFunc;
    private readonly Action<string>? _notify;
    private bool _loading = true;
    private bool _expanded;

    public ClickBlock Block => _block;
    public BlockRunner Runner => _runner;

    public BlockCard(ClickBlock block, BlockRunner runner, Action onChanged,
        Action<BlockCard> onRemove, Func<Action<string>, Task<string?>> captureFunc,
        Action<string>? notify = null)
    {
        InitializeComponent();
        _block = block;
        _runner = runner;
        _onChanged = onChanged;
        _notify = notify;
        _onRemove = onRemove;
        _captureFunc = captureFunc;

        ModeBox.Items.Add(I18n.T("mode_hold"));
        ModeBox.Items.Add(I18n.T("mode_toggle"));
        ModeBox.Items.Add(I18n.T("mode_combined"));

        LoadFields();
        // Async marshal: state fires from poll/click threads, never block them.
        _runner.StateChanged += () => Dispatcher.BeginInvoke(new Action(UpdateState));
        _loading = false;
        UpdateState();
        ApplyLang();
    }

    private void LoadFields()
    {
        EnabledBox.IsChecked = _block.Enabled;
        ActivationBtn.Content = KeyCodes.DisplayName(_block.ActivationKey);
        ClickBtn.Content = KeyCodes.DisplayName(_block.ClickKey);
        ModeBox.SelectedIndex = (int)_block.Mode;
        CpsSlider.Value = Num.Clamp(_block.Cps, 1, 150);
        CpsBox.Text = _block.Cps.ToString("0.#");
        RandomSlider.Value = _block.RandomPercent;
        RandomBox.Text = _block.RandomPercent.ToString("0.#");
        DutySlider.Value = _block.DutyCycle;
        DutyBox.Text = _block.DutyCycle.ToString("0.#");
        UnlimitedBox.IsChecked = _block.Unlimited;
        LimitBox.Text = _block.ClickLimit.ToString();
        LimitBox.IsEnabled = !_block.Unlimited;
        DelaySlider.Value = Num.Clamp(_block.DelayMs, 0, 1000);
        DelayBox.Text = _block.DelayMs.ToString();
    }

    public void ApplyLang()
    {
        ActivationBtn.ToolTip = I18n.T("activation_key") + "\n" + I18n.T("hint_activation");
        ClickBtn.ToolTip = I18n.T("click_key") + "\n" + I18n.T("hint_click");
        EnabledBox.ToolTip = I18n.T("hint_enabled");
        var idx = ModeBox.SelectedIndex;
        ModeBox.Items.Clear();
        ModeBox.Items.Add(I18n.T("mode_hold"));
        ModeBox.Items.Add(I18n.T("mode_toggle"));
        ModeBox.Items.Add(I18n.T("mode_combined"));
        ModeBox.SelectedIndex = idx;
        CpsLabel.Content = I18n.T("cps");
        RandomLabel.Content = I18n.T("random");
        DutyLabel.Content = I18n.T("duty");
        LimitLabel.Content = I18n.T("click_limit");
        DelayLabel.Content = I18n.T("delay");
        UnlimitedBox.Content = I18n.T("unlimited");
        UnlimitedBox.ToolTip = I18n.T("hint_unlimited");
        HelpCps.ToolTip = I18n.T("hint_cps");
        HelpRandom.ToolTip = I18n.T("hint_random");
        HelpDuty.ToolTip = I18n.T("hint_duty");
        HelpLimit.ToolTip = I18n.T("hint_limit");
        HelpDelay.ToolTip = I18n.T("hint_delay");
        RemoveBtn.ToolTip = I18n.T("delete_tip");
        ExpandBtn.ToolTip = _expanded ? I18n.T("collapse_tip") : I18n.T("expand_tip");
        UpdateState();
    }

    public void UpdateState()
    {
        ClicksText.Text = $"{I18n.T("clicks")}: {_runner.ClicksDone}";
    }

    private async void ActivationBtn_Click(object sender, RoutedEventArgs e)
    {
        var id = await _captureFunc(t => ActivationBtn.Content = t);
        if (id != null)
        {
            if (id == _block.ClickKey)
            {
                _notify?.Invoke(I18n.T("same_key_err"));
            }
            else
            {
                _block.ActivationKey = id;
                _onChanged();
            }
            ActivationBtn.Content = KeyCodes.DisplayName(_block.ActivationKey);
        }
        else ActivationBtn.Content = KeyCodes.DisplayName(_block.ActivationKey);
    }

    private async void ClickBtn_Click(object sender, RoutedEventArgs e)
    {
        var id = await _captureFunc(t => ClickBtn.Content = t);
        if (id != null)
        {
            if (id == _block.ActivationKey)
            {
                _notify?.Invoke(I18n.T("same_key_err"));
            }
            else
            {
                _block.ClickKey = id;
                _onChanged();
            }
            ClickBtn.Content = KeyCodes.DisplayName(_block.ClickKey);
        }
        else ClickBtn.Content = KeyCodes.DisplayName(_block.ClickKey);
    }

    private void RemoveBtn_Click(object sender, RoutedEventArgs e) => _onRemove(this);

    private void ExpandBtn_Click(object sender, RoutedEventArgs e)
    {
        _expanded = !_expanded;
        DetailsPanel.Visibility = _expanded ? Visibility.Visible : Visibility.Collapsed;
        ExpandBtn.ToolTip = _expanded ? I18n.T("collapse_tip") : I18n.T("expand_tip");
    }

    private void Field_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _block.Enabled = EnabledBox.IsChecked == true;
        _block.Mode = (ClickMode)Math.Max(0, ModeBox.SelectedIndex);
        _block.Unlimited = UnlimitedBox.IsChecked == true;
        LimitBox.IsEnabled = !_block.Unlimited;
        _runner.RefreshAfterEdit();
        UpdateState();
        _onChanged();
    }

    private void CpsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _block.Cps = Math.Round(e.NewValue, 1);
        CpsBox.Text = _block.Cps.ToString("0.#");
        _onChanged();
    }

    private void CpsBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        if (double.TryParse(CpsBox.Text.Replace(',', '.'), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v))
        {
            v = Num.Clamp(v, 0.5, 1000);
            _block.Cps = v;
            // Slider max is 150; if value is higher, keep slider at max position
            if (v >= 1 && v <= 150)
                CpsSlider.Value = v;
            else if (v > 150)
                CpsSlider.Value = 150;
            _onChanged();
        }
    }

    private void RandomSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _block.RandomPercent = Math.Round(e.NewValue, 1);
        RandomBox.Text = _block.RandomPercent.ToString("0.#");
        _onChanged();
    }

    private void RandomBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        if (double.TryParse(RandomBox.Text.Replace(',', '.'), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v))
        {
            _block.RandomPercent = Num.Clamp(v, 0, 100);
            _onChanged();
        }
    }

    private void DutySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _block.DutyCycle = Math.Round(e.NewValue, 1);
        DutyBox.Text = _block.DutyCycle.ToString("0.#");
        _onChanged();
    }

    private void DutyBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        if (double.TryParse(DutyBox.Text.Replace(',', '.'), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v))
        {
            _block.DutyCycle = Num.Clamp(v, 5, 95);
            _onChanged();
        }
    }

    private void LimitBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        if (int.TryParse(LimitBox.Text, out var v))
        {
            _block.ClickLimit = Math.Max(0, v);
            _onChanged();
        }
    }

    private void DelaySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _block.DelayMs = Num.Clamp((int)Math.Round(e.NewValue), 0, 1000);
        DelayBox.Text = _block.DelayMs.ToString();
        _runner.RefreshAfterEdit();
        _onChanged();
    }

    private void DelayBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        if (int.TryParse(DelayBox.Text, out var v))
        {
            v = Num.Clamp(v, 0, 1000);
            _block.DelayMs = v;
            if (Math.Abs(DelaySlider.Value - v) > 0.01)
                DelaySlider.Value = v;
            _runner.RefreshAfterEdit();
            _onChanged();
        }
    }
}
