using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ikePon.UI.Dialogs;

public partial class BankDetailDialog : Window
{
    public string? ResultLabel       { get; private set; }
    public string? ResultBgColor     { get; private set; }

    private string? _selectedBgColor;

    private static readonly string[] BgColorPalette =
    [
        "#303030",
        "#162744",
        "#143A14",
        "#3A1414",
        "#2A1040",
        "#3A2010",
        "#103A3A",
        "#2A3010",
    ];

    public BankDetailDialog(string currentLabel, string? currentBgColor)
    {
        InitializeComponent();
        Loaded += (_, _) => App.SetLightTitleBar(this);
        TbDisplayName.Text = currentLabel;
        _selectedBgColor = currentBgColor;
        BuildBgColorSwatches();
        TbDisplayName.Focus();
        TbDisplayName.SelectAll();

        var cm   = new ContextMenu();
        var item = new MenuItem { Header = "初期値に戻す" };
        item.Click += (_, _) => TbDisplayName.Text = "";
        cm.Items.Add(item);
        TbDisplayName.ContextMenu = cm;
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
        _selectedBgColor = (hex == "#303030") ? null : hex;
        UpdateSwatchBorders();
    }

    private void UpdateSwatchBorders()
    {
        string effective = _selectedBgColor ?? "#303030";
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
        ResultLabel  = string.IsNullOrWhiteSpace(TbDisplayName.Text) ? null : TbDisplayName.Text.Trim();
        ResultBgColor = _selectedBgColor;
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        Mouse.Capture(null);
        if (e.OriginalSource is TextBox tb) { tb.Focus(); return; }
        Keyboard.ClearFocus();
    }
}
