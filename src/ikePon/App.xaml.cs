using System.Windows;
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
}
