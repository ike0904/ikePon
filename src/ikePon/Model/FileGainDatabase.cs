using System.IO;
using System.Text.Json;

namespace ikePon.Model;

public class FileGainDatabase
{
    private static readonly string DbPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ikePon", "filegain.json");

    private Dictionary<string, float> _gains = new(StringComparer.OrdinalIgnoreCase);

    // キー = "ファイル名|バイト数|最終更新UTC ticks"
    // フォルダ移動では変化しない、ファイル更新でリセットされる
    private static string MakeKey(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (info.Exists)
                return $"{info.Name}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch { }
        return filePath;
    }

    public float GetGain(string? filePath)
    {
        if (filePath == null) return 1.0f;
        return _gains.TryGetValue(MakeKey(filePath), out float g) ? g : 1.0f;
    }

    public void SetGain(string filePath, float gain)
    {
        _gains[MakeKey(filePath)] = gain;
    }

    public static FileGainDatabase Load()
    {
        var db = new FileGainDatabase();
        try
        {
            if (File.Exists(DbPath))
            {
                var json = File.ReadAllText(DbPath);
                db._gains = JsonSerializer.Deserialize<Dictionary<string, float>>(json)
                    ?? new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            }
        }
        catch { }
        return db;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
            File.WriteAllText(DbPath, JsonSerializer.Serialize(_gains, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
