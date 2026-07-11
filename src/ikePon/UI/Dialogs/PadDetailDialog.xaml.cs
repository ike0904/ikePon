using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ikePon.Model;
using TapBehavior = ikePon.Model.TapBehavior;

namespace ikePon.UI.Dialogs;

public partial class PadDetailDialog : Window
{
    private readonly PadSettings _padSettings;
    private readonly float _fileTotalSec;

    public AudioCategory       ResultCategory          { get; private set; }
    public string?             ResultLabel             { get; private set; }
    public string?             ResultFilePath          { get; private set; }
    public float               ResultPadGain           { get; private set; }
    public float               ResultStartSec          { get; private set; }
    public float               ResultEndSec            { get; private set; }
    public AfterPlaybackBehavior ResultAfterPlayback   { get; private set; }
    public string?             ResultPadBackgroundColor { get; private set; }
    public TapBehavior         ResultTapBehavior       { get; private set; }
    public float               ResultLoopStartSec      { get; private set; }

    private string? _selectedBgColor;
    private readonly Dictionary<TextBox, string> _savedTexts = new();

    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tiff", ".tif" };

    private static readonly string[] BgColorPalette =
    [
        "#3C3C3C",
        "#162744",
        "#143A14",
        "#3A1414",
        "#2A1040",
        "#3A2010",
        "#103A3A",
        "#2A3010",
    ];

    // Mouse drag state
    private TextBox? _dragBox;
    private double _dragStartY;
    private double _dragStartVal;
    private bool _isDragging;

    public PadDetailDialog(PadSettings padSettings, float fileTotalSec)
    {
        _padSettings  = padSettings;
        _fileTotalSec = fileTotalSec;
        InitializeComponent();
        Loaded += (_, _) => App.SetLightTitleBar(this);
        LoadValues();

        SetResetMenu(TbDisplayName, () =>
            string.IsNullOrEmpty(TbFilePath.Text) ? "" : Path.GetFileNameWithoutExtension(TbFilePath.Text));
        SetResetMenu(TbPadGain,  "100");
        SetResetMenu(TbStartPos,  "0:00.00");
        SetResetMenu(TbEndPos,    "");
        SetResetMenu(TbLoopStart, "");

        foreach (var tb in new[] { TbPadGain, TbStartPos, TbEndPos, TbLoopStart })
        {
            var captured = tb;
            tb.GotFocus += (_, _) => _savedTexts[captured] = captured.Text;
        }
    }

    private static void SetResetMenu(TextBox tb, string defaultValue)
    {
        var cm   = new ContextMenu();
        var item = new MenuItem { Header = L.S("Str_Btn_ResetDefault") };
        item.Click += (_, _) => tb.Text = defaultValue;
        cm.Items.Add(item);
        tb.ContextMenu = cm;
    }

    private static void SetResetMenu(TextBox tb, Func<string> getDefault)
    {
        var cm   = new ContextMenu();
        var item = new MenuItem { Header = L.S("Str_Btn_ResetDefault") };
        item.Click += (_, _) => tb.Text = getDefault();
        cm.Items.Add(item);
        tb.ContextMenu = cm;
    }

    private void LoadValues()
    {
        CbCategory.SelectedIndex = _padSettings.Category switch
        {
            AudioCategory.Movie => 0,
            AudioCategory.BGM   => 1,
            _                   => 2
        };

        CbAfterPlayback.SelectedIndex = _padSettings.AfterPlayback switch
        {
            AfterPlaybackBehavior.FreezeLastFrame => 1,
            AfterPlaybackBehavior.Loop            => 2,
            _                                     => 0
        };
        CbTapBehavior.SelectedIndex = _padSettings.TapBehavior switch
        {
            TapBehavior.CutOut      => 1,
            TapBehavior.PauseResume => 2,
            _                       => 0
        };
        UpdateAfterPlaybackItemStates();

        string defaultName = string.IsNullOrEmpty(_padSettings.FilePath)
            ? "" : Path.GetFileNameWithoutExtension(_padSettings.FilePath);
        TbDisplayName.Text = _padSettings.CustomLabel ?? defaultName;

        TbFilePath.Text = _padSettings.FilePath ?? "";

        TbPadGain.Text = Math.Clamp((int)Math.Round(_padSettings.PadGain * 100), 0, 500).ToString();

        TbStartPos.Text = SecsToTimestamp(_padSettings.StartPositionSec);

        TbEndPos.Text = _padSettings.EndPositionSec >= 0 ? SecsToTimestamp(_padSettings.EndPositionSec) : "";

        TbLoopStart.Text = _padSettings.LoopStartSec >= 0 ? SecsToTimestamp(_padSettings.LoopStartSec) : "";
        UpdateLoopStartState();

        if (_fileTotalSec > 0)
            LblTotalTime.Content = L.F("Str_Dlg_Pad_TotalTime", SecsToTimestamp(_fileTotalSec));

        _selectedBgColor = _padSettings.PadBackgroundColor;
        BuildBgColorSwatches();
        ApplyImagePadConstraints();
    }

