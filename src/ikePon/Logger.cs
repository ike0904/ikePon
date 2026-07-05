using System;
using System.Diagnostics;
using System.IO;

namespace ikePon;

/// <summary>
/// デバッグ用ファイルログ。DEBUG ビルドのみ有効（Release では全て no-op）。
/// ログファイル: %LocalAppData%\ikePon\debug.log（起動のたびに上書き）
/// </summary>
public static class Logger
{
#if DEBUG
    private static readonly string LogDir  = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ikePon");
    private static readonly string LogFile = Path.Combine(LogDir, "debug.log");
    private static readonly object _lock   = new();

    static Logger()
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            File.WriteAllText(LogFile,
                $"=== ikéPon Debug Log [{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ==={Environment.NewLine}");
        }
        catch { }
    }
#endif

    public static void Log(string message)
    {
#if DEBUG
        Debug.WriteLine(message);
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}";
        lock (_lock)
            try { File.AppendAllText(LogFile, line); } catch { }
#endif
    }
}
