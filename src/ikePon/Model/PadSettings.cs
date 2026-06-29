namespace ikePon.Model;

public class PadSettings
{
    public string? FilePath { get; set; }
    public AudioCategory Category { get; set; } = AudioCategory.BGM;
    public float PadGain { get; set; } = 1.0f;
    public string? CustomLabel { get; set; }

    public PadSettings Clone() => new()
    {
        FilePath = FilePath,
        Category = Category,
        PadGain = PadGain,
        CustomLabel = CustomLabel,
    };
}