    private void ApplyImagePadConstraints()
    {
        if (string.IsNullOrEmpty(_padSettings.FilePath)) return;
        if (!ImageExts.Contains(Path.GetExtension(_padSettings.FilePath))) return;

        CbCategory.IsEnabled = false;
        CbCategory.Opacity   = 0.4;

        TbPadGain.IsEnabled = false;
        TbPadGain.Opacity   = 0.4;

        TbStartPos.IsEnabled = false;
        TbStartPos.Opacity   = 0.4;
        TbEndPos.IsEnabled   = false;
        TbEndPos.Opacity     = 0.4;

        CbAfterPlayback.IsEnabled = false;
        CbAfterPlayback.Opacity   = 0.4;

        CbTapPauseResume.IsEnabled = false;
        if (CbTapBehavior.SelectedIndex == 2)
            CbTapBehavior.SelectedIndex = 0;
    }

    private void BuildBgColorSwatches()
    {
        BgColorSwatchesPanel.Children.Clear();
        foreach (var hex in BgColorPalette)
        {
            var bd = new Border
            {
                Width = 28, Height = 28,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(0, 0, 4, 0),
                Cursor = Cursors.Hand,
                Tag = hex,
            };
            string captured = hex;
            bd.MouseLeftButtonDown += (_, _) => SelectBgColor(captured);
            BgColorSwatchesPanel.Children.Add(bd);
        }
        UpdateSwatchBorders();
    }

    private void SelectBgColor(string hex)
    {
        _selectedBgColor = (hex == "#3C3C3C") ? null : hex;
        UpdateSwatchBorders();
    }

