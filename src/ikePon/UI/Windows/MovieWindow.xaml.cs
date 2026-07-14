using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
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
    private bool   _isFullScreenTransitioning; // 高速連打による状態破損防止
    private double _savedLeft, _savedTop, _savedWidth, _savedHeight;

    private string? _currentFilePath;
    private AfterPlaybackBehavior _afterPlayback = AfterPlaybackBehavior.Stop;
    private bool            _videoVisible;
    private volatile int    _playSession; // volatile: VLC スレッドから EndReached 時に読む

    // PositionChanged によるループ検出 & 初回フレーム検知
    private float          _lastPosition;
    private volatile bool  _loopEndHandled;
    private double         _videoEndSec = -1; // PlayVideo に渡された endSec（:stop-time の基準）

    // PositionChanged で VLC の初回フレームデコードを検知して VideoView を表示する
    private volatile bool _waitingFirstFrame;
    private volatile int  _firstFrameSession;
    private DispatcherTimer? _firstFrameFallbackTimer; // UI スレッドのみ使用
    // ForegroundWindow の背景を黒に保ち続けるポーリングタイマー（Playing〜映像表示まで）
    private DispatcherTimer? _bgFixTimer;

    // レターボックス黒帯オーバーレイ（ForegroundWindow._grid への WPF Rectangle 追加）
    private static readonly System.Reflection.BindingFlags _reflFlags =
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
    private Window? _fgWin;
    private Grid?   _fgGrid;
    private readonly List<UIElement> _letterboxElements = new();
    // ForegroundWindow 内スタンバイレイヤー（VideoView 常時 Visible 化に伴い WPF StandbyLayer の代替）
    private Grid?  _fgStandbyLayer;
    private Image? _fgStandbyImage;

    public bool IsBuffering { get; private set; }

    /// <summary>
    /// DISPが点灯中にスタンバイ画像なし・映像なし・バッファ中でもない = 黒画面状態
    /// </summary>
    public bool IsBlackScreen =>
        !IsBuffering && !_videoVisible && (_fgStandbyImage?.Source ?? StandbyImage.Source) == null;

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
    private const int WM_RBUTTONDOWN = 0x0204;
    private const double DefaultWindowWidth  = 1280;
    private const double DefaultWindowHeight = 720;
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
    public bool IsFading     => _fadeTimer != null;
    public event Action<bool>? FullScreenChanged;
    // 動画がループ終端に達したとき（AfterPlayback=Loop）に発火。再起動はMainWindowが担う。
    public event Action? LoopEndReached;
    // 映像が画面に表示された瞬間に発火。パラメータは VLC の現在再生位置（ms）。音声同期補正用。
    public event Action<long>? VideoShown;

    // VLC の現在再生位置をミリ秒で返す。再生中でなければ -1。
    public long GetCurrentTimeMs() =>
        _mediaPlayer.State == VLCState.Playing ? _mediaPlayer.Time : -1L;

    public MovieWindow(AppSettings settings, LibVLC libVLC)
    {
        _settings    = settings;
        _libVLC      = libVLC;
        _mediaPlayer = new LibVLCSharp.Shared.MediaPlayer(_libVLC);

        InitializeComponent();

        Loaded += (_, _) =>
        {
            VideoView.MediaPlayer = _mediaPlayer;
            // ForegroundWindow は OnApplyTemplate() で生成される。Loaded 後にキャッシュしておく。
            CacheFgWin();
            // ウィンドウリサイズ時にレターボックス黒帯を再計算する
            VideoView.SizeChanged += (_, _) => { if (_videoVisible) ApplyLetterboxBlackOverlay(); };
            Debug.WriteLine("[MW] Loaded: MediaPlayer assigned");
        };

        _mediaPlayer.Playing          += (_, _) => Dispatcher.BeginInvoke(OnMediaPlaying);
        _mediaPlayer.EndReached       += (_, _) =>
        {
            int sess = _playSession; // volatile read: PrepareForPlay が先に走っていた場合にずれを検出
            Logger.Log($"[MW] EndReached fired (VLC thread, session={sess})");
            Dispatcher.BeginInvoke(() => OnMediaEnded(sess));
        };
        _mediaPlayer.EncounteredError += (_, _) => Dispatcher.BeginInvoke(OnMediaError);
        // EndReached が来ない .mov 対策：位置の後半→前半ジャンプでループ終端を検出する
        _mediaPlayer.PositionChanged  += (_, e) => CheckLoopFromPosition(e.Position);

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

    // WH_MOUSE_LL コールバック：左クリックでダブルクリック検出、右クリックでウィンドウサイズリセット。
    private IntPtr GlobalMouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsVisible)
        {
            int msg    = (int)wParam;
            var hook   = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            var myHwnd = new WindowInteropHelper(this).Handle;
            if (myHwnd != IntPtr.Zero && GetWindowRect(myHwnd, out var rc) &&
                hook.pt.x >= rc.left && hook.pt.x <= rc.right &&
                hook.pt.y >= rc.top  && hook.pt.y <= rc.bottom)
            {
                if (msg == WM_LBUTTONDOWN)
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
                else if (msg == WM_RBUTTONDOWN && !_isFullScreen)
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        Debug.WriteLine("[MW] GlobalMouseHook: RightClick → reset window size");
                        Width  = DefaultWindowWidth;
                        Height = DefaultWindowHeight;
                    });
                }
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    // ───────────────────────── 映像制御 ─────────────────────────

    public void LoadStandbyImage(string? path)
    {
        BitmapImage? bmp = null;
        if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
        {
            try { bmp = new BitmapImage(new Uri(path, UriKind.Absolute)); }
            catch { }
        }
        StandbyImage.Source = bmp;
        if (_fgStandbyImage != null) _fgStandbyImage.Source = bmp;
    }

    // 動画再生開始前の準備：スタンバイ画像は表示せず黒のみ（フラッシュ防止）
    private void PrepareForPlay()
    {
        _playSession++;       // セッション更新（VLC thread の EndReached が古いセッションを持つか判定に使う）
        _waitingFirstFrame   = false;  // 旧セッションの初回フレーム待ちをキャンセル
        _firstFrameFallbackTimer?.Stop();
        _firstFrameFallbackTimer = null;
        _loopEndHandled      = false;
        _lastPosition        = 0f;
        _videoEndSec         = -1;
        IsBuffering          = true;
        _videoVisible        = false;
        RemoveLetterboxBlackOverlay();
        // VideoView は常に Visible（Collapsed にすると D3D11 スワップチェーンが再作成され白フラッシュが発生）
        // Stop() は呼ばない。直後の _mediaPlayer.Play(newMedia) が内部で旧メディアを停止する。
        // Task.Run(Stop) + Play() の並列実行による VLC 内部デッドロックを回避。
        StandbyImage.Source = null;
        if (_fgStandbyImage != null) _fgStandbyImage.Source = null;
        ShowFgStandby(1.0); // 黒画面で即時覆う（スタンバイ画像なし）
        Logger.Log($"[MW] PrepareForPlay: session={_playSession}");
    }

    public void PlayVideo(string filePath, double startSec, double endSec = -1,
        AfterPlaybackBehavior afterPlayback = AfterPlaybackBehavior.Stop)
    {
        StopFadeTimer();
        StopStandbyFadeIn();
        PrepareForPlay(); // ShowStandby の代わりに黒画面で待機（スタンバイ画像フラッシュ防止）

        string ext     = System.IO.Path.GetExtension(filePath);
        bool   isVideo = VideoExts.Contains(ext);
        bool   isImage = ImageExts.Contains(ext);
        Logger.Log($"[MW] PlayVideo ext={ext} video={isVideo} image={isImage} start={startSec:F2} end={endSec:F2}");

        if (isImage)
        {
            BitmapImage? bmp = null;
            try { bmp = new BitmapImage(new Uri(filePath, UriKind.Absolute)); }
            catch { }
            StandbyImage.Source = bmp;
            if (_fgStandbyImage != null) _fgStandbyImage.Source = bmp;
            ShowFgStandby(1.0);
            _afterPlayback = afterPlayback;
            _videoVisible  = true;
            Debug.WriteLine("[MW]   static image displayed");
            return;
        }

        if (!isVideo)
        {
            // 映像/静止画でないファイル（音声のみ等）→ スタンバイ画面を維持
            LoadStandbyImage(_settings.MovieStandbyImagePath);
            ShowFgStandby(1.0);
            IsBuffering = false;
            Debug.WriteLine("[MW]   skipped: not video/image ext → show standby");
            return;
        }

        _videoEndSec      = endSec;
        _afterPlayback    = afterPlayback;
        _currentFilePath  = filePath;

        using var media = new Media(_libVLC, new Uri(filePath));
        // :start-time で VLC を指定位置から直接再生（PositionChanged で初回フレームを検知）
        if (startSec > 0)
        {
            media.AddOption($":start-time={startSec:F3}");
            Logger.Log($"[MW]   VLC :start-time={startSec:F3}");
        }
        if (endSec > 0)
        {
            media.AddOption($":stop-time={endSec:F3}");
            Logger.Log($"[MW]   VLC :stop-time={endSec:F3}");
        }
        _mediaPlayer.Play(media);
        Debug.WriteLine("[MW]   MediaPlayer.Play() called");
    }

    public void StopVideo()
    {
        Debug.WriteLine("[MW] StopVideo");
        StopFadeTimer();
        StopStandbyFadeIn();
        ShowStandby(immediate: true); // カットアウト：即時表示でVLC白地フラッシュを防ぐ
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
        else if (Content is FrameworkElement contentEl)
        {
            var pSrc  = PresentationSource.FromVisual(this);
            double dpiX = pSrc?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double dpiY = pSrc?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
            var pt = contentEl.PointToScreen(new System.Windows.Point(0, 0));
            overlayLeft   = pt.X / dpiX;
            overlayTop    = pt.Y / dpiY;
            overlayWidth  = contentEl.ActualWidth;
            overlayHeight = contentEl.ActualHeight;
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

        // VideoView は常に Visible（スワップチェーン維持）
        _videoVisible = false;

        // オーバーレイが覆っている間にスタンバイを opacity=1 で用意する。
        // 0 → フェードインにすると、オーバーレイが閉じた瞬間にVLC白地が透けるため即時表示。
        LoadStandbyImage(_settings.MovieStandbyImagePath);
        ShowFgStandby(1.0);

        // VLC停止（非同期）
        Task.Run(() => { try { _mediaPlayer.Stop(); } catch (Exception ex) { Debug.WriteLine($"[MW] Stop error: {ex.Message}"); } });

        // オーバーレイをBackground優先度で閉じる（スタンバイは既に opacity=1 なので即座に見える）
        var overlay = _fadeOverlay;
        _fadeOverlay = null;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            overlay?.Close();
            Logger.Log("[MW] FadeOut complete");
        }));
    }

    private void StopFadeTimer()
    {
        if (_fadeTimer == null) return;
        _fadeTimer.Stop();
        _fadeTimer.Tick -= FadeTimer_Tick;
        _fadeTimer = null;
        if (_fadeOverlay != null) { _fadeOverlay.Close(); _fadeOverlay = null; }
        SetFgStandbyOpacity(1.0);
        Debug.WriteLine("[MW] StopFadeTimer: interrupted");
    }

    private void StartStandbyFadeIn()
    {
        float duration = _settings.StandbyFadeInDuration;
        if (duration <= 0.02f)
        {
            SetFgStandbyOpacity(1.0);
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
        SetFgStandbyOpacity(Math.Min(progress, 1.0));
        if (progress >= 1.0)
        {
            SetFgStandbyOpacity(1.0);
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
    // immediate=true: カットアウト用（opacity 1 で即時表示。VLC白地が透けるフラッシュを防ぐ）
    // immediate=false: フェードアウト完了後用（opacity 0 から徐々にフェードイン）
    public void ShowStandby(bool immediate = false)
    {
        Logger.Log($"[MW] ShowStandby (videoVisible={_videoVisible} immediate={immediate})");
        _waitingFirstFrame = false;
        _firstFrameFallbackTimer?.Stop();
        _firstFrameFallbackTimer = null;
        _bgFixTimer?.Stop();
        _bgFixTimer = null;
        IsBuffering = false;
        StopFadeTimer();
        StopStandbyFadeIn();
        _playSession++;
        _videoVisible = false;
        // VideoView は常に Visible（スワップチェーン維持）
        Task.Run(() => { try { _mediaPlayer.Stop(); } catch { } });

        LoadStandbyImage(_settings.MovieStandbyImagePath);
        if (immediate)
        {
            ShowFgStandby(1.0); // 即時表示（カットアウト。VLC白地フラッシュを防ぐ）
        }
        else
        {
            ShowFgStandby(0.0);    // Opacity=0 でセット → フェードインへ（フェードアウト完了後）
            StartStandbyFadeIn();
        }
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
        Logger.Log($"[MW] OnMediaPlaying: videoVisible={_videoVisible} session={_playSession}");

        // Pause→Resume からの Playing イベント（映像表示済み）→ 不要
        if (_videoVisible)
        {
            Logger.Log("[MW] OnMediaPlaying: already visible → skip");
            return;
        }

        // Playing 発火直後に ForegroundWindow を黒に確保し、映像表示まで 50ms ごとに維持する
        EnsureVideoViewBlackBackground();
        _bgFixTimer?.Stop();
        _bgFixTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _bgFixTimer.Tick += (_, _) =>
        {
            EnsureVideoViewBlackBackground();
            if (_videoVisible) { _bgFixTimer!.Stop(); _bgFixTimer = null; }
        };
        _bgFixTimer.Start();

        int capturedSession = _playSession;
        _firstFrameSession = capturedSession;
        _waitingFirstFrame = true;

        // フォールバックタイマー：PositionChanged が 1000ms 来なければ強制表示
        _firstFrameFallbackTimer?.Stop();
        _firstFrameFallbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        _firstFrameFallbackTimer.Tick += (_, _) =>
        {
            _firstFrameFallbackTimer!.Stop();
            _firstFrameFallbackTimer = null;
            if (!_waitingFirstFrame) return;
            _waitingFirstFrame = false;
            Logger.Log("[MW] FirstFrame fallback: forced show");
            ShowVideoView(capturedSession);
        };
        _firstFrameFallbackTimer.Start();
        Logger.Log("[MW] OnMediaPlaying: waiting for first PositionChanged");
    }

    // VLC が最初のフレームをデコードしたとき（または fallback）に映像を表示する
    private void ShowVideoView(int session)
    {
        _bgFixTimer?.Stop();
        _bgFixTimer = null;
        IsBuffering = false;
        if (_playSession != session)
        {
            Logger.Log($"[MW] ShowVideoView: stale session ({session}!={_playSession}), skip");
            return;
        }
        if (_mediaPlayer.VideoTrack < 0)
        {
            LoadStandbyImage(_settings.MovieStandbyImagePath);
            ShowFgStandby(1.0);
            Logger.Log("[MW] ShowVideoView: no video track → standby");
            return;
        }
        // スタンバイレイヤーを隠す前に黒帯をセット → 映像の最初の描画で周囲が黒になる
        ApplyLetterboxBlackOverlay(((Grid)Content).ActualWidth, ((Grid)Content).ActualHeight);
        HideFgStandby(); // VideoView は常に Visible のまま → _fgStandbyLayer を外すだけで映像が見える
        _videoVisible = true;
        Logger.Log("[MW] ShowVideoView: _fgStandbyLayer=Collapsed → video visible");
        long vlcMs = _mediaPlayer.Time;
        if (vlcMs >= 0) VideoShown?.Invoke(vlcMs);
    }

    private void OnMediaEnded(int sessionAtFire = -1)
    {
        if (sessionAtFire >= 0 && sessionAtFire != _playSession)
        {
            Logger.Log($"[MW] EndReached STALE: session {sessionAtFire} != {_playSession} → skip");
            return;
        }
        Logger.Log($"[MW] OnMediaEnded: AfterPlayback={_afterPlayback} handled={_loopEndHandled} fadeTimer={_fadeTimer != null}");
        switch (_afterPlayback)
        {
            case AfterPlaybackBehavior.FreezeLastFrame:
                _mediaPlayer.Pause();
                break;
            case AfterPlaybackBehavior.Loop:
                // FireLoopEnd が先に処理済みの場合はスキップ。フェードアウト中も無視。
                if (_currentFilePath != null && _fadeTimer == null && !_loopEndHandled)
                {
                    _loopEndHandled = true;
                    StopFadeTimer();
                    StopStandbyFadeIn();
                    _videoVisible = false;
                    // VideoView は常に Visible（スワップチェーン維持）
                    StandbyImage.Source = null;
                    if (_fgStandbyImage != null) _fgStandbyImage.Source = null;
                    ShowFgStandby(1.0);
                    // Stop() は呼ばない（PrepareForPlay の Play(newMedia) に任せる）。
                    // Task.Run(Stop)+Play() 並列実行による VLC レンダラー破損防止。
                    Logger.Log("[MW] Loop end via EndReached → LoopEndReached");
                    LoopEndReached?.Invoke();
                }
                else
                {
                    Logger.Log($"[MW] EndReached SKIPPED (handled={_loopEndHandled} fade={_fadeTimer != null})");
                }
                break;
            default:
                ShowStandby();
                break;
        }
    }

    // PositionChanged から LoopEndReached を直接発火するハンドラ（UI スレッドで実行）。
    // OnMediaEnded を経由しないため _loopEndHandled チェックに引っかからない。
    private void FireLoopEnd()
    {
        if (_currentFilePath == null || _fadeTimer != null) return;
        StopFadeTimer();
        StopStandbyFadeIn();
        _videoVisible = false;
        // VideoView は常に Visible（スワップチェーン維持）→ _fgStandbyLayer で黒画面を被せる
        StandbyImage.Source = null;
        if (_fgStandbyImage != null) _fgStandbyImage.Source = null;
        ShowFgStandby(1.0);
        Logger.Log("[MW] FireLoopEnd → black screen (fgStandby), LoopEndReached");
        LoopEndReached?.Invoke();
        // VLC 停止は PrepareForPlay（次の PlayVideo 内）で行う（レース防止）
    }

    // VLC が EndReached を発火しない場合の代替検出（PositionChanged から呼ばれる、VLC スレッド）。
    // 初回フレームデコード検知も兼ねる。
    private void CheckLoopFromPosition(float pos)
    {
        float prev = _lastPosition;
        _lastPosition = pos;

        // 初回フレームデコード検知：VLC が pos > 0.01 に達した＝最初の映像フレーム準備完了
        if (_waitingFirstFrame && pos > 0.01f)
        {
            _waitingFirstFrame = false;
            int sess = _firstFrameSession;
            Dispatcher.BeginInvoke(() =>
            {
                _firstFrameFallbackTimer?.Stop();
                _firstFrameFallbackTimer = null;
                ShowVideoView(sess);
            });
        }

        if (_afterPlayback != AfterPlaybackBehavior.Loop) return;
        if (!_videoVisible || _fadeTimer != null || _loopEndHandled) return;

        // パターン1: 位置が後半→前半にジャンプ（VLC 内部自動ループ）
        if (prev > 0.80f && pos < 0.15f)
        {
            _loopEndHandled = true;
            Logger.Log($"[MW] Position jump: {prev:F3}→{pos:F3} → FireLoopEnd");
            // Stop() は呼ばない。Task.Run(Stop)+Play() の並列実行が VLC レンダラーを壊す原因のため。
            Dispatcher.BeginInvoke(FireLoopEnd);
            return;
        }

        // パターン2: endSec の 300ms 前に先手で VideoView を隠す
        if (_videoEndSec > 0)
        {
            long   timeMs  = _mediaPlayer.Time;
            double timeSec = timeMs / 1000.0;
            if (timeMs > 0 && timeSec >= _videoEndSec - 0.30)
            {
                _loopEndHandled = true;
                Logger.Log($"[MW] Time-based: {timeSec:F2}s >= endSec-0.30={_videoEndSec - 0.30:F2}s → FireLoopEnd");
                Dispatcher.BeginInvoke(FireLoopEnd);
            }
        }
    }

    // ─── レターボックス黒帯オーバーレイ ───────────────────────────────────
    // VLC D3D11 レンダラーはレターボックス領域を描画しない。
    // VideoHwndHost は Win32 'static' クラス HWND でその背景色が白 (COLOR_WINDOW)。
    // ForegroundWindow（VLC HWND の前面に位置する透明 WPF Window）の _grid に
    // 黒い Rectangle を追加してレターボックス部分だけを覆う。
    // 映像領域には WPF 要素を置かず透明のまま → VLC 映像が透けて見える。

    // ForegroundWindow と _grid への参照をキャッシュする（OnApplyTemplate() 後に生成される）
    private void CacheFgWin()
    {
        try
        {
            var prop = VideoView.GetType().GetProperty("ForegroundWindow", _reflFlags);
            _fgWin  = prop?.GetValue(VideoView) as Window;
            if (_fgWin == null) return;
            var f   = _fgWin.GetType().GetField("_grid", _reflFlags);
            _fgGrid = f?.GetValue(_fgWin) as Grid;
            if (_fgGrid == null) return;

            // ForegroundWindow 内にスタンバイレイヤーを作成（VideoView 常時 Visible 化の代替表示手段）
            _fgStandbyImage = new Image
            {
                Stretch             = System.Windows.Media.Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                Source              = StandbyImage.Source
            };
            _fgStandbyLayer = new Grid { Background = System.Windows.Media.Brushes.Black };
            Panel.SetZIndex(_fgStandbyLayer, 100); // letterbox 要素より必ず前面に配置
            _fgStandbyLayer.Children.Add(_fgStandbyImage);
            _fgGrid.Children.Add(_fgStandbyLayer);

            // Loaded 前の ShowStandby/PrepareForPlay 結果に合わせて初期状態を同期
            if (_videoVisible)
            {
                _fgStandbyLayer.Visibility = Visibility.Collapsed;
            }
            else
            {
                _fgStandbyLayer.Opacity    = StandbyLayer.Opacity;
                _fgStandbyLayer.Visibility = StandbyLayer.Visibility;
            }
            // WPF 側 StandbyLayer は _fgStandbyLayer が引き継ぐため非表示にする
            StandbyLayer.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) { Logger.Log($"[BG] CacheFgWin: {ex.Message}"); }
    }

    // レターボックス/ピラーボックス領域へ黒い Canvas を追加する
    // viewW/viewH を省略すると VideoView.ActualWidth/Height を使用（SizeChanged 用）
    // 省略しない場合は指定値を使用（ShowVideoView からの事前適用用）
    private void ApplyLetterboxBlackOverlay(double viewW = 0, double viewH = 0)
    {
        if (viewW <= 0) viewW = VideoView.ActualWidth;
        if (viewH <= 0) viewH = VideoView.ActualHeight;

        RemoveLetterboxBlackOverlay();
        if (_fgGrid == null || viewW <= 0 || viewH <= 0) return;

        uint vw = 0, vh = 0;
        if (!_mediaPlayer.Size(0, ref vw, ref vh) || vw == 0 || vh == 0) return;

        double videoAspect = (double)vw / vh;
        double viewAspect  = viewW / viewH;
        double diff        = videoAspect - viewAspect;

        if (Math.Abs(diff) < 0.005) return;

        var black = System.Windows.Media.Brushes.Black;
        // Canvas + Canvas.SetLeft/Top で絶対座標配置（HorizontalAlignment.Right 等はコンテナ幅依存で不安定）
        var canvas = new Canvas { IsHitTestVisible = false };

        if (diff > 0)
        {
            // VIDEO が VIEW より横長 → 幅に合わせてスケール → Letterbox（上下に黒帯）
            double bh = Math.Ceiling((viewH - viewW / videoAspect) / 2) + 1;
            if (bh > 1)
            {
                var top = new Rectangle { Fill = black, Width = viewW, Height = bh };
                Canvas.SetLeft(top, 0); Canvas.SetTop(top, 0);
                canvas.Children.Add(top);
                var bot = new Rectangle { Fill = black, Width = viewW, Height = bh };
                Canvas.SetLeft(bot, 0); Canvas.SetTop(bot, viewH - bh);
                canvas.Children.Add(bot);
            }
        }
        else
        {
            // VIDEO が VIEW より縦長 → 高さに合わせてスケール → Pillarbox（左右に黒帯）
            double bw = Math.Ceiling((viewW - viewH * videoAspect) / 2) + 1;
            if (bw > 1)
            {
                var lft = new Rectangle { Fill = black, Width = bw, Height = viewH };
                Canvas.SetLeft(lft, 0); Canvas.SetTop(lft, 0);
                canvas.Children.Add(lft);
                var rgt = new Rectangle { Fill = black, Width = bw, Height = viewH };
                Canvas.SetLeft(rgt, viewW - bw); Canvas.SetTop(rgt, 0); // 絶対X座標で右端を指定
                canvas.Children.Add(rgt);
            }
        }

        if (canvas.Children.Count > 0)
        {
            _fgGrid.Children.Add(canvas);
            _letterboxElements.Add(canvas);
        }
        Logger.Log($"[BG] Letterbox overlay: video={vw}x{vh} view={viewW:F0}x{viewH:F0} bars={canvas.Children.Count}");
    }

    private void RemoveLetterboxBlackOverlay()
    {
        if (_fgGrid == null || _letterboxElements.Count == 0) return;
        foreach (var el in _letterboxElements)
            _fgGrid.Children.Remove(el);
        _letterboxElements.Clear();
    }

    // ─── _fgStandbyLayer ヘルパー ─────────────────────────────────────────
    // _fgStandbyLayer が未生成の場合は WPF StandbyLayer にフォールバックする（Loaded 前の短期間のみ）

    private void ShowFgStandby(double opacity)
    {
        if (_fgStandbyLayer != null)
        {
            _fgStandbyLayer.Opacity    = opacity;
            _fgStandbyLayer.Visibility = Visibility.Visible;
        }
        else
        {
            StandbyLayer.Opacity    = opacity;
            StandbyLayer.Visibility = Visibility.Visible;
        }
    }

    private void HideFgStandby()
    {
        if (_fgStandbyLayer != null) _fgStandbyLayer.Visibility = Visibility.Collapsed;
        else StandbyLayer.Visibility = Visibility.Collapsed;
    }

    private void SetFgStandbyOpacity(double opacity)
    {
        if (_fgStandbyLayer != null) _fgStandbyLayer.Opacity = opacity;
        else StandbyLayer.Opacity = opacity;
    }

    // ForegroundWindow は透明であることを保証する（デフォルトで Transparent だが念のため）
    private void EnsureVideoViewBlackBackground()
    {
        if (_fgWin != null)
            _fgWin.Background = System.Windows.Media.Brushes.Transparent;
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
        if (_isFullScreenTransitioning) { Debug.WriteLine("[MW] SetFullScreen: transition in progress, skip"); return; }
        _isFullScreenTransitioning = true;
        Debug.WriteLine($"[MW] SetFullScreen: {_isFullScreen}→{full}");

        StopFadeTimer();
        StopStandbyFadeIn();
        // 遷移中のフラッシュ防止: スタンバイレイヤーの状態を保存してカバー閉鎖後に復元する
        bool fgStandbyWasVisible = (_fgStandbyLayer?.Visibility ?? StandbyLayer.Visibility) == Visibility.Visible;
        double savedFgOpacity   = _fgStandbyLayer?.Opacity ?? StandbyLayer.Opacity;
        if (_fgStandbyLayer != null) _fgStandbyLayer.Visibility = Visibility.Collapsed;
        StandbyLayer.Visibility = Visibility.Collapsed;
        // VideoView は Hidden にして ForegroundWindow サイズを保持しつつ非表示にする
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
            VideoView.Visibility = Visibility.Visible; // 常に Visible に戻す
            if (fgStandbyWasVisible)
            {
                if (_fgStandbyLayer != null)
                {
                    _fgStandbyLayer.Opacity    = savedFgOpacity;
                    _fgStandbyLayer.Visibility = Visibility.Visible;
                }
                else
                {
                    StandbyLayer.Opacity    = savedFgOpacity;
                    StandbyLayer.Visibility = Visibility.Visible;
                }
            }
            _isFullScreenTransitioning = false;
            Debug.WriteLine($"[MW] SetFullScreen: cover closed, VideoView=Visible fgStandby={fgStandbyWasVisible}");
        }));

        FullScreenChanged?.Invoke(_isFullScreen);
    }

    // ───────────────────────── ウィンドウイベント ─────────────────────────

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        _waitingFirstFrame = false;
        _firstFrameFallbackTimer?.Stop();
        _firstFrameFallbackTimer = null;
        _bgFixTimer?.Stop();
        _bgFixTimer = null;
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
