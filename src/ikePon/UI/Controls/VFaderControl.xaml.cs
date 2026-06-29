using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ikePon.UI.Controls;

public partial class VFaderControl : UserControl
{
    private float?[] _memories = new float?[4];
    private bool _suppressEvent;

    private static readonly SolidColorBrush BrushMemStored = new(Color.FromRgb(0xFF, 0xAA, 0x00));
    private static readonly SolidColorBrush BrushMemEmpty  = new(Color.FromRgb(0x33, 0x33, 0x33));
    private static readonly SolidColorBrush BrushMemText   = new(Color.FromRgb(0x88, 0x88, 0x88));
    private static readonly SolidColorBrush BrushMemTextS  = new(Color.FromRgb(0xFF, 0xDD, 0x44));

    public event EventHandler<double>? VolumeChanged;
    public event EventHandler<(int slot, bool quick)>? MemoryRecall;

    public string Label { get => FaderLabel.Text; set => FaderLabel.Text = value; }

    public double Value
    {
        get => FaderSlider.Value;
        set
        {
            _suppressEvent = true;
            FaderSlider.Value = Math.Clamp(value, 0, 1);
            _suppressEvent = false;
        }
    }

    public VFaderControl()
    {
        InitializeComponent();
    }

    private void FaderSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_suppressEvent)
            VolumeChanged?.Invoke(this, e.NewValue);
    }

    public void StoreMemory(int slot, double value)
    {
        if (slot < 0 || slot >= 4) return;
        _memories[slot] = (float)value;
        UpdateMemoryButton(slot, true);
    }

    public float? GetMemory(int slot) => slot >= 0 && slot < 4 ? _memories[slot] : null;

    private void UpdateMemoryButton(int slot, bool stored)
    {
        var btn = slot switch { 0 => Mem1, 1 => Mem2, 2 => Mem3, _ => Mem4 };
        btn.Background = stored ? BrushMemStored : BrushMemEmpty;
        btn.Foreground = stored ? BrushMemTextS : BrushMemText;
    }

    private void Memory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn) return;
        int slot = (int)btn.Tag;
        bool isShift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);

        if (_memories[slot].HasValue)
            MemoryRecall?.Invoke(this, (slot, !isShift));
        else
            // 未記憶時はクリックで現在値を記憶
            StoreMemory(slot, FaderSlider.Value);
    }

    private void Memory_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Button btn) return;
        int slot = (int)btn.Tag;
        if (!_memories[slot].HasValue) return;

        var cm = new ContextMenu();
        var quick = new MenuItem { Header = "即座に移動（0.5秒）" };
        var slow  = new MenuItem { Header = "ゆっくり移動（3.0秒）" };
        quick.Click += (_, _) => MemoryRecall?.Invoke(this, (slot, true));
        slow.Click  += (_, _) => MemoryRecall?.Invoke(this, (slot, false));
        cm.Items.Add(quick);
        cm.Items.Add(slow);
        cm.IsOpen = true;
        e.Handled = true;
    }
}
