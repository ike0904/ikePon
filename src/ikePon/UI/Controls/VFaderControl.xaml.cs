using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ikePon.UI.Controls;

public partial class VFaderControl : UserControl
{
    // 目盛り定義 — Logic Pro と同じ表記・間隔
    // 符号なし数字（0より上は+、下は−を省略）
    private static readonly (string label, double gain)[] ScaleMarks =
    [
        ("6",  GAIN_P6),
        ("3",  GAIN_P3),
        ("0",  GAIN_ZERO),
        ("3",  GAIN_M3),
        ("6",  GAIN_M6),
        ("10", GAIN_M10),
        ("20", GAIN_M20),
        ("30", GAIN_M30),
        ("∞",  0.0)
    ];

    // ────────────────────────────────────────────────────────────
    // Logic Pro 準拠 スケール定数
    //   pos=1.00 → +6dB  (上端)
    //   pos=0.87 → +3dB
    //   pos=0.75 → 0dB   (下から75%)
    //   pos=0.65 → -3dB
    //   pos=0.57 → -6dB
    //   pos=0.47 → -10dB
    //   pos=0.30 → -20dB
    //   pos=0.20 → -30dB
    //   pos=0.00 → -∞    (下端)
    // ────────────────────────────────────────────────────────────
    private const double POS_P6   = 1.00;
    private const double POS_P3   = 0.87;
    private const double POS_ZERO = 0.75;
    private const double POS_M3   = 0.65;
    private const double POS_M6   = 0.57;
    private const double POS_M10  = 0.47;
    private const double POS_M20  = 0.30;
    private const double POS_M30  = 0.20;

    private const double GAIN_P6   = 1.9953;  // +6dB
    private const double GAIN_P3   = 1.4125;  // +3dB
    private const double GAIN_ZERO = 1.0000;  // 0dB
    private const double GAIN_M3   = 0.7079;  // -3dB
    private const double GAIN_M6   = 0.5012;  // -6dB
    private const double GAIN_M10  = 0.3162;  // -10dB
    private const double GAIN_M20  = 0.1000;  // -20dB
    private const double GAIN_M30  = 0.0316;  // -30dB

    public const double FaderMax = GAIN_P6;

    private const double ThumbHalf = 10.0;

    private float?[] _memories = new float?[4];
    private bool _suppressEvent;
    private ModifierState _currentModifier = ModifierState.None;

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
    // 登録済みスロットへの上書きをMainWindowに確認させるイベント
    public event EventHandler<(int slot, double gain)>? MemoryRegisterRequested;

    public string Label { get => FaderLabel.Text; set => FaderLabel.Text = value; }

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
    // ゲイン↔位置変換（Logic Pro 準拠 多区間 dB-linear）
    // ------------------------------------------------------------------

    // dB 線形補間ヘルパー
    private static double DbLerp(double pos, double posLo, double posHi, double dbLo, double dbHi)
    {
        double t = (pos - posLo) / (posHi - posLo);
        return Math.Pow(10.0, (dbLo + t * (dbHi - dbLo)) / 20.0);
    }

    private static double PositionToGain(double pos)
    {
        pos = Math.Clamp(pos, 0.0, 1.0);
        if (pos >= POS_P3)   return DbLerp(pos, POS_P3,   POS_P6,   3.0,   6.0);
        if (pos >= POS_ZERO) return DbLerp(pos, POS_ZERO, POS_P3,   0.0,   3.0);
        if (pos >= POS_M3)   return DbLerp(pos, POS_M3,   POS_ZERO,-3.0,   0.0);
        if (pos >= POS_M6)   return DbLerp(pos, POS_M6,   POS_M3,  -6.0,  -3.0);
        if (pos >= POS_M10)  return DbLerp(pos, POS_M10,  POS_M6,  -10.0, -6.0);
        if (pos >= POS_M20)  return DbLerp(pos, POS_M20,  POS_M10, -20.0,-10.0);
        if (pos >= POS_M30)  return DbLerp(pos, POS_M30,  POS_M20, -30.0,-20.0);
        // -∞ 〜 -30dB: ゲイン線形
        return pos / POS_M30 * GAIN_M30;
    }

    // dB → 位置変換ヘルパー
    private static double GainToPos(double gain, double gainLo, double gainHi, double posLo, double posHi)
    {
        double db    = 20.0 * Math.Log10(gain);
        double dbLo  = 20.0 * Math.Log10(gainLo);
        double dbHi  = 20.0 * Math.Log10(gainHi);
        double t     = (db - dbLo) / (dbHi - dbLo);
        return posLo + t * (posHi - posLo);
    }

