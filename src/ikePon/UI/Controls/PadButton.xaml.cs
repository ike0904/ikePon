using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ikePon.Audio;
using ikePon.Model;

namespace ikePon.UI.Controls;

public partial class PadButton : UserControl
{
    // ──────────────────────────────────────────────
    // カラー定数（参考画像に合わせたダーク系パレット）
    // ──────────────────────────────────────────────
    private static readonly SolidColorBrush BrushPadDefault  = new(Color.FromRgb(0x3C, 0x3C, 0x3C));
    private static readonly SolidColorBrush BrushPadBgm      = new(Color.FromRgb(0x2A, 0x38, 0x45));
    private static readonly SolidColorBrush BrushPadSe       = new(Color.FromRgb(0x2A, 0x3D, 0x2A));
    private static readonly SolidColorBrush BrushPadMovie    = new(Color.FromRgb(0x3D, 0x2A, 0x3D));
    private static readonly SolidColorBrush BrushShift       = new(Color.FromRgb(0x0E, 0x22, 0x3A));
    private static readonly SolidColorBrush BrushCtrl        = new(Color.FromRgb(0x3A, 0x0E, 0x0E));
    private static readonly SolidColorBrush BrushBorderNormal = new(Color.FromRgb(0x55, 0x55, 0x55));
    private static readonly SolidColorBrush BrushBorderPlay  = new(Color.FromRgb(0xFF, 0xD7, 0x00));
    private static readonly SolidColorBrush BrushTextNormal  = new(Colors.White);
    private static readonly SolidColorBrush BrushTextPlay    = new(Color.FromRgb(0xFF, 0xD7, 0x00));
    private static readonly SolidColorBrush BrushKeyGray     = new(Color.FromRgb(0xAA, 0xAA, 0xAA));
    private static readonly SolidColorBrush BrushProgress    = new(Color.FromRgb(0x3A, 0x7F, 0xC1));
    private static readonly SolidColorBrush BrushProgressPlay= new(Color.FromRgb(0xFF, 0xD7, 0x00));
    private static readonly SolidColorBrush BrushCatBgm      = new(Color.FromRgb(0x3A, 0x7F, 0xC1));
    private static readonly SolidColorBrush BrushCatSe       = new(Color.FromRgb(0x3A, 0xA0, 0x4A));
    private static readonly SolidColorBrush BrushCatMovie    = new(Color.FromRgb(0x9A, 0x4A, 0xCC));

    private PadPlayState _state = PadPlayState.Idle;
    private AudioCategory _category = AudioCategory.BGM;
    private AfterPlaybackBehavior _afterPlayback = AfterPlaybackBehavior.Stop;
    private ModifierState _modifier = ModifierState.None;
    private float _progress;
    private float _fadeGain = 1f;
    private float _totalSec;
    private double _padWidth;
    private bool _initialized;
    private string? _padBgColor;
    private int _padGainInt = 100;
    private bool _hasFile;

    // 音量ドラッグ状態
    private double _volumeDragStartY;
    private int _volumeDragStartVal;
    private bool _volumeDragging;

    private static byte Lerp(byte a, byte b, float t)
        => (byte)Math.Clamp(a + (b - a) * t, 0, 255);

    public event EventHandler? CategoryTapped;
    public event EventHandler<AfterPlaybackBehavior>? AfterPlaybackChanged;
    public event EventHandler<float>? SeekRequested;
    public event EventHandler<int>? PadVolumeChanged;

