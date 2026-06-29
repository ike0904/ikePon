namespace ikePon.Controller;

/// <summary>
/// パニック（ESCキー / パニックボタン）の 1回押し/2回連打 判定。
/// 1回押し → 全フェードアウト開始。
/// 2回連打（200ms以内） → 即座に停止へ移行。
/// </summary>
public sealed class PanicController
{
    private readonly PlaybackController _playback;
    private long _lastPanicTick = 0;
    private const long DoubleTapMs = 300;

    public PanicController(PlaybackController playback)
    {
        _playback = playback;
    }

    public void Trigger()
    {
        long now = Environment.TickCount64;
        long elapsed = now - _lastPanicTick;

        if (elapsed <= DoubleTapMs)
        {
            // 2回連打 → 即座に全停止
            _playback.PanicStopAll();
            _lastPanicTick = 0;
        }
        else
        {
            // 1回目 → フェードアウト開始
            _playback.PanicFadeAll();
            _lastPanicTick = now;
        }
    }
}
