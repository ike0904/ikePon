using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ikePon.Model;
using LibVLCSharp.Shared;

namespace ikePon.UI.Windows;

public partial class MovieWindow : Window
{
    private readonly AppSettings _settings;
    private readonly LibVLC _libVLC;
    private readonly LibVLCSharp.Shared.MediaPlayer _mediaPlayer;

    private DispatcherTimer? _fadeTimer;
    private long _fadeStartTick;
    private double _fadeDurationSec;

    private bool _isFullScreen;
    private double _savedLeft, _savedTop, _savedWidth, _savedHeight;

    private double _pendingStartSec;
    private AfterPlaybackBehavior _afterPlayback = AfterPlaybackBehavior.Stop;
    private bool _videoVisible;

    private static readonly HashSet<string> VideoExts =
        new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".mkv", ".avi", ".wmv" };

    public bool IsFullScreen => _isFullScreen;

    public event Action<bool>? FullScreenChanged;

    public MovieWindow(AppSettings settings)
    {
        _settings = settings;

        Core.Initialize();
        _libVLC      = new LibVLC("--no-audio", "--no-video-title-show");
        _mediaPlayer = new LibVLCSharp.Shared.MediaPlayer(_libVLC);

        InitializeComponent();

        // VideoView の HWND 確保後に MediaPlayer を割り当てる
        Loaded += (_, _) =>
        {
            VideoView.MediaPlayer = _mediaPlayer;
            Debug.WriteLine("[MovieWindow] Loaded: MediaPlayer assigned to VideoView");
        };

        // イベント登録（LibVLC のイベントは別スレッドから発火するため Dispatcher 経由）
        _mediaPlayer.Playing        += (_, _) => Dispatcher.BeginInvoke(OnMediaPlaying);
        _mediaPlayer.EndReached     += (_, _) => Dispatcher.BeginInvoke(OnMediaEnded);
        _mediaPlayer.EncounteredError += (_, _) => Dispatcher.BeginInvoke(OnMediaError);

        if (_settings.MovieWindowX.HasValue)
        {
            Left = _settings.MovieWindowX.Value;
            Top  = _settings.MovieWindowY ?? 0;
        }
        if (_settings.MovieWindowWidth.HasValue)
        {
            Width  = Math.Max(_settings.MovieWindowWidth.Value,  320);
            Height = Math.Max(_settings.MovieWindowHeight ?? 720, 180);
        }

        LoadStandbyImage(_settings.MovieStandbyImagePath);
    }

    public void LoadStandbyImage(string? path)
    {
        if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
        {
            try
            {
                var bmp = new BitmapImage(new Uri(path, UriKind.Absolute));
                StandbyImage.Source = bmp;
                return;
            }
            catch { }
        }
        StandbyImage.Source = null;
    }

    public void PlayVideo(string filePath, double startSec,
        AfterPlaybackBehavior afterPlayback = AfterPlaybackBehavior.Stop)
    {
        StopFadeTimer();
        ShowStandby();

        string ext = System.IO.Path.GetExtension(filePath);
        bool isVideo = VideoExts.Contains(ext);
        Debug.WriteLine($"[MovieWindow] PlayVideo: {filePath}");
        Debug.WriteLine($"[MovieWindow]   ext={ext}, isVideo={isVideo}, startSec={startSec}");
        if (!isVideo)
        {
            Debug.WriteLine("[MovieWindow]   Skipped: not a video extension");
            return;
        }

        _pendingStartSec = startSec;
        _afterPlayback   = afterPlayback;

        var media = new Media(_libVLC, new Uri(filePath));
        _mediaPlayer.Play(media);
        media.Dispose();
        Debug.WriteLine("[MovieWindow]   MediaPlayer.Play() called, waiting for Playing event...");
    }

    public void StopVideo()
    {
        StopFadeTimer();
        ShowStandby();
    }

    public void FadeVideo(double durationSec)
    {
        if (!_videoVisible) return;
        StopFadeTimer();
        FadeOverlay.Visibility = Visibility.Visible;
        FadeOverlay.Opacity    = 0;
        _fadeDurationSec = Math.Max(durationSec, 0.01);
        _fadeStartTick   = Environment.TickCount64;
        _fadeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _fadeTimer.Tick += FadeTimer_Tick;
        _fadeTimer.Start();
    }

    private void FadeTimer_Tick(object? sender, EventArgs e)
    {
        double elapsed  = (Environment.TickCount64 - _fadeStartTick) / 1000.0;
        double progress = elapsed / _fadeDurationSec;
        if (progress >= 1.0)
        {
            StopFadeTimer();
            ShowStandby();
        }
        else
        {
            FadeOverlay.Opacity = progress;
        }
    }

    private void StopFadeTimer()
    {
        if (_fadeTimer == null) return;
        _fadeTimer.Stop();
        _fadeTimer.Tick -= FadeTimer_Tick;
        _fadeTimer = null;
    }

    private void ShowStandby()
    {
        _mediaPlayer.Stop();
        FadeOverlay.Visibility  = Visibility.Collapsed;
        FadeOverlay.Opacity     = 0;
        StandbyLayer.Visibility = Visibility.Visible;
        _videoVisible = false;
    }

    public void UpdateAfterPlayback(AfterPlaybackBehavior afterPlayback)
    {
        _afterPlayback = afterPlayback;
    }

    private void OnMediaPlaying()
    {
        if (_pendingStartSec > 0)
            _mediaPlayer.Time = (long)(_pendingStartSec * 1000);

        StandbyLayer.Visibility = Visibility.Collapsed;
        _videoVisible = true;
        Debug.WriteLine("[MovieWindow] Playing: StandbyLayer hidden, video visible");
    }

    private void OnMediaEnded()
    {
        Debug.WriteLine($"[MovieWindow] EndReached, AfterPlayback={_afterPlayback}");
        switch (_afterPlayback)
        {
            case AfterPlaybackBehavior.FreezeLastFrame:
                _mediaPlayer.Pause();
                break;
            case AfterPlaybackBehavior.Loop:
                _mediaPlayer.Time = (long)(_pendingStartSec * 1000);
                _mediaPlayer.Play();
                break;
            default:
                ShowStandby();
                break;
        }
    }

    private void OnMediaError()
    {
        Debug.WriteLine("[MovieWindow] EncounteredError: VLC media player error");
        ShowStandby();
    }

    public void SetFullScreen(bool full)
    {
        if (full == _isFullScreen) return;
        _isFullScreen = full;

        if (full)
        {
            _savedLeft   = Left;
            _savedTop    = Top;
            _savedWidth  = Width;
            _savedHeight = Height;
            WindowStyle  = WindowStyle.None;
            ResizeMode   = ResizeMode.NoResize;
            WindowState  = WindowState.Maximized;
        }
        else
        {
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode  = ResizeMode.CanResize;
            Left   = _savedLeft;
            Top    = _savedTop;
            Width  = _savedWidth;
            Height = _savedHeight;
        }

        FullScreenChanged?.Invoke(_isFullScreen);
    }

    private void Window_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        SetFullScreen(!_isFullScreen);
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        StopFadeTimer();
        _mediaPlayer.Stop();

        double saveLeft   = _isFullScreen ? _savedLeft   : Left;
        double saveTop    = _isFullScreen ? _savedTop    : Top;
        double saveWidth  = _isFullScreen ? _savedWidth  : Width;
        double saveHeight = _isFullScreen ? _savedHeight : Height;

        _settings.MovieWindowX      = saveLeft;
        _settings.MovieWindowY      = saveTop;
        _settings.MovieWindowWidth  = saveWidth;
        _settings.MovieWindowHeight = saveHeight;

        _mediaPlayer.Dispose();
        _libVLC.Dispose();
    }
}
