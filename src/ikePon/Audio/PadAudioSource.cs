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

    public WaveFormat WaveFormat => _format;
    public PadPlayState State    => (PadPlayState)_stateInt;
    public float PlaybackPosition { get; private set; }
    public string? FilePath { get; private set; }

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
                    _preloaded    = resampled;
                    _preloadTotal = resampled.Length;
                    _reader       = null;
                    _streamProvider = null;
                    FilePath      = filePath;
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
                }
            }
            return true;
        }
        catch
        {
            return false;
        }
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
            PlaybackPosition = 0f;
        }
    }

    // ------------------------------------------------------------------
    // Trigger / Stop
    // ------------------------------------------------------------------
    public void Trigger(AppSettings settings)
    {
        lock (_lock)
        {
            if (_preloaded == null && _reader == null) return; // データなし

            var st = (PadPlayState)_stateInt;
            if (st == PadPlayState.Playing || st == PadPlayState.FadingOut)
            {
                _fade.StartFadeOut(settings.LongFadeDuration, _format.SampleRate);
                _stateInt = (int)PadPlayState.FadingOut;
            }
            else
            {
                _readPos = 0;
                if (_reader != null)
                {
                    _reader.Seek(0, SeekOrigin.Begin);
                    // ストリーミングは seek 後にプロバイダを再生成（リサンプラー内部状態リセット）
                    _streamProvider = BuildStreamProvider(_reader);
                }
                _fade.Reset();
                _stateInt = (int)PadPlayState.Playing;
                PlaybackPosition = 0f;
            }
        }
    }

    public void Stop(float fadeDuration)
    {
        lock (_lock)
        {
            var st = (PadPlayState)_stateInt;
            if (st == PadPlayState.Playing || st == PadPlayState.FadingOut)
            {
                _fade.StartFadeOut(fadeDuration, _format.SampleRate);
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
        PadPlayState st;
        lock (_lock) st = (PadPlayState)_stateInt;

        if (st == PadPlayState.Idle)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }

        try
        {
            ReadSource(buffer, offset, count, st);

            float gain = _fileGain * _padGain;
            if (Math.Abs(gain - 1f) > 0.001f)
                for (int i = 0; i < count; i++) buffer[offset + i] *= gain;

            if (st == PadPlayState.FadingOut)
            {
                bool done;
                lock (_lock) done = _fade.Apply(buffer, offset, count);
                if (done)
                    lock (_lock)
                    {
                        if ((PadPlayState)_stateInt == PadPlayState.FadingOut)
                            _stateInt = (int)PadPlayState.Idle;
                    }
            }
        }
        catch
        {
            lock (_lock) { _stateInt = (int)PadPlayState.Idle; _fade.Reset(); }
            Array.Clear(buffer, offset, count);
        }

        return count;
    }

    private void ReadSource(float[] buffer, int offset, int count, PadPlayState st)
    {
        if (_preloaded != null)
        {
            int available = _preloadTotal - _readPos;
            int toRead    = Math.Min(count, available);

            if (toRead > 0) Array.Copy(_preloaded, _readPos, buffer, offset, toRead);
            _readPos += toRead;

            if (toRead < count) Array.Clear(buffer, offset + toRead, count - toRead);

            PlaybackPosition = _preloadTotal > 0 ? (float)_readPos / _preloadTotal : 0f;

            if (_readPos >= _preloadTotal && st == PadPlayState.Playing)
            {
                lock (_lock)
                    if ((PadPlayState)_stateInt == PadPlayState.Playing)
                        _stateInt = (int)PadPlayState.Idle;
                PlaybackPosition = 0f;
            }
            return;
        }

        // ストリーミング: フォーマット変換済みプロバイダで読み取る
        var provider = _streamProvider ?? (ISampleProvider?)_reader;
        if (provider != null)
        {
            int read = provider.Read(buffer, offset, count);
            if (read < count) Array.Clear(buffer, offset + read, count - read);

            if (_reader != null && _reader.Length > 0)
                PlaybackPosition = (float)_reader.Position / _reader.Length;

            if (read < count && st == PadPlayState.Playing)
            {
                lock (_lock)
                    if ((PadPlayState)_stateInt == PadPlayState.Playing)
                        _stateInt = (int)PadPlayState.Idle;
                PlaybackPosition = 0f;
            }
            return;
        }

        Array.Clear(buffer, offset, count);
    }

    // ------------------------------------------------------------------
    // ヘルパー
    // ------------------------------------------------------------------

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
        int n;
        while ((n = p.Read(buf, 0, buf.Length)) > 0)
            for (int i = 0; i < n; i++) list.Add(buf[i]);
        return list.ToArray();
    }

    public void Dispose() => Unload();
}
