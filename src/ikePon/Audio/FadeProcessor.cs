namespace ikePon.Audio;

internal struct FadeProcessor
{
    private int _totalSamples;
    private int _currentSample;
    public bool IsActive;

    public void StartFadeOut(float durationSecs, int sampleRate)
    {
        _totalSamples = Math.Max(1, (int)(durationSecs * sampleRate));
        _currentSample = 0;
        IsActive = true;
    }

    /// <summary>
    /// バッファにフェードアウト乗算を適用する。完了したら true を返す。
    /// </summary>
    public bool Apply(float[] buffer, int offset, int count)
    {
        if (!IsActive) return true;

        for (int i = 0; i < count; i++)
        {
            if (_currentSample >= _totalSamples)
            {
                buffer[offset + i] = 0f;
            }
            else
            {
                float t = (float)_currentSample / _totalSamples;
                buffer[offset + i] *= 1f - t;
                _currentSample++;
            }
        }

        if (_currentSample >= _totalSamples)
        {
            IsActive = false;
            return true;
        }
        return false;
    }

    public void Reset() { IsActive = false; _currentSample = 0; }
}
