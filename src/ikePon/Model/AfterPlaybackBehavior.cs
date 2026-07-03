namespace ikePon.Model;

public enum AfterPlaybackBehavior
{
    Stop,            // そのまま終了（デフォルト）
    FreezeLastFrame, // 最終フレームで止める（MOVIE のみ有効）
    Loop             // ループ再生（SE は無効）
}
