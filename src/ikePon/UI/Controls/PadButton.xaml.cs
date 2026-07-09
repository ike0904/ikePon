using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ikePon.Audio;
using ikePon.Model;
using TapBehavior = ikePon.Model.TapBehavior;

namespace ikePon.UI.Controls;

public partial class PadButton : UserControl
{
    // ──────────────────────────────────────────────
    // カラー定数（参考画像に合わせたダーク系パレット）
    // ──────────────────────────────────────────────
    private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tiff", ".tif" };

    private static readonly SolidColorBrush BrushPadDefault  = new(Color.FromRgb(0x3C, 0x3C, 0x3C));
    private static readonly SolidColorBrush BrushPadBgm      = new(Color.FromRgb(0x2A, 0x38, 0x45));
    private static readonly SolidColorBrush BrushPadSe       = new(Color.FromRgb(0x2A, 0x3D, 0x2A));
    private static readonly SolidColorBrush BrushPadMovie    = new(Color.FromRgb(0x3D, 0x2A, 0x3D));
    private static readonly SolidColorBrush BrushShift       = new(Color.FromRgb(0x0E, 0x22, 0x3A));
    private static readonly SolidColorBrush BrushCtrl        = new(Color.FromRgb(0x3A, 0x0E, 0x0E));
    private static readonly SolidColorBrush BrushPause       = new(Color.FromRgb(0x0E, 0x3A, 0x0E));
    private static readonly SolidColorBrush BrushBorderNormal  = new(Color.FromRgb(0x55, 0x55, 0x55));
    private static readonly SolidColorBrush BrushBorderPlay   = new(Color.FromRgb(0xFF, 0xD7, 0x00));
    private static readonly SolidColorBrush BrushBorderMissing = new(Colors.Red);
    private static readonly SolidColorBrush BrushTextNormal  = new(Colors.White);
    private static readonly SolidColorBrush BrushTextPlay    = new(Color.FromRgb(0xFF, 0xD7, 0x00));
    private static readonly SolidColorBrush BrushKeyGray     = new(Color.FromRgb(0xAA, 0xAA, 0xAA));
    private static readonly SolidColorBrush BrushProgress    = new(Color.FromRgb(0x3A, 0x7F, 0xC1));
    private static readonly SolidColorBrush BrushProgressPlay= new(Color.FromRgb(0xFF, 0xD7, 0x00));
    private static readonly SolidColorBrush BrushCatBgm      = new(Color.FromRgb(0x3A, 0x7F, 0xC1));
    private static readonly SolidColorBrush BrushCatSe       = new(Color.FromRgb(0x3A, 0xA0, 0x4A));
    private static readonly SolidColorBrush BrushCatMovie    = new(Color.FromRgb(0x9A, 0x4A, 0xCC));
    private static readonly SolidColorBrush BrushTapCut      = new(Color.FromRgb(0xFF, 0x44, 0x44)); // 赤
    private static readonly SolidColorBrush BrushTapFade     = new(Color.FromRgb(0x44, 0x88, 0xFF)); // 青
    private static readonly SolidColorBrush BrushTapPause    = new(Color.FromRgb(0x44, 0xCC, 0x44)); // 緑

    private PadPlayState _state = PadPlayState.Idle;
    private AudioCategory _category = AudioCategory.BGM;
    private AfterPlaybackBehavior _afterPlayback = AfterPlaybackBehavior.Stop;
    private TapBehavior _tapBehavior = TapBehavior.FadeOut;
    private float _progress;
    private float _fadeGain = 1f;
    private float _totalSec;
    private float _startSec;
    private float _endSec = -1f;
    private double _padWidth;
    private bool _initialized;
    private string? _padBgColor;
    private int _padGainInt = 100;
    private bool _hasFile;
    private bool _imageDisplaying;
    private float _imageFadeGain = -1f;
    private bool _isMissing;
    private bool _blinkPhase;

    public bool CanEdit { get; set; } = true;

    // 音量ドラッグ状態
    private double _volumeDragStartY;
    private int _volumeDragStartVal;
    private bool _volumeDragging;

    // シークバードラッグ状態
    private bool _seekDragging;
    private float _seekDragFraction;

    // 現在時間ドラッグ状態
    private bool _timeDragging;
    private double _timeDragStartY;
    private float _timeDragStartVal;

    private static byte Lerp(byte a, byte b, float t)
        => (byte)Math.Clamp(a + (b - a) * t, 0, 255);

