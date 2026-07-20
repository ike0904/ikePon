using System.IO;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using ikePon.Model;

namespace ikePon.Audio;

/// <summary>
/// 全パッドのオーディオソースを管理する Root ISampleProvider。
/// 8バンク × 16パッド = 128 ソースを事前確保し、GC負荷をゼロに抑える。
/// </summary>
public sealed class AudioEngine : ISampleProvider, IDisposable
{
    public const int BankCount = ProjectData.BankCount;
    public const int PadCount = BankData.PadCount;

    private readonly WaveFormat _format;
    private readonly PadAudioSource[,] _sources;
    private readonly AudioCategory[,] _padCategories;

    private WasapiOut? _wasapiOut;
    private NAudio.Wave.IWavePlayer? _waveOutFallback;

    private volatile int _activeBank;
    private volatile float _masterVol = 1f;
    private volatile float _bgmVol = 1f;
    private volatile float _seVol = 1f;
    private volatile float _movieVol = 1f;
    private volatile bool _paSeparate;
    private volatile bool _muteMaster;
    private volatile bool _muteBgm;
    private volatile bool _muteSe;
    private volatile bool _muteMovie;

    private readonly float[] _tempBuf = new float[65536];

    public WaveFormat WaveFormat => _format;

    public AudioEngine()
    {
        _format = WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);
        _sources = new PadAudioSource[BankCount, PadCount];
        _padCategories = new AudioCategory[BankCount, PadCount];

