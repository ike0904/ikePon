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
    private NAudio.Wave.WaveOut? _waveOutFallback;

    private volatile int _activeBank;
    private volatile float _masterVol = 1f;
    private volatile float _bgmVol = 1f;
    private volatile float _seVol = 1f;
    private volatile float _movieVol = 1f;
    private volatile bool _paSeparate;

    private float[] _tempBuf = new float[16384];

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
        try
        {
            _wasapiOut = new WasapiOut(AudioClientShareMode.Shared, latencyMs);
            _wasapiOut.Init(this);
            _wasapiOut.Play();
        }
        catch
        {
            // WaveOut フォールバック
            _waveOutFallback = new NAudio.Wave.WaveOut { DesiredLatency = 100 };
            _waveOutFallback.Init(new NAudio.Wave.SampleProviders.SampleToWaveProvider(this));
            _waveOutFallback.Play();
        }
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
    public float MasterVolume { get => _masterVol; set => _masterVol = Clamp01(value); }
    public float BgmVolume    { get => _bgmVol;    set => _bgmVol    = Clamp01(value); }
    public float SeVolume     { get => _seVol;     set => _seVol     = Clamp01(value); }
    public float MovieVolume  { get => _movieVol;  set => _movieVol  = Clamp01(value); }
    public bool  PaSeparate   { get => _paSeparate; set => _paSeparate = value; }

    private static float Clamp01(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

    // ------------------------------------------------------------------
    // ISampleProvider.Read — オーディオスレッドから呼ばれる（GC負荷ゼロ）
    // ------------------------------------------------------------------
    public int Read(float[] buffer, int offset, int count)
    {
        Array.Clear(buffer, offset, count);

        if (count > _tempBuf.Length)
            _tempBuf = new float[count + 512];

        int bank = _activeBank;
        float mstr = _masterVol;
        bool separate = _paSeparate;

        for (int pad = 0; pad < PadCount; pad++)
        {
            var src = _sources[bank, pad];
            if (src.State == PadPlayState.Idle) continue;

            var cat = _padCategories[bank, pad];
            float catVol = cat switch
            {
                AudioCategory.BGM   => _bgmVol,
                AudioCategory.SE    => _seVol,
                _                   => _movieVol
            };

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

        return count;
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
