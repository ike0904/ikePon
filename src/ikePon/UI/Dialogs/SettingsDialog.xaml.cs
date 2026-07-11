using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ikePon.Controller;
using ikePon.Model;

namespace ikePon.UI.Dialogs;

public partial class SettingsDialog : Window
{
    private readonly AppSettings _settings;

    // マウスドラッグ状態
    private TextBox? _dragBox;
    private double _dragStartY;
    private double _dragStartVal;
    private bool _isDragging;

    public SettingsDialog(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        CbPaSeparate.SelectedIndex = settings.PaSeparateMode ? 1 : 0;
        TbStandbyFadeIn.Text = settings.StandbyFadeInDuration.ToString("F1");
        TbLongFade.Text  = settings.LongFadeDuration.ToString("F1");
        TbInterlock.Text = settings.InterLockMs.ToString();
        TbLatency.Text   = settings.WasapiLatencyMs.ToString();
        TbPreload.Text   = settings.PreloadThresholdSeconds.ToString();

        TbMovieStandby.Text = settings.MovieStandbyImagePath ?? "";

        // MIDIデバイス一覧を列挙
        CbMidiDevice.Items.Add(L.S("Str_Dlg_Settings_MidiNone"));
        foreach (var name in MidiController.GetDeviceNames())
            CbMidiDevice.Items.Add(name);
        // 保存済みデバイスを選択
        int midiIdx = CbMidiDevice.Items.Cast<string>()
            .ToList().IndexOf(settings.SelectedMidiDeviceName);
        CbMidiDevice.SelectedIndex = midiIdx >= 0 ? midiIdx : 0;

        // 言語選択
        CbLanguage.SelectedIndex = settings.Language == "en" ? 1 : 0;
        CbLanguage.SelectionChanged += CbLanguage_SelectionChanged;

        SetResetMenu(TbStandbyFadeIn, "1.0");
        SetResetMenu(TbLongFade,      "2.0");
        SetResetMenu(TbInterlock,     "500");
        SetResetMenu(TbLatency,       "30");
        SetResetMenu(TbPreload,       "10");
        SetResetMenu(TbMovieStandby,  "");
    }

    private void CbLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        string selected = CbLanguage.SelectedIndex == 1 ? "en" : "ja";
        TbLangNote.Visibility = selected != _settings.Language
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static void SetResetMenu(TextBox tb, string defaultValue)
    {
        var cm   = new ContextMenu();
        var item = new MenuItem { Header = L.S("Str_Btn_ResetDefault") };
        item.Click += (_, _) => tb.Text = defaultValue;
        cm.Items.Add(item);
        tb.ContextMenu = cm;
    }

