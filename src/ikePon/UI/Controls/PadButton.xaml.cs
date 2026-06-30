using System.Windows;
using System.Windows.Controls;
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
    private static readonly SolidColorBrush BrushTextPlay    = new(Colors.White);
    private static readonly SolidColorBrush BrushKeyGray     = new(Color.FromRgb(0xAA, 0xAA, 0xAA));
    private static readonly SolidColorBrush BrushProgress    = new(Color.FromRgb(0x3A, 0x7F, 0xC1));
    private static readonly SolidColorBrush BrushProgressPlay= new(Color.FromRgb(0xFF, 0xD7, 0x00));
    private static readonly SolidColorBrush BrushCatBgm      = new(Color.FromRgb(0x3A, 0x7F, 0xC1));
    private static readonly SolidColorBrush BrushCatSe       = new(Color.FromRgb(0x3A, 0xA0, 0x4A));
    private static readonly SolidColorBrush BrushCatMovie    = new(Color.FromRgb(0x9A, 0x4A, 0xCC));

    private PadPlayState _state = PadPlayState.Idle;
    private AudioCategory _category = AudioCategory.BGM;
    private ModifierState _modifier = ModifierState.None;
    private float _progress;
    private float _fadeGain = 1f;
    private double _padWidth;

    private static byte Lerp(byte a, byte b, float t)
        => (byte)Math.Clamp(a + (b - a) * t, 0, 255);

    public PadButton()
    {
        InitializeComponent();
        SizeChanged += (_, e) => { _padWidth = e.NewSize.Width - 8; UpdateProgress(); };
    }

    public void SetKey(string label) => KeyLabel.Text = label;

    public void UpdateState(PadPlayState state, float progress, PadSettings? settings, ModifierState modifier, float fadeGain = 1f)
    {
        bool changed = _state != state || _modifier != modifier ||
                       (settings != null && _category != settings.Category) ||
                       Math.Abs(_progress - progress) > 0.005f ||
                       (state == PadPlayState.FadingOut && Math.Abs(_fadeGain - fadeGain) > 0.01f);

        _state = state;
        _progress = progress;
        _modifier = modifier;
        _fadeGain = fadeGain;

        if (settings != null)
        {
            _category = settings.Category;
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
        }
        else
        {
            FileNameLabel.Text = "---";
        }

        if (!changed) return;

        bool playing = state != PadPlayState.Idle;

        // 背景色 — SHIFT/CTRL: 再生中のパッドのみ色変更
        SolidColorBrush catBrush = _category switch
        {
            AudioCategory.BGM   => BrushPadBgm,
            AudioCategory.SE    => BrushPadSe,
            _                   => BrushPadMovie
        };
        BorderRoot.Background = modifier switch
        {
            ModifierState.Shift => playing ? BrushShift : catBrush,
            ModifierState.Ctrl  => playing ? BrushCtrl  : catBrush,
            _ => playing ? BrushPadDefault : catBrush
        };

        // ボーダー色・テキスト色（ショートカットキーは状態によらず常時グレー）
        if (state == PadPlayState.FadingOut)
        {
            float g = Math.Clamp(fadeGain, 0f, 1f);
            byte br = Lerp(0x55, 0xFF, g); byte bg2 = Lerp(0x55, 0xD7, g); byte bb = Lerp(0x55, 0x00, g);
            byte tr = Lerp(0xFF, 0xFF, g); byte tg2 = Lerp(0xFF, 0xD7, g); byte tb2 = Lerp(0xFF, 0x00, g);
            BorderRoot.BorderBrush = new SolidColorBrush(Color.FromRgb(br, bg2, bb));
            BorderRoot.BorderThickness = new Thickness(2.5);
            FileNameLabel.Foreground = new SolidColorBrush(Color.FromRgb(tr, tg2, tb2));
            KeyLabel.Foreground  = BrushKeyGray;
            KeyBadge.BorderBrush = BrushKeyGray;
        }
        else
        {
            BorderRoot.BorderBrush = playing ? BrushBorderPlay : BrushBorderNormal;
            BorderRoot.BorderThickness = playing ? new Thickness(2.5) : new Thickness(1.5);
            FileNameLabel.Foreground = playing ? BrushTextPlay : BrushTextNormal;
            KeyLabel.Foreground  = BrushKeyGray;
            KeyBadge.BorderBrush = BrushKeyGray;
        }
        ProgressBar.Fill = playing ? BrushProgressPlay : BrushProgress;

        UpdateProgress();
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
        BorderRoot.Background = BrushPadDefault;
        BorderRoot.BorderBrush = BrushBorderNormal;
        BorderRoot.BorderThickness = new Thickness(1.5);
        FileNameLabel.Text = "---";
        FileNameLabel.Foreground = BrushTextNormal;
        KeyLabel.Foreground  = BrushKeyGray;
        KeyBadge.BorderBrush = BrushKeyGray;
        ProgressBar.Width = 0;
    }
}

public enum ModifierState { None, Shift, Ctrl }
