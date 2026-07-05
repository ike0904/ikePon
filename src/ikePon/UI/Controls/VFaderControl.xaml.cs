using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ikePon.UI.Controls;

public partial class VFaderControl : UserControl
{
    // 0-127 linear scale: 96 = unity gain (±0dB)
    private const double SLIDER_MAX  = 127.0;
    private const double SLIDER_ZERO = 96.0;   // ±0dB position

    public const double FaderMax = SLIDER_MAX / SLIDER_ZERO;  // ≈1.323

    private const double ThumbHalf = 10.0;

    private float?[] _memories = new float?[4]; // slot 3 は未使用（MU button に変更）
    private bool _suppressEvent;
    private bool _isMuted;

    // アニメーション
    private readonly DispatcherTimer _animTimer;
    private double _animFrom;
    private double _animTarget;
    private double _animDuration;
    private DateTime _animStartTime;

    private static readonly SolidColorBrush BrushMemStored       = new(Color.FromRgb(0xFF, 0xAA, 0x00));
    private static readonly SolidColorBrush BrushMemEmpty        = new(Color.FromRgb(0x2E, 0x2E, 0x2E));
    private static readonly SolidColorBrush BrushMemText         = new(Color.FromRgb(0x66, 0x66, 0x66));
    private static readonly SolidColorBrush BrushMuteInactive    = new(Color.FromRgb(0x3A, 0x0E, 0x0E)); // CutOut と同じ暗赤
    private static readonly SolidColorBrush BrushMuteInactiveBd  = new(Color.FromRgb(0x7A, 0x2A, 0x2A));
    private static readonly SolidColorBrush BrushMemTextMatch    = new(Colors.White);
    private static readonly SolidColorBrush BrushMemTextReg      = new(Colors.White);
    private static readonly SolidColorBrush BrushMemRegistered   = new(Color.FromRgb(0x0E, 0x22, 0x3A)); // 青
    private static readonly SolidColorBrush BrushMemBorderYellow = new(Color.FromRgb(0xFF, 0xD7, 0x00));
    private static readonly SolidColorBrush BrushMemBorderEmpty  = new(Color.FromRgb(0x44, 0x44, 0x44));
    private static readonly SolidColorBrush BrushScaleGray       = new(Color.FromRgb(0xAA, 0xAA, 0xAA));
    private static readonly SolidColorBrush BrushScaleWhite      = new(Color.FromRgb(0xFF, 0xFF, 0xFF));

    public event EventHandler<double>? VolumeChanged;
    public event EventHandler<(int slot, bool quick)>? MemoryRecall;
    public event EventHandler<(int slot, double gain)>? MemoryRegisterRequested;
    public event EventHandler<bool>? MuteChanged;

    public string Label { get => FaderLabel.Text; set => FaderLabel.Text = value; }
    public System.Windows.Media.Brush LabelBrush { get => FaderLabel.Foreground; set => FaderLabel.Foreground = value; }

    public double Value
    {
        get => SliderToGain(FaderSlider.Value);
        set
        {
            _suppressEvent = true;
            FaderSlider.Value = Math.Clamp(GainToSlider(value), 0, SLIDER_MAX);
            _suppressEvent = false;
        }
    }

    public VFaderControl()
    {
        InitializeComponent();
        _animTimer = new DispatcherTimer(DispatcherPriority.Render)
            { Interval = TimeSpan.FromMilliseconds(16) };
        _animTimer.Tick += AnimTimer_Tick;
        Loaded += (_, _) => { DrawScale(); UpdateMuteButton(); };
    }

    // ------------------------------------------------------------------
    // ゲイン↔スライダー値変換（0-127 linear: 96=1.0）
    // ------------------------------------------------------------------
    private static double SliderToGain(double sliderVal) => sliderVal / SLIDER_ZERO;
    private static double GainToSlider(double gain) => Math.Clamp(gain * SLIDER_ZERO, 0.0, SLIDER_MAX);

    // ------------------------------------------------------------------
    // アニメーション
    // ------------------------------------------------------------------
    public void SmoothMoveTo(double gain, double durationSecs)
    {
        _animTimer.Stop();
        double sliderTarget = Math.Clamp(GainToSlider(gain), 0, SLIDER_MAX);
        if (durationSecs <= 0)
        {
            _suppressEvent = true;
            FaderSlider.Value = sliderTarget;
            _suppressEvent = false;
            VolumeChanged?.Invoke(this, SliderToGain(sliderTarget));
            return;
        }
        _animFrom     = FaderSlider.Value;
        _animTarget   = sliderTarget;
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
        VolumeChanged?.Invoke(this, SliderToGain(current));

        if (t >= 1.0) _animTimer.Stop();
    }