    private static double GainToPosition(double gain)
    {
        gain = Math.Clamp(gain, 0.0, GAIN_P6);
        if (gain <= 0)        return 0.0;
        if (gain <= GAIN_M30) return gain / GAIN_M30 * POS_M30;
        if (gain <= GAIN_M20) return GainToPos(gain, GAIN_M30, GAIN_M20, POS_M30, POS_M20);
        if (gain <= GAIN_M10) return GainToPos(gain, GAIN_M20, GAIN_M10, POS_M20, POS_M10);
        if (gain <= GAIN_M6)  return GainToPos(gain, GAIN_M10, GAIN_M6,  POS_M10, POS_M6);
        if (gain <= GAIN_M3)  return GainToPos(gain, GAIN_M6,  GAIN_M3,  POS_M6,  POS_M3);
        if (gain <= GAIN_ZERO)return GainToPos(gain, GAIN_M3,  GAIN_ZERO,POS_M3,  POS_ZERO);
        if (gain <= GAIN_P3)  return GainToPos(gain, GAIN_ZERO,GAIN_P3,  POS_ZERO,POS_P3);
        return GainToPos(gain, GAIN_P3, GAIN_P6, POS_P3, POS_P6);
    }

    // ------------------------------------------------------------------
    // アニメーション
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
        _animFrom     = FaderSlider.Value;
        _animTarget   = posTarget;
        _animDuration = durationSecs;
        _animStartTime = DateTime.UtcNow;
        _animTimer.Start();
    }

    private void AnimTimer_Tick(object? sender, EventArgs e)
    {
        double elapsed = (DateTime.UtcNow - _animStartTime).TotalSeconds;
        double t       = Math.Clamp(elapsed / _animDuration, 0.0, 1.0);
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
    // 目盛り描画
    // ------------------------------------------------------------------
    private void DrawScale()
    {
        ScaleCanvas.Children.Clear();
        double h = FaderSlider.ActualHeight;
        if (h < 20) return;

        double trackH   = h - ThumbHalf * 2;
        double trackTop = ThumbHalf;
        double cw       = ScaleCanvas.ActualWidth > 0 ? ScaleCanvas.ActualWidth : 24;

        foreach (var (label, gain) in ScaleMarks)
        {
            double pct = 1.0 - GainToPosition(gain);
            double y   = trackTop + pct * trackH;
            bool   isZero = label == "0";

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
    // メモリ操作
    // ------------------------------------------------------------------
    public void StoreMemory(int slot, double gain)
    {
        if (slot < 0 || slot >= 4) return;
        _memories[slot] = (float)gain;
        UpdateMemoryButton(slot, true, _currentModifier);
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
            bool match = _memories[slot].HasValue &&
                         Math.Abs((double)_memories[slot]!.Value - Value) < 0.001;
            if (match)
            {
                btn.Background  = BrushMemStored;
                btn.Foreground  = BrushMemTextMatch;
                btn.BorderBrush = BrushMemBorderYellow;
            }
            else if (modifier == ModifierState.Shift)
            {
                btn.Background  = BrushMemRegistered;
                btn.Foreground  = BrushMemTextReg;
                btn.BorderBrush = BrushMemBorderEmpty;
            }
            else
            {
                btn.Background  = BrushMemRedReg;
                btn.Foreground  = BrushMemTextReg;
                btn.BorderBrush = BrushMemBorderEmpty;
            }
        }
    }

    private void UpdateAllMemoryButtons()
    {
        if (Mem1 == null) return;
        for (int i = 0; i < 4; i++)
            UpdateMemoryButton(i, _memories[i].HasValue, _currentModifier);
    }

    public void UpdateModifierState(ModifierState modifier)
    {
        _currentModifier = modifier;
        UpdateAllMemoryButtons();
    }

    private void Memory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        int slot = Convert.ToInt32(btn.Tag);
        if (_memories[slot].HasValue)
            MemoryRecall?.Invoke(this, (slot, true));  // 登録済 → 即座に移動
        else
            StoreMemory(slot, Value);  // 空 → 現在値を登録
    }

    private void Memory_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button btn) return;
        int slot = Convert.ToInt32(btn.Tag);
        if (!_memories[slot].HasValue) { e.Handled = true; return; }

        var cm = new ContextMenu();
        var quick = new MenuItem { Header = "即座に移動" };
        var slow  = new MenuItem { Header = "ゆっくり移動" };
        var reReg = new MenuItem { Header = "再登録" };
        var del   = new MenuItem { Header = "削除" };
        quick.Click += (_, _) => MemoryRecall?.Invoke(this, (slot, true));
        slow.Click  += (_, _) => MemoryRecall?.Invoke(this, (slot, false));
        reReg.Click += (_, _) => RequestStoreMemory(slot);
        del.Click   += (_, _) => { _memories[slot] = null; UpdateMemoryButton(slot, false); };
        cm.Items.Add(quick);
        cm.Items.Add(slow);
        cm.Items.Add(reReg);
        cm.Items.Add(del);
        cm.IsOpen = true;
        e.Handled = true;
    }

    private void FaderSlider_RightClick(object sender, MouseButtonEventArgs e)
    {
        var cm = new ContextMenu();
        for (int i = 0; i < 4; i++)
        {
            int captured = i;
            var item = new MenuItem { Header = $"M{i + 1}に登録" };
            item.Click += (_, _) => RequestStoreMemory(captured);
            cm.Items.Add(item);
        }
        cm.IsOpen = true;
        e.Handled = true;
    }

    private void RequestStoreMemory(int slot)
    {
        if (_memories[slot].HasValue)
            MemoryRegisterRequested?.Invoke(this, (slot, Value));  // 上書き確認をMainWindowに依頼
        else
            StoreMemory(slot, Value);
    }
}
