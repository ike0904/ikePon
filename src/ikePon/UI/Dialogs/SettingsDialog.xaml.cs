using System.Windows;
using ikePon.Model;

namespace ikePon.UI.Dialogs;

public partial class SettingsDialog : Window
{
    private readonly AppSettings _settings;

    public SettingsDialog(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        TbShortFade.Text = settings.ShortFadeDuration.ToString("F1");
        TbLongFade.Text  = settings.LongFadeDuration.ToString("F1");
        TbInterlock.Text = settings.InterLockMs.ToString();
        TbLatency.Text   = settings.WasapiLatencyMs.ToString();
        TbPreload.Text   = settings.PreloadThresholdSeconds.ToString();
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParsePositive(TbShortFade.Text, 0f, 9.9f, out float shortFade)) { ShowError(TbShortFade, "0.0〜9.9"); return; }
        if (!TryParsePositive(TbLongFade.Text,  0f, 9.9f, out float longFade))  { ShowError(TbLongFade,  "0.0〜9.9"); return; }
        if (!TryParseInt(TbInterlock.Text, 0, 5000, out int interlock))         { ShowError(TbInterlock, "0〜5000");  return; }
        if (!TryParseInt(TbLatency.Text,   1, 500,  out int latency))           { ShowError(TbLatency,   "1〜500");   return; }
        if (!TryParseInt(TbPreload.Text,   1, 600,  out int preload))           { ShowError(TbPreload,   "1〜600");   return; }

        _settings.ShortFadeDuration       = shortFade;
        _settings.LongFadeDuration        = longFade;
        _settings.InterLockMs             = interlock;
        _settings.WasapiLatencyMs         = latency;
        _settings.PreloadThresholdSeconds = preload;

        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static void ShowError(System.Windows.Controls.TextBox tb, string range)
    {
        MessageBox.Show($"値が無効です。{range} の範囲で入力してください。", "入力エラー",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        tb.Focus();
        tb.SelectAll();
    }

    private static bool TryParsePositive(string s, float min, float max, out float result)
    {
        if (float.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out result))
            return result >= min && result <= max;
        return false;
    }

    private static bool TryParseInt(string s, int min, int max, out int result)
    {
        if (int.TryParse(s, out result))
            return result >= min && result <= max;
        return false;
    }
}
