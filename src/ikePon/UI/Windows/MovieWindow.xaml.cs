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
// WPF Airspace 問題により、WPF オーバーレイで ForegroundWindow を覆えない。
// → VideoView.Visibility = Hidden で ForegroundWindow も非表示になる性質を利用。
// → フェードアウトは VLC adjust フィルタ（libvlc P/Invoke）で brightness を 1.0→0.0 に変化させ、
//    VLC 内部レンダラで映像を暗くする（Airspace 問題を回避）。
public partial class MovieWindow : Window
{
    private readonly AppSettings _settings;
    private readonly LibVLC _libVLC;
    private readonly LibVLCSharp.Shared.MediaPlayer _mediaPlayer;

    // 映像フェードアウトタイマー（brightness 1.0 → 0.0）
    private DispatcherTimer? _fadeTimer;
    private long   _fadeStartTick;
    private double _fadeDurationSec;

    // スタンバイ画像フェードインタイマー
    private DispatcherTimer? _standbyFadeInTimer;
    private long _standbyFadeInStartTick;

    private bool   _isFullScreen;
    private double _savedLeft, _savedTop, _savedWidth, _savedHeight;

    private double              _pendingStartSec;
    private AfterPlaybackBehavior _afterPlayback = AfterPlaybackBehavior.Stop;
    private bool _videoVisible;

    // WM_LBUTTONDOWN 手動ダブルクリック追跡用
    private long _lastLBtnDownTick;
    private int  _lastLBtnDownX, _lastLBtnDownY;