    public PadButton()
    {
        InitializeComponent();
        SizeChanged += (_, e) => { _padWidth = e.NewSize.Width - 8; UpdateProgress(); };
        ContentBg.Background = BrushPadDefault;
        DeadZone.Background = BrushPadDefault;

        // 上部エリア全体でON/OFFを防ぐ
        TopArea.MouseLeftButtonDown += (_, e) => e.Handled = true;

        CategoryBadge.MouseLeftButtonDown += (s, e) =>
        {
            if (_state != PadPlayState.Idle) { e.Handled = true; return; }
            CategoryTapped?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        };
        AfterPlaybackBadge.MouseLeftButtonDown += (s, e) =>
        {
            CycleAfterPlayback();
            e.Handled = true;
        };
        ProgressBorder.MouseLeftButtonDown += (s, e) =>
        {
            if (_state == PadPlayState.Idle) { e.Handled = true; return; }
            double clickX = e.GetPosition(ProgressBorder).X;
            double totalWidth = ProgressBorder.ActualWidth;
            if (totalWidth > 0)
            {
                float fraction = (float)Math.Clamp(clickX / totalWidth, 0.0, 1.0);
                SeekRequested?.Invoke(this, fraction);
            }
            e.Handled = true;
        };
        DeadZone.MouseLeftButtonDown += (_, e) => e.Handled = true;

        // 音量TextBoxのイベント
        VolumeLabel.GotFocus += (_, _) =>
        {
            VolumeLabel.BorderBrush = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
            // フォーカス時は % を除去して数値のみ編集
            string raw = VolumeLabel.Text.TrimEnd('%').Trim();
            VolumeLabel.Text = raw;
            VolumeLabel.SelectAll();
        };
        VolumeLabel.LostFocus += VolumeLabel_LostFocus;
        VolumeLabel.KeyDown += VolumeLabel_KeyDown;
        VolumeLabel.PreviewMouseLeftButtonDown += VolumeLabel_MouseDown;
        VolumeLabel.PreviewMouseMove += VolumeLabel_MouseMove;
        VolumeLabel.PreviewMouseLeftButtonUp += VolumeLabel_MouseUp;
        VolumeLabel.PreviewMouseWheel += VolumeLabel_MouseWheel;
    }

    // ------------------------------------------------------------------
    // 音量TextBoxのイベントハンドラ
    // ------------------------------------------------------------------
    private void VolumeLabel_LostFocus(object sender, RoutedEventArgs e)
    {
        VolumeLabel.BorderBrush = new SolidColorBrush(Colors.Transparent);
        CommitVolumeLabel();
    }

