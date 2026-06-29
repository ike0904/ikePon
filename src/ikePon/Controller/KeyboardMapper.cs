using System.Windows.Input;

namespace ikePon.Controller;

/// <summary>
/// キーボードのキー → パッド/バンクインデックスへのマッピング。
/// JIS/US 配列は WPF の Key 列挙型が物理キーに依存しないため自動対応。
/// </summary>
public sealed class KeyboardMapper
{
    // パッドキー配置: 4×4 グリッド、左上から右下へ
    private static readonly Key[] PadKeys =
    [
        Key.D1, Key.D2, Key.D3, Key.D4,
        Key.Q,  Key.W,  Key.E,  Key.R,
        Key.A,  Key.S,  Key.D,  Key.F,
        Key.Z,  Key.X,  Key.C,  Key.V
    ];

    // バンクキー配置: 2×4 グリッド
    private static readonly Key[] BankKeys =
    [
        Key.D7, Key.D8,
        Key.U,  Key.I,
        Key.J,  Key.K,
        Key.M,  Key.OemComma
    ];

    // パッドキーの表示ラベル
    public static readonly string[] PadLabels =
    [
        "1","2","3","4",
        "Q","W","E","R",
        "A","S","D","F",
        "Z","X","C","V"
    ];

    // バンクキーの表示ラベル
    public static readonly string[] BankLabels =
    [
        "7","8",
        "U","I",
        "J","K",
        "M",","
    ];

    public int? GetPadIndex(Key key)
    {
        int idx = Array.IndexOf(PadKeys, key);
        return idx >= 0 ? idx : null;
    }

    public int? GetBankIndex(Key key)
    {
        int idx = Array.IndexOf(BankKeys, key);
        return idx >= 0 ? idx : null;
    }

    public string GetPadLabel(int padIndex) =>
        padIndex >= 0 && padIndex < PadLabels.Length ? PadLabels[padIndex] : "";

    public string GetBankLabel(int bankIndex) =>
        bankIndex >= 0 && bankIndex < BankLabels.Length ? BankLabels[bankIndex] : "";
}
