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
// WPF Airspace 問題により、WPF オーバーレイで ForegroundWindow を覆えない。
// → VideoView.Visibility = Hidden で ForegroundWindow も非表示になる性質を利用。
// → フェードアウトは Win32 SetLayeredWindowAttributes で ForegroundWindow 自体のアルファを変化させる。
// → ダブルクリックは WH_MOUSE_LL グローバルフックで検出（VLC が別スレッドでも動作）。
public partial class MovieWindow : Window
{
    private readonly AppSettings _settings;
    private readonly LibVLC _libVLC;
    private readonly LibVLCSharp.Shared.MediaPlayer _mediaPlayer;

    // 映像フェードアウトタイマー（ForegroundWindow alpha 255→0）
    private DispatcherTimer? _fadeTimer;
    private long   _fadeStartTick;
    private double _fadeDurationSec;

    // スタンバイ画像フェードインタイマー
    private DispatcherTimer? _standbyFadeInTimer;
    private long _standbyFadeInStartTick;

    private bool   _isFullScreen;
    private double _savedLeft, _savedTop, _savedWidth, _savedHeight;

    private double               _pendingStartSec;
    private AfterPlaybackBehavior _afterPlayback = AfterPlaybackBehavior.Stop;
    private bool _videoVisible;

    // VLC ForegroundWindow HWND（フェード用にキャッシュ）
    private IntPtr _fgWindowHwnd = IntPtr.Zero;

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
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint dwThreadId, EnumWindowsProc lpfn, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    // Layered window（ForegroundWindow フェード用）
    [DllImport("user32.dll")] private static extern int  GetWindowLong(IntPtr hwnd, int nIndex);
    [DllImport("user32.dll")] private static extern int  SetWindowLong(IntPtr hwnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);
    private const int  GWL_EXSTYLE  = -20;
    private const int  WS_EX_TOPMOST  = 0x00000008;
    private const int  WS_EX_LAYERED  = 0x00080000;
    private const uint LWA_ALPHA      = 0x00000002;

    // グローバルマウスフック（ダブルクリック検出）
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool   UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? lpModuleName);
    private const int WH_MOUSE_LL    = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int SM_CXDOUBLECLK = 36;
    private const int SM_CYDOUBLECLK = 37;

    private delegate bool   EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
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

    // ───────────────────────── ForegroundWindow 操作 ─────────────────────────

    // スレッド上の Topmost ウィンドウを列挙して VLC ForegroundWindow の HWND を取得（キャッシュ付き）
    private IntPtr GetOrFindForegroundWindowHwnd()
    {
        if (_fgWindowHwnd != IntPtr.Zero) return _fgWindowHwnd;

        var myHwnd = new WindowInteropHelper(this).Handle;
        IntPtr found = IntPtr.Zero;

        var enumProc = new EnumWindowsProc((hwnd, _) =>
        {
            if (hwnd == myHwnd || !IsWindowVisible(hwnd)) return true;
            if ((GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0)
            {
                found = hwnd;
                return false;
            }
            return true;
        });
        EnumThreadWindows(GetCurrentThreadId(), enumProc, IntPtr.Zero);
        GC.KeepAlive(enumProc);

        _fgWindowHwnd = found;
        Debug.WriteLine($"[MW] FgWindowHwnd={_fgWindowHwnd} (found={found != IntPtr.Zero})");
        return _fgWindowHwnd;
    }

    // ForegroundWindow のアルファ値を設定（255=不透明, 0=透明）
    private void SetFgWindowAlpha(byte alpha)
    {
        var hwnd = GetOrFindForegroundWindowHwnd();
        if (hwnd == IntPtr.Zero) return;
        try
        {
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            if ((exStyle & WS_EX_LAYERED) == 0)
                SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_LAYERED);
            SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MW] SetFgWindowAlpha({alpha}) error: {ex.Message}");
        }
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
    // VLC が DBLCLK を横取りしてもこちらには届く（全スレッドの低レベルイベントを受信）。
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
                    // フック中は UI 操作を避け BeginInvoke で遅延実行
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

    // フェードアウト:
    //   ForegroundWindow の WS_EX_LAYERED アルファを 255→0 にして映像を徐々に透明化する。
    //   0 到達後に VideoView を Hidden にし、スタンバイ画像フェードインへ移行する。
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
        double elapsed  = (Environment.TickCount64 - _fadeStartTick) / 1000.0;
        double progress = elapsed / _fadeDurationSec;
        byte   alpha    = (byte)(Math.Clamp(1.0 - progress, 0.0, 1.0) * 255);
        SetFgWindowAlpha(alpha);

        if (progress < 1.0) return;

        // フェードアウト完了
        _fadeTimer!.Stop();
        _fadeTimer.Tick -= FadeTimer_Tick;
        _fadeTimer = null;

        // VideoView を先に非表示（ForegroundWindow も非表示）にしてから VLC を停止する。
        // Stop() より先に Hidden にすることで VLC の白フレームが表示されるのを防ぐ。
        _videoVisible        = false;
        VideoView.Visibility = Visibility.Hidden;
        SetFgWindowAlpha(255); // 次回表示のためにアルファをリセット

        LoadStandbyImage(_settings.MovieStandbyImagePath);
        StandbyLayer.Opacity    = 0;
        StandbyLayer.Visibility = Visibility.Visible;
        Debug.WriteLine("[MW] FadeOut complete → StandbyFadeIn start");

        // VLC 停止を非同期で実行（UIスレッドのブロック回避・クラッシュ防止）
        Task.Run(() => { try { _mediaPlayer.Stop(); } catch (Exception ex) { Debug.WriteLine($"[MW] Stop error: {ex.Message}"); } });

        StartStandbyFadeIn();
    }

    private void StopFadeTimer()
    {
        if (_fadeTimer == null) return;
        _fadeTimer.Stop();
        _fadeTimer.Tick -= FadeTimer_Tick;
        _fadeTimer = null;
        SetFgWindowAlpha(255); // フェード中断時にアルファをリセット
        StandbyLayer.Opacity = 1.0;
        Debug.WriteLine("[MW] StopFadeTimer: interrupted, alpha reset");
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
        // VideoView を先に非表示にしてから Stop（白フレームを見せない）
        _videoVisible        = false;
        VideoView.Visibility = Visibility.Hidden;
        Task.Run(() => { try { _mediaPlayer.Stop(); } catch { } });

        SetFgWindowAlpha(255);
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

        SetFgWindowAlpha(255); // フェード中断後にアルファをリセット
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

    // ───────────────────────── フルスクリーン ─────────────────────────

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
            // 座標をカバー生成より先に保存
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

        // カバーウィンドウが表示されている間に ForegroundWindow HWND キャッシュが汚染されないよう
        // ForegroundWindow の検索は SetFullScreen が完了してから行う
        _fgWindowHwnd = IntPtr.Zero;

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

        // _libVLC は MovieController が管理するため Dispose しない
        try { _mediaPlayer.Stop(); } catch { }
        _mediaPlayer.Dispose();
    }
}