    public event EventHandler? CategoryTapped;
    public event EventHandler<AfterPlaybackBehavior>? AfterPlaybackChanged;
    public event EventHandler<TapBehavior>? TapBehaviorChanged;
    public event EventHandler<float>? SeekRequested;
    public event EventHandler<int>? PadVolumeChanged;
    public event EventHandler<float>? StartPositionChanged;

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
            if (!CanEdit || _state != PadPlayState.Idle) { e.Handled = true; return; }
            CategoryTapped?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        };
        AfterPlaybackBadge.MouseLeftButtonDown += (s, e) =>
        {
            if (!CanEdit || _state != PadPlayState.Idle) { e.Handled = true; return; }
            CycleAfterPlayback();
            e.Handled = true;
        };
        TapBehaviorBadge.MouseLeftButtonDown += (s, e) =>
        {
            if (!CanEdit || _state != PadPlayState.Idle) { e.Handled = true; return; }
            CycleTapBehavior();
            e.Handled = true;
        };
        ProgressBorder.MouseLeftButtonDown += (s, e) =>
        {
            if (_state == PadPlayState.Idle) { e.Handled = true; return; }
            _seekDragging = true;
            ProgressBorder.CaptureMouse();
            double clickX = e.GetPosition(ProgressBorder).X;
            double totalWidth = ProgressBorder.ActualWidth;
            if (totalWidth > 0)
            {
                _seekDragFraction = (float)Math.Clamp(clickX / totalWidth, 0.0, 1.0);
                ShowSeekDragDisplay();
            }
            e.Handled = true;
        };
        ProgressBorder.MouseMove += (s, e) =>
        {
            if (!_seekDragging || !ProgressBorder.IsMouseCaptured) return;
            double clickX = e.GetPosition(ProgressBorder).X;
            double totalWidth = ProgressBorder.ActualWidth;
            if (totalWidth > 0)
            {
                _seekDragFraction = (float)Math.Clamp(clickX / totalWidth, 0.0, 1.0);
                ShowSeekDragDisplay();
            }
            e.Handled = true;
        };
        ProgressBorder.MouseLeftButtonUp += (s, e) =>
        {
            if (!_seekDragging) return;
            _seekDragging = false;
            ProgressBorder.ReleaseMouseCapture();
            SeekRequested?.Invoke(this, _seekDragFraction);
            e.Handled = true;
        };
        DeadZone.MouseLeftButtonDown += (_, e) => e.Handled = true;

        // TimePanel / TimeDeadZone クリックがパッドに伝わらないよう吸収
        TimePanel.MouseLeftButtonDown    += (_, e) => e.Handled = true;
        TimeDeadZone.MouseLeftButtonDown += (_, e) => e.Handled = true;
        KeyBadge.MouseLeftButtonDown     += (_, e) => e.Handled = true;

        // 現在時間TextBoxのイベント
        CurrentTimeBox.GotFocus += (_, _) =>
        {
            if (_state != PadPlayState.Idle) return; // 再生中はドラッグ用にフォーカスを保持
            CurrentTimeBox.BorderBrush = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
            CurrentTimeBox.Text = CurrentTimeBox.Text.Trim();
            CurrentTimeBox.SelectAll();
        };
        CurrentTimeBox.LostFocus += (_, _) =>
        {
            CurrentTimeBox.BorderBrush = new SolidColorBrush(Colors.Transparent);
            CommitTimeBox();
        };
        CurrentTimeBox.KeyDown += CurrentTimeBox_KeyDown;
        CurrentTimeBox.PreviewMouseLeftButtonDown += CurrentTimeBox_MouseDown;
        CurrentTimeBox.PreviewMouseMove += CurrentTimeBox_MouseMove;
        CurrentTimeBox.PreviewMouseLeftButtonUp += CurrentTimeBox_MouseUp;
        CurrentTimeBox.PreviewMouseWheel += CurrentTimeBox_MouseWheel;

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
        VolumeLabel.Focus();
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
        // ドラッグ中もリアルタイムで音量を反映
        if (newVal != _padGainInt)
        {
            _padGainInt = newVal;
            PadVolumeChanged?.Invoke(this, newVal);
        }
        e.Handled = true;
    }

    private void VolumeLabel_MouseUp(object sender, MouseButtonEventArgs e)
    {
        VolumeLabel.ReleaseMouseCapture();
        if (_volumeDragging)
        {
            CommitVolumeLabel();
            Keyboard.ClearFocus();
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

    // ------------------------------------------------------------------
    // 現在時間TextBoxのイベントハンドラ
    // ------------------------------------------------------------------
    private void CurrentTimeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Return or Key.Enter)
        {
            CommitTimeBox();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CurrentTimeBox.Text = FormatTimeFixed(_startSec);
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void CurrentTimeBox_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _timeDragStartY = e.GetPosition(this).Y;
        _timeDragging   = false;
        if (_state != PadPlayState.Idle)
        {
            // 再生中: 現在再生位置をドラッグ基準値に設定
            float displayEnd = _endSec > 0f ? _endSec : _totalSec;
            _timeDragStartVal = displayEnd > 0f
                ? Math.Clamp(_progress * _totalSec, 0f, displayEnd)
                : 0f;
        }
        else
        {
            _timeDragStartVal = _startSec;
        }
        CurrentTimeBox.CaptureMouse();
        CurrentTimeBox.Focus();
        e.Handled = true;
    }

    private void CurrentTimeBox_MouseMove(object sender, MouseEventArgs e)
    {
        if (!CurrentTimeBox.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
        double deltaY = _timeDragStartY - e.GetPosition(this).Y;
        if (!_timeDragging && Math.Abs(deltaY) < 3) return;

        if (_state != PadPlayState.Idle)
        {
            // 再生中: シーク
            _timeDragging = true;
            bool shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
            int steps = (int)(deltaY / 5.0);
            float stepSize = shift ? 10f : 1f;
            float displayEnd = _endSec > 0f ? _endSec : _totalSec;
            float newVal = Math.Max(0f, _timeDragStartVal + steps * stepSize);
            if (displayEnd > 0f) newVal = Math.Min(newVal, displayEnd - 0.1f);
            CurrentTimeBox.Text = FormatTimeFixed(newVal);
            if (_totalSec > 0f)
                SeekRequested?.Invoke(this, newVal / _totalSec);
        }
        // アイドル時: ドラッグは何もしない（現在時間は再生開始位置と無関係）
        e.Handled = true;
    }

    private void CurrentTimeBox_MouseUp(object sender, MouseButtonEventArgs e)
    {
        CurrentTimeBox.ReleaseMouseCapture();
        if (_timeDragging)
        {
            if (_state == PadPlayState.Idle)
            {
                CommitTimeBox();
            }
            else if (_totalSec > 0f)
            {
                // 再生中: 最終シーク確定
                string raw = ToHalfWidth(CurrentTimeBox.Text).Trim();
                if (TryParseSimpleTime(raw, out float secs))
                {
                    float displayEnd = _endSec > 0f ? _endSec : _totalSec;
                    secs = Math.Clamp(secs, 0f, displayEnd > 0f ? displayEnd - 0.1f : _totalSec);
                    SeekRequested?.Invoke(this, secs / _totalSec);
                }
            }
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (_state != PadPlayState.Idle)
        {
            // 再生中の単純クリック（ドラッグなし）→ フォーカスを解除
            Keyboard.ClearFocus();
        }
        _timeDragging = false;
    }

    private void CurrentTimeBox_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        bool shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
        float step = e.Delta > 0 ? (shift ? 10f : 1f) : (shift ? -10f : -1f);
        float displayEnd = _endSec > 0f ? _endSec : _totalSec;

        if (_state != PadPlayState.Idle)
        {
            // 再生中: 現在位置を基準にホイールシーク
            float currentSec = _totalSec > 0f
                ? Math.Clamp(_progress * _totalSec, 0f, displayEnd > 0f ? displayEnd : _totalSec)
                : 0f;
            float newVal = Math.Max(0f, currentSec + step);
            if (displayEnd > 0f) newVal = Math.Min(newVal, displayEnd - 0.1f);
            CurrentTimeBox.Text = FormatTimeFixed(newVal);
            if (_totalSec > 0f)
                SeekRequested?.Invoke(this, newVal / _totalSec);
            e.Handled = true;
            return;
        }

        // アイドル時: ホイールは何もしない（現在時間は再生開始位置と無関係）
        e.Handled = true;
    }

    private void CommitTimeBox()
    {
        string raw = ToHalfWidth(CurrentTimeBox.Text).Trim();
        if (TryParseSimpleTime(raw, out float secs))
        {
            float cap = _endSec > 0f ? _endSec : _totalSec;
            secs = Math.Max(0f, secs);
            if (cap > 0f) secs = Math.Min(secs, cap - 0.1f);
            if (Math.Abs(secs - _startSec) > 0.05f)
            {
                _startSec = secs;
                StartPositionChanged?.Invoke(this, secs);
            }
        }
        CurrentTimeBox.Text = FormatTimeFixed(_startSec);
    }

    private static bool TryParseSimpleTime(string s, out float secs)
    {
        secs = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        int colonIdx = s.IndexOf(':');
        if (colonIdx >= 0)
        {
            string mStr = s[..colonIdx].Trim();
            string sStr = s[(colonIdx + 1)..].Trim();
            if (int.TryParse(mStr, out int m) && int.TryParse(sStr, out int sec))
            {
                secs = m * 60f + sec;
                return secs >= 0;
            }
            return false;
        }
        if (float.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float plain))
        {
            secs = plain;
            return secs >= 0;
        }
        return false;
    }

    // ------------------------------------------------------------------

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

    private void CommitVolumeLabel()
    {
        string raw = ToHalfWidth(VolumeLabel.Text).TrimEnd('%').Trim();
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

    private void CycleTapBehavior()
    {
        TapBehavior next = _tapBehavior switch
        {
            TapBehavior.CutOut      => TapBehavior.FadeOut,
            TapBehavior.FadeOut     => _category == AudioCategory.SE ? TapBehavior.CutOut : TapBehavior.PauseResume,
            TapBehavior.PauseResume => TapBehavior.CutOut,
            _                       => TapBehavior.FadeOut
        };
        _tapBehavior = next;
        UpdateTapBehaviorIcon();
        TapBehaviorChanged?.Invoke(this, next);
    }

    private void UpdateTapBehaviorIcon()
    {
        TapBehaviorDot.Fill = _tapBehavior switch
        {
            TapBehavior.CutOut      => BrushTapCut,
            TapBehavior.PauseResume => BrushTapPause,
            _                       => BrushTapFade
        };
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

    public void UpdateState(PadPlayState state, float progress, PadSettings? settings, float fadeGain = 1f, float totalSec = 0f, bool imageDisplaying = false, float imageFadeGain = -1f, bool isMissing = false)
    {
        int newGainInt = settings != null ? Math.Clamp((int)Math.Round(settings.PadGain * 100), 0, 500) : 100;
        bool fileExists = settings != null && !string.IsNullOrEmpty(settings.FilePath);
        float newStartSec = settings?.StartPositionSec ?? 0f;
        float newEndSec   = settings?.EndPositionSec   ?? -1f;
        TapBehavior newTap = settings?.TapBehavior ?? TapBehavior.FadeOut;
        bool newBlinkPhase = (Environment.TickCount64 / 500) % 2 == 0;
        bool changed = !_initialized ||
                       _state != state ||
                       _tapBehavior != newTap ||
                       _imageDisplaying != imageDisplaying ||
                       Math.Abs(_imageFadeGain - imageFadeGain) > 0.01f ||
                       (settings != null && _category != settings.Category) ||
                       (settings != null && _afterPlayback != settings.AfterPlayback) ||
                       Math.Abs(_progress - progress) > 0.001f ||
                       Math.Abs(_totalSec - totalSec) > 0.5f ||
                       Math.Abs(_startSec - newStartSec) > 0.05f ||
                       Math.Abs(_endSec   - newEndSec)   > 0.05f ||
                       (state == PadPlayState.FadingOut && Math.Abs(_fadeGain - fadeGain) > 0.01f) ||
                       _isMissing != isMissing ||
                       (isMissing && _blinkPhase != newBlinkPhase);
        _initialized = true;

        _state = state;
        _tapBehavior = newTap;
        _fadeGain = fadeGain;
        _imageDisplaying = imageDisplaying;
        _imageFadeGain = imageFadeGain;
        _isMissing = isMissing;
        _blinkPhase = newBlinkPhase;

        if (settings != null)
        {
            _category = settings.Category;
            _afterPlayback = settings.AfterPlayback;
            string label = settings.CustomLabel
                ?? System.IO.Path.GetFileNameWithoutExtension(settings.FilePath ?? "");
            string displayName = string.IsNullOrEmpty(label) ? "---" : label;
            FileNameLabel.Text = isMissing ? $"[未リンク] {displayName}" : displayName;

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
                DeadZone.Background = ContentBg.Background;
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
        _startSec  = newStartSec;
        _endSec    = newEndSec;

        bool playing = state != PadPlayState.Idle;

        // デッドゾーンはパッド背景色と同一（TapBehavior表示はカテゴリ右の●アイコンに移行）
        DeadZone.Background = ContentBg.Background;

        // ボーダー色・テキスト色（ショートカットキーは状態によらず常時グレー）
        if (isMissing)
        {
            // 未リンク: 赤点滅
            BorderRoot.BorderBrush     = newBlinkPhase ? BrushBorderMissing : BrushBorderNormal;
            BorderRoot.BorderThickness = newBlinkPhase ? new Thickness(2.5) : new Thickness(1.5);
            FileNameLabel.Foreground   = BrushTextNormal;
            KeyLabel.Foreground        = BrushKeyGray;
            KeyBadge.BorderBrush       = BrushKeyGray;
        }
        else if (imageFadeGain >= 0f)
        {
            // 静止画フェードアウト中: 黄色→グレー補間
            float g = Math.Clamp(imageFadeGain, 0f, 1f);
            byte br = Lerp(0x55, 0xFF, g); byte bg2 = Lerp(0x55, 0xD7, g); byte bb = Lerp(0x55, 0x00, g);
            BorderRoot.BorderBrush = new SolidColorBrush(Color.FromRgb(br, bg2, bb));
            BorderRoot.BorderThickness = new Thickness(2.5);
            FileNameLabel.Foreground = BrushTextNormal;
            KeyLabel.Foreground  = BrushKeyGray;
            KeyBadge.BorderBrush = BrushKeyGray;
        }
        else if (imageDisplaying)
        {
            // 静止画表示中: 黄色ボーダー
            BorderRoot.BorderBrush = BrushBorderPlay;
            BorderRoot.BorderThickness = new Thickness(2.5);
            FileNameLabel.Foreground = BrushTextNormal;
            KeyLabel.Foreground  = BrushKeyGray;
            KeyBadge.BorderBrush = BrushKeyGray;
        }
        else if (state == PadPlayState.FadingOut)
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
        UpdateTapBehaviorIcon();
        UpdateTimeLabel(state, progress, totalSec, newStartSec, newEndSec);
    }

    private void ShowSeekDragDisplay()
    {
        float displayEnd = _endSec > 0f ? _endSec : _totalSec;
        if (displayEnd <= 0f) return;
        float currentSec = Math.Clamp(_seekDragFraction * _totalSec, 0f, displayEnd);
        if (!CurrentTimeBox.IsFocused)
            CurrentTimeBox.Text = FormatTimeFixed(currentSec);
        double w = _padWidth * Math.Clamp(_seekDragFraction, 0f, 1f);
        ProgressBar.Width = double.IsNaN(w) || w < 0 ? 0 : w;
    }

    private static string FormatTimeFixed(float secs)
    {
        if (secs < 0) secs = 0;
        int totalSec = (int)Math.Round(secs);
        int m = totalSec / 60;
        int s = totalSec % 60;
        return m < 10 ? $" {m}:{s:00}" : $"{m}:{s:00}";
    }

    private void UpdateTimeLabel(PadPlayState state, float progress, float totalSec, float startSec, float endSec)
    {
        if (_seekDragging) return;
        float displayEnd = endSec > 0f ? endSec : totalSec;
        if (displayEnd <= 0f)
        {
            TimePanel.Visibility = Visibility.Collapsed;
            return;
        }

        TimePanel.Visibility = Visibility.Visible;
        float displayCurrent = state == PadPlayState.Idle
            ? startSec
            : Math.Clamp(progress * totalSec, 0f, displayEnd);

        if (!CurrentTimeBox.IsFocused)
            CurrentTimeBox.Text = FormatTimeFixed(displayCurrent);
        TotalTimeLabel.Text = "/" + FormatTimeFixed(displayEnd);

        bool idle = state == PadPlayState.Idle;
        CurrentTimeBox.IsReadOnly = !idle; // テキスト直接入力はアイドル時のみ
        CurrentTimeBox.Cursor = idle ? Cursors.IBeam : Cursors.SizeNS;
    }

    private void UpdateProgress()
    {
        if (_seekDragging) return;
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
        TimePanel.Visibility   = Visibility.Collapsed;
    }
}

