using ikePon.Audio;
using ikePon.Model;

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

    public PlaybackController(AudioEngine engine, AppSettings settings, FileGainDatabase gainDb)
    {
        _engine = engine;
        _settings = settings;
        _gainDb = gainDb;
    }

    public void SetProject(ProjectData project)
    {
        _project = project;
        _engine.ActiveBank = project.ActiveBankIndex;
        LoadBank(project.ActiveBankIndex);
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
            if (onComplete != null)
                System.Windows.Application.Current.Dispatcher.Invoke(onComplete);
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
            src.Stop(_settings.ShortFadeDuration);
            return;
        }

        // Shift: フェードアウト
        if (fadeOut)
        {
            src.Stop(_settings.LongFadeDuration);
            return;
        }

        int catIdx = (int)pad.Category;

        // 同カテゴリの別パッドが再生中なら短いフェードアウトで停止
        int prev = _activePad[catIdx];
        if (prev >= 0 && prev != padIndex)
        {
            var prevSrc = _engine.GetSource(bank, prev);
            prevSrc.Stop(_settings.ShortFadeDuration);
        }

        _activePad[catIdx] = padIndex;

        // MOVIE/BGM: 同パッドを再押しした場合は長いフェードアウト（再起動しない）
        bool isSamePad = prev == padIndex;
        bool isMovieBgm = pad.Category == AudioCategory.Movie || pad.Category == AudioCategory.BGM;
        if (isSamePad && isMovieBgm && src.State != PadPlayState.Idle)
        {
            PadAudioSource.DiagLog.Enqueue($"{DateTime.Now:HH:mm:ss.fff} TRIGGER_STOP(same BGM/MOV) pad={padIndex} cat={pad.Category} prev={prev}");
            src.Stop(_settings.LongFadeDuration);
            _activePad[catIdx] = -1;
            return;
        }

        PadAudioSource.DiagLog.Enqueue($"{DateTime.Now:HH:mm:ss.fff} TRIGGER_PAD pad={padIndex} cat={pad.Category} prev={prev} prevStop={(prev >= 0 && prev != padIndex)}");
        src.Trigger(pad.StartPositionSec, pad.EndPositionSec, _settings.ShortFadeDuration);
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
            _engine.GetSource(bank, p).Stop(_settings.ShortFadeDuration);
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

    public void UpdatePadGain(int padIndex, float fileGain, float padGain)
        => _engine.GetSource(_engine.ActiveBank, padIndex).UpdateGain(fileGain, padGain);

    public int ActiveBank => _engine.ActiveBank;

    // ボリューム
    public float MasterVolume { get => _engine.MasterVolume; set => _engine.MasterVolume = value; }
    public float BgmVolume    { get => _engine.BgmVolume;    set => _engine.BgmVolume = value; }
    public float SeVolume     { get => _engine.SeVolume;     set => _engine.SeVolume = value; }
    public float MovieVolume  { get => _engine.MovieVolume;  set => _engine.MovieVolume = value; }

    // WASAPIバッファを強制フラッシュ（ハードウェアループ解除）
    public void FlushOutput() => _engine.FlushOutput();
}
