using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
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
    private Window? _fadeOverlay;

    private bool _isFullScreen;
    private double _savedLeft, _savedTop, _savedWidth, _savedHeight;

    private double _pendingStartSec;
    private AfterPlaybackBehavior _afterPlayback = AfterPlaybackBehavior.Stop;
    private bool _videoVisible;

    private static readonly HashSet<string> VideoExts =
        new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".mkv", ".avi", ".wmv" };
    private static readonly HashSet<string> ImageExts =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tiff", ".tif" };

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    private const uint GA_ROOT = 2;

    public bool IsFullScreen => _isFullScreen;

    public event Action<bool>? FullScreenChanged;

    // LibVLC はMovieControllerが管理するため、ここでは参照のみ保持してDisposeしない
    public MovieWindow(AppSettings settings, LibVLC libVLC)
    {
        _settings = settings;
        _libVLC = libVLC;
        _mediaPlayer = new LibVLCSharp.Shared.MediaPlayer(_libVLC);

        InitializeComponent();

        Loaded += (_, _) =>
        {
            VideoView.MediaPlayer = _mediaPlayer;
            Debug.WriteLine("[MovieWindow] Loaded: MediaPlayer assigned to VideoView");
        };

        _mediaPlayer.Playing          += (_, _) => Dispatcher.BeginInvoke(OnMediaPlaying);
        _mediaPlayer.EndReached       += (_, _) => Dispatcher.BeginInvoke(OnMediaEnded);
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

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // VLC子HWNDのダブルクリックをフックして全画面切り替えに使う（問題3対策）
        ComponentDispatcher.ThreadPreprocessMessage += OnThreadPreprocessMessage;
    }

    private void OnThreadPreprocessMessage(ref MSG msg, ref bool handled)
    {
        const int WM_LBUTTONDBLCLK = 0x0203;
        if (msg.message != WM_LBUTTONDBLCLK || handled) return;

        // このウィンドウ（またはVLC子HWND）でのダブルクリックかを確認
        var myHwnd = new WindowInteropHelper(this).Handle;
        var rootHwnd = GetAncestor(msg.hwnd, GA_ROOT);
        if (rootHwnd != myHwnd) return;

        SetFullScreen(!_isFullScreen);
        handled = true;
    }

    public void LoadStandbyImage(string? path)
    {
        if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
        {
            try
            {
                StandbyImage.Source = new BitmapImage(new Uri(path, UriKind.Absolute));
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
        bool isImage = ImageExts.Contains(ext);
        Debug.WriteLine($"[MovieWindow] PlayVideo: {filePath}");
        Debug.WriteLine($"[MovieWindow]   ext={ext}, isVideo={isVideo}, isImage={isImage}, startSec={startSec}");

        if (isImage)
        {
            try { StandbyImage.Source = new BitmapImage(new Uri(filePath, UriKind.Absolute)); }
            catch { StandbyImage.Source = null; }
            _afterPlayback = afterPlayback;
            _videoVisible  = true;
            Debug.WriteLine("[MovieWindow]   Image displayed");
            return;
        }

        if (!isVideo)
        {
            Debug.WriteLine("[MovieWindow]   Skipped: not a video or image extension");
            return;
        }

        _pendingStartSec = startSec;
        _afterPlayback   = afterPlayback;

        using var media = new Media(_libVLC, new Uri(filePath));
        _mediaPlayer.Play(media);
        Debug.WriteLine("[MovieWindow]   MediaPlayer.Play() called");
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
        _fadeDurationSec = Math.Max(durationSec, 0.01);
        _fadeStartTick   = Environment.TickCount64;

        _fadeOverlay = MakeCoverWindow(opacity: 0);

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
            if (_fadeTimer != null)
            {
                _fadeTimer.Stop();
                _fadeTimer.Tick -= FadeTimer_Tick;
                _fadeTimer = null;
            }
            if (_fadeOverlay != null) _fadeOverlay.Opacity = 1.0;
            ShowStandby();
        }
        else
        {
            if (_fadeOverlay != null)
                _fadeOverlay.Opacity = progress;
        }
    }

    private void StopFadeTimer()
    {
        if (_fadeTimer != null)
        {
            _fadeTimer.Stop();
            _fadeTimer.Tick -= FadeTimer_Tick;
            _fadeTimer = null;
        }
        CloseFadeOverlay();
    }

    private void CloseFadeOverlay()
    {
        if (_fadeOverlay == null) return;
        _fadeOverlay.Close();
        _fadeOverlay = null;
    }

    // フェードなしで停止する場合も黒カバーで白フラッシュを防ぐ（問題4対策）
    private void ShowStandby()
    {
        if (_videoVisible && _fadeOverlay == null)
        {
            var cover = MakeCoverWindow(opacity: 1.0);
            _fadeOverlay = cover;
        }

        _mediaPlayer.Stop();
        _videoVisible = false;
        StandbyLayer.Opacity    = 1.0;
        LoadStandbyImage(_settings.MovieStandbyImagePath);
        StandbyLayer.Visibility = Visibility.Visible;

        if (_fadeOverlay != null)
        {
            var overlay = _fadeOverlay;
            _fadeOverlay = null;
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(overlay.Close));
        }
    }

    // 黒カバーウィンドウを作成（フェード用・フラッシュ防止用）
    private Window MakeCoverWindow(double opacity)
    {
        var w = new Window
        {
            WindowStyle   = WindowStyle.None,
            ResizeMode    = ResizeMode.NoResize,
            Background    = Brushes.Black,
            Opacity       = opacity,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost       = true,
        };

        if (_isFullScreen)
        {
            w.Left = _savedLeft;
            w.Top  = _savedTop;
            w.Show();
            w.WindowState = WindowState.Maximized;
        }
        else
        {
            w.Left   = Left;
            w.Top    = Top;
            w.Width  = ActualWidth;
            w.Height = ActualHeight;
            w.Show();
        }
        return w;
    }

    public void SeekVideo(float fraction)
    {
        _mediaPlayer.Position = Math.Clamp(fraction, 0f, 1f);
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
        Debug.WriteLine("[MovieWindow] Playing: video visible");
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
        Debug.WriteLine("[MovieWindow] EncounteredError");
        ShowStandby();
    }

    public void SetFullScreen(bool full)
    {
        if (full == _isFullScreen) return;

        StopFadeTimer();
        _isFullScreen = full;

        // WindowState変更中にVLC HWNDが白くなるのを防ぐ（問題2対策）
        VideoView.Visibility = Visibility.Hidden;

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

        // レンダリング後に VideoView を復元
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            VideoView.Visibility = Visibility.Visible;
        }));

        FullScreenChanged?.Invoke(_isFullScreen);
    }

    private void Window_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // StandbyLayer（WPF）上のダブルクリックはここで処理される
        // VideoView（VLC HWND）上はOnThreadPreprocessMessageで処理
        if (e.ChangedButton != MouseButton.Left) return;
        SetFullScreen(!_isFullScreen);
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        ComponentDispatcher.ThreadPreprocessMessage -= OnThreadPreprocessMessage;
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

        // _libVLCはMovieControllerが管理するためDisposeしない
        _mediaPlayer.Dispose();
    }
}
