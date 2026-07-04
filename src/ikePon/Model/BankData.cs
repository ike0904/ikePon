namespace ikePon.Model;

public class BankData
{
    public const int PadCount = 16;

    public PadSettings[] Pads { get; set; } = Enumerable.Range(0, PadCount)
        .Select(i => new PadSettings { Category = i < 8 ? AudioCategory.BGM : AudioCategory.SE })
        .ToArray();

    public string? BankLabel { get; set; }
    public string? BankBackgroundColor { get; set; }

    public BankData Clone()
    {
        var clone = new BankData { BankLabel = BankLabel, BankBackgroundColor = BankBackgroundColor };
        for (int i = 0; i < PadCount; i++)
            clone.Pads[i] = Pads[i].Clone();
        return clone;
    }
}
