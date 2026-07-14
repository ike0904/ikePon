using System;
using System.Threading.Tasks;
using System.Windows;
using ikePon.Model;
using ikePon.UI.Windows;
using LibVLCSharp.Shared;

namespace ikePon.Controller;

public sealed class MovieController : IDisposable
{
    private readonly AppSettings _settings;
    private MovieWindow? _window;

    private LibVLC? _libVLC;
    private volatile bool _vlcReady;
    private volatile bool _pendingOpen;

    public bool DisplayActive  { get; private set; }
    public bool IsFullScreen   => _settings.MovieMode == MovieDisplayMode.FullScreen;
    public bool IsBuffering    => _window?.IsBuffering ?? false;
    public bool IsFading       => _window?.IsFading    ?? false;

    /// <summary>DISPが点灯中で映像もスタンバイ画像も表示されていない（黒画面）場合に true</summary>
    public bool IsBlackScreen  => DisplayActive && (_window?.IsBlackScreen ?? false);

    public event Action<bool>? DisplayActiveChanged;
    public event Action<bool>? FullScreenChanged;
    public event Action<string>? StatusMessage;
    public event Action? VideoLoopEndReached;
    public event Action<long>? VideoShown;

    public MovieController(AppSettings settings)
    {
        _settings     = settings;
        DisplayActive = false; // 起動時は常にOFF
        _pendingOpen  = false;

        // バックグラウンドでVLC初期化（初回DISP押下の待ち時間を解消）
        Task.Run(() =>
        {
            try
            {
                Core.Initialize();
                _libVLC = new LibVLC("--no-audio", "--aout=none", "--no-video-title-show");
                _vlcReady = true;

                if (_pendingOpen)
                {
                    _pendingOpen = false;
                    Application.Current?.Dispatcher.BeginInvoke(new Action(OpenDisplay));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MovieController] VLC init error: {ex}");
            }
        });
    }

    public void OpenDisplay()
    {
        if (!_vlcReady)
        {
            StatusMessage?.Invoke(L.S("Str_Ctrl_MovieInit"));
            _pendingOpen = true;
            // VLC がちょうど初期化完了した場合のダブルチェック（競合状態対策）
            if (!_vlcReady) return;
            _pendingOpen = false;
        }

        if (DisplayActive && _window != null && _window.IsVisible) return;
        DisplayActive = true;
        _settings.DisplayOutputActive = true;

        if (_window == null || !_window.IsVisible)
        {
            _window = new MovieWindow(_settings, _libVLC!);
            _window.FullScreenChanged += OnWindowFullScreenChanged;
            _window.LoopEndReached    += OnWindowLoopEndReached;
            _window.VideoShown        += OnWindowVideoShown;
            _window.Closed += OnWindowClosed;
            _window.Show();
            _window.ShowStandby(); // 初期スタンバイ画像表示（映像再生時はすぐ上書きされる）

            if (_settings.MovieMode == MovieDisplayMode.FullScreen)
                _window.SetFullScreen(true);
        }

        DisplayActiveChanged?.Invoke(true);
    }

    public void CloseDisplay()
    {
        _pendingOpen = false;
        DisplayActive = false;
        _settings.DisplayOutputActive = false;

        if (_window != null)
        {
            _window.FullScreenChanged -= OnWindowFullScreenChanged;
            _window.LoopEndReached    -= OnWindowLoopEndReached;
            _window.VideoShown        -= OnWindowVideoShown;
            _window.Closed -= OnWindowClosed;
            _window.Close();
            _window = null;
        }

        DisplayActiveChanged?.Invoke(false);
        FullScreenChanged?.Invoke(IsFullScreen);
    }

    public void ToggleDisplay()
    {
        if (DisplayActive) CloseDisplay();
        else OpenDisplay();
    }

    public void ToggleFullScreen()
    {
        bool newState = _settings.MovieMode != MovieDisplayMode.FullScreen;
        _settings.MovieMode = newState ? MovieDisplayMode.FullScreen : MovieDisplayMode.Window;
        _window?.SetFullScreen(newState);
        FullScreenChanged?.Invoke(newState);
    }

    public void PlayVideo(string filePath, double startSec, double endSec = -1,
        AfterPlaybackBehavior afterPlayback = AfterPlaybackBehavior.Stop)
    {
        if (!DisplayActive || _window == null) return;
        _window.PlayVideo(filePath, startSec, endSec, afterPlayback);
    }

    public void StopVideo()
    {
        _window?.StopVideo();
    }

    public void FadeVideo(double durationSec)
    {
        _window?.FadeVideo(durationSec);
    }

    public void PanicFade(double durationSec)
    {
        _window?.FadeVideo(durationSec);
    }

    public void PanicStop()
    {
        CloseDisplay();
    }

    public void PauseVideo()
    {
        _window?.PauseVideo();
    }

    public void ResumeVideo()
    {
        _window?.ResumeVideo();
    }

    public void SeekVideo(float fraction)
    {
        _window?.SeekVideo(fraction);
    }

    public long GetCurrentVideoTimeMs() => _window?.GetCurrentTimeMs() ?? -1L;

    public void UpdateAfterPlayback(AfterPlaybackBehavior afterPlayback)
    {
        _window?.UpdateAfterPlayback(afterPlayback);
    }

    public void ReloadStandbyImage()
    {
        _window?.LoadStandbyImage(_settings.MovieStandbyImagePath);
    }

    public void Dispose()
    {
        CloseDisplay();
        _libVLC?.Dispose();
        _libVLC = null;
    }

    private void OnWindowFullScreenChanged(bool isFullScreen)
    {
        _settings.MovieMode = isFullScreen ? MovieDisplayMode.FullScreen : MovieDisplayMode.Window;
        FullScreenChanged?.Invoke(isFullScreen);
    }

    private void OnWindowLoopEndReached() => VideoLoopEndReached?.Invoke();
    private void OnWindowVideoShown(long ms) => VideoShown?.Invoke(ms);

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _window = null;
        DisplayActive = false;
        _settings.DisplayOutputActive = false;
        DisplayActiveChanged?.Invoke(false);
        FullScreenChanged?.Invoke(IsFullScreen);
    }
}
