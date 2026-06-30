using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ikePon;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 未処理例外をキャッチしてクラッシュを防ぐ
        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show($"予期しないエラーが発生しました:\n{ex.Exception.Message}",
                "ikePon - エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            ex.Handled = true;
        };
    }

    // タイトルバーを非ダークモード（黒テキスト）にする
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    public static void SetLightTitleBar(Window window)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
            int value = 0; // 0 = light title bar (black text)
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
        }
        catch { }
    }
}
