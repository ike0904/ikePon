namespace ikePon.Model;

public class PadSettings
{
    public string? FilePath { get; set; }
    public AudioCategory Category { get; set; } = AudioCategory.BGM;
    public float PadGain { get; set; } = 1.0f;
    public string? CustomLabel { get; set; }
    public float StartPositionSec { get; set; } = 0f;
    public float EndPositionSec { get; set; } = -1f; // -1 = end of file
    public AfterPlaybackBehavior AfterPlayback { get; set; } = AfterPlaybackBehavior.Stop;
    public string? PadBackgroundColor { get; set; }  // null = default (#3C3C3C)
    public TapBehavior TapBehavior { get; set; } = TapBehavior.FadeOut;

    public PadSettings Clone() => new()
    {
        FilePath = FilePath,
        Category = Category,
        PadGain = PadGain,
        CustomLabel = CustomLabel,
        StartPositionSec = StartPositionSec,
        EndPositionSec = EndPositionSec,
        AfterPlayback = AfterPlayback,
        PadBackgroundColor = PadBackgroundColor,
        TapBehavior = TapBehavior,
    };
}
