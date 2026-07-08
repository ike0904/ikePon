using ikePon.Audio;
using ikePon.Model;
using TapBehavior = ikePon.Model.TapBehavior;

namespace ikePon.Controller;

/// <summary>
/// UIとAudioEngineを繋ぐコントローラ。
/// パッドトリガー・排他制御・インターロックを担当する。
/// </summary>
public sealed class PlaybackController
{
    private readonly AudioEngine _engine;
    private readonly AppSettings _settings;
    private readonly FileGainDatabase _gainDb;

    // カテゴリごとに現在再生中のパッドインデックス（-1=なし）
    private readonly int[] _activePad = [-1, -1, -1]; // [BGM, SE, Movie]

    // インターロック: 前回トリガー時刻 [bank, pad]
    private readonly long[,] _lastTick = new long[ProjectData.BankCount, BankData.PadCount];

    private ProjectData? _project;
    private int _loadingCount; // アクティブバンクのロード中カウンタ

    // アクティブバンクのロード開始/完了通知（UIスレッドから呼び出し）
    public event Action? BankLoadStarted;
    public event Action? BankLoadCompleted;

    public PlaybackController(AudioEngine engine, AppSettings settings, FileGainDatabase gainDb)
    {
        _engine = engine;
        _settings = settings;
        _gainDb = gainDb;
    }

    public void SetProject(ProjectData project, Action? onComplete = null)
    {
        _project = project;
        _engine.ActiveBank = project.ActiveBankIndex;
        LoadBank(project.ActiveBankIndex, onComplete);
    }

