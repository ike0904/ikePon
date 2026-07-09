using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ikePon.Model;
using LibVLCSharp.Shared;

namespace ikePon.UI.Windows;

// LibVLCSharp.WPF の VideoView は内部に ForegroundWindow（独立した Topmost WPF Window）を持つ。
// → リフレクションで ForegroundWindow を取得し Window.Opacity でフェードアウトを実現。
// → ダブルクリックは WH_MOUSE_LL グローバルフックで検出（VLC が別スレッドでも動作）。
public partial class MovieWindow : Window
{
    private readonly AppSettings _settings;
    private readonly LibVLC _libVLC;
    private readonly LibVLCSharp.Shared.MediaPlayer _mediaPlayer;

    // 映像フェードアウトタイマー（黒オーバーレイWindow を Opacity 0→1 でフェードイン）
    private DispatcherTimer? _fadeTimer;
    private long   _fadeStartTick;
    private double _fadeDurationSec;
    private Window? _fadeOverlay;

    // スタンバイ画像フェードインタイマー
    private DispatcherTimer? _standbyFadeInTimer;
    private long _standbyFadeInStartTick;

    private bool   _isFullScreen;
    private double _savedLeft, _savedTop, _savedWidth, _savedHeight;

    private string? _currentFilePath;
    private double               _pendingStartSec;
    private AfterPlaybackBehavior _afterPlayback = AfterPlaybackBehavior.Stop;
    private bool _videoVisible;
    private int  _playSession;

    public bool IsBuffering { get; private set; }

    // WH_MOUSE_LL フック（ダブルクリック検出）
    private IntPtr _mouseHook = IntPtr.Zero;
    private LowLevelMouseProc? _mouseProcDelegate;
    private long _lastLBtnDownTick;
    private int  _lastLBtnDownX, _lastLBtnDownY;

