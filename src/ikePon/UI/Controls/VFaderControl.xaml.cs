using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ikePon.UI.Controls;

public partial class VFaderControl : UserControl
{
    // 目盛り定義: (ラベル, ゲイン値) ※ -12/-18/-30 は文字重なりのため省略
    private static readonly (string label, double gain)[] ScaleMarks =
    [
        ("+6",  GAIN_MAX),
        ("0",   GAIN_ZERO),
        ("-6",  GAIN_M6),
        ("-24", GAIN_M24),
        ("-∞",  0.0)
    ];

    // ────────────────────────────────────────────────────────────
    // 非線形スケール定数（スライダー位置=0..1、ゲイン=0..GAIN_MAX）
    // 参考画像に倣い dB-linear ベースの自然な配置:
    //   pos=1.00 → +6dB  (上端)
    //   pos=0.75 → 0dB   (下から75%)
    //   pos=0.65 → -6dB
    //   pos=0.35 → -24dB
    //   pos=0.00 → -∞    (下端)
    // ────────────────────────────────────────────────────────────
    private const double POS_TOP  = 1.00;
    private const double POS_ZERO = 0.75;
    private const double POS_M6   = 0.65;
    private const double POS_M24  = 0.35;
    private const double POS_BOT  = 0.00;

    private const double GAIN_MAX  = 1.995;  // +6dB
    private const double GAIN_ZERO = 1.000;  // 0dB
    private const double GAIN_M6   = 0.501;  // -6dB
    private const double GAIN_M24  = 0.063;  // -24dB

    public const double FaderMax = GAIN_MAX;

    private const double ThumbHalf = 10.0;

    private float?[] _memories = new float?[4];
    private bool _suppressEvent;

    // アニメーション
    private readonly DispatcherTimer _animTimer;
    private double _animFrom;
    private double _animTarget;
    private double _animDuration;
    private DateTime _animStartTime;

    private static readonly SolidColorBrush BrushMemStored       = new(Color.FromRgb(0xFF, 0xAA, 0x00));
    private static readonly SolidColorBrush BrushMemEmpty        = new(Color.FromRgb(0x2E, 0x2E, 0x2E));
    private static readonly SolidColorBrush BrushMemText         = new(Color.FromRgb(0x66, 0x66, 0x66));
    private static readonly SolidColorBrush BrushMemTextMatch    = new(Colors.White);
    private static readonly SolidColorBrush BrushMemTextReg      = new(Color.FromRgb(0xFF, 0xDD, 0x44));
    private static readonly SolidColorBrush BrushMemRegistered   = new(Color.FromRgb(0x0E, 0x22, 0x3A));
    private static readonly SolidColorBrush BrushMemRedReg       = new(Color.FromRgb(0x3A, 0x0E, 0x0E));
    private static readonly SolidColorBrush BrushMemBorderYellow = new(Color.FromRgb(0xFF, 0xD7, 0x00));
    private static readonly SolidColorBrush BrushMemBorderEmpty  = new(Color.FromRgb(0x44, 0x44, 0x44));
    private static readonly SolidColorBrush BrushScaleGray       = new(Color.FromRgb(0xAA, 0xAA, 0xAA));

    public event EventHandler<double>? VolumeChanged;
    public event EventHandler<(int slot, bool quick)>? MemoryRecall;

    public string Label { get => FaderLabel.Text; set => FaderLabel.Text = value; }

    // 外部向けは常にゲイン値（0..GAIN_MAX）で受け渡し
    public double Value
    {
        get => PositionToGain(FaderSlider.Value);
        set
        {
            _suppressEvent = true;
            FaderSlider.Value = Math.Clamp(GainToPosition(value), 0, 1.0);
            _suppressEvent = false;
        }
    }

    public VFaderControl()
    {
        InitializeComponent();
        Loaded += (_, _) => DrawScale();

        _animTimer = new DispatcherTimer(DispatcherPriority.Render)
            { Interval = TimeSpan.FromMilliseconds(16) };
        _animTimer.Tick += AnimTimer_Tick;
    }

