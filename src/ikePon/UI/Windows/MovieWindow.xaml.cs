using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ikePon.Model;

namespace ikePon.UI.Windows;

public partial class MovieWindow : Window
{
    private readonly AppSettings _settings;
    private DispatcherTimer? _fadeTimer;
    private long _fadeStartTick;
    private double _fadeDurationSec;

    private bool _isFullScreen;
    private double _savedLeft, _savedTop, _savedWidth, _savedHeight;

    private double _pendingStartSec;
    private AfterPlaybackBehavior _afterPlayback = AfterPlaybackBehavior.Stop;

    private static readonly HashSet<string> VideoExts =
        new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".mkv", ".avi", ".wmv" };

    public bool IsFullScreen => _isFullScreen;

    public event Action<bool>? FullScreenChanged;

    public MovieWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();

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
        VideoPlayer.Source = new Uri(filePath, UriKind.Absolute);
        Debug.WriteLine("[MovieWindow]   Source set, waiting for MediaOpened...");
    }

    public void StopVideo()
    {
        StopFadeTimer();
        ShowStandby();
    }

    public void FadeVideo(double durationSec)
    {
        if (VideoPlayer.Visibility != Visibility.Visible) return;
        StopFadeTimer();
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
            VideoPlayer.Opacity = 1.0 - progress;
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
        VideoPlayer.Stop();
        VideoPlayer.Source     = null;
        VideoPlayer.Opacity    = 1.0;
        VideoPlayer.Visibility = Visibility.Collapsed;
    }

    public void UpdateAfterPlayback(AfterPlaybackBehavior afterPlayback)
    {
        _afterPlayback = afterPlayback;
    }

    private void VideoPlayer_MediaOpened(object sender, RoutedEventArgs e)
    {
        Debug.WriteLine($"[MovieWindow] MediaOpened: Duration={VideoPlayer.NaturalDuration}, HasVideo={VideoPlayer.HasVideo}, HasAudio={VideoPlayer.HasAudio}");
        if (_pendingStartSec > 0)
            VideoPlayer.Position = TimeSpan.FromSeconds(_pendingStartSec);
        VideoPlayer.Play();
        VideoPlayer.Opacity    = 1.0;
        VideoPlayer.Visibility = Visibility.Visible;
        Debug.WriteLine("[MovieWindow]   Play() called, Visibility=Visible");
    }

    private void VideoPlayer_MediaEnded(object sender, RoutedEventArgs e)
    {
        switch (_afterPlayback)
        {
            case AfterPlaybackBehavior.FreezeLastFrame:
                VideoPlayer.Pause();
                break;
            case AfterPlaybackBehavior.Loop:
                VideoPlayer.Position = TimeSpan.FromSeconds(_pendingStartSec);
                VideoPlayer.Play();
                break;
            default:
                ShowStandby();
                break;
        }
    }

    private void VideoPlayer_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        Debug.WriteLine($"[MovieWindow] MediaFailed: {e.ErrorException?.Message}");
        Debug.WriteLine($"[MovieWindow]   HResult=0x{e.ErrorException?.HResult:X8}, Type={e.ErrorException?.GetType().Name}");
        Dispatcher.BeginInvoke(ShowStandby);
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
        VideoPlayer.Stop();

        double saveLeft   = _isFullScreen ? _savedLeft   : Left;
        double saveTop    = _isFullScreen ? _savedTop    : Top;
        double saveWidth  = _isFullScreen ? _savedWidth  : Width;
        double saveHeight = _isFullScreen ? _savedHeight : Height;

        _settings.MovieWindowX      = saveLeft;
        _settings.MovieWindowY      = saveTop;
        _settings.MovieWindowWidth  = saveWidth;
        _settings.MovieWindowHeight = saveHeight;
    }
}