    private static readonly HashSet<string> VideoExts =
        new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".mkv", ".avi", ".wmv" };
    private static readonly HashSet<string> ImageExts =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tiff", ".tif" };

    // Win32 P/Invoke
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref POINT lpPoint);
    [DllImport("user32.dll")] private static extern int  GetDoubleClickTime();
    [DllImport("user32.dll")] private static extern int  GetSystemMetrics(int nIndex);
    private const int SM_CXDOUBLECLK = 36;
    private const int SM_CYDOUBLECLK = 37;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT  { public int left, top, right, bottom; }

    // VLC adjust フィルタ P/Invoke
    // Core.Initialize() が DLL サーチパスを設定済みのため "libvlc" で解決される
    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    private static extern void libvlc_video_set_adjust_int(IntPtr p_mi, uint option, int value);
    [DllImport("libvlc", CallingConvention = CallingConvention.Cdecl)]
    private static extern void libvlc_video_set_adjust_float(IntPtr p_mi, uint option, float value);
    private const uint VlcAdjustEnable     = 0;
    private const uint VlcAdjustBrightness = 2;

    public bool IsFullScreen => _isFullScreen;
    public event Action<bool>? FullScreenChanged;

    // LibVLC は MovieController が管理するため参照のみ保持
    public MovieWindow(AppSettings settings, LibVLC libVLC)
    {
        _settings    = settings;
        _libVLC      = libVLC;
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
            Width  = Math.Max(_settings.MovieWindowWidth.Value,   320);
            Height = Math.Max(_settings.MovieWindowHeight ?? 720, 180);
        }

        LoadStandbyImage(_settings.MovieStandbyImagePath);
    }

    // VLC adjust フィルタで brightness を設定（0.0=黒, 1.0=通常）
    private void SetVlcBrightness(float brightness)
    {
        try
        {
            var mp = _mediaPlayer.NativeReference;
            if (mp == IntPtr.Zero) return;
            libvlc_video_set_adjust_int(mp, VlcAdjustEnable, 1);
            libvlc_video_set_adjust_float(mp, VlcAdjustBrightness, brightness);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MW] SetVlcBrightness({brightness:F2}) error: {ex.Message}");
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ComponentDispatcher.ThreadPreprocessMessage += OnThreadPreprocessMessage;
    }

    // ForegroundWindow（LibVLC の独立 Topmost Window）上のクリックをスクリーン座標で判定する。
    // WM_LBUTTONDBLCLK: VLC が横取りしない場合（スタンバイ中など）はこちらが先に処理される。
    // WM_LBUTTONDOWN: VLC が DBLCLK を横取りする場合（動画再生中）の手動ダブルクリック追跡。
    private void OnThreadPreprocessMessage(ref MSG msg, ref bool handled)
    {
        const int WM_LBUTTONDOWN   = 0x0201;
        const int WM_LBUTTONDBLCLK = 0x0203;
        if ((msg.message != WM_LBUTTONDOWN && msg.message != WM_LBUTTONDBLCLK) || handled || !IsVisible) return;

        var pt = new POINT
        {
            x = (short)(msg.lParam.ToInt32() & 0xFFFF),
            y = (short)(msg.lParam.ToInt32() >> 16)
        };
        if (!ClientToScreen(msg.hwnd, ref pt)) return;

        var myHwnd = new WindowInteropHelper(this).Handle;
        if (!GetWindowRect(myHwnd, out var rc)) return;
        if (pt.x < rc.left || pt.x > rc.right || pt.y < rc.top || pt.y > rc.bottom) return;

        Debug.WriteLine($"[MW] msg={msg.message:X4} at ({pt.x},{pt.y})");

        if (msg.message == WM_LBUTTONDBLCLK)
        {
            Debug.WriteLine("[MW] WM_LBUTTONDBLCLK → toggle fullscreen");
            _lastLBtnDownTick = 0;
            SetFullScreen(!_isFullScreen);
            handled = true;
            return;
        }

        // WM_LBUTTONDOWN: 手動ダブルクリック追跡
        long now = Environment.TickCount64;
        if (_lastLBtnDownTick > 0 &&
            now - _lastLBtnDownTick <= GetDoubleClickTime() &&
            Math.Abs(pt.x - _lastLBtnDownX) <= GetSystemMetrics(SM_CXDOUBLECLK) &&
            Math.Abs(pt.y - _lastLBtnDownY) <= GetSystemMetrics(SM_CYDOUBLECLK))
        {
            Debug.WriteLine("[MW] Manual DoubleClick (LBUTTONDOWN×2) → toggle fullscreen");
            _lastLBtnDownTick = 0;
            SetFullScreen(!_isFullScreen);
            handled = true;
        }
        else
        {
            _lastLBtnDownTick = now;
            _lastLBtnDownX    = pt.x;
            _lastLBtnDownY    = pt.y;
        }
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
        StopStandbyFadeIn();
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
        StopStandbyFadeIn();
        ShowStandby();
    }

    // フェードアウト: VLC adjust フィルタで brightness 1.0→0.0 にして映像を内部から暗くする。
    // brightness=0 到達後に VideoView を非表示→スタンバイ画像フェードインへ移行する。
    public void FadeVideo(double durationSec)
    {
        if (!_videoVisible)
        {
            Debug.WriteLine("[MW] FadeVideo: skip (_videoVisible=false)");
            return;
        }
        Debug.WriteLine($"[MW] FadeVideo: {durationSec:F2}s");

        StopFadeTimer();
        StopStandbyFadeIn();

        _fadeDurationSec = Math.Max(durationSec, 0.01);
        _fadeStartTick   = Environment.TickCount64;

        _fadeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _fadeTimer.Tick += FadeTimer_Tick;
        _fadeTimer.Start();
    }

    private void FadeTimer_Tick(object? sender, EventArgs e)
    {
        double elapsed    = (Environment.TickCount64 - _fadeStartTick) / 1000.0;
        double progress   = elapsed / _fadeDurationSec;
        float  brightness = (float)Math.Clamp(1.0 - progress, 0.0, 1.0);
        SetVlcBrightness(brightness);

        if (progress < 1.0) return;

        // フェードアウト完了
        _fadeTimer!.Stop();
        _fadeTimer.Tick -= FadeTimer_Tick;
        _fadeTimer = null;

        _mediaPlayer.Stop();
        _videoVisible        = false;
        VideoView.Visibility = Visibility.Hidden;
        LoadStandbyImage(_settings.MovieStandbyImagePath);
        StandbyLayer.Opacity    = 0;
        StandbyLayer.Visibility = Visibility.Visible;
        Debug.WriteLine("[MW] FadeOut complete → StandbyFadeIn start");

        StartStandbyFadeIn();
    }

    private void StopFadeTimer()
    {
        if (_fadeTimer == null) return;
        _fadeTimer.Stop();
        _fadeTimer.Tick -= FadeTimer_Tick;
        _fadeTimer = null;
        SetVlcBrightness(1.0f);    // フェード中断時に brightness をリセット
        StandbyLayer.Opacity = 1.0;
        Debug.WriteLine("[MW] StopFadeTimer: interrupted, brightness reset");
    }

    private void StartStandbyFadeIn()
    {
        float duration = _settings.StandbyFadeInDuration;
        if (duration <= 0.02f)
        {
            StandbyLayer.Opacity = 1.0;
            return;
        }
        _standbyFadeInStartTick = Environment.TickCount64;
        _standbyFadeInTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _standbyFadeInTimer.Tick += StandbyFadeIn_Tick;
        _standbyFadeInTimer.Start();
    }

    private void StandbyFadeIn_Tick(object? sender, EventArgs e)
    {
        double duration = Math.Max(_settings.StandbyFadeInDuration, 0.01f);
        double progress = (Environment.TickCount64 - _standbyFadeInStartTick) / 1000.0 / duration;
        StandbyLayer.Opacity = Math.Min(progress, 1.0);
        if (progress >= 1.0)
        {
            StandbyLayer.Opacity = 1.0;
            StopStandbyFadeIn();
            Debug.WriteLine("[MW] StandbyFadeIn: complete");
        }
    }

    private void StopStandbyFadeIn()
    {
        if (_standbyFadeInTimer == null) return;
        _standbyFadeInTimer.Stop();
        _standbyFadeInTimer.Tick -= StandbyFadeIn_Tick;
        _standbyFadeInTimer = null;
    }

    private void ShowStandby()
    {
        Debug.WriteLine($"[MW] ShowStandby (videoVisible={_videoVisible})");
        _mediaPlayer.Stop();
        _videoVisible        = false;
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

        SetVlcBrightness(1.0f);         // フェード中断後の復帰時に brightness をリセット
        StandbyLayer.Visibility = Visibility.Collapsed;
        VideoView.Visibility    = Visibility.Visible;
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
        StopStandbyFadeIn();
        VideoView.Visibility = Visibility.Hidden;

        Window cover;
        if (full)
        {
            // 座標をカバー生成より先に保存（旧バグ：カバー後に保存していたため cover が誤位置に出ていた）
            _savedLeft   = Left;
            _savedTop    = Top;
            _savedWidth  = Width;
            _savedHeight = Height;

            cover = new Window
            {
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                Background = Brushes.Black, ShowInTaskbar = false, ShowActivated = false, Topmost = true,
                Left = _savedLeft, Top = _savedTop, Width = 1, Height = 1
            };
            cover.Show();
            cover.WindowState = WindowState.Maximized;

            _isFullScreen = true;
            WindowStyle   = WindowStyle.None;
            ResizeMode    = ResizeMode.NoResize;
            WindowState   = WindowState.Maximized;
        }
        else
        {
            cover = new Window
            {
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                Background = Brushes.Black, ShowInTaskbar = false, ShowActivated = false, Topmost = true,
                Left = Left, Top = Top, Width = 1, Height = 1
            };
            cover.Show();
            cover.WindowState = WindowState.Maximized;

            _isFullScreen = false;
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
            if (_videoVisible && StandbyLayer.Visibility != Visibility.Visible)
                VideoView.Visibility = Visibility.Visible;
            Debug.WriteLine($"[MW] SetFullScreen: cover closed, VideoView={VideoView.Visibility}");
        }));

        FullScreenChanged?.Invoke(_isFullScreen);
    }

    private void Window_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // StandbyLayer（WPF）上のダブルクリック（動画再生中でない場合）
        if (e.ChangedButton != MouseButton.Left) return;
        SetFullScreen(!_isFullScreen);
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        ComponentDispatcher.ThreadPreprocessMessage -= OnThreadPreprocessMessage;
        StopFadeTimer();
        StopStandbyFadeIn();
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
