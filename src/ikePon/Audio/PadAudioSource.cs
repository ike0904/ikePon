using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using ikePon.Model;

namespace ikePon.Audio;

/// <summary>
/// 1パッド分の音声ソース。全ファイルを完全プリロード（メモリ再生）に一本化。
/// Read() はオーディオスレッドから、Trigger/Stop は UI スレッドから呼ばれる。
/// </summary>
public sealed class PadAudioSource : ISampleProvider, IDisposable
{

    private readonly WaveFormat _format;
    private readonly object _lock = new();

    // 全ファイル完全プリロード（ストリーミング廃止）
    private float[]? _preloaded;
    private int _preloadTotal;
    private int _readPos;

    // State
    private volatile int _stateInt; // PadPlayState
    private FadeProcessor _fade;

    // Gain
    private float _fileGain = 1f;
    private float _padGain  = 1f;

    // Playback range
    private float _startSec     = 0f;
    private float _endSec       = -1f; // -1 = end of file
    private float _loopStartSec = -1f; // -1 = loop from _startSec
    private float _shortFadeSec = 0.5f;
    private bool  _shouldLoop   = false;

    // ループクロスフェード（1秒間）
    private bool _canCrossfade;   // 条件を満たすか（Trigger時に評価）
    private bool _inCrossfade;    // クロスフェード実行中
    private int  _xfadeReadPos;   // ループ側の読み取り位置
    private int  _xfadeSamples;   // クロスフェード総サンプル数（1秒）
    private int  _xfadeDone;      // 完了済みサンプル数

    public WaveFormat WaveFormat => _format;
    public PadPlayState State    => (PadPlayState)_stateInt;
    public float PlaybackPosition { get; private set; }
    public string? FilePath { get; private set; }
    public float FileTotalSec { get; private set; }

    /// <summary>フェードアウト中の現在ゲイン係数（1.0=開始, 0.0=無音）。再生中は 1.0。</summary>
    public float FadeGain =>
        (PadPlayState)_stateInt == PadPlayState.FadingOut ? _fade.Gain : 1f;

    public PadAudioSource(WaveFormat format) { _format = format; }

