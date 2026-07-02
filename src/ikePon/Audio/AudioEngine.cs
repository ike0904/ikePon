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
    private int _latencyMs = 30;

    private volatile int _activeBank;
    private volatile float _masterVol = 1f;
    private volatile float _bgmVol = 1f;
    private volatile float _seVol = 1f;
    private volatile float _movieVol = 1f;
    private volatile bool _paSeparate;

    private readonly float[] _tempBuf = new float[65536];
    private bool _wasActive; // 前回Readで1つ以上のパッドが再生中だったか

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

    public void Start(int latencyMs = 30)
    {
        _latencyMs = latencyMs;

        // WaveOutEvent を優先使用（WASAPI shared mode より安定; バッファ管理がシンプルで確実）
        try
        {
            var provider = new NAudio.Wave.SampleProviders.SampleToWaveProvider(this);
            var woe = new NAudio.Wave.WaveOutEvent { DesiredLatency = 100, NumberOfBuffers = 3 };
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

        // WaveOutEvent 失敗時のフォールバック: WASAPI Shared
        try
        {
            _wasapiOut = new WasapiOut(AudioClientShareMode.Shared, latencyMs);
            _wasapiOut.Init(this);
            _wasapiOut.Play();
        }
        catch { /* 両方失敗した場合は無音で動作継続 */ }
    }

    // ------------------------------------------------------------------
    // ソース管理
    // ------------------------------------------------------------------
    public PadAudioSource GetSource(int bankIdx, int padIdx) => _sources[bankIdx, padIdx];

    public void SetPadCategory(int bankIdx, int padIdx, AudioCategory cat)
    {
        _padCategories[bankIdx, padIdx] = cat;
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
            float mstr = _masterVol;
            bool separate = _paSeparate;
            bool anyActive = false;

            for (int pad = 0; pad < PadCount; pad++)
            {
                var src = _sources[bank, pad];
                if (src.State == PadPlayState.Idle) continue;
                anyActive = true;

                var cat = _padCategories[bank, pad];
                float catVol = cat switch
                {
                    AudioCategory.BGM   => _bgmVol,
                    AudioCategory.SE    => _seVol,
                    _                   => _movieVol
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

            // 再生中→全Idle になった瞬間だけログ（ハードウェアが本当に無音を受け取っているかの確認用）
            if (_wasActive && !anyActive)
                PadAudioSource.DiagLog.Enqueue($"{DateTime.Now:HH:mm:ss.fff} ENGINE_ALL_IDLE bank={bank} outputting silence");
            _wasActive = anyActive;
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

        Start(_latencyMs);
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
