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
    public float?[,] FaderMemories { get; set; } = new float?[4, 4];
    public float[] FaderPositions { get; set; } = [1.0f, 1.0f, 1.0f, 1.0f];
}
