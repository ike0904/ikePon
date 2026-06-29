using System.IO;
using NAudio.Wave;
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

    // Streaming
    private AudioFileReader? _reader;
    private long _streamLength;

    // State (protected by _lock for writes; volatile-read from audio thread)
    private volatile int _stateInt; // PadPlayState
    private int _readPos;           // sample index into _preloaded
    private FadeProcessor _fade;

    // Info
    private float _fileGain = 1f;
    private float _padGain = 1f;
    private long _totalPcmSamples; // for position reporting

    public WaveFormat WaveFormat => _format;
    public PadPlayState State => (PadPlayState)_stateInt;
    public float PlaybackPosition { get; private set; }
    public string? FilePath { get; private set; }

    /// <summary>
    /// フェードアウト中の現在ゲイン係数（1.0=開始, 0.0=無音）。再生中は 1.0 を返す。
    /// </summary>
    public float FadeGain =>
        (PadPlayState)_stateInt == PadPlayState.FadingOut ? _fade.Gain : 1f;

    public PadAudioSource(WaveFormat format)
    {
        _format = format;
    }

    /// <summary>
    /// バンク読み込み時にファイルをロードする（バックグラウンドスレッド可）
    /// </summary>
    public bool Load(string filePath, int thresholdSecs, float fileGain, float padGain)
    {
        Unload();
        FilePath = filePath;
        _fileGain = fileGain;
        _padGain = padGain;

        try
        {
            using var probe = new AudioFileReader(filePath);
            double duration = probe.TotalTime.TotalSeconds;

            if (duration <= thresholdSecs)
            {
                // Fully decode into RAM
                var resampled = ConvertToFormat(probe, _format);
                lock (_lock)
                {
                    _preloaded = resampled;
                    _preloadTotal = resampled.Length;
                    _reader = null;
                    _totalPcmSamples = resampled.Length / _format.Channels;
                }
            }
            else
            {
                // Streaming
                var reader = new AudioFileReader(filePath);
                lock (_lock)
                {
                    _reader = reader;
                    _preloaded = null;
                    _streamLength = reader.Length;
                    _totalPcmSamples = reader.Length / (reader.WaveFormat.BitsPerSample / 8);
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
            _stateInt = (int)PadPlayState.Idle;
            _fade.Reset();
            _reader?.Dispose();
            _reader = null;
            _preloaded = null;
            _readPos = 0;
            FilePath = null;
            PlaybackPosition = 0f;
        }
    }

    /// <summary>
    /// パッドをトリガー（UIスレッドから）
    /// </summary>
    public void Trigger(AppSettings settings)
    {
        lock (_lock)
        {
            var st = (PadPlayState)_stateInt;
            if (st == PadPlayState.Playing || st == PadPlayState.FadingOut)
            {
                // 再生中：フェードアウト開始
                _fade.StartFadeOut(settings.LongFadeDuration, _format.SampleRate);
                _stateInt = (int)PadPlayState.FadingOut;
            }
            else
            {
                // 停止中：最初から再生
                _readPos = 0;
                _reader?.Seek(0, SeekOrigin.Begin);
                _fade.Reset();
                _stateInt = (int)PadPlayState.Playing;
                PlaybackPosition = 0f;
            }
        }
    }

    /// <summary>
    /// フェードアウト停止（UIスレッドから）
    /// </summary>
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

    /// <summary>
    /// 即座に停止（UIスレッドから）
    /// </summary>
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

            // ゲイン適用
            float gain = _fileGain * _padGain;
            if (Math.Abs(gain - 1f) > 0.001f)
            {
                for (int i = 0; i < count; i++)
                    buffer[offset + i] *= gain;
            }

            // フェード適用
            if (st == PadPlayState.FadingOut)
            {
                bool done;
                lock (_lock) done = _fade.Apply(buffer, offset, count);
                if (done)
                {
                    lock (_lock)
                    {
                        if ((PadPlayState)_stateInt == PadPlayState.FadingOut)
                            _stateInt = (int)PadPlayState.Idle;
                    }
                }
            }
        }
        catch
        {
            // 予期しないエラー時は即座に停止してバッファをクリア
            lock (_lock) { _stateInt = (int)PadPlayState.Idle; _fade.Reset(); }
            Array.Clear(buffer, offset, count);
        }

        return count;
    }

    private int ReadSource(float[] buffer, int offset, int count, PadPlayState st)
    {
        if (_preloaded != null)
        {
            int available = _preloadTotal - _readPos;
            int toRead = Math.Min(count, available);

            Array.Copy(_preloaded, _readPos, buffer, offset, toRead);
            _readPos += toRead;

            if (toRead < count)
                Array.Clear(buffer, offset + toRead, count - toRead);

            if (_preloadTotal > 0)
                PlaybackPosition = (float)_readPos / _preloadTotal;

            if (_readPos >= _preloadTotal && st == PadPlayState.Playing)
            {
                lock (_lock)
                {
                    if ((PadPlayState)_stateInt == PadPlayState.Playing)
                        _stateInt = (int)PadPlayState.Idle;
                }
                PlaybackPosition = 0f;
            }
            return toRead;
        }

        if (_reader != null)
        {
            int read = _reader.Read(buffer, offset, count);
            if (read < count)
                Array.Clear(buffer, offset + read, count - read);

            long pos = _reader.Position;
            long len = _reader.Length;
            if (len > 0) PlaybackPosition = (float)pos / len;

            if (read < count && st == PadPlayState.Playing)
            {
                lock (_lock)
                {
                    if ((PadPlayState)_stateInt == PadPlayState.Playing)
                        _stateInt = (int)PadPlayState.Idle;
                }
                PlaybackPosition = 0f;
            }
            return read;
        }

        Array.Clear(buffer, offset, count);
        return count;
    }

    // ------------------------------------------------------------------
    // ヘルパー: float[] へフルデコード（プリロード用）
    // ------------------------------------------------------------------
    private static float[] ConvertToFormat(AudioFileReader reader, WaveFormat targetFormat)
    {
        ISampleProvider provider = reader;

        // サンプルレート変換
        if (provider.WaveFormat.SampleRate != targetFormat.SampleRate)
            provider = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(provider, targetFormat.SampleRate);

        // チャンネル変換
        if (provider.WaveFormat.Channels != targetFormat.Channels)
        {
            provider = targetFormat.Channels == 2
                ? (ISampleProvider)new NAudio.Wave.SampleProviders.MonoToStereoSampleProvider(provider)
                : new NAudio.Wave.SampleProviders.StereoToMonoSampleProvider(provider);
        }

        var list = new List<float>(44100 * targetFormat.Channels * 10);
        var buf = new float[4096];
        int n;
        while ((n = provider.Read(buf, 0, buf.Length)) > 0)
        {
            for (int i = 0; i < n; i++) list.Add(buf[i]);
        }
        return list.ToArray();
    }

    public void Dispose()
    {
        Unload();
    }
}
