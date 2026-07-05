using System.IO;
using System.Text.Json;

namespace ikePon.Model;

public class AppSettings
{
    private static readonly string SettingsPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ikePon", "settings.json");

    public float LongFadeDuration { get; set; } = 2.0f;
    public int WasapiLatencyMs { get; set; } = 30;
    public int PreloadThresholdSeconds { get; set; } = 10;
    public bool PaSeparateMode { get; set; } = false;
    public MovieDisplayMode MovieMode { get; set; } = MovieDisplayMode.Window;
    public int MovieMonitorIndex { get; set; } = 1;
    public bool DisplayOutputActive { get; set; } = false;
    public string MovieStandbyImagePath { get; set; } = "";
    public float StandbyFadeInDuration { get; set; } = 1.0f;
    public double? MovieWindowX { get; set; }
    public double? MovieWindowY { get; set; }
    public double? MovieWindowWidth { get; set; }
    public double? MovieWindowHeight { get; set; }
    public double InterLockMs { get; set; } = 500;
    public double? WindowWidth { get; set; } = null;
    public double? WindowHeight { get; set; } = null;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

public enum MovieDisplayMode
{
    AudioOnly,
    Window,
    FullScreen
}
