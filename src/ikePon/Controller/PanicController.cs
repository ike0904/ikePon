namespace ikePon.Controller;

/// <summary>
/// パニック（ESCキー / パニックボタン）の 1回押し/2回連打 判定。
/// 1回押し → 全フェードアウト開始。
/// 2回連打（200ms以内） → 即座に停止へ移行。
/// </summary>
public sealed class PanicController
{
    private readonly PlaybackController _playback;
    private long _lastPressTick;
    private volatile bool _isFading;

    public bool IsFading => _isFading;

    public PanicController(PlaybackController playback)
    {
        _playback = playback;
    }

    public void ClearFadeState() => _isFading = false;

    // Returns true if this was an immediate stop (2nd press / any press during fade)
    public bool Trigger()
    {
        long now = Environment.TickCount64;
        bool doubleTap = _isFading; // フェード中は時間に関係なく即座に停止
        _lastPressTick = now;

        if (doubleTap)
        {
            _playback.PanicStopAll();
            _playback.FlushOutput();
            _isFading = false;
            return true;
        }
        else
        {
            _playback.PanicFadeAll();
            _isFading = true;
            return false;
        }
    }
}
