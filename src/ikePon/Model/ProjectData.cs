using System.IO;
using System.Text.Json;

namespace ikePon.Model;

public class ProjectData
{
    public const int BankCount = 8;

    public string ProjectName { get; set; } = "新規プロジェクト";
    public BankData[] Banks { get; set; } = Enumerable.Range(0, BankCount)
        .Select(_ => new BankData())
        .ToArray();
    public int ActiveBankIndex { get; set; } = 0;

    // [faderIndex 0..3][memorySlot 0..3] (MOVIE=0, BGM=1, SE=2, MASTER=3)
    // jagged array で JSON シリアライズ可能にする
    public float?[][] FaderMemories { get; set; } =
        Enumerable.Range(0, 4).Select(_ => new float?[4]).ToArray();

    public float[] FaderPositions { get; set; } = [1.0f, 1.0f, 1.0f, 1.0f];

    private static readonly JsonSerializerOptions JsonOpts =
        new() { WriteIndented = true };

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
    }

    public static ProjectData? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<ProjectData>(File.ReadAllText(path)); }
        catch { return null; }
    }
}