    private void UpdateSwatchBorders()
    {
        string effective = _selectedBgColor ?? "#3C3C3C";
        foreach (Border bd in BgColorSwatchesPanel.Children.OfType<Border>())
        {
            bool sel = bd.Tag is string h && h.Equals(effective, StringComparison.OrdinalIgnoreCase);
            bd.BorderBrush = sel
                ? new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00))
                : new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
        }
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        var cat = CbCategory.SelectedIndex switch
        {
            0 => AudioCategory.Movie,
            1 => AudioCategory.BGM,
            _ => AudioCategory.SE
        };

        if (!TryParseGainInt(TbPadGain.Text, out int padGainInt))
        { ShowError(TbPadGain, L.S("Str_Dlg_Pad_GainError")); return; }

        CommitPosBox(TbStartPos);
        CommitPosBox(TbEndPos);
        CommitPosBox(TbLoopStart);

        float startSec = 0f;
        if (!string.IsNullOrWhiteSpace(TbStartPos.Text))
        {
            if (!TryParseTimestampLenient(TbStartPos.Text, out startSec) || startSec < 0)
                startSec = 0f;
        }

        float endSec = -1f;
        if (!string.IsNullOrWhiteSpace(TbEndPos.Text))
        {
            if (!TryParseTimestampLenient(TbEndPos.Text, out endSec) || endSec <= startSec)
            { ShowError(TbEndPos, L.S("Str_Dlg_Pad_PosError")); return; }
        }

        float loopStartSec = -1f;
        bool isLoop = CbAfterPlayback.SelectedIndex == 2;
        if (isLoop && !string.IsNullOrWhiteSpace(TbLoopStart.Text))
        {
            if (!TryParseTimestampLenient(TbLoopStart.Text, out loopStartSec) || loopStartSec < 0)
                loopStartSec = -1f;
        }

        ResultCategory           = cat;
        ResultLabel              = string.IsNullOrWhiteSpace(TbDisplayName.Text) ? null : TbDisplayName.Text.Trim();
        ResultFilePath           = string.IsNullOrWhiteSpace(TbFilePath.Text) ? null : TbFilePath.Text.Trim();
        ResultPadGain            = padGainInt / 100.0f;
        ResultStartSec           = startSec;
        ResultEndSec             = endSec;
        ResultAfterPlayback      = CbAfterPlayback.SelectedIndex switch
        {
            1 => AfterPlaybackBehavior.FreezeLastFrame,
            2 => AfterPlaybackBehavior.Loop,
            _ => AfterPlaybackBehavior.Stop
        };
        ResultPadBackgroundColor = _selectedBgColor;
        ResultTapBehavior = CbTapBehavior.SelectedIndex switch
        {
            1 => TapBehavior.CutOut,
            2 when cat != AudioCategory.SE => TapBehavior.PauseResume,
            _ => TapBehavior.FadeOut
        };
        ResultLoopStartSec  = loopStartSec;

        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void CbCategory_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateAfterPlaybackItemStates();
        UpdateTapBehaviorState();
    }

    private void CbAfterPlayback_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateLoopStartState();
    }

    private void UpdateAfterPlaybackItemStates()
    {
        if (CbAfterFreeze == null || CbAfterLoop == null) return;
        var cat = CbCategory.SelectedIndex switch { 0 => AudioCategory.Movie, 1 => AudioCategory.BGM, _ => AudioCategory.SE };
        CbAfterFreeze.IsEnabled = (cat == AudioCategory.Movie);
        CbAfterLoop.IsEnabled   = (cat != AudioCategory.SE);

        if (!CbAfterFreeze.IsEnabled && CbAfterPlayback.SelectedIndex == 1)
            CbAfterPlayback.SelectedIndex = 0;
        if (!CbAfterLoop.IsEnabled && CbAfterPlayback.SelectedIndex == 2)
            CbAfterPlayback.SelectedIndex = 0;
    }

    private void UpdateTapBehaviorState()
    {
        if (CbTapBehavior == null || CbTapPauseResume == null) return;
        bool isSE = CbCategory.SelectedIndex == 2;
        TapBehaviorRow.IsEnabled = !isSE;
        TapBehaviorRow.Opacity   = isSE ? 0.4 : 1.0;
        if (isSE && CbTapBehavior.SelectedIndex == 2)
            CbTapBehavior.SelectedIndex = 0;
    }

    private void UpdateLoopStartState()
    {
        if (TbLoopStart == null) return;
        bool isLoop = CbAfterPlayback.SelectedIndex == 2;
        TbLoopStart.IsEnabled = isLoop;
        TbLoopStart.Opacity   = isLoop ? 1.0 : 0.4;
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // TextBox のマウスキャプチャのみ解放（ComboBox等のキャプチャは触らない）
        if (Mouse.Captured is TextBox) Mouse.Capture(null);
        if (e.OriginalSource is TextBox tb) { tb.Focus(); return; }
        Keyboard.ClearFocus();
    }

    // ------------------------------------------------------------------
    // テキストボックス確定・自動補正
    // ------------------------------------------------------------------
    private void NumericBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Return or Key.Enter)) return;
        if (sender is TextBox tb)
        {
            bool isGain = (tb == TbPadGain);
            if (isGain) CommitGainBox(tb);
            else CommitPosBox(tb);
            Keyboard.ClearFocus();
        }
        e.Handled = true;
    }

    private void GainBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) CommitGainBox(tb);
    }

    private void PosBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb) CommitPosBox(tb);
    }

    private void CommitGainBox(TextBox tb)
    {
        string converted = ToHalfWidth(tb.Text);
        if (TryParseGainInt(converted, out int val))
        {
            _savedTexts[tb] = val.ToString();
            tb.Text = _savedTexts[tb];
        }
        else
        {
            tb.Text = _savedTexts.TryGetValue(tb, out var saved) ? saved : "100";
        }
    }

    private void CommitPosBox(TextBox tb)
    {
        if (string.IsNullOrWhiteSpace(tb.Text))
        {
            _savedTexts[tb] = "";
            return;
        }

        string converted = ToHalfWidth(tb.Text);
        if (!TryParseTimestampLenient(converted, out float secs) || secs < 0)
        {
            tb.Text = _savedTexts.TryGetValue(tb, out var saved) ? saved : "";
            return;
        }

        if (tb == TbStartPos)
        {
            if (!string.IsNullOrWhiteSpace(TbEndPos.Text) &&
                TryParseTimestampLenient(ToHalfWidth(TbEndPos.Text), out float endSec) &&
                secs >= endSec)
            {
                tb.Text = _savedTexts.TryGetValue(tb, out var saved) ? saved : "";
                return;
            }
        }
        else if (tb == TbEndPos)
        {
            if (!string.IsNullOrWhiteSpace(TbStartPos.Text) &&
                TryParseTimestampLenient(ToHalfWidth(TbStartPos.Text), out float startSec) &&
                secs <= startSec)
            {
                tb.Text = _savedTexts.TryGetValue(tb, out var saved) ? saved : "";
                return;
            }
            if (!string.IsNullOrWhiteSpace(TbLoopStart.Text) &&
                TryParseTimestampLenient(ToHalfWidth(TbLoopStart.Text), out float loopSec) &&
                secs <= loopSec)
            {
                tb.Text = _savedTexts.TryGetValue(tb, out var saved) ? saved : "";
                return;
            }
        }
        else if (tb == TbLoopStart)
        {
            if (!string.IsNullOrWhiteSpace(TbEndPos.Text) &&
                TryParseTimestampLenient(ToHalfWidth(TbEndPos.Text), out float endSec) &&
                secs >= endSec)
            {
                tb.Text = _savedTexts.TryGetValue(tb, out var saved) ? saved : "";
                return;
            }
        }

        string normalized = SecsToTimestamp(secs);
        _savedTexts[tb] = normalized;
        tb.Text = normalized;
    }

    private static string ToHalfWidth(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c >= '！' && c <= '～') sb.Append((char)(c - 0xFEE0));
            else if (c == '　') sb.Append(' ');
            else sb.Append(c);
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------
    // マウスドラッグ
    // ------------------------------------------------------------------
    private void NumericBox_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox tb || e.LeftButton != MouseButtonState.Pressed) return;
        _dragBox = tb;
        _dragStartY = e.GetPosition(this).Y;
        _isDragging = false;

        bool isGain = (tb == TbPadGain);
        if (isGain)
            _dragStartVal = TryParseGainInt(tb.Text, out int v) ? v : 100.0;
        else if (TryParseTimestampLenient(tb.Text, out float v))
            _dragStartVal = v;
        else
            _dragStartVal = (tb == TbEndPos && _fileTotalSec > 0) ? _fileTotalSec : 0.0;
    }

    private void NumericBox_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragBox == null || sender is not TextBox tb || tb != _dragBox) return;
        if (e.LeftButton != MouseButtonState.Pressed) { _dragBox = null; return; }

        double deltaY = _dragStartY - e.GetPosition(this).Y; // up = positive = increase
        if (Math.Abs(deltaY) < 3) return;
        _isDragging = true;

        bool shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
        bool isGain = (tb == TbPadGain);
        int steps = (int)(deltaY / 5.0); // 5px per step

        if (isGain)
        {
            double stepSize = shift ? 10.0 : 1.0;
            int newVal = Math.Clamp((int)(_dragStartVal + steps * stepSize), 0, 500);
            tb.Text = newVal.ToString();
        }
        else
        {
            double stepSize = shift ? 1.0 : 0.1;
            float newVal = (float)Math.Max(0, _dragStartVal + steps * stepSize);
            if (_fileTotalSec > 0) newVal = Math.Min(newVal, _fileTotalSec);
            tb.Text = SecsToTimestamp(newVal);
        }

        e.Handled = true; // ドラッグ中はテキスト選択を抑制
    }

    private void NumericBox_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDragging) e.Handled = true;
        _dragBox = null;
        _isDragging = false;
    }

    // ------------------------------------------------------------------
    // マウスホイール
    // ------------------------------------------------------------------
    private void GainBox_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not TextBox tb) return;
        bool shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
        int step = e.Delta > 0 ? (shift ? 10 : 1) : (shift ? -10 : -1);

        if (!TryParseGainInt(tb.Text, out int val)) val = 100;
        tb.Text = Math.Clamp(val + step, 0, 500).ToString();
        e.Handled = true;
    }

    private void PosBox_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not TextBox tb) return;
        bool shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
        float step = e.Delta > 0 ? (shift ? 1.0f : 0.1f) : (shift ? -1.0f : -0.1f);

        float val;
        if (!TryParseTimestampLenient(tb.Text, out val))
            val = (tb == TbEndPos && _fileTotalSec > 0) ? _fileTotalSec : 0f;
        val = (float)Math.Max(0, val + step);
        if (_fileTotalSec > 0) val = Math.Min(val, _fileTotalSec);
        tb.Text = SecsToTimestamp(val);
        e.Handled = true;
    }

    private void BtnClearFilePath_Click(object sender, RoutedEventArgs e)
    {
        TbFilePath.Text = "";
    }

    // ------------------------------------------------------------------
    // ファイルパス D&D
    // ------------------------------------------------------------------
    private void TbFilePath_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private static readonly HashSet<string> AcceptedExts = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".wav", ".flac", ".ogg", ".aac", ".m4a", ".mp4", ".mov", ".mkv", ".avi", ".wmv",
          ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tiff", ".tif" };
    private static readonly HashSet<string> VideoImageExts = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mov", ".mkv", ".avi", ".wmv",
          ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tiff", ".tif" };

    private void TbFilePath_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;

        string? file = files.FirstOrDefault(f => AcceptedExts.Contains(Path.GetExtension(f)));
        if (file == null) return;

        TbFilePath.Text = file;
        // 動画・画像ファイルは自動的に MOV カテゴリに設定
        if (VideoImageExts.Contains(Path.GetExtension(file)))
            CbCategory.SelectedIndex = 0; // MOVIE
        if (string.IsNullOrWhiteSpace(TbDisplayName.Text))
            TbDisplayName.Text = Path.GetFileNameWithoutExtension(file);
        e.Handled = true;
    }

    // ------------------------------------------------------------------
    // ヘルパー
    // ------------------------------------------------------------------
    private static string SecsToTimestamp(float secs)
    {
        if (secs < 0) secs = 0;
        int m  = (int)(secs / 60);
        float r = secs - m * 60;
        int ss = (int)r;
        int nn = (int)Math.Round((r - ss) * 100);
        if (nn >= 100) { nn = 0; if (++ss >= 60) { ss = 0; m++; } }
        return $"{m}:{ss:00}.{nn:00}";
    }

    /// <summary>
    /// タイムスタンプを寛容にパース。
    /// "12" → 12秒, "1:23" → 83秒, "1:23.5" / "1:23:5" / "1.23.5" → 83.5秒
    /// </summary>
    private static bool TryParseTimestampLenient(string s, out float secs)
    {
        secs = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();

        if (s.Contains(':'))
        {
            var parts = s.Split(':');
            // m:ss.n 形式
            if (parts.Length == 2
                && int.TryParse(parts[0].Trim(), out int m)
                && float.TryParse(parts[1].Trim(),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out float sec))
            {
                secs = m * 60 + sec;
                return true;
            }
            // m:ss:n 形式（"1:23:5" → 83.5秒）
            if (parts.Length == 3
                && int.TryParse(parts[0].Trim(), out int m3)
                && int.TryParse(parts[1].Trim(), out int ss3)
                && float.TryParse("0." + parts[2].Trim(),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out float frac3))
            {
                secs = m3 * 60 + ss3 + frac3;
                return true;
            }
            return false;
        }

        // m.ss.n 形式（"1.23.5" → 83.5秒）
        var dotParts = s.Split('.');
        if (dotParts.Length == 3
            && int.TryParse(dotParts[0].Trim(), out int mDot)
            && int.TryParse(dotParts[1].Trim(), out int ssDot)
            && float.TryParse("0." + dotParts[2].Trim(),
                NumberStyles.Float, CultureInfo.InvariantCulture, out float fracDot))
        {
            secs = mDot * 60 + ssDot + fracDot;
            return true;
        }

        // コロンなし → 秒数として解釈
        if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float plainSecs))
        {
            secs = plainSecs;
            return true;
        }
        return false;
    }

    private static bool TryParseGainInt(string s, out int val)
    {
        if (int.TryParse(s.Trim(), out val))
            return val >= 0 && val <= 500;
        val = 100;
        return false;
    }

    private static void ShowError(TextBox tb, string message)
    {
        MessageBox.Show(message, L.S("Str_Dlg_Pad_ErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        tb.Focus();
        tb.SelectAll();
    }
}
