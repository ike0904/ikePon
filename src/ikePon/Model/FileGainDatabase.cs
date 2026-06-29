using System.IO;
using System.Text.Json;

namespace ikePon.Model;

public class FileGainDatabase
{
    private static readonly string DbPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ikePon", "filegain.json");

    private Dictionary<string, float> _gains = new(StringComparer.OrdinalIgnoreCase);

    public float GetGain(string? filePath)
    {
        if (filePath == null) return 1.0f;
        return _gains.TryGetValue(filePath, out float g) ? g : 1.0f;
    }

    public void SetGain(string filePath, float gain)
    {
        _gains[filePath] = gain;
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
