using System.IO;
using System.Windows;
using ikePon.Model;

namespace ikePon.UI.Dialogs;

public partial class PadDetailDialog : Window
{
    private readonly PadSettings _padSettings;
    private readonly FileGainDatabase _gainDb;
    private readonly float _fileTotalSec;

    public AudioCategory ResultCategory   { get; private set; }
    public string?        ResultLabel     { get; private set; }
    public string?        ResultFilePath  { get; private set; }
    public float          ResultFileGain  { get; private set; }
    public float          ResultPadGain   { get; private set; }
    public float          ResultStartSec  { get; private set; }
    public float          ResultEndSec    { get; private set; }

    public PadDetailDialog(PadSettings padSettings, FileGainDatabase gainDb, float fileTotalSec)
    {
        _padSettings  = padSettings;
        _gainDb       = gainDb;
        _fileTotalSec = fileTotalSec;
        InitializeComponent();
        LoadValues();
    }

    private void LoadValues()
    {
        CbCategory.SelectedIndex = _padSettings.Category switch
        {
            AudioCategory.Movie => 0,
            AudioCategory.BGM   => 1,
            _                   => 2
        };

        string defaultName = string.IsNullOrEmpty(_padSettings.FilePath)
            ? "" : Path.GetFileNameWithoutExtension(_padSettings.FilePath);
        TbDisplayName.Text = _padSettings.CustomLabel ?? defaultName;

        TbFilePath.Text = _padSettings.FilePath ?? "";

        float fileGain = _gainDb.GetGain(_padSettings.FilePath);
        TbFileGain.Text = fileGain.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        TbPadGain.Text  = _padSettings.PadGain.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

        TbStartPos.Text = SecsToTimestamp(_padSettings.StartPositionSec);

        if (_padSettings.EndPositionSec < 0)
            TbEndPos.Text = "";
        else
            TbEndPos.Text = SecsToTimestamp(_padSettings.EndPositionSec);

        if (_fileTotalSec > 0)
            LblTotalTime.Content = $"/ {SecsToTimestamp(_fileTotalSec)}  （総時間）";
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        var cat = CbCategory.SelectedIndex switch
        {
            0 => AudioCategory.Movie,
            1 => AudioCategory.BGM,
            _ => AudioCategory.SE
        };

        if (!TryParseGain(TbFileGain.Text, out float fileGain))
        { ShowError(TbFileGain, "ファイルゲイン: 0.0〜4.0"); return; }
        if (!TryParseGain(TbPadGain.Text, out float padGain))
        { ShowError(TbPadGain, "パッドゲイン: 0.0〜4.0"); return; }

        if (!TryParseTimestamp(TbStartPos.Text, out float startSec) || startSec < 0)
        { ShowError(TbStartPos, "形式: M:SS.f  例: 0:00.0"); return; }

        float endSec = -1f;
        if (!string.IsNullOrWhiteSpace(TbEndPos.Text))
        {
            if (!TryParseTimestamp(TbEndPos.Text, out endSec) || endSec <= startSec)
            { ShowError(TbEndPos, "開始位置より後の時刻を入力してください"); return; }
        }

        ResultCategory  = cat;
        ResultLabel     = string.IsNullOrWhiteSpace(TbDisplayName.Text) ? null : TbDisplayName.Text.Trim();
        ResultFilePath  = string.IsNullOrWhiteSpace(TbFilePath.Text) ? null : TbFilePath.Text.Trim();
        ResultFileGain  = fileGain;
        ResultPadGain   = padGain;
        ResultStartSec  = startSec;
        ResultEndSec    = endSec;

        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void TbFilePath_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void TbFilePath_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;

        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".mp3", ".wav", ".flac", ".ogg", ".aac", ".m4a", ".mp4", ".mov", ".mkv", ".avi", ".wmv" };
        string? file = files.FirstOrDefault(f => exts.Contains(Path.GetExtension(f)));
        if (file == null) return;

        TbFilePath.Text = file;
        if (string.IsNullOrWhiteSpace(TbDisplayName.Text))
            TbDisplayName.Text = Path.GetFileNameWithoutExtension(file);
        // ファイルゲインを新ファイル用に更新
        float fg = _gainDb.GetGain(file);
        TbFileGain.Text = fg.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        e.Handled = true;
    }

    // ------------------------------------------------------------------
    // ヘルパー
    // ------------------------------------------------------------------

    private static string SecsToTimestamp(float secs)
    {
        if (secs < 0) secs = 0;
        int m   = (int)(secs / 60);
        float r = secs - m * 60;
        return $"{m}:{r:00.0}";
    }

    private static bool TryParseTimestamp(string s, out float secs)
    {
        secs = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var parts = s.Trim().Split(':');
        if (parts.Length == 2
            && int.TryParse(parts[0].Trim(), out int m)
            && float.TryParse(parts[1].Trim(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float sec))
        {
            secs = m * 60 + sec;
            return true;
        }
        return false;
    }

    private static bool TryParseGain(string s, out float gain)
    {
        if (float.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out gain))
            return gain >= 0f && gain <= 4f;
        return false;
    }

    private static void ShowError(System.Windows.Controls.TextBox tb, string message)
    {
        MessageBox.Show(message, "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
        tb.Focus();
        tb.SelectAll();
    }
}
