namespace ikePon.Controller;

/// <summary>
/// パニック（ESCキー / パニックボタン）の 1回押し/2回連打 判定。
/// 1回押し → 全フェードアウト開始。
/// 2回連打（200ms以内） → 即座に停止へ移行。
/// </summary>
public sealed class PanicController
{
    private readonly PlaybackController _playback;

    public PanicController(PlaybackController playback)
    {
        _playback = playback;
    }

    public void Trigger()
    {
        // フェードアウトでは止まらないケースがあるため、常に即座停止に統一。
        // 個別フェードは SHIFT+パッドで行う。
        _playback.PanicStopAll();

        // IAudioClient を完全に閉じ新規セッションを開くことでドライバループを解除
        _playback.FlushOutput();
    }
}