    // ------------------------------------------------------------------
    // ゲイン↔位置変換（4区間 dB-linear）
    // ------------------------------------------------------------------
    private static double PositionToGain(double pos)
    {
        pos = Math.Clamp(pos, 0, 1.0);
        if (pos <= POS_BOT) return 0.0;
        if (pos <= POS_M24)
        {
            // -∞ 〜 -24dB: ゲイン線形
            return pos / POS_M24 * GAIN_M24;
        }
        if (pos <= POS_M6)
        {
            // -24 〜 -6dB: dB線形
            double t = (pos - POS_M24) / (POS_M6 - POS_M24);
            double db = -24.0 + t * 18.0;
            return Math.Pow(10.0, db / 20.0);
        }
        if (pos <= POS_ZERO)
        {
            // -6 〜 0dB: dB線形
            double t = (pos - POS_M6) / (POS_ZERO - POS_M6);
            double db = -6.0 + t * 6.0;
            return Math.Pow(10.0, db / 20.0);
        }
        // 0 〜 +6dB: ゲイン線形
        return GAIN_ZERO + (pos - POS_ZERO) / (POS_TOP - POS_ZERO) * (GAIN_MAX - GAIN_ZERO);
    }

    private static double GainToPosition(double gain)
    {
        gain = Math.Clamp(gain, 0, GAIN_MAX);
        if (gain <= 0) return 0.0;
        if (gain <= GAIN_M24)
        {
            return gain / GAIN_M24 * POS_M24;
        }
        if (gain <= GAIN_M6)
        {
            double db = 20.0 * Math.Log10(gain);
            double t = (db - (-24.0)) / 18.0;
            return POS_M24 + t * (POS_M6 - POS_M24);
        }
        if (gain <= GAIN_ZERO)
        {
            double db = 20.0 * Math.Log10(gain);
            double t = (db - (-6.0)) / 6.0;
            return POS_M6 + t * (POS_ZERO - POS_M6);
        }
        return POS_ZERO + (gain - GAIN_ZERO) / (GAIN_MAX - GAIN_ZERO) * (POS_TOP - POS_ZERO);
    }

    // ------------------------------------------------------------------
    // アニメーション（内部は位置空間、外部はゲイン値）
    // ------------------------------------------------------------------
    public void SmoothMoveTo(double gain, double durationSecs)
    {
        _animTimer.Stop();
        double posTarget = Math.Clamp(GainToPosition(gain), 0, 1.0);
        if (durationSecs <= 0)
        {
            _suppressEvent = true;
            FaderSlider.Value = posTarget;
            _suppressEvent = false;
            VolumeChanged?.Invoke(this, PositionToGain(posTarget));
            return;
        }
        _animFrom    = FaderSlider.Value;
        _animTarget  = posTarget;
        _animDuration = durationSecs;
        _animStartTime = DateTime.UtcNow;
        _animTimer.Start();
    }

    private void AnimTimer_Tick(object? sender, EventArgs e)
    {
        double elapsed = (DateTime.UtcNow - _animStartTime).TotalSeconds;
        double t = Math.Clamp(elapsed / _animDuration, 0.0, 1.0);
        double current = _animFrom + (_animTarget - _animFrom) * t;

        _suppressEvent = true;
        FaderSlider.Value = current;
        _suppressEvent = false;
        VolumeChanged?.Invoke(this, PositionToGain(current));

        if (t >= 1.0) _animTimer.Stop();
    }

