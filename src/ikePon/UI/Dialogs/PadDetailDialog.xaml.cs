using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ikePon.Model;

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

    private string? _selectedBgColor;

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
        SetResetMenu(TbStartPos, "0:00.0");
        SetResetMenu(TbEndPos,   () => _fileTotalSec > 0 ? SecsToTimestamp(_fileTotalSec) : "");
    }

    private static void SetResetMenu(TextBox tb, string defaultValue)
    {
        var cm   = new ContextMenu();
        var item = new MenuItem { Header = "初期値に戻す" };
        item.Click += (_, _) => tb.Text = defaultValue;
        cm.Items.Add(item);
        tb.ContextMenu = cm;
    }

    private static void SetResetMenu(TextBox tb, Func<string> getDefault)
    {
        var cm   = new ContextMenu();
        var item = new MenuItem { Header = "初期値に戻す" };
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
        UpdateAfterPlaybackItemStates();

        string defaultName = string.IsNullOrEmpty(_padSettings.FilePath)
            ? "" : Path.GetFileNameWithoutExtension(_padSettings.FilePath);
        TbDisplayName.Text = _padSettings.CustomLabel ?? defaultName;

        TbFilePath.Text = _padSettings.FilePath ?? "";

        TbPadGain.Text = Math.Clamp((int)Math.Round(_padSettings.PadGain * 100), 0, 500).ToString();

        TbStartPos.Text = SecsToTimestamp(_padSettings.StartPositionSec);

        if (_padSettings.EndPositionSec < 0)
            TbEndPos.Text = _fileTotalSec > 0 ? SecsToTimestamp(_fileTotalSec) : "";
        else
            TbEndPos.Text = SecsToTimestamp(_padSettings.EndPositionSec);

        if (_fileTotalSec > 0)
            LblTotalTime.Content = $"/ {SecsToTimestamp(_fileTotalSec)}  （総時間）";

        _selectedBgColor = _padSettings.PadBackgroundColor;
        BuildBgColorSwatches();
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
        { ShowError(TbPadGain, "音量: 0〜500の整数"); return; }

        CommitPosBox(TbStartPos);
        CommitPosBox(TbEndPos);

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
            { ShowError(TbEndPos, "開始位置より後の時刻を入力してください"); return; }
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

        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void CbCategory_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateAfterPlaybackItemStates();

    private void CbAfterPlayback_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

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

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not TextBox)
            FocusManager.SetFocusedElement(this, null);
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
            tb.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
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

    private static void CommitGainBox(TextBox tb)
    {
        if (TryParseGainInt(tb.Text, out int val))
            tb.Text = val.ToString();
        else
            tb.Text = "100";
    }

    private void CommitPosBox(TextBox tb)
    {
        if (string.IsNullOrWhiteSpace(tb.Text)) return;
        if (TryParseTimestampLenient(tb.Text, out float secs))
            tb.Text = SecsToTimestamp(secs);
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
        int m   = (int)(secs / 60);
        float r = secs - m * 60;
        return $"{m}:{r:00.0}";
    }

    /// <summary>
    /// タイムスタンプを寛容にパース。
    /// "12" → 12秒, "1:23" → 83秒, "1:23.5" → 83.5秒
    /// </summary>
    private static bool TryParseTimestampLenient(string s, out float secs)
    {
        secs = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();

        if (s.Contains(':'))
        {
            var parts = s.Split(':');
            if (parts.Length == 2
                && int.TryParse(parts[0].Trim(), out int m)
                && float.TryParse(parts[1].Trim(),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out float sec))
            {
                secs = m * 60 + sec;
                return true;
            }
            return false;
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
        MessageBox.Show(message, "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
        tb.Focus();
        tb.SelectAll();
    }
}
