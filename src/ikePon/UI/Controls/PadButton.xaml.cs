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
    private static readonly SolidColorBrush BrushTextNormal  = new(Color.FromRgb(0xCC, 0xCC, 0xCC));
    private static readonly SolidColorBrush BrushTextPlay    = new(Color.FromRgb(0xFF, 0xD7, 0x00));
    private static readonly SolidColorBrush BrushKeyNormal   = new(Color.FromRgb(0x66, 0x66, 0x66));
    private static readonly SolidColorBrush BrushKeyPlay     = new(Color.FromRgb(0xAA, 0x88, 0x00));
    private static readonly SolidColorBrush BrushProgress    = new(Color.FromRgb(0x3A, 0x7F, 0xC1));
    private static readonly SolidColorBrush BrushProgressPlay= new(Color.FromRgb(0xFF, 0xD7, 0x00));
    private static readonly SolidColorBrush BrushCatBgm      = new(Color.FromRgb(0x3A, 0x7F, 0xC1));
    private static readonly SolidColorBrush BrushCatSe       = new(Color.FromRgb(0x3A, 0xA0, 0x4A));
    private static readonly SolidColorBrush BrushCatMovie    = new(Color.FromRgb(0x9A, 0x4A, 0xCC));

    private PadPlayState _state = PadPlayState.Idle;
    private AudioCategory _category = AudioCategory.BGM;
    private ModifierState _modifier = ModifierState.None;
    private float _progress;
    private double _padWidth;

    public PadButton()
    {
        InitializeComponent();
        SizeChanged += (_, e) => { _padWidth = e.NewSize.Width - 8; UpdateProgress(); };
    }

    public void SetKey(string label) => KeyLabel.Text = label;

    public void UpdateState(PadPlayState state, float progress, PadSettings? settings, ModifierState modifier)
    {
        bool changed = _state != state || _modifier != modifier ||
                       (settings != null && _category != settings.Category) ||
                       Math.Abs(_progress - progress) > 0.005f;

        _state = state;
        _progress = progress;
        _modifier = modifier;

        if (settings != null)
        {
            _category = settings.Category;
            string fname = System.IO.Path.GetFileName(settings.FilePath ?? "");
            FileNameLabel.Text = string.IsNullOrEmpty(fname) ? "---" : fname;

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

        // 背景色
        BorderRoot.Background = modifier switch
        {
            ModifierState.Shift => BrushShift,
            ModifierState.Ctrl  => BrushCtrl,
            _ => playing ? BrushPadDefault : _category switch
            {
                AudioCategory.BGM   => BrushPadBgm,
                AudioCategory.SE    => BrushPadSe,
                _                   => BrushPadMovie
            }
        };

        // ボーダー色・テキスト色
        BorderRoot.BorderBrush = playing ? BrushBorderPlay : BrushBorderNormal;
        BorderRoot.BorderThickness = playing ? new Thickness(2.5) : new Thickness(1.5);
        FileNameLabel.Foreground = playing ? BrushTextPlay : BrushTextNormal;
        KeyLabel.Foreground = playing ? BrushKeyPlay : BrushKeyNormal;
        KeyBadge.BorderBrush = playing
            ? new SolidColorBrush(Color.FromRgb(0xAA, 0x88, 0x00))
            : new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x4A));
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
        ProgressBar.Width = 0;
    }
}

public enum ModifierState { None, Shift, Ctrl }