    // ------------------------------------------------------------------
    // バンク読み込み（バックグラウンドで非同期実行）
    // ------------------------------------------------------------------
    public void LoadBank(int bankIndex, Action? onComplete = null)
    {
        if (_project == null) return;
        var bank = _project.Banks[bankIndex];

        // カテゴリ設定を先に更新（同期）
        for (int p = 0; p < BankData.PadCount; p++)
            _engine.SetPadCategory(bankIndex, p, bank.Pads[p].Category);

        bool isActive = bankIndex == _engine.ActiveBank;
        if (isActive)
        {
            if (System.Threading.Interlocked.Increment(ref _loadingCount) == 1)
                BankLoadStarted?.Invoke(); // ローディング開始（UIスレッドから呼ばれる前提）
        }

        Task.Run(() =>
        {
            for (int p = 0; p < BankData.PadCount; p++)
            {
                var pad = bank.Pads[p];
                var src = _engine.GetSource(bankIndex, p);

                if (string.IsNullOrEmpty(pad.FilePath))
                {
                    src.Unload();
                    continue;
                }

                if (src.FilePath != pad.FilePath)
                    src.Load(pad.FilePath, _settings.PreloadThresholdSeconds,
                        _gainDb.GetGain(pad.FilePath), pad.PadGain);
            }
            System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            {
                if (isActive && System.Threading.Interlocked.Decrement(ref _loadingCount) == 0)
                    BankLoadCompleted?.Invoke();
                onComplete?.Invoke();
            });
        });
    }

    // ------------------------------------------------------------------
    // パッドトリガー（UIスレッドから）
    // ------------------------------------------------------------------
    public void TriggerPad(int padIndex, bool fadeOut = false, bool stopImmediate = false)
    {
        if (_project == null) return;

        int bank = _engine.ActiveBank;

        // インターロック確認
        long now = Environment.TickCount64;
        if (now - _lastTick[bank, padIndex] < (long)_settings.InterLockMs)
            return;
        _lastTick[bank, padIndex] = now;

        var pad = _project.Banks[bank].Pads[padIndex];
        if (string.IsNullOrEmpty(pad.FilePath)) return;

        var src = _engine.GetSource(bank, padIndex);
        if (src.FilePath == null) return;

        // Ctrl: 即座に停止
        if (stopImmediate)
        {
            src.StopImmediate();
            return;
        }

        // Shift: フェードアウト
        if (fadeOut)
        {
            src.Stop(_settings.LongFadeDuration);
            return;
        }

        int catIdx = (int)pad.Category;

        // 同カテゴリの別パッドが再生中なら即座に停止
        int prev = _activePad[catIdx];
        if (prev >= 0 && prev != padIndex)
        {
            var prevSrc = _engine.GetSource(bank, prev);
            prevSrc.StopImmediate();
        }

        _activePad[catIdx] = padIndex;

        // MOVIE/BGM: 同パッドを再押しした場合はTapBehaviorによる処理
        bool isSamePad = prev == padIndex;
        bool isMovieBgm = pad.Category == AudioCategory.Movie || pad.Category == AudioCategory.BGM;
        if (isSamePad && isMovieBgm && src.State != PadPlayState.Idle)
        {
            if (pad.TapBehavior == TapBehavior.PauseResume)
            {
                if (src.State == PadPlayState.Playing) src.Pause();
                else if (src.State == PadPlayState.Paused) src.Resume();
                return; // _activePad[catIdx] はそのまま（一時停止中も「アクティブ」）
            }
            if (pad.TapBehavior == TapBehavior.CutOut) src.StopImmediate();
            else src.Stop(_settings.LongFadeDuration);
            _activePad[catIdx] = -1;
            return;
        }

        bool shouldLoop = pad.AfterPlayback == AfterPlaybackBehavior.Loop
                       && pad.Category != AudioCategory.SE;
        src.Trigger(pad.StartPositionSec, pad.EndPositionSec, 0.0f, shouldLoop, pad.LoopStartSec);
    }

    // ------------------------------------------------------------------
    // PAUSE ボタン：MOV/BGM の一時停止・再開
    // ------------------------------------------------------------------
    public void PauseAllMovieBgm()
    {
        int bank = _engine.ActiveBank;
        for (int p = 0; p < BankData.PadCount; p++)
        {
            var src = _engine.GetSource(bank, p);
            if (src.State == PadPlayState.Playing)
            {
                var cat = _project?.Banks[bank].Pads[p].Category;
                if (cat == AudioCategory.Movie || cat == AudioCategory.BGM)
                    src.Pause();
            }
        }
    }

    public void ResumeAllPaused()
    {
        int bank = _engine.ActiveBank;
        for (int p = 0; p < BankData.PadCount; p++)
        {
            var src = _engine.GetSource(bank, p);
            if (src.State == PadPlayState.Paused)
                src.Resume();
        }
    }

    public void ForcePauseResumePad(int padIndex)
    {
        var src = _engine.GetSource(_engine.ActiveBank, padIndex);
        if (src.State == PadPlayState.Playing)       src.Pause();
        else if (src.State == PadPlayState.Paused)   src.Resume();
    }

    /// <summary>現在アクティブな MOV/BGM/SE パッドの再生数を返す（UI グレーアウト判定用）。</summary>
    public bool HasAnyPlaying()
    {
        int bank = _engine.ActiveBank;
        for (int p = 0; p < BankData.PadCount; p++)
        {
            var st = _engine.GetSource(bank, p).State;
            if (st == PadPlayState.Playing || st == PadPlayState.FadingOut || st == PadPlayState.Paused)
                return true;
        }
        return false;
    }

    /// <summary>SE 以外（MOV/BGM）で Paused 状態のパッドがあるか（PAUSE ボタン枠色判定）。</summary>
    public bool HasMovieBgmPaused()
    {
        int bank = _engine.ActiveBank;
        for (int p = 0; p < BankData.PadCount; p++)
        {
            if (_engine.GetSource(bank, p).State != PadPlayState.Paused) continue;
            var cat = _project?.Banks[bank].Pads[p].Category;
            if (cat == AudioCategory.Movie || cat == AudioCategory.BGM) return true;
        }
        return false;
    }

    /// <summary>SE 以外（MOV/BGM）の再生中パッドがあるか（PAUSE ボタン活性判定）。</summary>
    public bool HasMovieBgmPlaying()
    {
        int bank = _engine.ActiveBank;
        for (int p = 0; p < BankData.PadCount; p++)
        {
            var st = _engine.GetSource(bank, p).State;
            if (st != PadPlayState.Playing && st != PadPlayState.Paused) continue;
            var cat = _project?.Banks[bank].Pads[p].Category;
            if (cat == AudioCategory.Movie || cat == AudioCategory.BGM) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------
    // パニック
    // ------------------------------------------------------------------
    public void PanicFadeAll()
    {
        int bank = _engine.ActiveBank;
        for (int p = 0; p < BankData.PadCount; p++)
            _engine.GetSource(bank, p).Stop(_settings.LongFadeDuration);
        ResetActivePads();
    }

    /// <summary>一時停止中のパッドのみを即座に停止する（ALL FADE 中 PAUSE 解除用）。</summary>
    public void StopPausedPads()
    {
        int bank = _engine.ActiveBank;
        for (int p = 0; p < BankData.PadCount; p++)
        {
            var src = _engine.GetSource(bank, p);
            if (src.State == PadPlayState.Paused)
                src.StopImmediate();
        }
        ResetActivePads();
    }

    public void PanicStopAll()
    {
        int bank = _engine.ActiveBank;
        for (int p = 0; p < BankData.PadCount; p++)
            _engine.GetSource(bank, p).StopImmediate();
        ResetActivePads();
    }

    private void ResetActivePads()
    {
        for (int i = 0; i < _activePad.Length; i++) _activePad[i] = -1;
    }

    // ------------------------------------------------------------------
    // バンク切り替え
    // ------------------------------------------------------------------
    public void SwitchBank(int bankIndex)
    {
        if (_project == null) return;
        StopAllCurrentBank();
        _engine.ActiveBank = bankIndex;
        _project.ActiveBankIndex = bankIndex;
        LoadBank(bankIndex);
        ResetActivePads();
    }

    private void StopAllCurrentBank()
    {
        int bank = _engine.ActiveBank;
        for (int p = 0; p < BankData.PadCount; p++)
            _engine.GetSource(bank, p).StopImmediate();
    }

    // ------------------------------------------------------------------
    // 状態取得（UI更新用 – UIスレッドから）
    // ------------------------------------------------------------------
    public PadPlayState GetPadState(int padIndex)
        => _engine.GetSource(_engine.ActiveBank, padIndex).State;

    public float GetPadPosition(int padIndex)
        => _engine.GetSource(_engine.ActiveBank, padIndex).PlaybackPosition;

    public float GetPadFadeGain(int padIndex)
        => _engine.GetSource(_engine.ActiveBank, padIndex).FadeGain;

    public PadSettings? GetPadSettings(int padIndex)
        => _project?.Banks[_engine.ActiveBank].Pads[padIndex];

    public float GetPadTotalTime(int padIndex)
        => _engine.GetSource(_engine.ActiveBank, padIndex).FileTotalSec;

    public void UpdatePadGain(int padIndex, float padGain)
        => _engine.GetSource(_engine.ActiveBank, padIndex).UpdateGain(1.0f, padGain);

    public int ActiveBank => _engine.ActiveBank;

    // ボリューム
    public float MasterVolume { get => _engine.MasterVolume; set => _engine.MasterVolume = value; }
    public float BgmVolume    { get => _engine.BgmVolume;    set => _engine.BgmVolume = value; }
    public float SeVolume     { get => _engine.SeVolume;     set => _engine.SeVolume = value; }
    public float MovieVolume  { get => _engine.MovieVolume;  set => _engine.MovieVolume = value; }

    // ミュート
    public bool MuteMaster { get => _engine.MuteMaster; set => _engine.MuteMaster = value; }
    public bool MuteBgm    { get => _engine.MuteBgm;    set => _engine.MuteBgm    = value; }
    public bool MuteSe     { get => _engine.MuteSe;     set => _engine.MuteSe     = value; }
    public bool MuteMovie  { get => _engine.MuteMovie;  set => _engine.MuteMovie  = value; }

    // WASAPIバッファを強制フラッシュ（ハードウェアループ解除）
    public void FlushOutput() => _engine.FlushOutput();
}
