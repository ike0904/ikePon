using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
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

    private readonly MediaPlayer _player = new();
    private readonly VideoDrawing _videoDrawing;

    private static readonly HashSet<string> VideoExts =
        new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".mkv", ".avi", ".wmv" };

    public bool IsFullScreen => _isFullScreen;

    public event Action<bool>? FullScreenChanged;

    public MovieWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        // MediaPlayer + VideoDrawing でフレームを WPF 描画パイプラインに乗せる
        _videoDrawing = new VideoDrawing
        {
            Player = _player,
            Rect   = new Rect(0, 0, 1920, 1080),
        };
        VideoRect.Fill = new DrawingBrush(_videoDrawing)
        {
            Stretch = Stretch.Uniform,
        };

        _player.MediaOpened += Player_MediaOpened;
        _player.MediaEnded  += Player_MediaEnded;
        _player.MediaFailed += (_, _) => Dispatcher.BeginInvoke(ShowStandby);

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
        ShowStandby(); // 読み込み完了まではスタンバイを維持

        if (!VideoExts.Contains(System.IO.Path.GetExtension(filePath)))
            return;

        _pendingStartSec = startSec;
        _afterPlayback   = afterPlayback;
        _player.Open(new Uri(filePath, UriKind.Absolute));
        // VideoRect の表示は Player_MediaOpened で Play() 呼び出し後に行う
    }

    public void StopVideo()
    {
        StopFadeTimer();
        ShowStandby();
    }

    public void FadeVideo(double durationSec)
    {
        if (VideoRect.Visibility != Visibility.Visible) return;
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
            VideoRect.Opacity = 1.0 - progress;
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
        _player.Stop();
        VideoRect.Opacity    = 1.0;
        VideoRect.Visibility = Visibility.Collapsed;
    }

    public void UpdateAfterPlayback(AfterPlaybackBehavior afterPlayback)
    {
        _afterPlayback = afterPlayback;
    }

    private void Player_MediaOpened(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var w = _player.NaturalVideoWidth;
            var h = _player.NaturalVideoHeight;
            if (w > 0 && h > 0)
                _videoDrawing.Rect = new Rect(0, 0, w, h);

            if (_pendingStartSec > 0)
                _player.Position = TimeSpan.FromSeconds(_pendingStartSec);
            _player.Play();
            // Play() 呼び出し後に表示（黒画面フラッシュを防ぐ）
            VideoRect.Opacity    = 1.0;
            VideoRect.Visibility = Visibility.Visible;
        });
    }

    private void Player_MediaEnded(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            switch (_afterPlayback)
            {
                case AfterPlaybackBehavior.FreezeLastFrame:
                    // VideoRect はそのまま表示を継続（最終フレームが見える）
                    break;
                case AfterPlaybackBehavior.Loop:
                    _player.Position = TimeSpan.FromSeconds(_pendingStartSec);
                    _player.Play();
                    break;
                default:
                    ShowStandby();
                    break;
            }
        });
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
        _player.Stop();
        _player.Close();

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