        for (int b = 0; b < BankCount; b++)
            for (int p = 0; p < PadCount; p++)
            {
                _sources[b, p] = new PadAudioSource(_format);
                _padCategories[b, p] = p < 8 ? AudioCategory.BGM : AudioCategory.SE;
            }
    }

    public void Start()
    {
        // 1. WASAPI Exclusive 44100Hz — デバイスを 44100Hz に固定し 48kHz バッファ問題を回避
        // 48kHz Shared で発生する「発音時ブザー音」を根本的に防ぐ
        try
        {
            _wasapiOut = new WasapiOut(AudioClientShareMode.Exclusive, 30);
            _wasapiOut.Init(this);
            _wasapiOut.Play();
            return;
        }
        catch
        {
            try { _wasapiOut?.Stop(); } catch { }
            try { _wasapiOut?.Dispose(); } catch { }
            _wasapiOut = null;
        }

        // 2. WASAPI Shared — 他アプリと音声を共存させる（デバイスが 48kHz の場合はリサンプラー経由）
        try
        {
            int mixRate = _format.SampleRate;
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                mixRate = device.AudioClient.MixFormat.SampleRate;
            }
            catch { }

            ISampleProvider provider = this;
            if (mixRate != _format.SampleRate)
                provider = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(this, mixRate);

            _wasapiOut = new WasapiOut(AudioClientShareMode.Shared, 0);
            _wasapiOut.Init(provider);
            _wasapiOut.Play();
            return;
        }
        catch
        {
            try { _wasapiOut?.Stop(); } catch { }
            try { _wasapiOut?.Dispose(); } catch { }
            _wasapiOut = null;
        }

        // 3. WaveOutEvent — 大きめバッファで安定動作
        try
        {
            var provider = new NAudio.Wave.SampleProviders.SampleToWaveProvider(this);
            var woe = new NAudio.Wave.WaveOutEvent { DesiredLatency = 200, NumberOfBuffers = 4 };
            woe.Init(provider);
            woe.Play();
            _waveOutFallback = woe;
            return;
        }
        catch
        {
            try { _waveOutFallback?.Stop(); } catch { }
            try { _waveOutFallback?.Dispose(); } catch { }
            _waveOutFallback = null;
        }

        // 4. WASAPI Exclusive 100ms — 最終フォールバック
        try
        {
            _wasapiOut = new WasapiOut(AudioClientShareMode.Exclusive, 100);
            _wasapiOut.Init(this);
            _wasapiOut.Play();
        }
        catch { /* すべて失敗した場合は無音で動作継続 */ }
    }

    // ------------------------------------------------------------------
    // ソース管理
    // ------------------------------------------------------------------
    public PadAudioSource GetSource(int bankIdx, int padIdx) => _sources[bankIdx, padIdx];

    public void SetPadCategory(int bankIdx, int padIdx, AudioCategory cat)
    {
        _padCategories[bankIdx, padIdx] = cat;
    }

    // 同バンク内の2スロットのオーディオソース参照を入れ替える（再ロード不要）
    public void SwapSources(int bankIdx, int padA, int padB)
    {
        (_sources[bankIdx, padA], _sources[bankIdx, padB]) =
            (_sources[bankIdx, padB], _sources[bankIdx, padA]);
    }

    // 2バンク間の全ソース参照を入れ替える（再ロード不要）
    public void SwapBankSources(int bankA, int bankB)
    {
        for (int p = 0; p < PadCount; p++)
            (_sources[bankA, p], _sources[bankB, p]) = (_sources[bankB, p], _sources[bankA, p]);
    }

    public int ActiveBank
    {
        get => _activeBank;
        set => _activeBank = Math.Clamp(value, 0, BankCount - 1);
    }

    // ボリューム（volatile write でスレッドセーフ）
    public float MasterVolume { get => _masterVol; set => _masterVol = ClampVol(value); }
    public float BgmVolume    { get => _bgmVol;    set => _bgmVol    = ClampVol(value); }
    public float SeVolume     { get => _seVol;     set => _seVol     = ClampVol(value); }
    public float MovieVolume  { get => _movieVol;  set => _movieVol  = ClampVol(value); }
    public bool  PaSeparate   { get => _paSeparate; set => _paSeparate = value; }
    public bool  MuteMaster   { get => _muteMaster; set => _muteMaster = value; }
    public bool  MuteBgm      { get => _muteBgm;    set => _muteBgm    = value; }
    public bool  MuteSe       { get => _muteSe;     set => _muteSe     = value; }
    public bool  MuteMovie    { get => _muteMovie;  set => _muteMovie  = value; }

    private static float ClampVol(float v) => v < 0f ? 0f : v > 4f ? 4f : v;

    // ------------------------------------------------------------------
    // ISampleProvider.Read — オーディオスレッドから呼ばれる（GC負荷ゼロ）
    // ------------------------------------------------------------------
    public int Read(float[] buffer, int offset, int count)
    {
        try
        {
            Array.Clear(buffer, offset, count);

            if (count > _tempBuf.Length)
                count = _tempBuf.Length;

            int bank = _activeBank;
            float mstr = _muteMaster ? 0f : _masterVol;
            bool separate = _paSeparate;

            for (int pad = 0; pad < PadCount; pad++)
            {
                var src = _sources[bank, pad];
                if (src.State == PadPlayState.Idle) continue;

                var cat = _padCategories[bank, pad];
                float catVol = cat switch
                {
                    AudioCategory.BGM   => _muteBgm   ? 0f : _bgmVol,
                    AudioCategory.SE    => _muteSe    ? 0f : _seVol,
                    _                   => _muteMovie ? 0f : _movieVol
                };

                Array.Clear(_tempBuf, 0, count);
                src.Read(_tempBuf, 0, count);
                float gain = catVol * mstr;

                if (!separate)
                {
                    for (int i = 0; i < count; i++)
                        buffer[offset + i] += _tempBuf[i] * gain;
                }
                else
                {
                    // PAセパレート: L=BGM+MOVIE, R=SE
                    if (cat == AudioCategory.SE)
                    {
                        for (int i = 0; i < count - 1; i += 2)
                        {
                            float mono = (_tempBuf[i] + _tempBuf[i + 1]) * 0.5f * gain;
                            buffer[offset + i + 1] += mono;  // R のみ
                        }
                    }
                    else
                    {
                        for (int i = 0; i < count - 1; i += 2)
                        {
                            float mono = (_tempBuf[i] + _tempBuf[i + 1]) * 0.5f * gain;
                            buffer[offset + i] += mono;       // L のみ
                        }
                    }
                }
            }

        }
        catch
        {
            // レンダースレッドをクラッシュさせないため例外を飲み込んで無音を返す
            try { Array.Clear(buffer, offset, count); } catch { }
        }

        return count;
    }

    // ------------------------------------------------------------------
    // WASAPI/WaveOut 完全再初期化（PANIC時にセッションを閉じてループを解除）
    // Stop()+Play() はセッションを使い回すためドライバ側のループが残る場合がある。
    // Dispose()+Start() で IAudioClient を完全に閉じ、新規セッションを開く。
    // ------------------------------------------------------------------
    public void FlushOutput()
    {
        var oldWasapi  = _wasapiOut;
        var oldWaveOut = _waveOutFallback;
        _wasapiOut      = null;
        _waveOutFallback = null;

        try { oldWasapi?.Stop();    } catch { }
        try { oldWasapi?.Dispose(); } catch { }
        try { oldWaveOut?.Stop();    } catch { }
        try { oldWaveOut?.Dispose(); } catch { }

        Start();
    }

    public void Dispose()
    {
        _wasapiOut?.Stop();
        _wasapiOut?.Dispose();
        _waveOutFallback?.Stop();
        _waveOutFallback?.Dispose();

        for (int b = 0; b < BankCount; b++)
            for (int p = 0; p < PadCount; p++)
                _sources[b, p].Dispose();
    }
}
