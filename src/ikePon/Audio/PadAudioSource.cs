using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using ikePon.Model;

namespace ikePon.Audio;

/// <summary>
/// 1パッド分の音声ソース。プリロード（≤10秒）とストリーミング（>10秒）を自動切り替え。
/// Read() はオーディオスレッドから、Trigger/Stop は UI スレッドから呼ばれる。
/// </summary>
public sealed class PadAudioSource : ISampleProvider, IDisposable
{
    private readonly WaveFormat _format;
    private readonly object _lock = new();

    // Preloaded
    private float[]? _preloaded;
    private int _preloadTotal;
    private int _readPos;

    // Streaming — _reader は seek 用、_streamProvider は変換済みの読み取り用
    private AudioFileReader? _reader;
    private ISampleProvider? _streamProvider;

    // State
    private volatile int _stateInt; // PadPlayState
    private FadeProcessor _fade;

    // Gain
    private float _fileGain = 1f;
    private float _padGain  = 1f;

    // Playback range
    private float _startSec = 0f;
    private float _endSec   = -1f; // -1 = end of file
    private float _shortFadeSec = 0.5f;

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
            using var probe = new AudioFileReader(filePath);
            double duration = probe.TotalTime.TotalSeconds;

            if (duration <= thresholdSecs)
            {
                // 全データをRAMにデコード（フォーマット変換込み）
                var resampled = ConvertToFormat(probe, _format);
                lock (_lock)
                {
                    _preloaded      = resampled;
                    _preloadTotal   = resampled.Length;
                    _reader         = null;
                    _streamProvider = null;
                    FilePath        = filePath;
                    FileTotalSec    = (float)duration;
                }
            }
            else
            {
                // ストリーミング（フォーマット変換ラッパー付き）
                var reader   = new AudioFileReader(filePath);
                var provider = BuildStreamProvider(reader);
                lock (_lock)
                {
                    _reader         = reader;
                    _streamProvider = provider;
                    _preloaded      = null;
                    FilePath        = filePath;
                    FileTotalSec    = (float)duration;
                }
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
            _stateInt       = (int)PadPlayState.Idle;
            _fade.Reset();
            _reader?.Dispose();
            _reader         = null;
            _streamProvider = null;
            _preloaded      = null;
            _readPos        = 0;
            FilePath        = null;
            FileTotalSec    = 0f;
            PlaybackPosition = 0f;
        }
    }

    // ------------------------------------------------------------------
    // Trigger / Stop
    // ------------------------------------------------------------------

    /// <summary>
    /// パッドをトリガー。再生中・フェード中問わず即座に startSec から再生開始。
    /// </summary>
    public void Trigger(float startSec, float endSec, float shortFadeSec)
    {
        lock (_lock)
        {
            if (_preloaded == null && _reader == null) return;

            _startSec     = Math.Max(0f, startSec);
            _endSec       = endSec;
            _shortFadeSec = shortFadeSec;

            if (_preloaded != null)
            {
                // プリロード: サンプル位置を計算
                _readPos = Math.Clamp(
                    (int)(_startSec * _format.SampleRate * _format.Channels),
                    0, _preloadTotal);
            }
            else if (_reader != null)
            {
                // ストリーミング: 開始位置にシーク
                SeekReaderToSec(_startSec);
                _streamProvider = BuildStreamProvider(_reader);
            }

            _fade.Reset();
            _stateInt = (int)PadPlayState.Playing;
            PlaybackPosition = 0f;
        }
    }

    public void Stop(float fadeDuration)
    {
        lock (_lock)
        {
            var st = (PadPlayState)_stateInt;
            if (st == PadPlayState.Playing || st == PadPlayState.FadingOut)
            {
                _fade.StartFadeOut(fadeDuration, _format.SampleRate * _format.Channels);
                _stateInt = (int)PadPlayState.FadingOut;
            }
        }
    }

    public void StopImmediate()
    {
        lock (_lock)
        {
            _stateInt = (int)PadPlayState.Idle;
            _fade.Reset();
            PlaybackPosition = 0f;
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

            if (st == PadPlayState.Idle)
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
                            _stateInt = (int)PadPlayState.Idle;
                    }
                }
            }
            catch
            {
                _stateInt = (int)PadPlayState.Idle;
                _fade.Reset();
                Array.Clear(buffer, offset, count);
            }

            return count;
        }
    }

    private int ReadSource(float[] buffer, int offset, int count, PadPlayState st)
    {
        if (_preloaded != null)
        {
            int available = _preloadTotal - _readPos;
            int toRead    = Math.Min(count, available);

            if (toRead > 0) Array.Copy(_preloaded, _readPos, buffer, offset, toRead);
            _readPos += toRead;

            if (toRead < count) Array.Clear(buffer, offset + toRead, count - toRead);

            PlaybackPosition = _preloadTotal > 0 ? (float)_readPos / _preloadTotal : 0f;

            // 終了位置チェック
            if (_endSec > 0 && st == PadPlayState.Playing)
            {
                float currentSec = _format.SampleRate > 0 && _format.Channels > 0
                    ? (float)_readPos / (_format.SampleRate * _format.Channels)
                    : 0f;
                if (currentSec >= _endSec)
                    TriggerEndFade();
            }

            if (_readPos >= _preloadTotal)
            {
                var curState = (PadPlayState)_stateInt;
                if (curState == PadPlayState.Playing || curState == PadPlayState.FadingOut)
                {
                    _stateInt = (int)PadPlayState.Idle;
                    _fade.Reset();
                    PlaybackPosition = 0f;
                }
            }
            return toRead;
        }

        // ストリーミング: 1回の Read のみ（複数回ループするとステレオL/Rフレームがずれてノイズになる）
        var provider = _streamProvider ?? (ISampleProvider?)_reader;
        if (provider != null)
        {
            int totalRead = provider.Read(buffer, offset, count);

            if (totalRead < count)
                Array.Clear(buffer, offset + totalRead, count - totalRead);

            if (_reader != null && _reader.Length > 0)
                PlaybackPosition = (float)_reader.Position / _reader.Length;

            // 終了位置チェック
            if (_endSec > 0 && st == PadPlayState.Playing && _reader != null)
            {
                double totalSecs = _reader.TotalTime.TotalSeconds;
                if (totalSecs > 0 && _reader.Length > 0)
                {
                    float currentSec = (float)(_reader.Position / (double)_reader.Length * totalSecs);
                    if (currentSec >= _endSec)
                        TriggerEndFade();
                }
            }

            bool streamEof = (totalRead == 0) ||
                             (_reader != null && _reader.Length > 0 && _reader.Position >= _reader.Length);
            if (streamEof)
            {
                var curState = (PadPlayState)_stateInt;
                if (curState == PadPlayState.Playing || curState == PadPlayState.FadingOut)
                {
                    _stateInt = (int)PadPlayState.Idle;
                    _fade.Reset();
                    PlaybackPosition = 0f;
                }
            }

            return totalRead;
        }

        Array.Clear(buffer, offset, count);
        return 0;
    }

    private void TriggerEndFade()
    {
        lock (_lock)
        {
            if ((PadPlayState)_stateInt == PadPlayState.Playing)
            {
                _fade.StartFadeOut(_shortFadeSec, _format.SampleRate * _format.Channels);
                _stateInt = (int)PadPlayState.FadingOut;
            }
        }
    }

    // ------------------------------------------------------------------
    // ヘルパー
    // ------------------------------------------------------------------

    private void SeekReaderToSec(float sec)
    {
        if (_reader == null) return;
        long byteOffset = 0;
        if (sec > 0 && _reader.TotalTime.TotalSeconds > 0 && _reader.Length > 0)
        {
            byteOffset = (long)(sec / _reader.TotalTime.TotalSeconds * _reader.Length);
            byteOffset = Math.Clamp(byteOffset, 0, _reader.Length);
            if (_reader.BlockAlign > 0)
                byteOffset -= byteOffset % _reader.BlockAlign;
        }
        _reader.Seek(byteOffset, SeekOrigin.Begin);
    }

    /// <summary>
    /// AudioFileReader をエンジンフォーマット（44100Hz ステレオ）に変換する ISampleProvider を構築。
    /// ストリーミングパスで seek 後にも呼ぶ（リサンプラー内部状態をリセット）。
    /// </summary>
    private ISampleProvider BuildStreamProvider(AudioFileReader reader)
    {
        ISampleProvider p = reader;
        if (p.WaveFormat.SampleRate != _format.SampleRate)
            p = new WdlResamplingSampleProvider(p, _format.SampleRate);
        if (p.WaveFormat.Channels != _format.Channels)
            p = _format.Channels == 2
                ? (ISampleProvider)new MonoToStereoSampleProvider(p)
                : new StereoToMonoSampleProvider(p);
        return p;
    }

    /// <summary>プリロード用: 全データを一括デコード＆フォーマット変換。</summary>
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
        // n==0 が連続しても即終了せずリトライ（WDL resampler warmup / MF デコーダ初期化対策）
        int zeros = 0;
        while (zeros < 10)
        {
            int n = p.Read(buf, 0, buf.Length);
            if (n > 0) { zeros = 0; for (int i = 0; i < n; i++) list.Add(buf[i]); }
            else zeros++;
        }
        return list.ToArray();
    }

    public void Dispose() => Unload();
}
