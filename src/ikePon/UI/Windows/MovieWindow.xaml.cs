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

// LibVLCSharp.WPF の VideoView は内部に ForegroundWindow（独立した Topmost WPF Window）を持つ。
// この ForegroundWindow は MovieWindow の WPF コンテンツ（StandbyLayer）より常に前面にあるため、
// WPF オーバーレイで覆えない（WPF Airspace 問題）。
// → VideoView.Visibility = Hidden にすると ForegroundWindow も非表示になる性質を利用して解決する。
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
    private static readonly HashSet<string> ImageExts =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tiff", ".tif" };

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT  { public int left, top, right, bottom; }

    public bool IsFullScreen => _isFullScreen;
    public event Action<bool>? FullScreenChanged;

    // LibVLC は MovieController が管理するため、ここでは参照のみ保持
    public MovieWindow(AppSettings settings, LibVLC libVLC)
    {
        _settings = settings;
        _libVLC   = libVLC;
        _mediaPlayer = new LibVLCSharp.Shared.MediaPlayer(_libVLC);

        InitializeComponent();

        Loaded += (_, _) =>
        {
            VideoView.MediaPlayer = _mediaPlayer;
            Debug.WriteLine("[MW] Loaded: MediaPlayer assigned");
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
        ComponentDispatcher.ThreadPreprocessMessage += OnThreadPreprocessMessage;
    }

    // ForegroundWindow（LibVLCの独立Topmost Window）上のダブルクリックをスクリーン座標で判定
    // GetAncestor では ForegroundWindow が MovieWindow の子ではないため判定できない
    private void OnThreadPreprocessMessage(ref MSG msg, ref bool handled)
    {
        const int WM_LBUTTONDBLCLK = 0x0203;
        if (msg.message != WM_LBUTTONDBLCLK || handled || !IsVisible) return;

        // lParam はクリック元 HWND のクライアント座標 → スクリーン座標に変換
        var pt = new POINT
        {
            x = (short)(msg.lParam.ToInt32() & 0xFFFF),
            y = (short)(msg.lParam.ToInt32() >> 16)
        };
        if (!ClientToScreen(msg.hwnd, ref pt)) return;

        // MovieWindow のスクリーン矩形内かを確認
        var myHwnd = new WindowInteropHelper(this).Handle;
        if (!GetWindowRect(myHwnd, out var rc)) return;
        if (pt.x < rc.left || pt.x > rc.right || pt.y < rc.top || pt.y > rc.bottom)
        {
            Debug.WriteLine($"[MW] DoubleClick ({pt.x},{pt.y}) outside window rect → skip");
            return;
        }

        Debug.WriteLine($"[MW] DoubleClick ({pt.x},{pt.y}) → toggle fullscreen");
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

        string ext     = System.IO.Path.GetExtension(filePath);
        bool   isVideo = VideoExts.Contains(ext);
        bool   isImage = ImageExts.Contains(ext);
        Debug.WriteLine($"[MW] PlayVideo ext={ext} video={isVideo} image={isImage} start={startSec:F2}");

        if (isImage)
        {
            try { StandbyImage.Source = new BitmapImage(new Uri(filePath, UriKind.Absolute)); }
            catch { StandbyImage.Source = null; }
            _afterPlayback = afterPlayback;
            _videoVisible  = true;
            Debug.WriteLine("[MW]   static image displayed");
            return;
        }

        if (!isVideo)
        {
            Debug.WriteLine("[MW]   skipped: not video/image ext");
            return;
        }

        _pendingStartSec = startSec;
        _afterPlayback   = afterPlayback;

        using var media = new Media(_libVLC, new Uri(filePath));
        _mediaPlayer.Play(media);
        Debug.WriteLine("[MW]   MediaPlayer.Play() called");
    }

    public void StopVideo()
    {
        Debug.WriteLine("[MW] StopVideo");
        StopFadeTimer();
        ShowStandby();
    }

    // フェードアウト:
    //   VideoView を即座に Hidden（ForegroundWindow も非表示）してから、
    //   StandbyLayer を Opacity 0→1 でフェードイン。
    //   ForegroundWindow を WPF オーバーレイで覆えない Airspace 問題を根本回避する。
    public void FadeVideo(double durationSec)
    {
        if (!_videoVisible)
        {
            Debug.WriteLine("[MW] FadeVideo: skip (_videoVisible=false)");
            return;
        }
        Debug.WriteLine($"[MW] FadeVideo: {durationSec:F2}s");

        StopFadeTimer();

        // VideoView 非表示 → ForegroundWindow も非表示（Airspace 回避）
        VideoView.Visibility = Visibility.Hidden;

        // StandbyLayer を透明で表示（フェードイン開始）
        LoadStandbyImage(_settings.MovieStandbyImagePath);
        StandbyLayer.Opacity    = 0;
        StandbyLayer.Visibility = Visibility.Visible;

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
            if (_fadeTimer != null)
            {
                _fadeTimer.Stop();
                _fadeTimer.Tick -= FadeTimer_Tick;
                _fadeTimer = null;
            }
            StandbyLayer.Opacity = 1.0;
            _mediaPlayer.Stop();
            _videoVisible = false;
            Debug.WriteLine("[MW] FadeTimer: complete → standby confirmed");
        }
        else
        {
            StandbyLayer.Opacity = progress; // 0→1（透明→不透明）
        }
    }

    private void StopFadeTimer()
    {
        if (_fadeTimer == null) return;
        _fadeTimer.Stop();
        _fadeTimer.Tick -= FadeTimer_Tick;
        _fadeTimer = null;
        // フェード中断時はスタンバイを確定状態に
        StandbyLayer.Opacity = 1.0;
        Debug.WriteLine("[MW] StopFadeTimer: fade interrupted, opacity=1");
    }

    private void ShowStandby()
    {
        Debug.WriteLine($"[MW] ShowStandby (videoVisible={_videoVisible})");
        _mediaPlayer.Stop();
        _videoVisible = false;

        // VideoView を非表示 → ForegroundWindow も非表示（白フラッシュ防止）
        VideoView.Visibility = Visibility.Hidden;

        StandbyLayer.Opacity    = 1.0;
        LoadStandbyImage(_settings.MovieStandbyImagePath);
        StandbyLayer.Visibility = Visibility.Visible;
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
        Debug.WriteLine($"[MW] OnMediaPlaying: pendingStart={_pendingStartSec:F2}");
        if (_pendingStartSec > 0)
            _mediaPlayer.Time = (long)(_pendingStartSec * 1000);

        StandbyLayer.Visibility = Visibility.Collapsed;
        VideoView.Visibility    = Visibility.Visible; // ForegroundWindow を表示
        _videoVisible = true;
        Debug.WriteLine("[MW] OnMediaPlaying: VideoView=Visible, StandbyLayer=Collapsed");
    }

    private void OnMediaEnded()
    {
        Debug.WriteLine($"[MW] EndReached AfterPlayback={_afterPlayback}");
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
        Debug.WriteLine("[MW] EncounteredError → standby");
        ShowStandby();
    }

    public void SetFullScreen(bool full)
    {
        if (full == _isFullScreen) return;
        Debug.WriteLine($"[MW] SetFullScreen: {_isFullScreen}→{full}");

        StopFadeTimer();
        _isFullScreen = full;

        // VideoView 非表示（ForegroundWindow も非表示）+ 黒カバーで WPF フラッシュを両面対策
        VideoView.Visibility = Visibility.Hidden;
        var cover = MakeBlackCover();

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

        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            cover.Close();
            // 再生中なら VideoView を復元（静止画表示中や待機中は Hidden のまま）
            if (_videoVisible && StandbyLayer.Visibility != Visibility.Visible)
                VideoView.Visibility = Visibility.Visible;
            Debug.WriteLine($"[MW] SetFullScreen: cover closed, VideoView={VideoView.Visibility}");
        }));

        FullScreenChanged?.Invoke(_isFullScreen);
    }

    // SetFullScreen 遷移中の WPF フラッシュを隠す黒カバーウィンドウ
    private Window MakeBlackCover()
    {
        var cover = new Window
        {
            WindowStyle   = WindowStyle.None,
            ResizeMode    = ResizeMode.NoResize,
            Background    = Brushes.Black,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost       = true,
        };

        if (_isFullScreen)
        {
            cover.Left = _savedLeft;
            cover.Top  = _savedTop;
            cover.Show();
            cover.WindowState = WindowState.Maximized;
        }
        else
        {
            cover.Left   = Left;
            cover.Top    = Top;
            cover.Width  = ActualWidth;
            cover.Height = ActualHeight;
            cover.Show();
        }
        return cover;
    }

    private void Window_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // StandbyLayer（WPF）上のダブルクリック（再生中でない場合）
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

        // _libVLC は MovieController が管理するため Dispose しない
        _mediaPlayer.Dispose();
    }
}
