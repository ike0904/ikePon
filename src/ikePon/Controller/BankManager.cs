namespace ikePon.Controller;

/// <summary>
/// バンク切り替えの確認（Y/N待ち）ロジックを管理する。
/// </summary>
public sealed class BankManager
{
    private readonly PlaybackController _playback;
    private int _pendingBankIndex = -1;

    public bool IsPendingConfirmation => _pendingBankIndex >= 0;
    public int PendingBankIndex => _pendingBankIndex;

    /// <summary>確認ダイアログ表示を UI に要求するイベント</summary>
    public event Action<int>? BankSwitchRequested;
    /// <summary>確認完了（バンク切り替え実行済み）</summary>
    public event Action<int>? BankSwitched;
    /// <summary>キャンセル</summary>
    public event Action? BankSwitchCancelled;

    public BankManager(PlaybackController playback)
    {
        _playback = playback;
    }

    /// <summary>バンクキー押下時 – 即切り替えはせず確認待ちへ</summary>
    public void RequestSwitch(int bankIndex)
    {
        if (bankIndex == _playback.ActiveBank && !IsPendingConfirmation) return;

        _pendingBankIndex = bankIndex;
        BankSwitchRequested?.Invoke(bankIndex);
    }

    /// <summary>Y キーで確定</summary>
    public void Confirm()
    {
        if (_pendingBankIndex < 0) return;
        int idx = _pendingBankIndex;
        _pendingBankIndex = -1;
        _playback.SwitchBank(idx);
        BankSwitched?.Invoke(idx);
    }

    /// <summary>N キーまたは他操作でキャンセル</summary>
    public void Cancel()
    {
        if (_pendingBankIndex < 0) return;
        _pendingBankIndex = -1;
        BankSwitchCancelled?.Invoke();
    }
}
