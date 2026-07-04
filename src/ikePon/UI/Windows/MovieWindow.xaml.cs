using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
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
    private Window? _fadeOverlay; // 黒オーバーレイウィンドウ（フェード用）

    private bool _isFullScreen;
    private double _savedLeft, _savedTop, _savedWidth, _savedHeight;

    private double _pendingStartSec;
    private AfterPlaybackBehavior _afterPlayback = AfterPlaybackBehavior.Stop;
    private bool _videoVisible;

    private static readonly HashSet<string> VideoExts =
        new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".mkv", ".avi", ".wmv" };
    private static readonly HashSet<string> ImageExts =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tiff", ".tif" };

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

        // イベント（別スレッドから発火するため Dispatcher 経由）
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

    // 黒オーバーレイウィンドウをフェードインさせて映像を隠す（VLC DirectX レンダリング対応）
    public void FadeVideo(double durationSec)
    {
        if (!_videoVisible) return;
        StopFadeTimer();
        _fadeDurationSec = Math.Max(durationSec, 0.01);
        _fadeStartTick   = Environment.TickCount64;

        // 黒オーバーレイウィンドウを作成（VLC HWNDの上に重ねる）
        var overlay = new Window
        {
            WindowStyle   = WindowStyle.None,
            ResizeMode    = ResizeMode.NoResize,
            Background    = Brushes.Black,
            Opacity       = 0,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost       = true,
        };

        if (_isFullScreen)
        {
            // フルスクリーン: 同じモニターを全画面で覆う
            overlay.Left = _savedLeft;
            overlay.Top  = _savedTop;
            overlay.Show();
            overlay.WindowState = WindowState.Maximized;
        }
        else if (WindowState == WindowState.Maximized)
        {
            overlay.Left   = Left;
            overlay.Top    = Top;
            overlay.Width  = ActualWidth;
            overlay.Height = ActualHeight;
            overlay.Show();
        }
        else
        {
            overlay.Left   = Left;
            overlay.Top    = Top;
            overlay.Width  = ActualWidth;
            overlay.Height = ActualHeight;
            overlay.Show();
        }

        _fadeOverlay = overlay;

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
            // タイマーを止め、オーバーレイは ShowStandby で閉じる（フラッシュ防止）
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
                _fadeOverlay.Opacity = progress; // 0→1（透明→黒）
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
        CloseFadeOverlay(); // 中断時は即座に閉じる
    }

    private void CloseFadeOverlay()
    {
        if (_fadeOverlay == null) return;
        _fadeOverlay.Close();
        _fadeOverlay = null;
    }

    private void ShowStandby()
    {
        _mediaPlayer.Stop();
        StandbyLayer.Opacity    = 1.0;
        LoadStandbyImage(_settings.MovieStandbyImagePath);
        StandbyLayer.Visibility = Visibility.Visible;
        _videoVisible = false;
        // スタンバイが描画された後にオーバーレイを閉じる（白フラッシュ防止）
        if (_fadeOverlay != null)
        {
            var overlay = _fadeOverlay;
            _fadeOverlay = null;
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(overlay.Close));
        }
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

        // フルスクリーン遷移前にフェードを中断してオーバーレイを閉じる（白画面防止）
        StopFadeTimer();

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
