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

    // バンクキー: キー → バンクインデックス
    // グリッド表示順 A/E, B/F, C/G, D/H に合わせ、
    // キーボードの行（7,8 / U,I / J,K / M,,）がグリッドの行と対応
    private static readonly Dictionary<Key, int> BankKeyMap = new()
    {
        { Key.D7,       0 },  // A (上段左)
        { Key.D8,       4 },  // E (上段右)
        { Key.U,        1 },  // B (2段左)
        { Key.I,        5 },  // F (2段右)
        { Key.J,        2 },  // C (3段左)
        { Key.K,        6 },  // G (3段右)
        { Key.M,        3 },  // D (下段左)
        { Key.OemComma, 7 },  // H (下段右)
    };

    // パッドキーの表示ラベル
    public static readonly string[] PadLabels =
    [
        "1","2","3","4",
        "Q","W","E","R",
        "A","S","D","F",
        "Z","X","C","V"
    ];

    // バンクキーの表示ラベル（インデックス = バンク番号 A=0...H=7）
    public static readonly string[] BankLabels =
    [
        "7", "U", "J", "M",   // A, B, C, D
        "8", "I", "K", ","    // E, F, G, H
    ];

    public int? GetPadIndex(Key key)
    {
        int idx = Array.IndexOf(PadKeys, key);
        return idx >= 0 ? idx : null;
    }

    public int? GetBankIndex(Key key) =>
        BankKeyMap.TryGetValue(key, out int idx) ? idx : null;

    public string GetPadLabel(int padIndex) =>
        padIndex >= 0 && padIndex < PadLabels.Length ? PadLabels[padIndex] : "";

    public string GetBankLabel(int bankIndex) =>
        bankIndex >= 0 && bankIndex < BankLabels.Length ? BankLabels[bankIndex] : "";
}
