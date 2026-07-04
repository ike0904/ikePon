namespace ikePon.Controller;

/// <summary>
/// パニック（ESCキー / パニックボタン）の 1回押し/2回押し 判定。
/// 1回押し → 全フェードアウト開始。
/// 2回押し（フェード中 or フェード完了後も含む） → 即座に停止へ移行。
/// </summary>
public sealed class PanicController
{
    private readonly PlaybackController _playback;
    private volatile bool _isFading;
    private volatile bool _panicActivated; // 1回目押し後、2回目押しまで維持

    public bool IsFading    => _isFading;
    public bool IsActivated => _panicActivated;

    public PanicController(PlaybackController playback)
    {
        _playback = playback;
    }

    /// <summary>音声フェード完了時にフェード中フラグのみクリア（_panicActivatedは残す）</summary>
    public void ClearFadeState() => _isFading = false;

    /// <summary>新規再生開始時など、全パニック状態をクリア</summary>
    public void ClearAllState()
    {
        _isFading       = false;
        _panicActivated = false;
    }

    // Returns true if this was an immediate stop (2nd press / any press during or after fade)
    public bool Trigger()
    {
        // _isFading: まだ音声フェード中
        // _panicActivated: 1回目押し済み（フェード完了後でも維持）
        bool doubleTap = _isFading || _panicActivated;

        if (doubleTap)
        {
            _playback.PanicStopAll();
            _playback.FlushOutput();
            _isFading       = false;
            _panicActivated = false;
            return true;
        }
        else
        {
            _playback.PanicFadeAll();
            _isFading       = true;
            _panicActivated = true;
            return false;
        }
    }
}