    private void FaderSlider_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        bool shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
        double step = e.Delta > 0 ? FaderSlider.SmallChange : -FaderSlider.SmallChange;
        if (shift) step *= 8;
        FaderSlider.Value = Math.Clamp(FaderSlider.Value + step, 0, SLIDER_MAX);
        e.Handled = true;
    }

    private void FaderSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateAllMemoryButtons();
        if (!_suppressEvent)
            VolumeChanged?.Invoke(this, SliderToGain(e.NewValue));
    }

    private void FaderSlider_SizeChanged(object sender, SizeChangedEventArgs e) => DrawScale();

    // ------------------------------------------------------------------
    // 目盛り描画（0-127 等間隔、96位置は2倍太く白）
    // ------------------------------------------------------------------
    private static readonly int[] ScaleMarkValues = [0, 8, 16, 24, 32, 40, 48, 56, 64, 72, 80, 88, 96, 104, 112, 120, 127];

    private void DrawScale()
    {
        ScaleCanvas.Children.Clear();
        double h = FaderSlider.ActualHeight;
        if (h < 20) return;

        double trackH   = h - ThumbHalf * 2;
        double trackTop = ThumbHalf;
        double cw       = ScaleCanvas.ActualWidth > 0 ? ScaleCanvas.ActualWidth : 24;

        foreach (int m in ScaleMarkValues)
        {
            bool isZero = (m == (int)SLIDER_ZERO);
            double pct  = 1.0 - (m / SLIDER_MAX);
            double y    = trackTop + pct * trackH;

            var line = new Line
            {
                X1 = cw - 10, X2 = cw - 1,
                Y1 = y, Y2 = y,
                Stroke = isZero ? BrushScaleWhite : BrushScaleGray,
                StrokeThickness = isZero ? 2 : 1
            };
            ScaleCanvas.Children.Add(line);
        }
    }

    // ------------------------------------------------------------------
    // メモリ操作（M1-M3 のみ: slot 0-2）
    // ------------------------------------------------------------------
    public void StoreMemory(int slot, double gain)
    {
        if (slot < 0 || slot >= 3) return;
        _memories[slot] = (float)gain;
        UpdateMemoryButton(slot);
    }

    public float? GetMemory(int slot) => slot >= 0 && slot < 3 ? _memories[slot] : null;

    private void UpdateMemoryButton(int slot)
    {
        var btn = slot switch { 0 => Mem1, 1 => Mem2, _ => Mem3 };
        bool stored = _memories[slot].HasValue;
        if (!stored)
        {
            btn.Background  = BrushMemEmpty;
            btn.Foreground  = BrushMemText;
            btn.BorderBrush = BrushMemBorderEmpty;
            btn.BorderThickness = new Thickness(1);
        }
        else
        {
            bool match = Math.Abs((double)_memories[slot]!.Value - Value) < 0.001;
            if (match)
            {
                btn.Background      = BrushMemStored;
                btn.Foreground      = BrushMemTextMatch;
                btn.BorderBrush     = BrushMemBorderYellow;
                btn.BorderThickness = new Thickness(2.5);
            }
            else
            {
                btn.Background      = BrushMemRegistered; // 常時青
                btn.Foreground      = BrushMemTextReg;
                btn.BorderBrush     = BrushMemBorderEmpty;
                btn.BorderThickness = new Thickness(1);
            }
        }
    }

    private void UpdateAllMemoryButtons()
    {
        if (Mem1 == null) return;
        for (int i = 0; i < 3; i++)
            UpdateMemoryButton(i);
    }

    private void Memory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        int slot = Convert.ToInt32(btn.Tag);
        if (slot >= 3) return;
        if (_memories[slot].HasValue)
            MemoryRecall?.Invoke(this, (slot, false));  // 常時フェード移動
        else
            StoreMemory(slot, Value);
    }

    private void Memory_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button btn) return;
        int slot = Convert.ToInt32(btn.Tag);
        if (slot >= 3 || !_memories[slot].HasValue) { e.Handled = true; return; }

        var cm = new ContextMenu();
        var quick = new MenuItem { Header = "即座に移動" };
        var reReg = new MenuItem { Header = "再登録" };
        var del   = new MenuItem { Header = "削除" };
        quick.Click += (_, _) => MemoryRecall?.Invoke(this, (slot, true));
        reReg.Click += (_, _) => RequestStoreMemory(slot);
        del.Click   += (_, _) => { _memories[slot] = null; UpdateMemoryButton(slot); };
        cm.Items.Add(quick);
        cm.Items.Add(reReg);
        cm.Items.Add(del);
        cm.IsOpen = true;
        e.Handled = true;
    }

    private void FaderSlider_RightClick(object sender, MouseButtonEventArgs e)
    {
        var cm = new ContextMenu();
        for (int i = 0; i < 3; i++) // M1-M3 のみ（M4廃止）
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
        if (slot >= 3) return;
        if (_memories[slot].HasValue)
            MemoryRegisterRequested?.Invoke(this, (slot, Value));
        else
            StoreMemory(slot, Value);
    }

    // ------------------------------------------------------------------
    // ミュートボタン
    // ------------------------------------------------------------------
    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        _isMuted = !_isMuted;
        UpdateMuteButton();
        MuteChanged?.Invoke(this, _isMuted);
    }

    private void UpdateMuteButton()
    {
        if (MuteBtn == null) return;
        if (_isMuted)
        {
            MuteBtn.Background      = BrushMemStored;        // 黄色（ミュート中）
            MuteBtn.Foreground      = BrushMemTextMatch;     // 白
            MuteBtn.BorderBrush     = BrushMemBorderYellow;
            MuteBtn.BorderThickness = new Thickness(2.5);
        }
        else
        {
            MuteBtn.Background      = BrushMuteInactive;     // 暗赤（常時有効色）
            MuteBtn.Foreground      = BrushMemTextMatch;     // 白
            MuteBtn.BorderBrush     = BrushMuteInactiveBd;
            MuteBtn.BorderThickness = new Thickness(1);
        }
    }
}