    private void VolumeLabel_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Return or Key.Enter)
        {
            CommitVolumeLabel();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            VolumeLabel.Text = _padGainInt.ToString();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void VolumeLabel_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _volumeDragStartY = e.GetPosition(this).Y;
        _volumeDragging = false;
        if (int.TryParse(VolumeLabel.Text, out int v))
            _volumeDragStartVal = v;
        else
            _volumeDragStartVal = _padGainInt;
        VolumeLabel.CaptureMouse();
        e.Handled = true;
    }

    private void VolumeLabel_MouseMove(object sender, MouseEventArgs e)
    {
        if (!VolumeLabel.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
        double deltaY = _volumeDragStartY - e.GetPosition(this).Y;
        if (!_volumeDragging && Math.Abs(deltaY) < 3) return;
        _volumeDragging = true;
        bool shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
        int steps = (int)(deltaY / 5.0);
        int stepSize = shift ? 10 : 1;
        int newVal = Math.Clamp(_volumeDragStartVal + steps * stepSize, 0, 500);
        VolumeLabel.Text = newVal.ToString();
        e.Handled = true;
    }

    private void VolumeLabel_MouseUp(object sender, MouseButtonEventArgs e)
    {
        VolumeLabel.ReleaseMouseCapture();
        if (_volumeDragging)
        {
            CommitVolumeLabel();
            e.Handled = true;
        }
        _volumeDragging = false;
    }

    private void VolumeLabel_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        bool shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
        int step = e.Delta > 0 ? (shift ? 10 : 1) : (shift ? -10 : -1);
        string raw = VolumeLabel.Text.TrimEnd('%').Trim();
        if (!int.TryParse(raw, out int val)) val = _padGainInt;
        int newVal = Math.Clamp(val + step, 0, 500);
        VolumeLabel.Text = newVal.ToString() + "%";
        CommitVolumeLabel();
        e.Handled = true;
    }

    private void CommitVolumeLabel()
    {
        string raw = VolumeLabel.Text.TrimEnd('%').Trim();
        if (!int.TryParse(raw, out int val) || val < 0 || val > 500)
            val = _padGainInt;
        val = Math.Clamp(val, 0, 500);
        VolumeLabel.Text = val.ToString() + "%";
        if (val != _padGainInt)
        {
            _padGainInt = val;
            PadVolumeChanged?.Invoke(this, val);
        }
    }

    // ------------------------------------------------------------------

    private void CycleAfterPlayback()
    {
        _afterPlayback = GetNextAfterPlayback(_afterPlayback);
        UpdateAfterPlaybackIcon();
        AfterPlaybackChanged?.Invoke(this, _afterPlayback);
    }

    private AfterPlaybackBehavior GetNextAfterPlayback(AfterPlaybackBehavior current)
    {
        var next = current switch
        {
            AfterPlaybackBehavior.Stop            => AfterPlaybackBehavior.FreezeLastFrame,
            AfterPlaybackBehavior.FreezeLastFrame => AfterPlaybackBehavior.Loop,
            AfterPlaybackBehavior.Loop            => AfterPlaybackBehavior.Stop,
            _                                     => AfterPlaybackBehavior.Stop
        };
        if (next == AfterPlaybackBehavior.FreezeLastFrame && _category != AudioCategory.Movie)
            return GetNextAfterPlayback(next);
        if (next == AfterPlaybackBehavior.Loop && _category == AudioCategory.SE)
            return GetNextAfterPlayback(next);
        return next;
    }

    private void UpdateAfterPlaybackIcon()
    {
        IconStop.Visibility   = Visibility.Collapsed;
        IconFreeze.Visibility = Visibility.Collapsed;
        IconLoop.Visibility   = Visibility.Collapsed;
        switch (_afterPlayback)
        {
            case AfterPlaybackBehavior.FreezeLastFrame:
                IconFreeze.Visibility = Visibility.Visible; break;
            case AfterPlaybackBehavior.Loop:
                IconLoop.Visibility = Visibility.Visible; break;
            default:
                IconStop.Visibility = Visibility.Visible; break;
        }
    }

    public void SetKey(string label) => KeyLabel.Text = label;

    public void UpdateState(PadPlayState state, float progress, PadSettings? settings, ModifierState modifier, float fadeGain = 1f, float totalSec = 0f)
    {
        // modifier変化はIdle以外の時のみ再描画トリガー（全Idleパッドの同時更新でレイアウトが揺れるのを防ぐ）
        bool modifierAffectsVisual = state != PadPlayState.Idle || _state != PadPlayState.Idle;
        int newGainInt = settings != null ? Math.Clamp((int)Math.Round(settings.PadGain * 100), 0, 500) : 100;
        bool fileExists = settings != null && !string.IsNullOrEmpty(settings.FilePath);
        bool changed = !_initialized ||
                       _state != state ||
                       (modifierAffectsVisual && _modifier != modifier) ||
                       (settings != null && _category != settings.Category) ||
                       (settings != null && _afterPlayback != settings.AfterPlayback) ||
                       Math.Abs(_progress - progress) > 0.005f ||
                       Math.Abs(_totalSec - totalSec) > 0.5f ||
                       (state == PadPlayState.FadingOut && Math.Abs(_fadeGain - fadeGain) > 0.01f);
        _initialized = true;

        _state = state;
        _modifier = modifier;
        _fadeGain = fadeGain;

        if (settings != null)
        {
            _category = settings.Category;
            _afterPlayback = settings.AfterPlayback;
            string label = settings.CustomLabel
                ?? System.IO.Path.GetFileNameWithoutExtension(settings.FilePath ?? "");
            FileNameLabel.Text = string.IsNullOrEmpty(label) ? "---" : label;

            CategoryLabel.Text = settings.Category switch
            {
                AudioCategory.BGM   => "BGM",
                AudioCategory.SE    => "SE",
                _                   => "MOV"
            };
            CategoryLabel.Foreground = settings.Category switch
            {
                AudioCategory.BGM   => BrushCatBgm,
                AudioCategory.SE    => BrushCatSe,
                _                   => BrushCatMovie
            };

            if (_padBgColor != settings.PadBackgroundColor)
            {
                _padBgColor = settings.PadBackgroundColor;
                ContentBg.Background = string.IsNullOrEmpty(_padBgColor)
                    ? BrushPadDefault
                    : new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString(_padBgColor));
            }

            // 音量表示更新（フォーカス中は上書きしない）
            if (!VolumeLabel.IsFocused)
            {
                if (_hasFile != fileExists || _padGainInt != newGainInt)
                {
                    _hasFile = fileExists;
                    _padGainInt = newGainInt;
                    VolumeLabel.Text = _padGainInt.ToString() + "%";
                }
            }
            VolumeLabel.Visibility = fileExists ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            FileNameLabel.Text = "---";
            _hasFile = false;
            VolumeLabel.Visibility = Visibility.Collapsed;
        }

        if (!changed) return;
        _progress  = progress;
        _totalSec  = totalSec;

        bool playing = state != PadPlayState.Idle;

        // デッドゾーン背景色（状態インジケーター）— フェードアウト中は即グレー
        // BorderRootは常にデフォルト色（ユーザーがカスタマイズできるよう固定）
        bool isMovieBgm = _category == AudioCategory.Movie || _category == AudioCategory.BGM;
        DeadZone.Background = (state == PadPlayState.FadingOut) ? BrushPadDefault :
            modifier switch
            {
                ModifierState.Shift => playing ? BrushShift    : BrushPadDefault,
                ModifierState.Ctrl  => playing ? BrushCtrl     : BrushPadDefault,
                _                   => (playing && isMovieBgm) ? BrushShift : BrushPadDefault
            };

        // ボーダー色・テキスト色（ショートカットキーは状態によらず常時グレー）
        if (state == PadPlayState.FadingOut)
        {
            float g = Math.Clamp(fadeGain, 0f, 1f);
            byte br = Lerp(0x55, 0xFF, g); byte bg2 = Lerp(0x55, 0xD7, g); byte bb = Lerp(0x55, 0x00, g);
            BorderRoot.BorderBrush = new SolidColorBrush(Color.FromRgb(br, bg2, bb));
            BorderRoot.BorderThickness = new Thickness(2.5);
            FileNameLabel.Foreground = BrushTextNormal;
            KeyLabel.Foreground  = BrushKeyGray;
            KeyBadge.BorderBrush = BrushKeyGray;
        }
        else
        {
            BorderRoot.BorderBrush = playing ? BrushBorderPlay : BrushBorderNormal;
            BorderRoot.BorderThickness = playing ? new Thickness(2.5) : new Thickness(1.5);
            FileNameLabel.Foreground = BrushTextNormal;
            KeyLabel.Foreground  = BrushKeyGray;
            KeyBadge.BorderBrush = BrushKeyGray;
        }
        ProgressBar.Fill    = playing ? BrushProgressPlay : BrushProgress;
        ProgressBar.Opacity = (state == PadPlayState.FadingOut) ? Math.Clamp(fadeGain, 0f, 1f) : 1.0;

        UpdateProgress();
        UpdateAfterPlaybackIcon();
        UpdateTimeLabel(state, progress, totalSec);
    }

    private static string FormatTime(float secs)
    {
        int m = (int)(secs / 60);
        int s = (int)(secs % 60);
        return $"{m}:{s:00}";
    }

    private void UpdateTimeLabel(PadPlayState state, float progress, float totalSec)
    {
        if (totalSec <= 0f)
        {
            TimeLabel.Text = "";
            return;
        }
        if (state == PadPlayState.Idle)
        {
            TimeLabel.Text = FormatTime(totalSec);
        }
        else
        {
            float currentSec = Math.Clamp(progress * totalSec, 0f, totalSec);
            TimeLabel.Text = $"{FormatTime(currentSec)}/{FormatTime(totalSec)}";
        }
    }

    private void UpdateProgress()
    {
        double w = _padWidth * Math.Clamp(_progress, 0f, 1f);
        ProgressBar.Width = double.IsNaN(w) || w < 0 ? 0 : w;
    }

    public void ClearState()
    {
        _state = PadPlayState.Idle;
        _progress = 0f;
        _padBgColor = null;
        _hasFile = false;
        ContentBg.Background = BrushPadDefault;
        DeadZone.Background = BrushPadDefault;
        BorderRoot.BorderBrush = BrushBorderNormal;
        BorderRoot.BorderThickness = new Thickness(1.5);
        FileNameLabel.Text = "---";
        FileNameLabel.Foreground = BrushTextNormal;
        KeyLabel.Foreground  = BrushKeyGray;
        KeyBadge.BorderBrush = BrushKeyGray;
        ProgressBar.Width   = 0;
        ProgressBar.Opacity = 1.0;
        VolumeLabel.Visibility = Visibility.Collapsed;
    }
}

public enum ModifierState { None, Shift, Ctrl }