    // ------------------------------------------------------------------
    // Load / Unload
    // ------------------------------------------------------------------
    public bool Load(string filePath, int thresholdSecs, float fileGain, float padGain)
    {
        Unload();
        _fileGain = fileGain;
        _padGain  = padGain;

        try
        {
            using var reader = new AudioFileReader(filePath);
            double duration  = reader.TotalTime.TotalSeconds;
            var resampled    = ConvertToFormat(reader, _format);
            lock (_lock)
            {
                _preloaded    = resampled;
                _preloadTotal = resampled.Length;
                FilePath      = filePath;
                FileTotalSec  = _format.SampleRate > 0 && _format.Channels > 0
                    ? (float)_preloadTotal / (_format.SampleRate * _format.Channels)
                    : (float)duration;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void UpdateGain(float fileGain, float padGain)
    {
        _fileGain = fileGain;
        _padGain  = padGain;
    }

    public void Unload()
    {
        lock (_lock)
        {
            _stateInt        = (int)PadPlayState.Idle;
            _fade.Reset();
            _preloaded       = null;
            _preloadTotal    = 0;
            _readPos         = 0;
            FilePath         = null;
            FileTotalSec     = 0f;
            PlaybackPosition = 0f;
            _inCrossfade     = false;
            _canCrossfade    = false;
        }
    }

    // ------------------------------------------------------------------
    // Trigger / Stop
    // ------------------------------------------------------------------

    /// <summary>
    /// パッドをトリガー。再生中・フェード中問わず即座に startSec から再生開始。
    /// </summary>
    public void Trigger(float startSec, float endSec, float shortFadeSec, bool shouldLoop = false, float loopStartSec = -1f)
    {
        lock (_lock)
        {
            if (_preloaded == null) return;

            _startSec     = Math.Max(0f, startSec);
            _endSec       = endSec;
            _loopStartSec = loopStartSec;
            _shortFadeSec = shortFadeSec;
            _shouldLoop   = shouldLoop;

            if (_preloaded != null)
            {
                _readPos = Math.Clamp(
                    (int)(_startSec * _format.SampleRate * _format.Channels),
                    0, _preloadTotal);
            }

            _fade.Reset();
            _stateInt        = (int)PadPlayState.Playing;
            PlaybackPosition = 0f;
            _inCrossfade     = false;
            _canCrossfade    = EvalCanCrossfade();
        }
    }

    public void Stop(float fadeDuration)
    {
        lock (_lock)
        {
            var st = (PadPlayState)_stateInt;
            if (st == PadPlayState.Playing || st == PadPlayState.FadingOut)
            {
                _inCrossfade = false; // クロスフェード中断
                _fade.StartFadeOut(fadeDuration, _format.SampleRate * _format.Channels);
                _stateInt = (int)PadPlayState.FadingOut;
            }
        }
    }

    public void StopImmediate()
    {
        lock (_lock)
        {
            _stateInt        = (int)PadPlayState.Idle;
            _fade.Reset();
            PlaybackPosition = 0f;
            _inCrossfade     = false;
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            if ((PadPlayState)_stateInt == PadPlayState.Playing)
                _stateInt = (int)PadPlayState.Paused;
        }
    }

    public void Resume()
    {
        lock (_lock)
        {
            if ((PadPlayState)_stateInt == PadPlayState.Paused)
                _stateInt = (int)PadPlayState.Playing;
        }
    }

    // ------------------------------------------------------------------
    // ISampleProvider.Read — オーディオスレッドから呼ばれる
    // ------------------------------------------------------------------
    public int Read(float[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            PadPlayState st = (PadPlayState)_stateInt;

            if (st == PadPlayState.Idle || st == PadPlayState.Paused)
            {
                Array.Clear(buffer, offset, count);
                return count;
            }

            try
            {
                int actualRead = ReadSource(buffer, offset, count, st);

                float gain = _fileGain * _padGain;
                if (Math.Abs(gain - 1f) > 0.001f)
                    for (int i = 0; i < actualRead; i++) buffer[offset + i] *= gain;

                if (st == PadPlayState.FadingOut)
                {
                    bool done = _fade.Apply(buffer, offset, actualRead);
                    if (done)
                    {
                        if ((PadPlayState)_stateInt == PadPlayState.FadingOut)
                        {
                            _stateInt = (int)PadPlayState.Idle;
                            PlaybackPosition = 0f;
                        }
                    }
                }
            }
            catch
            {
                _stateInt = (int)PadPlayState.Idle;
                _fade.Reset();
                _inCrossfade = false;
                Array.Clear(buffer, offset, count);
            }

            return count;
        }
    }

    private int ReadSource(float[] buffer, int offset, int count, PadPlayState st)
    {
        if (_preloaded == null) { Array.Clear(buffer, offset, count); return count; }

        int sr = _format.SampleRate, ch = _format.Channels;
        int written = 0;

        while (written < count)
        {
            if (_inCrossfade)
            {
                // ── クロスフェードブレンド ──────────────────────────
                int remaining  = count - written;
                int xfLeft     = _xfadeSamples - _xfadeDone;
                int toProcess  = Math.Min(remaining, xfLeft);

                for (int i = 0; i < toProcess; i++)
                {
                    float t     = (float)(_xfadeDone + i) / _xfadeSamples;
                    float angle = t * (MathF.PI / 2f);
                    float main  = (_readPos     + i < _preloadTotal) ? _preloaded[_readPos     + i] : 0f;
                    float loop  = (_xfadeReadPos + i < _preloadTotal) ? _preloaded[_xfadeReadPos + i] : 0f;
                    buffer[offset + written + i] = main * MathF.Cos(angle) + loop * MathF.Sin(angle);
                }

                _readPos      += toProcess;
                _xfadeReadPos += toProcess;
                _xfadeDone    += toProcess;
                written       += toProcess;
                PlaybackPosition = _preloadTotal > 0 ? (float)_readPos / _preloadTotal : 0f;

                if (_xfadeDone >= _xfadeSamples)
                {
                    // クロスフェード完了：ループ側の位置へ移行
                    _inCrossfade = false;
                    _readPos     = _xfadeReadPos;
                    PlaybackPosition = _preloadTotal > 0 ? (float)_readPos / _preloadTotal : 0f;
                }
                continue;
            }

            // ── 通常読み取り ─────────────────────────────────────
            int available = _preloadTotal - _readPos;
            if (available <= 0) { break; }

            // クロスフェード開始位置で区切る
            int readLimit = count - written;
            if (_canCrossfade && _endSec > 0f && _shouldLoop && st == PadPlayState.Playing)
            {
                int xfStart = Math.Max(0, (int)((_endSec - 0.5f) * sr * ch));
                if (_readPos < xfStart)
                    readLimit = Math.Min(readLimit, xfStart - _readPos);
            }

            int toRead = Math.Min(readLimit, available);
            if (toRead > 0)
            {
                Array.Copy(_preloaded, _readPos, buffer, offset + written, toRead);
                _readPos += toRead;
                written  += toRead;
                PlaybackPosition = _preloadTotal > 0 ? (float)_readPos / _preloadTotal : 0f;
            }

            // クロスフェード開始判定
            if (_canCrossfade && _endSec > 0f && _shouldLoop && st == PadPlayState.Playing)
            {
                int xfStart = Math.Max(0, (int)((_endSec - 0.5f) * sr * ch));
                if (_readPos >= xfStart && !_inCrossfade)
                {
                    float loopTo  = _loopStartSec >= 0f ? _loopStartSec : _startSec;
                    _xfadeReadPos = Math.Max(0, (int)((loopTo - 0.5f) * sr * ch));
                    _xfadeSamples = sr * ch;
                    _xfadeDone    = 0;
                    _inCrossfade  = true;
                    continue; // クロスフェード処理へ
                }
            }

            // endSec チェック（クロスフェード非適用時）
            if (_endSec > 0f && st == PadPlayState.Playing)
            {
                float curSec = sr > 0 && ch > 0 ? (float)_readPos / (sr * ch) : 0f;
                if (curSec >= _endSec)
                {
                    if (_shouldLoop)
                    {
                        float loopTo = _loopStartSec >= 0f ? _loopStartSec : _startSec;
                        _readPos = Math.Clamp((int)(loopTo * sr * ch), 0, _preloadTotal);
                        PlaybackPosition = _preloadTotal > 0 ? (float)_readPos / _preloadTotal : 0f;
                        continue;
                    }
                    else { TriggerEndFade(); break; }
                }
            }

            // ファイル終端チェック
            if (_readPos >= _preloadTotal)
            {
                var cur = (PadPlayState)_stateInt;
                if (cur == PadPlayState.Playing)
                {
                    if (_shouldLoop)
                    {
                        float loopTo = _loopStartSec >= 0f ? _loopStartSec : _startSec;
                        _readPos = Math.Clamp((int)(loopTo * sr * ch), 0, _preloadTotal);
                        PlaybackPosition = _preloadTotal > 0 ? (float)_readPos / _preloadTotal : 0f;
                        continue;
                    }
                    else { _stateInt = (int)PadPlayState.Idle; _fade.Reset(); PlaybackPosition = 0f; break; }
                }
                else if (cur == PadPlayState.FadingOut)
                {
                    _stateInt = (int)PadPlayState.Idle; _fade.Reset(); PlaybackPosition = 0f; break;
                }
                else break;
            }

            if (toRead == 0) break; // 安全弁
        }

        if (written < count) Array.Clear(buffer, offset + written, count - written);
        return written > 0 ? written : count;
    }

    public void SeekToFraction(float fraction)
    {
        lock (_lock)
        {
            if (_preloaded == null || (PadPlayState)_stateInt == PadPlayState.Idle) return;
            _readPos = Math.Clamp((int)(fraction * _preloadTotal), 0, _preloadTotal);
            PlaybackPosition = _preloadTotal > 0 ? (float)_readPos / _preloadTotal : 0f;
            _inCrossfade = false;
        }
    }

    public void SetLoop(bool loop)
    {
        lock (_lock)
        {
            _shouldLoop   = loop;
            _canCrossfade = EvalCanCrossfade();
        }
    }

    /// <summary>クロスフェード適用条件を評価する。</summary>
    private bool EvalCanCrossfade()
    {
        if (!_shouldLoop || _endSec <= 0f) return false;
        float loopTo = _loopStartSec >= 0f ? _loopStartSec : _startSec;
        return (FileTotalSec - _endSec >= 0.5f) && (loopTo >= 0.5f);
    }

    private void TriggerEndFade()
    {
        lock (_lock)
        {
            if ((PadPlayState)_stateInt == PadPlayState.Playing)
            {
                _inCrossfade = false;
                _fade.StartFadeOut(_shortFadeSec, _format.SampleRate * _format.Channels);
                _stateInt = (int)PadPlayState.FadingOut;
            }
        }
    }

    // ------------------------------------------------------------------
    // ヘルパー
    // ------------------------------------------------------------------

    /// <summary>全データを一括デコード＆フォーマット変換（ロード時のみ実行）。</summary>
    private static float[] ConvertToFormat(AudioFileReader reader, WaveFormat target)
    {
        ISampleProvider p = reader;
        if (p.WaveFormat.SampleRate != target.SampleRate)
            p = new WdlResamplingSampleProvider(p, target.SampleRate);
        if (p.WaveFormat.Channels != target.Channels)
            p = target.Channels == 2
                ? (ISampleProvider)new MonoToStereoSampleProvider(p)
                : new StereoToMonoSampleProvider(p);

        var list = new List<float>(target.SampleRate * target.Channels * 10);
        var buf  = new float[4096];
        int maxSamples = target.SampleRate * target.Channels * 600; // 10分上限（コーデックが無限ループする場合の安全策）
        // n==0 が連続しても即終了せずリトライ（WDL resampler warmup / MF デコーダ初期化対策）
        int zeros = 0;
        while (zeros < 10 && list.Count < maxSamples)
        {
            int n = p.Read(buf, 0, buf.Length);
            if (n > 0) { zeros = 0; for (int i = 0; i < n; i++) list.Add(buf[i]); }
            else zeros++;
        }
        return list.ToArray();
    }

    public void Dispose() => Unload();
}