    private void TbMovieStandby_PreviewDragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void TbMovieStandby_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        var imageExts = new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tiff", ".tif" };
        string? file = files.FirstOrDefault(f => imageExts.Contains(
            Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
        if (file == null) return;
        TbMovieStandby.Text = file;
        e.Handled = true;
    }

    private void BtnClearMovieStandby_Click(object sender, RoutedEventArgs e)
    {
        TbMovieStandby.Text = "";
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParsePositive(TbStandbyFadeIn.Text, 0f, 9.9f, out float standbyFadeIn)) { ShowError(TbStandbyFadeIn, "0.0〜9.9"); return; }
        if (!TryParsePositive(TbLongFade.Text,  0f, 9.9f, out float longFade))  { ShowError(TbLongFade,  "0.0〜9.9"); return; }
        if (!TryParseInt(TbInterlock.Text, 0, 5000, out int interlock))         { ShowError(TbInterlock, "0〜5000");  return; }
        if (!TryParseInt(TbLatency.Text,   1, 500,  out int latency))           { ShowError(TbLatency,   "1〜500");   return; }
        if (!TryParseInt(TbPreload.Text,   1, 600,  out int preload))           { ShowError(TbPreload,   "1〜600");   return; }

        _settings.PaSeparateMode          = CbPaSeparate.SelectedIndex == 1;
        _settings.StandbyFadeInDuration   = standbyFadeIn;
        _settings.LongFadeDuration        = longFade;
        _settings.InterLockMs             = interlock;
        _settings.WasapiLatencyMs         = latency;
        _settings.PreloadThresholdSeconds = preload;
        _settings.MovieStandbyImagePath   = TbMovieStandby.Text;
        _settings.SelectedMidiDeviceName  = CbMidiDevice.SelectedIndex > 0
            ? (CbMidiDevice.SelectedItem as string ?? "")
            : "";
        _settings.Language = CbLanguage.SelectedIndex == 1 ? "en" : "ja";

        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    // ──────────────────────────── ウィンドウイベント ────────────────────────────

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Mouse.Captured is TextBox) Mouse.Capture(null);
        if (e.OriginalSource is TextBox tb) { tb.Focus(); return; }
        Keyboard.ClearFocus();
    }

    // ──────────────────────────── テキストボックス共通 ────────────────────────────

    // TextBox ごとの設定（min, max, isFloat）
    private (double min, double max, bool isFloat) GetBoxParams(TextBox tb)
    {
        if (tb == TbStandbyFadeIn) return (0.0, 9.9, true);
        if (tb == TbLongFade)      return (0.0, 9.9, true);
        if (tb == TbInterlock)     return (0.0, 5000.0, false);
        if (tb == TbLatency)       return (1.0, 500.0, false);
        if (tb == TbPreload)       return (1.0, 600.0, false);
        return (0.0, 9999.0, false);
    }

    private static double GetBoxCurrentValue(TextBox tb, bool isFloat)
    {
        if (isFloat)
        {
            return float.TryParse(tb.Text.Trim(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float f) ? f : 0.0;
        }
        return int.TryParse(tb.Text.Trim(), out int i) ? i : 0.0;
    }

    private static void SetBoxValue(TextBox tb, bool isFloat, double value)
    {
        tb.Text = isFloat
            ? ((float)value).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
            : ((int)Math.Round(value)).ToString();
    }

    private void CommitBox(TextBox tb)
    {
        var (min, max, isFloat) = GetBoxParams(tb);
        double val = GetBoxCurrentValue(tb, isFloat);
        SetBoxValue(tb, isFloat, Math.Clamp(val, min, max));
    }

    // ENTER キーで確定
    private void NumericBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Return or Key.Enter)) return;
        if (sender is TextBox tb)
        {
            CommitBox(tb);
            tb.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        }
        e.Handled = true;
    }

    // フォーカスアウトで確定
    private void NumericBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) CommitBox(tb);
    }

    // ──────────────────────────── マウスドラッグ ────────────────────────────

    private void NumericBox_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox tb || e.LeftButton != MouseButtonState.Pressed) return;
        var (_, _, isFloat) = GetBoxParams(tb);
        _dragBox      = tb;
        _dragStartY   = e.GetPosition(this).Y;
        _dragStartVal = GetBoxCurrentValue(tb, isFloat);
        _isDragging   = false;
    }

    private void NumericBox_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragBox == null || sender is not TextBox tb || tb != _dragBox) return;
        if (e.LeftButton != MouseButtonState.Pressed) { _dragBox = null; return; }

        double deltaY = _dragStartY - e.GetPosition(this).Y; // 上→増加
        if (Math.Abs(deltaY) < 3) return;
        _isDragging = true;

        var (min, max, isFloat) = GetBoxParams(tb);
        bool shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
        int steps = (int)(deltaY / 5.0);
        double stepSize = isFloat ? (shift ? 1.0 : 0.1) : (shift ? 10.0 : 1.0);

        double newVal = Math.Clamp(_dragStartVal + steps * stepSize, min, max);
        SetBoxValue(tb, isFloat, newVal);
        e.Handled = true;
    }

    private void NumericBox_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDragging) e.Handled = true;
        _dragBox    = null;
        _isDragging = false;
    }

    // ──────────────────────────── マウスホイール ────────────────────────────

    private void NumericBox_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not TextBox tb) return;
        var (min, max, isFloat) = GetBoxParams(tb);
        bool shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
        double stepSize = isFloat ? (shift ? 1.0 : 0.1) : (shift ? 10.0 : 1.0);
        double delta    = e.Delta > 0 ? stepSize : -stepSize;

        double val = GetBoxCurrentValue(tb, isFloat);
        SetBoxValue(tb, isFloat, Math.Clamp(val + delta, min, max));
        e.Handled = true;
    }

    // ──────────────────────────── ヘルパー ────────────────────────────

    private static void ShowError(System.Windows.Controls.TextBox tb, string range)
    {
        MessageBox.Show(L.F("Str_Dlg_Settings_ErrorRange", range),
            L.S("Str_Dlg_Settings_ErrorTitle"),
            MessageBoxButton.OK, MessageBoxImage.Warning);
        tb.Focus();
        tb.SelectAll();
    }

    private static bool TryParsePositive(string s, float min, float max, out float result)
    {
        if (float.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out result))
            return result >= min && result <= max;
        return false;
    }

    private static bool TryParseInt(string s, int min, int max, out int result)
    {
        if (int.TryParse(s, out result))
            return result >= min && result <= max;
        return false;
    }
}