    private static readonly HashSet<string> VideoExts =
        new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".mkv", ".avi", ".wmv" };
    private static readonly HashSet<string> ImageExts =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tiff", ".tif" };

    // ───────────────────────── Win32 P/Invoke ─────────────────────────

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern int  GetDoubleClickTime();
    [DllImport("user32.dll")] private static extern int  GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    // グローバルマウスフック（ダブルクリック検出）
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool   UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? lpModuleName);
    private const int WH_MOUSE_LL    = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int SM_CXDOUBLECLK = 36;
    private const int SM_CYDOUBLECLK = 37;

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] private struct RECT  { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData, flags, time;
        public IntPtr dwExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }

    // ───────────────────────── Public API ─────────────────────────

    public bool IsFullScreen => _isFullScreen;
    public event Action<bool>? FullScreenChanged;

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
        // 初期表示は黒（スタンバイ画像は ShowStandby() 呼び出し時にロード）
    }

    // ───────────────────────── グローバルマウスフック ─────────────────────────

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        InstallGlobalMouseHook();
    }

    private void InstallGlobalMouseHook()
    {
        _mouseProcDelegate = GlobalMouseProc;
        using var proc = System.Diagnostics.Process.GetCurrentProcess();
        using var mod  = proc.MainModule!;
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProcDelegate, GetModuleHandle(mod.ModuleName), 0);
        Debug.WriteLine($"[MW] WH_MOUSE_LL hook installed: {_mouseHook}");
    }

    private void UninstallGlobalMouseHook()
    {
        if (_mouseHook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_mouseHook);
        _mouseHook         = IntPtr.Zero;
        _mouseProcDelegate = null;
    }

    // WH_MOUSE_LL コールバック：WM_LBUTTONDOWN を手動追跡してダブルクリックを検出する。
    private IntPtr GlobalMouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (int)wParam == WM_LBUTTONDOWN && IsVisible)
        {
            var hook  = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            var myHwnd = new WindowInteropHelper(this).Handle;
            if (myHwnd != IntPtr.Zero && GetWindowRect(myHwnd, out var rc) &&
                hook.pt.x >= rc.left && hook.pt.x <= rc.right &&
                hook.pt.y >= rc.top  && hook.pt.y <= rc.bottom)
            {
                long now = Environment.TickCount64;
                if (_lastLBtnDownTick > 0 &&
                    now - _lastLBtnDownTick <= GetDoubleClickTime() &&
                    Math.Abs(hook.pt.x - _lastLBtnDownX) <= GetSystemMetrics(SM_CXDOUBLECLK) &&
                    Math.Abs(hook.pt.y - _lastLBtnDownY) <= GetSystemMetrics(SM_CYDOUBLECLK))
                {
                    _lastLBtnDownTick = 0;
                    Dispatcher.BeginInvoke(() =>
                    {
                        Debug.WriteLine("[MW] GlobalMouseHook: DoubleClick → toggle fullscreen");
                        SetFullScreen(!_isFullScreen);
                    });
                }
                else
                {
                    _lastLBtnDownTick = now;
                    _lastLBtnDownX    = hook.pt.x;
                    _lastLBtnDownY    = hook.pt.y;
                }
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    // ───────────────────────── 映像制御 ─────────────────────────

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

    // 動画再生開始前の準備：スタンバイ画像は表示せず黒のみ（フラッシュ防止）
    private void PrepareForPlay()
    {
        _playSession++;
        IsBuffering          = true;
        _videoVisible        = false;
        VideoView.Visibility = Visibility.Collapsed; // FGW=0×0でバッファリング中の黒画面を隠す
        Task.Run(() => { try { _mediaPlayer.Stop(); } catch { } });
        StandbyImage.Source     = null; // 黒（スタンバイ画像なし）
        StandbyLayer.Opacity    = 1.0;
        StandbyLayer.Visibility = Visibility.Visible;
        Logger.Log("[MW] PrepareForPlay: buffering start (VideoView=Collapsed)");
    }

    public void PlayVideo(string filePath, double startSec,
        AfterPlaybackBehavior afterPlayback = AfterPlaybackBehavior.Stop)
    {
        StopFadeTimer();
        StopStandbyFadeIn();
        PrepareForPlay(); // ShowStandby の代わりに黒画面で待機（スタンバイ画像フラッシュ防止）

        string ext     = System.IO.Path.GetExtension(filePath);
        bool   isVideo = VideoExts.Contains(ext);
        bool   isImage = ImageExts.Contains(ext);
        Logger.Log($"[MW] PlayVideo ext={ext} video={isVideo} image={isImage} start={startSec:F2}");

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
            // 映像/静止画でないファイル（音声のみ等）→ スタンバイ画面を維持
            LoadStandbyImage(_settings.MovieStandbyImagePath);
            StandbyLayer.Opacity    = 1.0;
            StandbyLayer.Visibility = Visibility.Visible;
            IsBuffering = false;
            Debug.WriteLine("[MW]   skipped: not video/image ext → show standby");
            return;
        }

        _pendingStartSec  = startSec;
        _afterPlayback    = afterPlayback;
        _currentFilePath  = filePath;

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

    // フェードアウト：黒いオーバーレイWindowをOpacity 0→1でフェードインして映像を覆う。
    public void FadeVideo(double durationSec)
    {
        if (!_videoVisible)
        {
            Debug.WriteLine("[MW] FadeVideo: skip (_videoVisible=false)");
            return;
        }
        Logger.Log($"[MW] FadeVideo: {durationSec:F2}s session={_playSession}");

        StopFadeTimer();
        StopStandbyFadeIn();

        _fadeDurationSec = Math.Max(durationSec, 0.01);
        _fadeStartTick   = Environment.TickCount64;

        // 全画面時は WPF の Left/Top がリストア位置を返すため GetWindowRect で実座標を取得
        double overlayLeft, overlayTop, overlayWidth, overlayHeight;
        var myHwnd = new WindowInteropHelper(this).Handle;
        if (_isFullScreen && myHwnd != IntPtr.Zero && GetWindowRect(myHwnd, out var rc))
        {
            var pSrc  = PresentationSource.FromVisual(this);
            double dpiX = pSrc?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double dpiY = pSrc?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
            overlayLeft   = rc.left   / dpiX;
            overlayTop    = rc.top    / dpiY;
            overlayWidth  = (rc.right  - rc.left) / dpiX;
            overlayHeight = (rc.bottom - rc.top)  / dpiY;
        }
        else
        {
            overlayLeft   = Left;
            overlayTop    = Top;
            overlayWidth  = ActualWidth > 0 ? ActualWidth : Width;
            overlayHeight = ActualHeight > 0 ? ActualHeight : Height;
        }
        _fadeOverlay = new Window
        {
            WindowStyle   = WindowStyle.None,
            AllowsTransparency = true,
            Background    = System.Windows.Media.Brushes.Black,
            Opacity       = 0.0,
            Topmost       = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            Left   = overlayLeft,
            Top    = overlayTop,
            Width  = overlayWidth,
            Height = overlayHeight
        };
        _fadeOverlay.Show();
        // オーバーレイを最前面（VLC ForegroundWindow の上）に強制配置
        var hwnd = new WindowInteropHelper(_fadeOverlay).Handle;
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);

        _fadeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _fadeTimer.Tick += FadeTimer_Tick;
        _fadeTimer.Start();
    }

    private void FadeTimer_Tick(object? sender, EventArgs e)
    {
        double elapsed  = (Environment.TickCount64 - _fadeStartTick) / 1000.0;
        double progress = elapsed / _fadeDurationSec;
        if (_fadeOverlay != null)
            _fadeOverlay.Opacity = Math.Clamp(progress, 0.0, 1.0);

        if (progress < 1.0) return;

        // フェードアウト完了
        _fadeTimer!.Stop();
        _fadeTimer.Tick -= FadeTimer_Tick;
        _fadeTimer = null;

        // VideoViewをCollapsedに → VLC ForegroundWindowが0×0になり非表示
        _videoVisible        = false;
        VideoView.Visibility = Visibility.Collapsed;

        // スタンバイをオーバーレイの背後でセットアップ（非表示状態）
        LoadStandbyImage(_settings.MovieStandbyImagePath);
        StandbyLayer.Opacity    = 0.0;
        StandbyLayer.Visibility = Visibility.Visible;

        // VLC停止（非同期）
        Task.Run(() => { try { _mediaPlayer.Stop(); } catch (Exception ex) { Debug.WriteLine($"[MW] Stop error: {ex.Message}"); } });

        // オーバーレイをBackground優先度で閉じる（VLC FGW縮小後にフェードイン開始）
        var overlay = _fadeOverlay;
        _fadeOverlay = null;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            overlay?.Close();
            Logger.Log("[MW] FadeOut complete → StandbyFadeIn start");
            StartStandbyFadeIn();
        }));
    }

    private void StopFadeTimer()
    {
        if (_fadeTimer == null) return;
        _fadeTimer.Stop();
        _fadeTimer.Tick -= FadeTimer_Tick;
        _fadeTimer = null;
        if (_fadeOverlay != null) { _fadeOverlay.Close(); _fadeOverlay = null; }
        StandbyLayer.Opacity = 1.0;
        Debug.WriteLine("[MW] StopFadeTimer: interrupted");
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

    // 映像を停止してスタンバイ画像を表示する（MovieController からも呼び出し可）
    public void ShowStandby()
    {
        Logger.Log($"[MW] ShowStandby (videoVisible={_videoVisible})");
        IsBuffering = false;
        StopFadeTimer();      // フェード中でも正しく停止（FADEモード時のスタンバイ不表示を防ぐ）
        StopStandbyFadeIn();
        _playSession++;
        _videoVisible        = false;
        VideoView.Visibility = Visibility.Collapsed; // FGW=0×0でスタンバイを正しく見せる
        Task.Run(() => { try { _mediaPlayer.Stop(); } catch { } });

        LoadStandbyImage(_settings.MovieStandbyImagePath);
        StandbyLayer.Opacity    = 0.0;
        StandbyLayer.Visibility = Visibility.Visible;
        StartStandbyFadeIn();  // CUT/FADEモードどちらでもフェードイン
    }

    public void PauseVideo()
    {
        if (_videoVisible) _mediaPlayer.SetPause(true);
    }

    public void ResumeVideo()
    {
        if (_videoVisible) _mediaPlayer.SetPause(false);
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
        Logger.Log($"[MW] OnMediaPlaying: pendingStart={_pendingStartSec:F2} videoVisible={_videoVisible} session={_playSession}");
        if (_pendingStartSec > 0)
            _mediaPlayer.Time = (long)(_pendingStartSec * 1000);

        // 既に映像表示中（ループ再生）→ スタンバイ切替不要・遅延なし
        if (_videoVisible)
        {
            IsBuffering = false;
            Logger.Log("[MW] OnMediaPlaying: loop restart, skip standby transition");
            return;
        }

        // 途中再生（シーク後の映像表示）はシーク完了に時間がかかるため遅延を延長
        int delayMs = _pendingStartSec > 0 ? 800 : 300;
        int capturedSession = _playSession;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            IsBuffering = false;
            if (_playSession != capturedSession)
            {
                Logger.Log($"[MW] OnMediaPlaying timer: session mismatch ({capturedSession}!={_playSession}), skip");
                return;
            }
            // 映像トラックが無い場合（音声のみファイル等）はスタンバイを維持
            if (_mediaPlayer.VideoTrack < 0)
            {
                LoadStandbyImage(_settings.MovieStandbyImagePath);
                StandbyLayer.Opacity    = 1.0;
                StandbyLayer.Visibility = Visibility.Visible;
                Logger.Log("[MW] OnMediaPlaying: no video track → maintain standby");
                return;
            }
            StandbyLayer.Visibility = Visibility.Collapsed;
            VideoView.Visibility    = Visibility.Visible;
            _videoVisible = true;
            Logger.Log($"[MW] OnMediaPlaying ({delayMs}ms): VideoView=Visible, StandbyLayer=Collapsed");
        };
        timer.Start();
    }

    private void OnMediaEnded()
    {
        Logger.Log($"[MW] EndReached AfterPlayback={_afterPlayback}");
        switch (_afterPlayback)
        {
            case AfterPlaybackBehavior.FreezeLastFrame:
                _mediaPlayer.Pause();
                break;
            case AfterPlaybackBehavior.Loop:
                // EndReached 後は Play() だけでは再開できないため新規 Media を生成して再生
                if (_currentFilePath != null)
                {
                    using var loopMedia = new Media(_libVLC, new Uri(_currentFilePath));
                    _mediaPlayer.Play(loopMedia);
                    Logger.Log($"[MW] Loop: restart {_currentFilePath} from {_pendingStartSec:F2}s");
                }
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

    // ───────────────────────── フルスクリーン ─────────────────────────

    public void SetFullScreen(bool full)
    {
        if (full == _isFullScreen) return;
        Debug.WriteLine($"[MW] SetFullScreen: {_isFullScreen}→{full}");

        StopFadeTimer();
        StopStandbyFadeIn();
        // スタンバイ表示中にフルスクリーン切替するとフェードインが中断される → 不透明度を確定
        if (!_videoVisible && StandbyLayer.Visibility == Visibility.Visible)
            StandbyLayer.Opacity = 1.0;
        VideoView.Visibility = Visibility.Hidden;

        Window cover;
        if (full)
        {
            _savedLeft   = Left;
            _savedTop    = Top;
            _savedWidth  = Width;
            _savedHeight = Height;

            cover = new Window
            {
                WindowStyle = WindowStyle.None, ResizeMode = System.Windows.ResizeMode.NoResize,
                Background = System.Windows.Media.Brushes.Black,
                ShowInTaskbar = false, ShowActivated = false, Topmost = true,
                Left = _savedLeft, Top = _savedTop, Width = 1, Height = 1
            };
            cover.Show();
            cover.WindowState = WindowState.Maximized;

            _isFullScreen = true;
            WindowStyle   = WindowStyle.None;
            ResizeMode    = System.Windows.ResizeMode.NoResize;
            WindowState   = WindowState.Maximized;
        }
        else
        {
            cover = new Window
            {
                WindowStyle = WindowStyle.None, ResizeMode = System.Windows.ResizeMode.NoResize,
                Background = System.Windows.Media.Brushes.Black,
                ShowInTaskbar = false, ShowActivated = false, Topmost = true,
                Left = Left, Top = Top, Width = 1, Height = 1
            };
            cover.Show();
            cover.WindowState = WindowState.Maximized;

            _isFullScreen = false;
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode  = System.Windows.ResizeMode.CanResize;
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

    // ───────────────────────── ウィンドウイベント ─────────────────────────

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        UninstallGlobalMouseHook();
        StopFadeTimer();
        StopStandbyFadeIn();

        double saveLeft   = _isFullScreen ? _savedLeft   : Left;
        double saveTop    = _isFullScreen ? _savedTop    : Top;
        double saveWidth  = _isFullScreen ? _savedWidth  : Width;
        double saveHeight = _isFullScreen ? _savedHeight : Height;

        _settings.MovieWindowX      = saveLeft;
        _settings.MovieWindowY      = saveTop;
        _settings.MovieWindowWidth  = saveWidth;
        _settings.MovieWindowHeight = saveHeight;

        try { _mediaPlayer.Stop(); } catch { }
        _mediaPlayer.Dispose();
    }
}