    private void FaderSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateAllMemoryButtons();
        if (!_suppressEvent)
            VolumeChanged?.Invoke(this, PositionToGain(e.NewValue));
    }

    private void FaderSlider_SizeChanged(object sender, SizeChangedEventArgs e) => DrawScale();

    // ------------------------------------------------------------------
    // 目盛り描画（非線形位置に基づく）
    // ------------------------------------------------------------------
    private void DrawScale()
    {
        ScaleCanvas.Children.Clear();
        double h = FaderSlider.ActualHeight;
        if (h < 20) return;

        double trackH   = h - ThumbHalf * 2;
        double trackTop = ThumbHalf;
        double cw = ScaleCanvas.ActualWidth > 0 ? ScaleCanvas.ActualWidth : 24;

        foreach (var (label, gain) in ScaleMarks)
        {
            double pct = 1.0 - GainToPosition(gain);
            double y   = trackTop + pct * trackH;

            bool isZero = label == "0";

            var line = new Line
            {
                X1 = cw - 10, X2 = cw - 1,
                Y1 = y, Y2 = y,
                Stroke = BrushScaleGray,
                StrokeThickness = isZero ? 1.5 : 1
            };
            ScaleCanvas.Children.Add(line);

            var tb = new TextBlock
            {
                Text = label,
                FontSize = 8,
                Foreground = BrushScaleGray
            };
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(tb, 0);
            Canvas.SetTop(tb, y - tb.DesiredSize.Height / 2);
            ScaleCanvas.Children.Add(tb);
        }
    }

    // ------------------------------------------------------------------
    // メモリ操作（ゲイン値で保存・比較）
    // ------------------------------------------------------------------
    public void StoreMemory(int slot, double gain)
    {
        if (slot < 0 || slot >= 4) return;
        _memories[slot] = (float)gain;
        UpdateMemoryButton(slot, true);
    }

    public float? GetMemory(int slot) => slot >= 0 && slot < 4 ? _memories[slot] : null;

    private void UpdateMemoryButton(int slot, bool stored, ModifierState modifier = ModifierState.None)
    {
        var btn = slot switch { 0 => Mem1, 1 => Mem2, 2 => Mem3, _ => Mem4 };
        if (!stored)
        {
            btn.Background  = BrushMemEmpty;
            btn.Foreground  = BrushMemText;
            btn.BorderBrush = BrushMemBorderEmpty;
        }
        else
        {
            float cur = (float)PositionToGain(FaderSlider.Value);
            bool matches = _memories[slot].HasValue && Math.Abs(_memories[slot]!.Value - cur) < 0.01f;
            if (matches)
            {
                btn.Background  = BrushMemStored;
                btn.Foreground  = BrushMemTextMatch;
                btn.BorderBrush = BrushMemBorderYellow;
            }
            else if (modifier == ModifierState.Shift)
            {
                btn.Background  = BrushMemRegistered;
                btn.Foreground  = BrushMemTextReg;
                btn.BorderBrush = BrushMemBorderYellow;
            }
            else
            {
                btn.Background  = BrushMemRedReg;
                btn.Foreground  = BrushMemTextReg;
                btn.BorderBrush = BrushMemBorderYellow;
            }
        }
    }

    private void UpdateAllMemoryButtons()
    {
        if (Mem1 == null) return;
        for (int i = 0; i < 4; i++)
            UpdateMemoryButton(i, _memories[i].HasValue);
    }

    public void UpdateModifierState(ModifierState modifier)
    {
        if (Mem1 == null) return;
        for (int i = 0; i < 4; i++)
            UpdateMemoryButton(i, _memories[i].HasValue, modifier);
    }

    private void Memory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        int slot = Convert.ToInt32(btn.Tag);
        bool isShift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
        if (_memories[slot].HasValue)
            MemoryRecall?.Invoke(this, (slot, !isShift));
    }

    private void Memory_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button btn) return;
        int slot = Convert.ToInt32(btn.Tag);

        var cm = new ContextMenu();
        var store = new MenuItem { Header = "登録" };
        store.Click += (_, _) => StoreMemory(slot, Value);
        cm.Items.Add(store);

        if (_memories[slot].HasValue)
        {
            cm.Items.Add(new Separator());
            var quick = new MenuItem { Header = "即座に移動" };
            var slow  = new MenuItem { Header = "ゆっくり移動" };
            quick.Click += (_, _) => MemoryRecall?.Invoke(this, (slot, true));
            slow.Click  += (_, _) => MemoryRecall?.Invoke(this, (slot, false));
            cm.Items.Add(quick);
            cm.Items.Add(slow);
        }

        cm.IsOpen = true;
        e.Handled = true;
    }
}
