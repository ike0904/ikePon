using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ikePon.UI.Controls;

public partial class VFaderControl : UserControl
{
    // 目盛り定義: (ラベル, ゲイン値)
    private static readonly (string label, double gain)[] ScaleMarks =
    [
        ("+6",  1.995),
        ("0",   1.000),
        ("-6",  0.501),
        ("-12", 0.251),
        ("-18", 0.126),
        ("-24", 0.063),
        ("-30", 0.032),
        ("-∞",  0.0)
    ];

    // 非線形スケール: 0dB(ゲイン=1.0)はスライダー下端から75%の位置
    private const double UnityPos  = 0.75;   // スライダー位置空間で0dBの位置
    private const double UnityGain = 1.0;    // 0dBのゲイン値
    private const double MaxGain   = 1.995;  // +6dBのゲイン値

    private const double ThumbHalf = 10.0;
    public  const double FaderMax  = MaxGain;

    private float?[] _memories = new float?[4];
    private bool _suppressEvent;

    // アニメーション
    private readonly DispatcherTimer _animTimer;
    private double _animFrom;
    private double _animTarget;
    private double _animDuration;  // seconds
    private DateTime _animStartTime;

    private static readonly SolidColorBrush BrushMemStored      = new(Color.FromRgb(0xFF, 0xAA, 0x00));
    private static readonly SolidColorBrush BrushMemEmpty       = new(Color.FromRgb(0x2E, 0x2E, 0x2E));
    private static readonly SolidColorBrush BrushMemText        = new(Color.FromRgb(0x66, 0x66, 0x66));
    private static readonly SolidColorBrush BrushMemTextMatch   = new(Colors.White);
    private static readonly SolidColorBrush BrushMemTextReg     = new(Color.FromRgb(0xFF, 0xDD, 0x44));
    private static readonly SolidColorBrush BrushMemRegistered  = new(Color.FromRgb(0x0E, 0x22, 0x3A)); // SHIFT: 青系
    private static readonly SolidColorBrush BrushMemRedReg      = new(Color.FromRgb(0x3A, 0x0E, 0x0E)); // 通常: 赤系
    private static readonly SolidColorBrush BrushMemBorderYellow= new(Color.FromRgb(0xFF, 0xD7, 0x00));
    private static readonly SolidColorBrush BrushMemBorderEmpty = new(Color.FromRgb(0x44, 0x44, 0x44));
    private static readonly SolidColorBrush BrushScaleGray  = new(Color.FromRgb(0xAA, 0xAA, 0xAA));

    public event EventHandler<double>? VolumeChanged;
    public event EventHandler<(int slot, bool quick)>? MemoryRecall;

    public string Label { get => FaderLabel.Text; set => FaderLabel.Text = value; }

    // Value は外部向けゲイン値（0..MaxGain）。スライダーは内部で位置空間(0..1)を使用。
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
            { Interval = TimeSpan.FromMilliseconds(16) }; // ~60fps
        _animTimer.Tick += AnimTimer_Tick;
    }

    // ------------------------------------------------------------------
    // 非線形変換（2区間線形）
    // 区間1: 位置 0..UnityPos → ゲイン 0..UnityGain（線形）
    // 区間2: 位置 UnityPos..1 → ゲイン UnityGain..MaxGain（線形）
    // ------------------------------------------------------------------
    private static double PositionToGain(double pos)
    {
        pos = Math.Clamp(pos, 0, 1.0);
        if (pos <= UnityPos)
            return pos * (UnityGain / UnityPos);
        else
            return UnityGain + (pos - UnityPos) * (MaxGain - UnityGain) / (1.0 - UnityPos);
    }

    private static double GainToPosition(double gain)
    {
        gain = Math.Clamp(gain, 0, MaxGain);
        if (gain <= UnityGain)
            return gain * (UnityPos / UnityGain);
        else
            return UnityPos + (gain - UnityGain) * (1.0 - UnityPos) / (MaxGain - UnityGain);
    }

    /// <summary>
    /// 指定ゲイン値へ durationSecs 秒かけて移動する。
    /// durationSecs ≤ 0 の場合は即時移動。
    /// </summary>
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
        _animFrom = FaderSlider.Value;
        _animTarget = posTarget;
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

        if (t >= 1.0)
            _animTimer.Stop();
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

        double trackH = h - ThumbHalf * 2;
        double trackTop = ThumbHalf;
        double cw = ScaleCanvas.ActualWidth > 0 ? ScaleCanvas.ActualWidth : 24;

        foreach (var (label, gain) in ScaleMarks)
        {
            double pct = 1.0 - GainToPosition(gain);
            double y = trackTop + pct * trackH;

            bool isZero = label == "0";

            // 目盛り線
            var line = new Line
            {
                X1 = cw - 10, X2 = cw - 1,
                Y1 = y, Y2 = y,
                Stroke = BrushScaleGray,
                StrokeThickness = isZero ? 1.5 : 1
            };
            ScaleCanvas.Children.Add(line);

            // ラベルテキスト
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
    // メモリ操作（ゲイン値で保存）
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
                btn.Background  = BrushMemRegistered; // 青系
                btn.Foreground  = BrushMemTextReg;
                btn.BorderBrush = BrushMemBorderYellow;
            }
            else
            {
                btn.Background  = BrushMemRedReg;     // 赤系（通常 or CTRL）
                btn.Foreground  = BrushMemTextReg;
                btn.BorderBrush = BrushMemBorderYellow;
            }
        }
    }

    private void UpdateAllMemoryButtons()
    {
        if (Mem1 == null) return; // XAML 初期化前は無視
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
            MemoryRecall?.Invoke(this, (slot, !isShift)); // SHIFTなし=quick(0.2秒), SHIFTあり=slow
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
