using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using ikePon.Model;

namespace ikePon;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 言語設定を読み込んで辞書をマージ（MainWindow 生成より前に実行）
        var settings = AppSettings.Load();
        string lang = settings.Language is "en" or "ja" ? settings.Language : "ja";
        var langDict = new System.Windows.ResourceDictionary
        {
            Source = new Uri($"Resources/Strings.{lang}.xaml", UriKind.Relative)
        };
        Resources.MergedDictionaries.Add(langDict);
        L.Load(Resources);

        base.OnStartup(e);

        // 未処理例外をキャッチしてクラッシュを防ぐ
        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show(L.F("Str_Msg_UnhandledError", ex.Exception.Message),
                L.S("Str_Msg_UnhandledErrorTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            ex.Handled = true;
        };

        new MainWindow().Show();
    }

    // タイトルバーを白背景・黒テキストに設定する
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    public static void SetLightTitleBar(Window window)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
            int light = 0; // 0 = light (black text)
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref light, sizeof(int));
            int captionColor = unchecked((int)0x00FFFFFF); // 白背景 (COLORREF: 0x00BBGGRR)
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));
            int textColor = unchecked((int)0x00000000); // 黒テキスト
            DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref textColor, sizeof(int));
        }
        catch { }
    }
}
