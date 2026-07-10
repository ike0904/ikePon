using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ikePon.Audio;
using ikePon.Controller;
using ikePon.Model;
using ikePon.UI.Controls;
using ikePon.UI.Dialogs;
using ikePon.UI.Windows;
using TapBehavior = ikePon.Model.TapBehavior;

namespace ikePon;

public partial class MainWindow : Window
{
    private readonly AudioEngine _engine;
    private readonly AppSettings _settings;
    private readonly FileGainDatabase _gainDb;
    private readonly PlaybackController _playback;
    private readonly KeyboardMapper _keyMapper;
    private readonly MovieController _movieCtrl;
    private readonly MidiController _midi;
    private ProjectData _project;
    private string? _projectFilePath;
    private bool _projectDirty;
    private bool _initComplete;
    private bool _authorTitleActive;      // 起動直後のみ "by Ike-san" 表示
    private string? _assocFilePath;      // 関連付けで渡された .ikp パス
    private bool _blackScreenWarning;     // 黒画面警告表示中フラグ
    private const string BlackScreenMsg = "映像ウィンドウが黒画面です（DISP を OFF→ON で復旧）";

    private readonly PadButton[] _padButtons = new PadButton[BankData.PadCount];
    private readonly Button[] _bankButtons = new Button[ProjectData.BankCount];
    private readonly VFaderControl[] _faders = new VFaderControl[4];

    private PadSettings? _clipboardPad;
    private BankData? _bankClipboard;
    private bool[,] _missingPads = new bool[ProjectData.BankCount, BankData.PadCount];

    private static readonly HashSet<string> AudioVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".wav", ".flac", ".ogg", ".aac", ".m4a", ".mp4", ".mov", ".mkv", ".avi", ".wmv",
          ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tiff", ".tif" };
    private static readonly HashSet<string> VideoImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mov", ".mkv", ".avi", ".wmv",
          ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tiff", ".tif" };
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tiff", ".tif" };

    private static bool IsImageFile(string? path) =>
        !string.IsNullOrEmpty(path) && ImageExtensions.Contains(System.IO.Path.GetExtension(path));

    private int _imageDisplayingPadIndex = -1;
    private int _imageFadingPadIndex = -1;
    private int _videoBufferingPadIndex = -1;
    private long _imageFadeStartTick;
    private float _imageFadeDuration;

    // 動画ループ制御：現在再生中の Movie パッドと、ループ再起動キャンセル用セッション番号
    private int _currentMoviePadIndex = -1;
    private int _movieLoopSession;
    private int _videoLoopingPadIndex = -1; // ループ間の300ms黒画面中、黄色枠を維持するため

    // 映像↔音声同期補正
    private int  _videoSyncMismatchFrames;
    private long _vlcLastKnownTimeMs = -1;  // 前回 VLC から取得した Time 値

    // 動画表示時間の壁掛け時計（整数秒カウンタ・VLC補正対応）
    private int  _movieDisplayedSec = 0;    // 表示用再生秒数（整数）
    private long _movieSecTick      = -1;   // 直近1秒起算点のTickCount64
    private bool _movieSecPaused    = false; // ポーズ中フラグ

    private readonly DispatcherTimer _uiTimer;

    // LOCK/FULL/DISP/FADE-CUT/PAUSEボタンのテンプレートパーツ（遅延キャッシュ）
    private System.Windows.Controls.Border? _lockBd;
    private System.Windows.Controls.Border? _fullBd;
    private System.Windows.Controls.TextBlock? _fullText;
    private System.Windows.Controls.Border? _dispBd;
    private System.Windows.Controls.TextBlock? _dispText;
    private System.Windows.Controls.Border? _panicBd;
    private System.Windows.Controls.Border? _fadeCutBd;
    private System.Windows.Controls.Border? _pauseBd;

    // ロック状態
    private bool _isLocked;

    // パッド D&D 状態
    private System.Windows.Point _padDragStartPoint;
    private int _padDragSourceIdx = -1;
    private bool _padDragActive;
    private bool _padShiftOnDown;
    private bool _padCtrlOnDown;

    // バンク D&D 状態
    private System.Windows.Point _bankDragStartPoint;
    private int _bankDragSourceIdx = -1;
    private bool _bankDragActive;

    // ALL CUT 黄色フラッシュ用タイマー
    private DispatcherTimer? _panicFlashTimer;
    // ALL FADE 黄→青アニメーション用タイマー
    private DispatcherTimer? _fadeAnimTimer;
    private long _fadeAnimStartTick;
    // PAUSEボタンの一時停止状態（PAUSE ボタン自身でまとめて一時停止したときのみ true）
    private bool _isPauseAllActive;

    // インフォメーション次アクション消去フラグ（確認メッセージ以外の通常メッセージのみ true）
    private bool _infoClearPending;

    // バンク読み込み中フラグ（true 中はパッドをグレーアウト・操作ブロック）
    private bool _bankLoading;
    // 読み込み完了後に表示するバンク切り替えメッセージ
    private string? _pendingBankSwitchMsg;

    // Undo/Redo スタック（ProjectData の JSON スナップショット）
    private readonly Stack<string> _undoStack = new();
    private readonly Stack<string> _redoStack = new();
    private const int MaxUndoHistory = 20;

    // 確認待ちフラグ（バンク切り替え）
    // 確認待ち（バンク削除）
    private int _pendingBankClearIndex = -1;
    // 確認待ち（フェーダーメモリ上書き）
    private (VFaderControl fader, int slot, double gain)? _pendingMemOverwrite;
    // 確認待ち（ikpファイルD&D読み込み）
    private string? _pendingIkpPath;
    // 確認待ち（ファイルメニュー「開く」）
    private bool _pendingOpenConfirm;
    // 確認待ち（新規プロジェクト）
    private bool _pendingNewConfirm;

    private static readonly SolidColorBrush BrushBankNormal   = new(Color.FromRgb(0x30, 0x30, 0x30));
    private static readonly SolidColorBrush BrushBankBorderN  = new(Color.FromRgb(0x55, 0x55, 0x55));
    private static readonly SolidColorBrush BrushBankBorderS  = new(Color.FromRgb(0xFF, 0xD7, 0x00)); // 黄色ボーダー（選択中）
    private static readonly SolidColorBrush BrushInfoWarnBg   = new(Color.FromRgb(0x44, 0x22, 0x00));
    private static readonly SolidColorBrush BrushInfoWarnText = new(Color.FromRgb(0xFF, 0xDD, 0x00));
    private static readonly SolidColorBrush BrushInfoNormal   = new(Colors.White);


    public MainWindow()
    {
        _settings = AppSettings.Load();
        _gainDb = FileGainDatabase.Load();
        _engine = new AudioEngine();
        _playback = new PlaybackController(_engine, _settings, _gainDb);
        _keyMapper = new KeyboardMapper();
        _movieCtrl = new MovieController(_settings);
        _midi = new MidiController(Dispatcher);
        _project   = new ProjectData();

        InitializeComponent();
        if (_settings.WindowWidth.HasValue && _settings.WindowHeight.HasValue)
        {
            Width  = Math.Max(_settings.WindowWidth.Value,  MinWidth);
            Height = Math.Max(_settings.WindowHeight.Value, MinHeight);
        }
        App.SetLightTitleBar(this);
        BuildPadGrid();
        BuildBankGrid();
        BuildMixerGrid();
        WireEvents();

        _playback.SetProject(_project);
        _engine.Start(_settings.WasapiLatencyMs);
        _engine.PaSeparate = _settings.PaSeparateMode;

        _uiTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _uiTimer.Tick += UiTimer_Tick;
        _uiTimer.Start();

        _movieCtrl.DisplayActiveChanged += on =>
        {
            void updateDisp()
            {
                UpdateDispButton(on);
                if (!on) { ++_movieLoopSession; _currentMoviePadIndex = -1; }
            }
            if (Dispatcher.CheckAccess()) updateDisp();
            else Dispatcher.Invoke(updateDisp);
        };
        _movieCtrl.FullScreenChanged += on =>
        {
            // Dispatcher.Invoke を UI スレッドから呼ぶと Normal 優先度での再入が起き得るため、
            // UI スレッド上では直接呼び出す
            if (Dispatcher.CheckAccess()) UpdateFullButton(on);
            else Dispatcher.Invoke(() => UpdateFullButton(on));
        };
        _movieCtrl.StatusMessage        += msg => Dispatcher.Invoke(() => SetInfo2(msg));
        _movieCtrl.VideoLoopEndReached  += OnVideoLoopEndReached;

        WireMidi();
        _midi.SetDevice(_settings.SelectedMidiDeviceName);

        // 関連付け起動の検出（.ikp ファイルがコマンドライン引数で渡された場合）
        string[] cmdArgs = Environment.GetCommandLineArgs();
        if (cmdArgs.Length >= 2 && System.IO.File.Exists(cmdArgs[1]) &&
            System.IO.Path.GetExtension(cmdArgs[1]).Equals(".ikp", StringComparison.OrdinalIgnoreCase))
            _assocFilePath = cmdArgs[1];

        _authorTitleActive = _assocFilePath == null; // 関連付け起動では著者名を表示しない

        UpdateBankHighlight();
        UpdateTitle();
        UpdateFullButton(_movieCtrl.IsFullScreen);
        UpdateDispButton(_movieCtrl.DisplayActive);
        UpdateLockButton();
        SetInfo2("準備完了");
        Loaded += async (_, _) =>
        {
            _initComplete = true;
            if (_assocFilePath != null) await LoadProjectAsync(_assocFilePath);
        };
    }

    // ------------------------------------------------------------------
    // UI 構築
    // ------------------------------------------------------------------
    private void BuildPadGrid()
    {
        for (int i = 0; i < BankData.PadCount; i++)
        {
            var pad = new PadButton();
            pad.SetKey(KeyboardMapper.PadLabels[i]);
            pad.Cursor = Cursors.Hand;

            int captured = i;
            pad.CategoryTapped += (_, _) => CyclePadCategory(captured);
            pad.AfterPlaybackChanged += (_, behavior) =>
            {
                PushUndo();
                var padSettings = _playback.GetPadSettings(captured);
                if (padSettings == null) return;
                padSettings.AfterPlayback = behavior;
                bool shouldLoop = behavior == AfterPlaybackBehavior.Loop
                               && padSettings.Category != AudioCategory.SE;
                _engine.GetSource(_playback.ActiveBank, captured).SetLoop(shouldLoop);
                if (padSettings.Category == AudioCategory.Movie)
                    _movieCtrl.UpdateAfterPlayback(behavior);
                MarkDirty();
            };
            pad.TapBehaviorChanged += (_, behavior) =>
            {
                PushUndo();
                var padSettings = _playback.GetPadSettings(captured);
                if (padSettings == null) return;
                padSettings.TapBehavior = behavior;
                MarkDirty();
            };
            pad.SeekRequested += (_, fraction) => SeekPad(captured, fraction);
            pad.PadVolumeChanged += (_, gainInt) =>
            {
                var padSettings = _playback.GetPadSettings(captured);
                if (padSettings == null) return;
                padSettings.PadGain = gainInt / 100.0f;
                _playback.UpdatePadGain(captured, padSettings.PadGain);
                MarkDirty();
            };
            pad.PadVolumeDragStarted += (_, _) => { PushUndo(); Keyboard.ClearFocus(); };
            pad.PreviewMouseDown += (_, _) => Keyboard.ClearFocus();

            // タップ vs ドラッグ判定のため MouseDown でのみ状態を記録（DeadZone/TopArea クリックは除外）
            pad.MouseLeftButtonDown += (_, e) =>
            {
                _padDragStartPoint = e.GetPosition(null);
                _padDragSourceIdx  = captured;
                _padDragActive     = false;
                _padShiftOnDown    = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
                _padCtrlOnDown     = Keyboard.IsKeyDown(Key.LeftCtrl)  || Keyboard.IsKeyDown(Key.RightCtrl);
                // Handled を設定しない → MouseLeftButtonUp まで待つ
            };

            // ドラッグ閾値を超えたら D&D 開始（LOCK中は無効）
            pad.PreviewMouseMove += (_, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed) return;
                if (_padDragSourceIdx != captured || _padDragActive) return;
                if (_isLocked) return;
                var diff = e.GetPosition(null) - _padDragStartPoint;
                if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    _padDragActive = true;
                    DragDrop.DoDragDrop(pad, new DataObject("PadDrop", captured), DragDropEffects.Move);
                    _padDragActive    = false;
                    _padDragSourceIdx = -1;
                }
            };

            // ドラッグなしの場合のみタップとしてパッドをトリガー
            pad.MouseLeftButtonUp += (_, e) =>
            {
                if (_padDragActive || _padDragSourceIdx != captured) return;
                _padDragSourceIdx = -1;
                if (_bankLoading) { e.Handled = true; return; } // ローディング中は操作ブロック
                TriggerPadWithMovie(captured, _padShiftOnDown, _padCtrlOnDown);
                e.Handled = true;
            };

            pad.MouseRightButtonUp += (_, e) => { if (!_bankLoading) PadRightClick(captured, e); };
            pad.AllowDrop = true;
            pad.DragOver  += (_, e) =>
            {
                if (e.Data.GetDataPresent("PadDrop"))
                    e.Effects = DragDropEffects.Move;
                else if (e.Data.GetDataPresent(DataFormats.FileDrop) && pad.CanEdit)
                    e.Effects = DragDropEffects.Copy;
                else
                    e.Effects = DragDropEffects.None;
                e.Handled = true;
            };
            pad.Drop += (_, e) =>
            {
                if (e.Data.GetData("PadDrop") is int srcIdx)
                {
                    if (srcIdx != captured)
                        SwapPads(srcIdx, captured);
                    e.Handled = true;
                }
                else if (pad.CanEdit)
                {
                    HandlePadDrop(captured, e);
                }
            };

            _padButtons[i] = pad;
            PadGrid.Children.Add(pad);
        }
    }

    private static readonly string[] BankNames = ["A", "B", "C", "D", "E", "F", "G", "H"];

    // 表示順：A/E, B/F, C/G, D/H（ショートカットは元の位置を維持）
    private static readonly int[] BankVisualOrder = [0, 4, 1, 5, 2, 6, 3, 7];

    private void BuildBankGrid()
    {
        BankGrid.Children.Clear();
        for (int i = 0; i < ProjectData.BankCount; i++)
        {
            int captured = i;

            string label = _project.Banks[i].BankLabel ?? $"Bank {BankNames[i]}";
            var content = new Grid { Margin = new Thickness(4) };
            content.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 12, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Colors.White)
            });
            var badge = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1C)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(4, 1, 4, 1),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Child = new TextBlock
                {
                    Text = KeyboardMapper.BankLabels[i],
                    FontSize = 16, FontWeight = FontWeights.Bold,
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA))
                }
            };
            content.Children.Add(badge);

            var btn = new Button
            {
                Content = content,
                Margin = new Thickness(2),
                MinHeight = 44,
                Cursor = Cursors.Hand,
                Background = BrushBankNormal,
                BorderBrush = BrushBankBorderN,
                Tag = new object?[] { content.Children[0] as TextBlock, badge },
                Focusable = false
            };
            btn.Style = CreateBankButtonStyle();
            btn.Click += (_, _) => RequestBankSwitch(captured);
            btn.MouseRightButtonUp += (_, e) => BankRightClick(captured, e);

            // バンク D&D
            btn.PreviewMouseLeftButtonDown += (_, e) =>
            {
                _bankDragStartPoint = e.GetPosition(null);
                _bankDragSourceIdx  = captured;
                _bankDragActive     = false;
            };
            btn.PreviewMouseMove += (_, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed) return;
                if (_bankDragSourceIdx != captured || _bankDragActive) return;
                if (_isLocked) return;
                var diff = e.GetPosition(null) - _bankDragStartPoint;
                if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                    Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
                {
                    _bankDragActive = true;
                    DragDrop.DoDragDrop(btn, new DataObject("BankDrop", captured), DragDropEffects.Move);
                    _bankDragActive    = false;
                    _bankDragSourceIdx = -1;
                }
            };
            btn.AllowDrop = true;
            btn.DragOver += (_, e) =>
            {
                e.Effects = e.Data.GetDataPresent("BankDrop") ? DragDropEffects.Move : DragDropEffects.None;
                e.Handled = true;
            };
            btn.Drop += (_, e) =>
            {
                if (e.Data.GetData("BankDrop") is int srcIdx && srcIdx != captured)
                {
                    SwapBanks(srcIdx, captured);
                    e.Handled = true;
                }
            };

            _bankButtons[i] = btn;
        }

        // A/E, B/F, C/G, D/H の表示順でグリッドに追加
        foreach (int idx in BankVisualOrder)
            BankGrid.Children.Add(_bankButtons[idx]);
    }

    private Style CreateBankButtonStyle()
    {
        var style = new Style(typeof(Button));
        var tpl = new ControlTemplate(typeof(Button));
        var bd = new FrameworkElementFactory(typeof(Border));
        bd.Name = "Bd";
        bd.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background")
            { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        bd.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush")
            { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        bd.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness")
            { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Stretch);
        bd.AppendChild(cp);
        tpl.VisualTree = bd;
        style.Setters.Add(new Setter(Control.BackgroundProperty, BrushBankNormal));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, BrushBankBorderN));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1.5)));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Colors.White)));
        style.Setters.Add(new Setter(Control.TemplateProperty, tpl));
        return style;
    }

    private static readonly SolidColorBrush[] MixerLabelColors =
    [
        new(Color.FromRgb(0x9A, 0x4A, 0xCC)),  // MOVIE
        new(Color.FromRgb(0x3A, 0x7F, 0xC1)),  // BGM
        new(Color.FromRgb(0x3A, 0xA0, 0x4A)),  // SE
        new(Color.FromRgb(0xAA, 0xAA, 0xAA)),  // MASTER（現状維持）
    ];

    private void BuildMixerGrid()
    {
        string[] labels = ["MOVIE", "BGM", "SE", "MASTER"];
        for (int i = 0; i < 4; i++)
        {
            var fader = new VFaderControl { Label = labels[i], LabelBrush = MixerLabelColors[i] };
            fader.Value = 1.0;
            int captured = i;
            fader.VolumeChanged += (_, v) => OnFaderChanged(captured, v);
            fader.FaderDragStarted += (_, _) => PushUndo();
            fader.MemoryRecall += (_, args) => OnMemoryRecall(captured, args.slot, args.quick);
            fader.MuteChanged += (_, muted) => OnFaderMute(captured, muted);
            fader.MemoryRegisterRequested += (s, args) =>
            {
                if (_pendingMemOverwrite.HasValue || _pendingBankClearIndex >= 0) return;
                var f = (VFaderControl)s!;
                _pendingMemOverwrite = (f, args.slot, args.gain);
                SetInfo2Warning($"{f.Label} M{args.slot + 1} 上書き登録しますか？\n[Y] 確定  /  [N] キャンセル");
            };

            _faders[i] = fader;
            MixerGrid.Children.Add(fader);
        }
    }

    private void WireEvents()
    {
        // MovieWindow フォーカス時でもショートカットを有効化（スレッドレベルでキー割り込み）
        ComponentDispatcher.ThreadPreprocessMessage += OnGlobalKey;


        _playback.BankLoadStarted  += () => SetBankLoading(true);
        _playback.BankLoadCompleted += () => SetBankLoading(false);
    }

    // ------------------------------------------------------------------
    // UI タイマー（30fps でパッド状態を更新）
    // ------------------------------------------------------------------
    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        // 読み込み中アニメーション（ドット数を 1→2→3→1 でサイクル）
        if (_bankLoading && InfoLine2.Text.StartsWith("読み込み中"))
        {
            int dots = (int)(Environment.TickCount64 / 400) % 3 + 1;
            InfoLine2.Text = "読み込み中" + new string('.', dots);
        }

        // 静止画フェード進行を計算（フェード完了したらリセット）
        int fadingIdx = _imageFadingPadIndex;
        float imageFadeGainForPad = -1f;
        if (fadingIdx >= 0)
        {
            float elapsed = (Environment.TickCount64 - _imageFadeStartTick) / 1000f;
            float gain = 1f - Math.Clamp(elapsed / Math.Max(_imageFadeDuration, 0.01f), 0f, 1f);
            if (gain <= 0f)
            {
                _imageFadingPadIndex = -1;
                fadingIdx = -1;
            }
            else
            {
                imageFadeGainForPad = gain;
            }
        }

        if (!_movieCtrl.IsBuffering && _videoBufferingPadIndex >= 0)
            _videoBufferingPadIndex = -1;

        for (int i = 0; i < BankData.PadCount; i++)
        {
            var state    = _playback.GetPadState(i);
            var pos      = _playback.GetPadPosition(i);
            var pad      = _playback.GetPadSettings(i);
            var fadeGain = _playback.GetPadFadeGain(i);
            var totalSec = _playback.GetPadTotalTime(i);
            // 動画パッドは整数秒カウンタで位置表示（VLC更新ラグ回避・長尺動画対応）
            if (i == _currentMoviePadIndex && _movieSecTick >= 0
                && pad?.Category == AudioCategory.Movie && !IsImageFile(pad.FilePath) && totalSec > 0)
            {
                if (!_movieSecPaused)
                {
                    long elapsed = (Environment.TickCount64 - _movieSecTick) / 1000;
                    if (elapsed > 0) { _movieDisplayedSec += (int)elapsed; _movieSecTick += elapsed * 1000; }
                }
                pos = (float)Math.Clamp((double)_movieDisplayedSec / totalSec, 0.0, 1.0);
            }
            bool imageDisplaying = _imageDisplayingPadIndex == i;
            float iGain = (fadingIdx == i) ? imageFadeGainForPad : -1f;
            bool isMissing = _missingPads[_playback.ActiveBank, i];
            bool isPreparingVideo = _videoBufferingPadIndex == i;
            // 動画ループの300ms黒画面中はオーディオ状態がIdleになるが、黄色枠を維持する
            if (state == PadPlayState.Idle && _videoLoopingPadIndex == i)
                state = PadPlayState.Playing;
            _padButtons[i].UpdateState(state, pos, pad, fadeGain, totalSec, imageDisplaying, iGain, isMissing, isPreparingVideo);
        }
        UpdateActionButtons();
        SyncMovieAudio();
        CheckBlackScreen();
    }

    // VLC（映像）と NAudio（音声）の再生位置を比較し、ズレが続くときだけ音声をシークして補正する。
    // VLC の Time は ~500ms ごとにしか更新されないため、値が変わったときだけ比較する。
    private void SyncMovieAudio()
    {
        int padIdx = _currentMoviePadIndex;
        if (padIdx < 0 || _movieCtrl.IsBuffering) return;

        var pad = _playback.GetPadSettings(padIdx);
        if (pad == null || IsImageFile(pad.FilePath)) return;

        if (_playback.GetPadState(padIdx) != PadPlayState.Playing) return;

        long rawVlcMs = _movieCtrl.GetCurrentVideoTimeMs();
        if (rawVlcMs < 0) { _vlcLastKnownTimeMs = -1; _videoSyncMismatchFrames = 0; return; }

        // VLC の Time が更新されていなければスキップ（~500ms ごとに1回だけ比較）
        if (rawVlcMs == _vlcLastKnownTimeMs) return;
        _vlcLastKnownTimeMs = rawVlcMs;

        // 壁掛け時計を VLC で補正（2秒以上ずれた場合のみ）
        if (_movieSecTick >= 0 && !_movieSecPaused)
        {
            int vlcSec = (int)(rawVlcMs / 1000);
            if (Math.Abs(vlcSec - _movieDisplayedSec) >= 2)
            {
                Logger.Log($"[WallClock] corrected: {_movieDisplayedSec}s→{vlcSec}s");
                _movieDisplayedSec = vlcSec;
                _movieSecTick      = Environment.TickCount64;
            }
        }

        var   src      = _engine.GetSource(_playback.ActiveBank, padIdx);
        float totalSec = src.FileTotalSec;
        if (totalSec <= 0f) return;

        long naudioMs = (long)(src.PlaybackPosition * totalSec * 1000f);
        long diffMs   = rawVlcMs - naudioMs;

        if (Math.Abs(diffMs) > 300 && Math.Abs(diffMs) <= 3000)
        {
            if (++_videoSyncMismatchFrames >= 2)
            {
                float fraction = Math.Clamp((float)(rawVlcMs / 1000.0) / totalSec, 0f, 1f);
                src.SeekToFraction(fraction);
                _videoSyncMismatchFrames = 0;
                Logger.Log($"[Sync] audio corrected: vlc={rawVlcMs}ms audio={naudioMs}ms diff={diffMs:+#;-#;0}ms");
            }
        }
        else
        {
            _videoSyncMismatchFrames = 0;
        }
    }

    // 黒画面状態を監視して警告を表示する
    private void CheckBlackScreen()
    {
        bool isBlack = _movieCtrl.IsBlackScreen;
        if (isBlack == _blackScreenWarning) return;

        _blackScreenWarning = isBlack;
        if (isBlack)
        {
            Logger.Log("[Display] Black screen detected");
            // 確認ダイアログ表示中でなければ警告を表示
            if (!_infoClearPending && BankConfirmPanel.Visibility != Visibility.Visible)
            {
                InfoLine2.Text             = BlackScreenMsg;
                InfoLine2.Foreground       = BrushInfoWarnText;
                InfoLine2Border.Background = BrushInfoWarnBg;
            }
        }
        else
        {
            Logger.Log("[Display] Black screen cleared");
            if (InfoLine2.Text == BlackScreenMsg)
            {
                InfoLine2.Text             = "";
                InfoLine2.Foreground       = BrushInfoNormal;
                InfoLine2Border.Background = System.Windows.Media.Brushes.Transparent;
            }
        }
    }

    // ------------------------------------------------------------------
    // 動画壁掛け時計（整数秒カウンタ・1秒ごとにインクリメント・VLC補正対応）
    // ------------------------------------------------------------------
    private void StartMovieWallClock(double startSec)
    {
        _movieDisplayedSec = (int)startSec;
        _movieSecTick      = Environment.TickCount64;
        _movieSecPaused    = false;
    }

    private void PauseMovieWallClock()
    {
        if (_movieSecPaused) return;
        _movieSecPaused = true;
    }

    private void ResumeMovieWallClock()
    {
        if (!_movieSecPaused) return;
        _movieSecPaused = false;
        _movieSecTick   = Environment.TickCount64; // 再開時点から1秒計測を再スタート
    }

    private void UpdateActionButtons()
    {
        bool movieBgmPlay = _playback.HasMovieBgmPlaying();

        _pauseBd ??= PauseAllButton.Template.FindName("PauseBd", PauseAllButton) as System.Windows.Controls.Border;
        if (_pauseBd != null)
        {
            if (movieBgmPlay)
            {
                // 黄色になるのは PAUSE ボタン自身でまとめて一時停止したときのみ
                _pauseBd.BorderBrush = _isPauseAllActive
                    ? new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00))
                    : new SolidColorBrush(Color.FromRgb(0x44, 0xCC, 0x44));
            }
            else if (_isPauseAllActive)
            {
                // MOV/BGM が全部止まったらフラグをリセット（バンク切り替え等）
                _isPauseAllActive = false;
                _pauseBd.BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0xCC, 0x44));
            }
        }
    }

    // ------------------------------------------------------------------
    // キーボードイベント
    // ------------------------------------------------------------------
    private void MainWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_authorTitleActive) { _authorTitleActive = false; UpdateTitle(); }
        // 通常インフォメーションを次のアクション（クリック）で消去
        if (_infoClearPending) { _infoClearPending = false; SetInfo2(""); }
        // メインウィンドウ内の任意のクリックでミキサースライダーのキーボードフォーカスをリセット
        Keyboard.ClearFocus();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        // OnGlobalKey が handled=true にした場合、WPF は e.Handled=true で届ける
        if (e.IsRepeat || e.Handled) return;
        if (HandleKeyDown(e.Key)) e.Handled = true;
    }

    private void Window_KeyUp(object sender, KeyEventArgs e) { }

    // Win32 メッセージレベルのグローバルキーフック
    // WPF の KeyDown（フォーカス依存）よりも先に処理されるため、ClearFocus 後でも確実に動作する
    private void OnGlobalKey(ref MSG msg, ref bool handled)
    {
        const int WM_KEYDOWN = 0x0100;
        if (msg.message != WM_KEYDOWN || handled) return;
        if (_authorTitleActive) { _authorTitleActive = false; UpdateTitle(); }

        // このアプリのどのウィンドウもアクティブでない場合は他アプリの入力を妨げない
        if (!IsActive && !_movieCtrl.DisplayActive) return;

        // ダイアログが開いている場合はショートカット無効（ダイアログの操作を妨げない）
        if (OwnedWindows.OfType<Window>().Any(w => w.IsVisible)) return;

        int lParam = msg.lParam.ToInt32();
        if ((lParam & 0x40000000) != 0) return;  // リピートキーは無視

        var key = KeyInterop.KeyFromVirtualKey((int)msg.wParam);

        // MovieWindow がアクティブで自ウィンドウが非アクティブの場合のみ対象キーを絞る
        // （他アプリでの自由なタイピングを妨げないため）
        if (!IsActive)
        {
            bool isOurKey = key == Key.Escape || key == Key.D9 || key == Key.D0 || key == Key.OemMinus ||
                            key == Key.Space || key == Key.Return ||
                            key == Key.Y || key == Key.N || key == Key.Z ||
                            _keyMapper.GetPadIndex(key).HasValue ||
                            _keyMapper.GetBankIndex(key).HasValue;
            if (!isOurKey) return;
        }

        if (HandleKeyDown(key)) handled = true;
    }

    // true を返した場合は e.Handled = true にする
    private bool HandleKeyDown(Key key)
    {
        // TextBox フォーカス中（文字入力モード）はすべてのショートカットを無効化
        if (Keyboard.FocusedElement is System.Windows.Controls.TextBox) return false;

        // 確認待ちでなければ、通常インフォメーションをキー操作で消去
        bool anyPending = _pendingMemOverwrite.HasValue ||
                          _pendingIkpPath != null || _pendingOpenConfirm || _pendingNewConfirm || _pendingBankClearIndex >= 0;
        if (_infoClearPending && !anyPending) { _infoClearPending = false; SetInfo2(""); }

        // メモリ上書き確認中
        if (_pendingMemOverwrite.HasValue)
        {
            if (key == Key.Y) { ConfirmMemOverwrite(); return true; }
            if (key == Key.N) { CancelMemOverwrite();  return true; }
        }

        // ikpファイル読み込み確認中
        if (_pendingIkpPath != null)
        {
            if (key == Key.Y) { ConfirmIkpLoad(); return true; }
            if (key == Key.N) { CancelIkpLoad();  return true; }
        }

        // ファイルメニュー「開く」確認中
        if (_pendingOpenConfirm)
        {
            if (key == Key.Y) { ConfirmOpenLoad(); return true; }
            if (key == Key.N) { CancelOpenLoad();  return true; }
        }

        // 新規プロジェクト確認中
        if (_pendingNewConfirm)
        {
            if (key == Key.Y) { ConfirmNew(); return true; }
            if (key == Key.N) { CancelNew();  return true; }
        }

        // バンク削除確認中
        if (_pendingBankClearIndex >= 0)
        {
            if (key == Key.Y) { ExecuteBankClear(); return true; }
            if (key == Key.N) { _pendingBankClearIndex = -1; SetInfo2(""); return true; }
        }

        // ファイルメニューショートカット（Ctrl+N/O/S/Z/Y）
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (key == Key.N) { Menu_New(null!, null!);      return true; }
            if (key == Key.O) { Menu_Open(null!, null!);     return true; }
            if (key == Key.S) { Menu_Save(null!, null!);     return true; }
            if (key == Key.E) { Menu_Settings(null!, null!); return true; }
            if (key == Key.Z) { ExecuteUndo(); return true; }
            if (key == Key.Y) { ExecuteRedo(); return true; }
        }

        // パニック
        if (key == Key.Escape)
        {
            ExecutePanic();
            return true;
        }

        // LOCK ボタン（[9]キー）
        if (key == Key.D9)
        {
            SetLocked(!_isLocked);
            return true;
        }

        // FULL ボタン（[0]キー）
        if (key == Key.D0)
        {
            if (_isLocked) return true;
            _fullBd ??= FullButton.Template.FindName("FullBd", FullButton) as System.Windows.Controls.Border;
            if (_fullBd != null) { _fullBd.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00)); _fullBd.BorderThickness = new Thickness(2.5); }
            _movieCtrl.ToggleFullScreen();
            UpdateFullButton(_movieCtrl.IsFullScreen);
            return true;
        }

        // DISPLAY ボタン（[-]キー）
        if (key == Key.OemMinus)
        {
            if (_isLocked) return true;
            _dispBd ??= DispButton.Template.FindName("DispBd", DispButton) as System.Windows.Controls.Border;
            if (_dispBd != null) { _dispBd.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00)); _dispBd.BorderThickness = new Thickness(2.5); }
            bool dispWasActive = _movieCtrl.DisplayActive;
            _movieCtrl.ToggleDisplay();
            if (_movieCtrl.DisplayActive && !dispWasActive)
                ResumeMovieIfPlaying();
            return true;
        }

        // ALL FADE（Spaceキー）
        if (key == Key.Space)
        {
            if (Keyboard.FocusedElement is System.Windows.Controls.TextBox) return false;
            ExecuteAllFade();
            return true;
        }

        // PAUSE（ENTERキー）
        if (key == Key.Return)
        {
            if (Keyboard.FocusedElement is System.Windows.Controls.TextBox) return false;
            ExecutePauseAll();
            return true;
        }

        // パッドキー
        var padIdx = _keyMapper.GetPadIndex(key);
        if (padIdx.HasValue)
        {
            if (Keyboard.FocusedElement is System.Windows.Controls.TextBox) return false;
            // 動画バッファリング中はMOVIEカテゴリパッドの操作を無視（静止画は除外）
            var padSets0 = _playback.GetPadSettings(padIdx.Value);
            var padCat = padSets0?.Category;
            if (padCat == AudioCategory.Movie && !IsImageFile(padSets0?.FilePath) && _movieCtrl.IsBuffering)
            {
                Logger.Log($"[MW] Pad{padIdx.Value + 1} blocked: buffering");
                SetInfo2("バッファリング中...");
                return true;
            }

            TriggerPadWithMovie(padIdx.Value);
            return true;
        }

        // バンクキー
        var bankIdx = _keyMapper.GetBankIndex(key);
        if (bankIdx.HasValue)
        {
            RequestBankSwitch(bankIdx.Value);
            return true;
        }

        return false;
    }

    // ------------------------------------------------------------------
    // パッド右クリック
    // ------------------------------------------------------------------
    private void PadRightClick(int padIndex, MouseButtonEventArgs e)
    {
        Keyboard.ClearFocus();
        var state = _playback.GetPadState(padIndex);
        bool imageActive = _imageDisplayingPadIndex == padIndex;

        // LOCK中はアイドル時のみ右クリックメニューを無効化（再生中は許可）
        if (_isLocked && state == PadPlayState.Idle && !imageActive)
        {
            e.Handled = true;
            return;
        }
        var pad0 = _playback.GetPadSettings(padIndex);
        bool isImagePad = pad0 != null && IsImageFile(pad0.FilePath);
        var cm = new ContextMenu();

        if (state != PadPlayState.Idle || imageActive)
        {
            bool duringPauseAll = _isPauseAllActive;
            // PAUSE中はカットアウト以外をグレーアウト
            var fadeOut = new MenuItem { Header = "フェードアウト", IsEnabled = !duringPauseAll };
            fadeOut.Click += (_, _) => TriggerPadWithMovie(padIndex, fadeOut: true);
            var cutOut = new MenuItem { Header = "カットアウト" };
            cutOut.Click += (_, _) =>
            {
                TriggerPadWithMovie(padIndex, stopImmediate: true);
                // PAUSE中カットアウト後、一時停止対象パッドがなくなった場合はPAUSE解除
                if (duringPauseAll)
                    Dispatcher.InvokeAsync(CheckReleasePauseAll, System.Windows.Threading.DispatcherPriority.Background);
            };
            bool pauseEnabled = !isImagePad && pad0?.Category != AudioCategory.SE && !duringPauseAll;
            var pauseResume = new MenuItem { Header = "一時停止／再開", IsEnabled = pauseEnabled };
            pauseResume.Click += (_, _) => PausePadWithMovie(padIndex);
            cm.Items.Add(fadeOut);
            cm.Items.Add(cutOut);
            cm.Items.Add(pauseResume);
        }
        else
        {
            var pad = _playback.GetPadSettings(padIndex);

            var detail = new MenuItem { Header = "詳細設定..." };
            detail.Click += (_, _) => OpenPadDetail(padIndex);
            cm.Items.Add(detail);

            if (pad != null)
            {
                cm.Items.Add(new Separator());

                var copyItem = new MenuItem { Header = "コピー", IsEnabled = !string.IsNullOrEmpty(pad.FilePath) };
                copyItem.Click += (_, _) => { _clipboardPad = pad.Clone(); };
                cm.Items.Add(copyItem);

                var pasteItem = new MenuItem { Header = "ペースト", IsEnabled = _clipboardPad != null };
                pasteItem.Click += (_, _) => PastePadSettings(padIndex);
                cm.Items.Add(pasteItem);

                var clear = new MenuItem { Header = "削除" };
                clear.Click += (_, _) => ClearPad(padIndex);
                cm.Items.Add(clear);
            }
        }

        cm.Closed += (_, _) => Dispatcher.BeginInvoke(Keyboard.ClearFocus);
        cm.IsOpen = true;
        e.Handled = true;
    }

    // ------------------------------------------------------------------
    // 動画ループ終端ハンドラ（MovieController.VideoLoopEndReachedから呼ばれる）
    // 音声を即座に停止 → 300ms 黒画面・無音 → 音声+映像を同時再起動
    // ------------------------------------------------------------------
    private void OnVideoLoopEndReached()
    {
        int padIdx = _currentMoviePadIndex;
        if (padIdx < 0) return;

        var pad = _playback.GetPadSettings(padIdx);
        if (pad == null || string.IsNullOrEmpty(pad.FilePath)) return;

        // 音声を即座に停止（NAudioループを中断して黒画面期間中は無音にする）
        _engine.GetSource(_playback.ActiveBank, padIdx).StopImmediate();
        _videoLoopingPadIndex = padIdx; // 300ms間、黄色枠を維持する

        string capturedPath  = pad.FilePath;
        float  capturedEnd   = pad.EndPositionSec;
        float  capturedLoop  = pad.LoopStartSec;
        float  capturedStart = pad.StartPositionSec;
        // LoopStartSec が設定されていればそこから、なければ StartPositionSec から再生
        float  restartSec    = capturedLoop >= 0f ? capturedLoop : capturedStart;
        int    capturedPad   = padIdx;
        int    capturedSess  = ++_movieLoopSession;

        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            _videoLoopingPadIndex = -1; // 300ms経過、黄色枠維持を解除

            // NAudio は常に再起動（DISP 切り替えタイミングで止まりっぱなしになるのを防ぐ）
            _engine.GetSource(_playback.ActiveBank, capturedPad)
                .Trigger(restartSec, capturedEnd, 0f, shouldLoop: true, capturedLoop);

            if (_movieLoopSession != capturedSess)
            {
                // DISP 切り替え等でセッションが変わった → DISP ON 中なら映像も再起動
                if (_movieCtrl.DisplayActive)
                {
                    ++_movieLoopSession;
                    _currentMoviePadIndex = capturedPad;
                    StartMovieWallClock(restartSec);
                    _movieCtrl.PlayVideo(capturedPath, restartSec, capturedEnd, AfterPlaybackBehavior.Loop);
                    Logger.Log($"[MW] Video loop: session changed, full restart (pad={capturedPad})");
                }
                else
                {
                    Logger.Log($"[MW] Video loop: session changed, audio-only restart (pad={capturedPad})");
                }
                return;
            }

            if (!_movieCtrl.DisplayActive)
            {
                Logger.Log($"[MW] Video loop: display off, audio-only restart (pad={capturedPad})");
                return;
            }

            StartMovieWallClock(restartSec); // ループ再起動で壁掛け時計もリセット

            // 映像再開（LibVLC）
            _movieCtrl.PlayVideo(capturedPath, restartSec, capturedEnd, AfterPlaybackBehavior.Loop);

            Logger.Log($"[MW] Video loop restart: audio+video (pad={capturedPad} restartSec={restartSec:F2})");
        };
        t.Start();
        Logger.Log($"[MW] Video loop end: 300ms black+silence timer started (pad={padIdx})");
    }

    // ------------------------------------------------------------------
    // パッドトリガー（音声 + 動画連動）
    // ------------------------------------------------------------------
    private void TriggerPadWithMovie(int padIndex, bool fadeOut = false, bool stopImmediate = false)
    {
        Logger.Log($"[MW] TriggerPad idx={padIndex} fadeOut={fadeOut} stopImmediate={stopImmediate}");
        var pad = _playback.GetPadSettings(padIndex);
        bool isMoviePad = pad?.Category == AudioCategory.Movie;
        bool isImagePad = isMoviePad && IsImageFile(pad?.FilePath);

        if (isImagePad)
        {
            // 静止画パッド: 音声なし、映像のみ制御
            if (stopImmediate)
            {
                _movieCtrl.StopVideo();
                _imageDisplayingPadIndex = -1;
            }
            else if (fadeOut)
            {
                _movieCtrl.FadeVideo(_settings.LongFadeDuration);
                _imageFadingPadIndex = _imageDisplayingPadIndex;
                _imageFadeStartTick  = Environment.TickCount64;
                _imageFadeDuration   = _settings.LongFadeDuration;
                _imageDisplayingPadIndex = -1;
            }
            else if (_imageDisplayingPadIndex == padIndex)
            {
                // 同じ画像が表示中 → フェードアウト
                _movieCtrl.FadeVideo(_settings.LongFadeDuration);
                _imageFadingPadIndex = padIndex;
                _imageFadeStartTick  = Environment.TickCount64;
                _imageFadeDuration   = _settings.LongFadeDuration;
                _imageDisplayingPadIndex = -1;
            }
            else
            {
                // 新規表示の前に同カテゴリの音声再生を停止（音声→静止画の順で操作したとき）
                _playback.StopMovieAudioPads();
                ++_movieLoopSession; _currentMoviePadIndex = -1; // 動画ループ再起動をキャンセル
                _movieCtrl.PlayVideo(pad!.FilePath!, pad.StartPositionSec, pad.EndPositionSec, pad.AfterPlayback);
                _imageDisplayingPadIndex = padIndex;
            }
            return;
        }

        // 音声パッド（動画含む）: 再生前の状態を記録（同パッド再押し判定）
        var stateBefore = _playback.GetPadState(padIndex);
        bool wasActive  = stateBefore != PadPlayState.Idle;

        // PAUSEボタンで一時停止中のパッドが押された場合 → そのパッドのみ再開し、PAUSEを解除
        if (_isPauseAllActive && !fadeOut && !stopImmediate && stateBefore == PadPlayState.Paused)
        {
            _isPauseAllActive = false;
            _playback.ForcePauseResumePad(padIndex);
            if (isMoviePad && !isImagePad) _movieCtrl.ResumeVideo();
            return;
        }

        // パッドの現在時間表示値を再生開始位置として使用（詳細設定の StartPositionSec とは独立）
        float startSecOverride = _padButtons[padIndex].CurrentStartSec;
        _playback.TriggerPad(padIndex, fadeOut, stopImmediate, startSecOverride);

        if (!isMoviePad) return;

        if (stopImmediate)
        {
            ++_movieLoopSession; _currentMoviePadIndex = -1;
            _movieCtrl.StopVideo();
            _imageDisplayingPadIndex = -1;
        }
        else if (fadeOut)
        {
            ++_movieLoopSession;
            _movieCtrl.FadeVideo(_settings.LongFadeDuration);
            _imageDisplayingPadIndex = -1;
        }
        else if (wasActive && pad?.TapBehavior == TapBehavior.PauseResume)
        {
            // 一時停止／再開: 音声は TriggerPad 内で処理済み。映像をそれに同期
            var stateAfter = _playback.GetPadState(padIndex);
            if (stateAfter == PadPlayState.Paused) { _movieCtrl.PauseVideo(); PauseMovieWallClock(); }
            else if (stateAfter == PadPlayState.Playing) { _movieCtrl.ResumeVideo(); ResumeMovieWallClock(); }
        }
        else if (wasActive)
        {
            // 同パッド再押し → 音声停止済み → 映像も同様に停止（TapBehavior準拠）
            bool cutOut = pad?.TapBehavior == TapBehavior.CutOut;
            ++_movieLoopSession; _currentMoviePadIndex = -1;
            if (cutOut)
                _movieCtrl.StopVideo();
            else
                _movieCtrl.FadeVideo(_settings.LongFadeDuration);
            _imageDisplayingPadIndex = -1;
        }
        else if (!string.IsNullOrEmpty(pad!.FilePath))
        {
            ++_movieLoopSession; _currentMoviePadIndex = padIndex; // 新規動画再生：ループ追跡開始
            StartMovieWallClock(startSecOverride); // 壁掛け時計スタート
            _movieCtrl.PlayVideo(pad.FilePath, startSecOverride, pad.EndPositionSec, pad.AfterPlayback);
            _imageDisplayingPadIndex = -1;
            // 動画ファイル再生時はバッファリング完了まで準備中を表示
            _videoBufferingPadIndex = VideoImageExtensions.Contains(System.IO.Path.GetExtension(pad.FilePath))
                                      && !IsImageFile(pad.FilePath) ? padIndex : -1;
        }
    }

    private void PastePadSettings(int padIndex)
    {
        if (_clipboardPad == null) return;
        if (_project == null) return;
        PushUndo();
        _missingPads[_playback.ActiveBank, padIndex] = false;
        var dest = _project.Banks[_playback.ActiveBank].Pads[padIndex];
        dest.FilePath           = _clipboardPad.FilePath;
        dest.Category           = _clipboardPad.Category;
        dest.PadGain            = _clipboardPad.PadGain;
        dest.CustomLabel        = _clipboardPad.CustomLabel;
        dest.PadBackgroundColor = _clipboardPad.PadBackgroundColor;
        dest.StartPositionSec   = _clipboardPad.StartPositionSec;
        dest.EndPositionSec     = _clipboardPad.EndPositionSec;
        dest.AfterPlayback      = _clipboardPad.AfterPlayback;
        dest.TapBehavior        = _clipboardPad.TapBehavior;
        dest.LoopStartSec       = _clipboardPad.LoopStartSec;
        _engine.SetPadCategory(_playback.ActiveBank, padIndex, dest.Category);
        string padMsg = $"Pad {padIndex + 1} に設定をペーストしました。";
        _playback.LoadBank(_playback.ActiveBank, () => SetInfo2(padMsg));
        MarkDirty();
    }

    private void PausePadWithMovie(int padIndex)
    {
        var state = _playback.GetPadState(padIndex);
        if (state == PadPlayState.Idle) return;
        _playback.ForcePauseResumePad(padIndex);
        var pad = _playback.GetPadSettings(padIndex);
        if (pad?.Category == AudioCategory.Movie)
        {
            var newState = _playback.GetPadState(padIndex);
            if (newState == PadPlayState.Paused)  { _movieCtrl.PauseVideo(); PauseMovieWallClock(); }
            else if (newState == PadPlayState.Playing) { _movieCtrl.ResumeVideo(); ResumeMovieWallClock(); }
        }
    }

    private void SeekPad(int padIndex, float fraction)
    {
        _engine.GetSource(_playback.ActiveBank, padIndex).SeekToFraction(fraction);
        var pad = _playback.GetPadSettings(padIndex);
        if (pad?.Category == AudioCategory.Movie)
        {
            _movieCtrl.SeekVideo(fraction);
            bool wasPaused = _movieSecPaused;
            StartMovieWallClock(fraction * (double)_playback.GetPadTotalTime(padIndex));
            if (wasPaused) PauseMovieWallClock(); // シーク後も一時停止状態を維持
        }
    }

    private void CyclePadCategory(int padIndex)
    {
        PushUndo();
        var pad = _project.Banks[_playback.ActiveBank].Pads[padIndex];
        pad.Category = pad.Category switch
        {
            AudioCategory.Movie => AudioCategory.BGM,
            AudioCategory.BGM   => AudioCategory.SE,
            _                   => AudioCategory.Movie
        };

        // 新カテゴリで無効なAfterPlaybackをStopにリセット
        if (pad.AfterPlayback == AfterPlaybackBehavior.FreezeLastFrame && pad.Category != AudioCategory.Movie)
            pad.AfterPlayback = AfterPlaybackBehavior.Stop;
        if (pad.AfterPlayback == AfterPlaybackBehavior.Loop && pad.Category == AudioCategory.SE)
            pad.AfterPlayback = AfterPlaybackBehavior.Stop;

        // SEカテゴリ変更時にPauseResumeは使用不可→FadeOutに強制変換
        if (pad.TapBehavior == TapBehavior.PauseResume && pad.Category == AudioCategory.SE)
            pad.TapBehavior = TapBehavior.FadeOut;

        bool shouldLoop = pad.AfterPlayback == AfterPlaybackBehavior.Loop;
        _engine.GetSource(_playback.ActiveBank, padIndex).SetLoop(shouldLoop);
        _engine.SetPadCategory(_playback.ActiveBank, padIndex, pad.Category);
        MarkDirty();
    }

    private void ClearPad(int padIndex)
    {
        if (_project == null) return;
        PushUndo();
        var pad = _project.Banks[_playback.ActiveBank].Pads[padIndex];
        _missingPads[_playback.ActiveBank, padIndex] = false;
        _engine.GetSource(_playback.ActiveBank, padIndex).Unload();
        pad.FilePath           = null;
        pad.CustomLabel        = null;
        pad.PadGain            = 1.0f;
        pad.PadBackgroundColor = null;
        pad.StartPositionSec   = 0f;
        pad.EndPositionSec     = -1f;
        pad.AfterPlayback      = AfterPlaybackBehavior.Stop;
        pad.TapBehavior        = TapBehavior.FadeOut;
        pad.LoopStartSec       = -1f;
        pad.Category           = padIndex < 8 ? AudioCategory.BGM : AudioCategory.SE;
        _engine.SetPadCategory(_playback.ActiveBank, padIndex, pad.Category);
        MarkDirty();
    }

    private void OpenPadDetail(int padIndex)
    {
        var pad = _playback.GetPadSettings(padIndex);
        if (pad == null) return;

        float totalSec = _playback.GetPadTotalTime(padIndex);
        var dlg = new PadDetailDialog(pad, totalSec) { Owner = this };
        bool confirmed = dlg.ShowDialog() == true;
        Keyboard.ClearFocus(); // ダイアログを閉じた後にパッドへのフォーカス移動を防ぐ
        if (!confirmed) return;
        PushUndo();

        bool fileChanged = dlg.ResultFilePath != pad.FilePath;

        pad.Category             = dlg.ResultCategory;
        pad.CustomLabel          = dlg.ResultLabel;
        pad.FilePath             = dlg.ResultFilePath;
        pad.PadGain              = dlg.ResultPadGain;
        pad.StartPositionSec     = dlg.ResultStartSec;
        pad.EndPositionSec       = dlg.ResultEndSec;
        pad.AfterPlayback        = dlg.ResultAfterPlayback;
        pad.PadBackgroundColor   = dlg.ResultPadBackgroundColor;
        pad.TapBehavior          = dlg.ResultTapBehavior;
        pad.LoopStartSec         = dlg.ResultLoopStartSec;

        _engine.SetPadCategory(_playback.ActiveBank, padIndex, pad.Category);

        if (fileChanged)
        {
            _missingPads[_playback.ActiveBank, padIndex] = false;
            if (!string.IsNullOrEmpty(pad.FilePath))
            {
                string? resDir = System.IO.Path.GetDirectoryName(pad.FilePath);
                if (!string.IsNullOrEmpty(resDir)) _settings.LastSelectedResourceDirectory = resDir;
            }
            _playback.LoadBank(_playback.ActiveBank, () =>
            {
                string msg = $"Pad {padIndex + 1}: 詳細設定を更新しました。";
                var src = _engine.GetSource(_playback.ActiveBank, padIndex);
                if (src.WasTruncated)
                    SetInfo2Warning(msg + "（99:59を超えるため打ち切りました）");
                else
                    SetInfo2(msg);
            });
        }
        else
        {
            _playback.UpdatePadGain(padIndex, pad.PadGain);
            SetInfo2($"Pad {padIndex + 1}: 詳細設定を更新しました。");
        }

        MarkDirty();
    }

    private void OpenFileForPad(int padIndex)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"パッド {padIndex + 1} のファイルを選択",
            Filter = "音声・動画・画像ファイル|*.mp3;*.wav;*.flac;*.ogg;*.aac;*.m4a;*.mp4;*.mov;*.mkv;*.avi;*.wmv;*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.tiff;*.tif|すべてのファイル|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        AssignFileToPad(padIndex, dlg.FileName);
    }

    private void HandlePadDrop(int padIndex, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        string? file = files.FirstOrDefault(f => AudioVideoExtensions.Contains(System.IO.Path.GetExtension(f)));
        if (file == null) return;
        AssignFileToPad(padIndex, file);
        e.Handled = true;
    }

    private void AssignFileToPad(int padIndex, string filePath)
    {
        if (_project == null) return;
        PushUndo();
        var pad = _project.Banks[_playback.ActiveBank].Pads[padIndex];
        pad.FilePath = filePath;
        pad.CustomLabel = null; // 表示名は新しいファイル名を採用（前の名前を引き継がない）
        // 動画・画像ファイルは自動的に MOV カテゴリに設定
        if (VideoImageExtensions.Contains(System.IO.Path.GetExtension(filePath)))
            pad.Category = AudioCategory.Movie;
        _missingPads[_playback.ActiveBank, padIndex] = false;
        string? resDir = System.IO.Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(resDir)) _settings.LastSelectedResourceDirectory = resDir;
        string fname = System.IO.Path.GetFileName(filePath);
        SetInfo2($"Pad {padIndex + 1}: {fname} を読み込み中...");
        _playback.LoadBank(_playback.ActiveBank, () =>
        {
            var src = _engine.GetSource(_playback.ActiveBank, padIndex);

            // 新ファイルの総時間を超えている位置値をデフォルトにリセット
            float total = src.FileTotalSec;
            if (total > 0)
            {
                if (pad.StartPositionSec > total) pad.StartPositionSec = 0f;
                if (pad.EndPositionSec   > total) pad.EndPositionSec   = -1f;
                if (pad.LoopStartSec     > total) pad.LoopStartSec     = -1f;
            }

            if (src.WasTruncated)
                SetInfo2Warning($"Pad {padIndex + 1}: {fname} は99:59を超えるため99:59で打ち切りました");
            else
                SetInfo2($"Pad {padIndex + 1}: {fname} を読み込みました");
        });
        MarkDirty();
    }

    // ------------------------------------------------------------------
    // ミキサーイベント
    // ------------------------------------------------------------------
    private void OnFaderChanged(int faderIndex, double value)
    {
        switch (faderIndex)
        {
            case 0: _playback.MovieVolume  = (float)value; break;
            case 1: _playback.BgmVolume    = (float)value; break;
            case 2: _playback.SeVolume     = (float)value; break;
            case 3: _playback.MasterVolume = (float)value; break;
        }
        MarkDirty();
    }

    private void MarkDirty()
    {
        if (!_initComplete) return;
        if (_projectDirty) return;
        _projectDirty = true;
        UpdateTitle();
    }

    // ------------------------------------------------------------------
    // Undo / Redo
    // ------------------------------------------------------------------
    private void PushUndo()
    {
        if (!_initComplete) return;
        SyncFadersToProject();
        string json = System.Text.Json.JsonSerializer.Serialize(_project);
        _undoStack.Push(json);
        if (_undoStack.Count > MaxUndoHistory)
        {
            var arr = _undoStack.ToArray();
            _undoStack.Clear();
            for (int i = MaxUndoHistory - 1; i >= 0; i--)
                _undoStack.Push(arr[i]);
        }
        _redoStack.Clear();
    }

    private void ExecuteUndo()
    {
        if (_undoStack.Count == 0) { SetInfo2("これ以上元に戻せません"); _infoClearPending = true; return; }
        SyncFadersToProject();
        _redoStack.Push(System.Text.Json.JsonSerializer.Serialize(_project));
        ApplyProjectSnapshot(_undoStack.Pop(), "元に戻しました");
    }

    private void ExecuteRedo()
    {
        if (_redoStack.Count == 0) { SetInfo2("やり直せる操作がありません"); _infoClearPending = true; return; }
        SyncFadersToProject();
        _undoStack.Push(System.Text.Json.JsonSerializer.Serialize(_project));
        ApplyProjectSnapshot(_redoStack.Pop(), "やり直しました");
    }

    private void Menu_Undo(object sender, RoutedEventArgs e) => ExecuteUndo();
    private void Menu_Redo(object sender, RoutedEventArgs e) => ExecuteRedo();

    private void ApplyProjectSnapshot(string json, string message)
    {
        var restored = System.Text.Json.JsonSerializer.Deserialize<ProjectData>(json);
        if (restored == null) return;
        int activeBankBefore = _project.ActiveBankIndex;
        _project = restored;
        _project.ActiveBankIndex = activeBankBefore;
        _projectDirty = true;
        _missingPads = new bool[ProjectData.BankCount, BankData.PadCount];
        _playback.SetProject(_project, () =>
        {
            int bank = _playback.ActiveBank;
            for (int p = 0; p < BankData.PadCount; p++)
            {
                var pad = _project.Banks[bank].Pads[p];
                if (!string.IsNullOrEmpty(pad.FilePath))
                    _playback.UpdatePadGain(p, pad.PadGain);
            }
            SetInfo2(message);
        });
        SyncFadersFromProject();
        RefreshAllBankLabels();
        UpdateBankHighlight();
        UpdateTitle();
    }

    private void OnMemoryRecall(int faderIndex, int slot, bool quick)
    {
        var mem = _faders[faderIndex].GetMemory(slot);
        if (!mem.HasValue) return;
        PushUndo();
        double duration = quick ? 0.0 : _settings.LongFadeDuration;
        _faders[faderIndex].SmoothMoveTo(mem.Value, duration);
    }

    private void OnFaderMute(int faderIndex, bool muted)
    {
        PushUndo();
        switch (faderIndex)
        {
            case 0: _playback.MuteMovie  = muted; break;
            case 1: _playback.MuteBgm    = muted; break;
            case 2: _playback.MuteSe     = muted; break;
            case 3: _playback.MuteMaster = muted; break;
        }
        MarkDirty();
    }

    // ------------------------------------------------------------------
    // MIDIワイヤリング
    // ------------------------------------------------------------------
    private void WireMidi()
    {
        _midi.PadTriggered += idx =>
        {
            var padSets1 = _playback.GetPadSettings(idx);
            var padCat1 = padSets1?.Category;
            if (padCat1 == AudioCategory.Movie && !IsImageFile(padSets1?.FilePath) && _movieCtrl.IsBuffering)
            { SetInfo2("バッファリング中..."); return; }
            TriggerPadWithMovie(idx);
        };
        _midi.AllCutTriggered    += ExecutePanic;
        _midi.AllFadeTriggered   += ExecuteAllFade;
        _midi.PauseTriggered     += ExecutePauseAll;
        _midi.LockTriggered      += () => SetLocked(!_isLocked);
        _midi.FullScreenTriggered += () =>
        {
            if (_isLocked) return;
            _fullBd ??= FullButton.Template.FindName("FullBd", FullButton) as System.Windows.Controls.Border;
            if (_fullBd != null) { _fullBd.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00)); _fullBd.BorderThickness = new Thickness(2.5); }
            _movieCtrl.ToggleFullScreen();
            UpdateFullButton(_movieCtrl.IsFullScreen);
        };
        _midi.DisplayTriggered += () =>
        {
            if (_isLocked) return;
            _dispBd ??= DispButton.Template.FindName("DispBd", DispButton) as System.Windows.Controls.Border;
            if (_dispBd != null) { _dispBd.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00)); _dispBd.BorderThickness = new Thickness(2.5); }
            bool was = _movieCtrl.DisplayActive;
            _movieCtrl.ToggleDisplay();
            if (_movieCtrl.DisplayActive && !was) ResumeMovieIfPlaying();
        };
        _midi.BankTriggered    += idx => RequestBankSwitch(idx);
        _midi.MuTriggered      += idx => _faders[idx].ToggleMute();
        _midi.MemTriggered     += (idx, slot) => _faders[idx].TriggerMemory(slot);
        _midi.FaderCCReceived  += (idx, val) => _faders[idx].Value = VFaderControl.FaderMax * val / 127.0;
    }

    // ------------------------------------------------------------------
    // LOCK ボタン
    // ------------------------------------------------------------------
    private void UpdateLockButton()
    {
        _lockBd ??= LockButton.Template.FindName("LockBd", LockButton) as System.Windows.Controls.Border;
        if (_lockBd == null) return;
        _lockBd.BorderBrush     = _isLocked
            ? new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00))
            : new SolidColorBrush(Color.FromRgb(0x7A, 0x2A, 0x2A));
        _lockBd.BorderThickness = _isLocked ? new Thickness(2.5) : new Thickness(2);
    }

    private void SetLocked(bool locked)
    {
        _isLocked = locked;
        foreach (var pad in _padButtons)
            pad.CanEdit = !locked;
        UpdateLockButton();
    }

    private void SetBankLoading(bool loading)
    {
        _bankLoading = loading;
        double opacity = loading ? 0.45 : 1.0;
        foreach (var pad in _padButtons)
            pad.Opacity = opacity;
        if (loading)
        {
            InfoLine2.Text = "読み込み中...";
            InfoLine2.Foreground = BrushInfoWarnText;
            InfoLine2Border.Background = BrushInfoWarnBg;
            BankConfirmPanel.Visibility = Visibility.Collapsed;
            _infoClearPending = false;
        }
        else if (InfoLine2.Text.StartsWith("読み込み中"))
        {
            string? pendingMsg = _pendingBankSwitchMsg;
            _pendingBankSwitchMsg = null;
            SetInfo2(pendingMsg ?? "");
        }
    }

    // ------------------------------------------------------------------
    // パッド / バンク 入れ替え（D&D）
    // ------------------------------------------------------------------
    private void SwapPads(int srcIdx, int dstIdx)
    {
        if (srcIdx == dstIdx) return;
        PushUndo();
        int bankIdx = _playback.ActiveBank;
        var bank = _project.Banks[bankIdx];

        (bank.Pads[srcIdx], bank.Pads[dstIdx]) = (bank.Pads[dstIdx], bank.Pads[srcIdx]);
        (_missingPads[bankIdx, srcIdx], _missingPads[bankIdx, dstIdx]) =
            (_missingPads[bankIdx, dstIdx], _missingPads[bankIdx, srcIdx]);

        _engine.SetPadCategory(bankIdx, srcIdx, bank.Pads[srcIdx].Category);
        _engine.SetPadCategory(bankIdx, dstIdx, bank.Pads[dstIdx].Category);
        string msg = $"パッド {srcIdx + 1} と {dstIdx + 1} を入れ替えました";
        _playback.LoadBank(bankIdx, () => SetInfo2(msg));
        MarkDirty();
    }

    private void SwapBanks(int srcIdx, int dstIdx)
    {
        if (srcIdx == dstIdx) return;
        if (IsAnyPadActive())
        {
            SetInfo2("再生中はバンクの入れ替えができません");
            return;
        }
        PushUndo();

        (_project.Banks[srcIdx], _project.Banks[dstIdx]) = (_project.Banks[dstIdx], _project.Banks[srcIdx]);
        for (int p = 0; p < BankData.PadCount; p++)
        {
            (_missingPads[srcIdx, p], _missingPads[dstIdx, p]) =
                (_missingPads[dstIdx, p], _missingPads[srcIdx, p]);
            _engine.SetPadCategory(srcIdx, p, _project.Banks[srcIdx].Pads[p].Category);
            _engine.SetPadCategory(dstIdx, p, _project.Banks[dstIdx].Pads[p].Category);
        }
        _playback.LoadBank(srcIdx);
        _playback.LoadBank(dstIdx);
        RefreshBankLabel(srcIdx);
        RefreshBankLabel(dstIdx);
        UpdateBankHighlight();
        MarkDirty();
        SetInfo2($"Bank {BankNames[srcIdx]} と Bank {BankNames[dstIdx]} を入れ替えました");
    }

    // PAUSE中カットアウト後にPAUSE可能パッドが残っていなければPAUSE解除
    private void CheckReleasePauseAll()
    {
        if (!_isPauseAllActive) return;
        for (int i = 0; i < BankData.PadCount; i++)
        {
            if (_playback.GetPadState(i) == PadPlayState.Idle) continue;
            var cat = _playback.GetPadSettings(i)?.Category;
            if (cat == AudioCategory.Movie || cat == AudioCategory.BGM) return;
        }
        _isPauseAllActive = false;
        UpdateActionButtons();
    }

    // ------------------------------------------------------------------
    // FULL / DISPLAY ボタン
    // ------------------------------------------------------------------
    private void LockButton_Click(object sender, RoutedEventArgs e) => SetLocked(!_isLocked);

    private bool _fullButtonBusy;

    private async void FullButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isLocked || _fullButtonBusy) return;
        _fullButtonBusy = true;
        try
        {
            _fullBd   ??= FullButton.Template.FindName("FullBd",   FullButton) as System.Windows.Controls.Border;
            _fullText ??= FullButton.Template.FindName("FullText", FullButton) as System.Windows.Controls.TextBlock;
            if (_fullBd != null) { _fullBd.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00)); _fullBd.BorderThickness = new Thickness(2.5); }
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            _movieCtrl.ToggleFullScreen();
            UpdateFullButton(_movieCtrl.IsFullScreen);

            // 画面遷移が完了するまでグレーアウト（ON時は黄色枠維持・OFF時はすぐ暗く）
            FullButton.IsEnabled = false;
            if (_fullBd   != null) { _fullBd.Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)); _fullBd.BorderBrush = _movieCtrl.IsFullScreen ? new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00)) : new SolidColorBrush(Color.FromRgb(0x2A, 0x7A, 0x2A)); }
            if (_fullText != null) _fullText.Foreground  = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            await Task.Delay(1000);
        }
        finally
        {
            _fullButtonBusy = false;
            FullButton.IsEnabled = true;
            UpdateFullButton(_movieCtrl.IsFullScreen);
        }
    }

    private bool _dispButtonBusy;

    private async void DispButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isLocked || _dispButtonBusy) return;
        _dispButtonBusy = true;
        try
        {
            _dispBd   ??= DispButton.Template.FindName("DispBd",   DispButton) as System.Windows.Controls.Border;
            _dispText ??= DispButton.Template.FindName("DispText", DispButton) as System.Windows.Controls.TextBlock;
            if (_dispBd != null) { _dispBd.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00)); _dispBd.BorderThickness = new Thickness(2.5); }
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            bool wasActive = _movieCtrl.DisplayActive;
            _movieCtrl.ToggleDisplay();
            if (_movieCtrl.DisplayActive && !wasActive)
                ResumeMovieIfPlaying();

            // VLC が安定するまでグレーアウト（ON時は黄色枠維持・OFF時はすぐ暗く）
            DispButton.IsEnabled = false;
            if (_dispBd   != null) { _dispBd.Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)); _dispBd.BorderBrush = _movieCtrl.DisplayActive ? new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00)) : new SolidColorBrush(Color.FromRgb(0x2A, 0x7A, 0x2A)); }
            if (_dispText != null) _dispText.Foreground  = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            await Task.Delay(3000);
        }
        finally
        {
            _dispButtonBusy = false;
            DispButton.IsEnabled = true;
            UpdateDispButton(_movieCtrl.DisplayActive);
        }
    }

    // DISP を開き直した際に静止画 or 動画を再開する
    private void ResumeMovieIfPlaying()
    {
        // 静止画パッドが待機中なら再表示
        if (_imageDisplayingPadIndex >= 0)
        {
            var imgPad = _playback.GetPadSettings(_imageDisplayingPadIndex);
            if (imgPad != null && !string.IsNullOrEmpty(imgPad.FilePath) && IsImageFile(imgPad.FilePath))
            {
                _movieCtrl.PlayVideo(imgPad.FilePath, imgPad.StartPositionSec, imgPad.EndPositionSec, imgPad.AfterPlayback);
                return;
            }
        }

        // 音声付き Movie パッドが再生中なら映像を再開（壁掛け時計・パッドIndexも復元）
        for (int i = 0; i < BankData.PadCount; i++)
        {
            var state = _playback.GetPadState(i);
            if (state == PadPlayState.Idle) continue;
            var pad = _playback.GetPadSettings(i);
            if (pad?.Category != AudioCategory.Movie) continue;
            if (string.IsNullOrEmpty(pad.FilePath)) continue;
            float pos       = _playback.GetPadPosition(i);
            float totalSec  = _playback.GetPadTotalTime(i);
            double startSec = pos * totalSec;
            ++_movieLoopSession;
            _currentMoviePadIndex = i;
            StartMovieWallClock(startSec);
            _movieCtrl.PlayVideo(pad.FilePath, startSec, pad.EndPositionSec, pad.AfterPlayback);
            break;
        }
    }

    private void UpdateFullButton(bool isOn)
    {
        _fullBd   ??= FullButton.Template.FindName("FullBd",   FullButton) as System.Windows.Controls.Border;
        _fullText ??= FullButton.Template.FindName("FullText", FullButton) as System.Windows.Controls.TextBlock;
        if (_fullBd == null || _fullText == null) return;

        if (isOn)
        {
            _fullBd.Background      = new SolidColorBrush(Color.FromRgb(0x1A, 0x5C, 0x1A));
            _fullBd.BorderBrush     = new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00));
            _fullBd.BorderThickness = new Thickness(2.5);
            _fullText.Foreground    = new SolidColorBrush(Colors.White);
        }
        else
        {
            _fullBd.Background      = new SolidColorBrush(Color.FromRgb(0x0E, 0x3A, 0x0E));
            _fullBd.BorderBrush     = new SolidColorBrush(Color.FromRgb(0x2A, 0x7A, 0x2A));
            _fullBd.BorderThickness = new Thickness(2);
            _fullText.Foreground    = new SolidColorBrush(Colors.White);
        }
    }

    private void UpdateDispButton(bool isOn)
    {
        _dispBd   ??= DispButton.Template.FindName("DispBd",   DispButton) as System.Windows.Controls.Border;
        _dispText ??= DispButton.Template.FindName("DispText", DispButton) as System.Windows.Controls.TextBlock;
        if (_dispBd == null || _dispText == null) return;

        if (isOn)
        {
            _dispBd.Background      = new SolidColorBrush(Color.FromRgb(0x1A, 0x5C, 0x1A));
            _dispBd.BorderBrush     = new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00));
            _dispBd.BorderThickness = new Thickness(2.5);
            _dispText.Foreground    = new SolidColorBrush(Colors.White);
        }
        else
        {
            _dispBd.Background      = new SolidColorBrush(Color.FromRgb(0x0E, 0x3A, 0x0E));
            _dispBd.BorderBrush     = new SolidColorBrush(Color.FromRgb(0x2A, 0x7A, 0x2A));
            _dispBd.BorderThickness = new Thickness(2);
            _dispText.Foreground    = new SolidColorBrush(Colors.White);
        }
    }

    // ------------------------------------------------------------------
    // FADE/CUTモードボタン
    // ------------------------------------------------------------------
    // ALL FADEボタン
    private void FadeCutButton_Click(object sender, RoutedEventArgs e) => ExecuteAllFade();

    private void ExecuteAllFade()
    {
        if (!_playback.HasAnyPlaying() && _imageDisplayingPadIndex < 0) return;

        _fadeCutBd ??= FadeCutButton.Template.FindName("FadeCutBd", FadeCutButton) as System.Windows.Controls.Border;
        if (_fadeCutBd != null)
        {
            _fadeCutBd.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00));
            _fadeAnimTimer?.Stop();
            _fadeAnimStartTick = Environment.TickCount64;
            double duration = Math.Max(_settings.LongFadeDuration, 0.1);
            _fadeAnimTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _fadeAnimTimer.Tick += (_, _) =>
            {
                double t = Math.Clamp((Environment.TickCount64 - _fadeAnimStartTick) / 1000.0 / duration, 0.0, 1.0);
                byte r = (byte)(0xFF + (0x44 - 0xFF) * t);
                byte g = (byte)(0xD7 + (0x88 - 0xD7) * t);
                byte b = (byte)(0x00 + (0xFF - 0x00) * t);
                _fadeCutBd!.BorderBrush = new SolidColorBrush(Color.FromRgb(r, g, b));
                if (t >= 1.0) { _fadeAnimTimer!.Stop(); _fadeAnimTimer = null; }
            };
            _fadeAnimTimer.Start();
        }

        bool allFadeWasPaused = _isPauseAllActive;
        _isPauseAllActive = false;
        if (_imageDisplayingPadIndex >= 0)
        {
            _imageFadingPadIndex = _imageDisplayingPadIndex;
            _imageFadeStartTick  = Environment.TickCount64;
            _imageFadeDuration   = _settings.LongFadeDuration;
        }
        _imageDisplayingPadIndex = -1;
        ++_movieLoopSession; // ALL FADE中にループ再起動が始まらないようキャンセル
        _playback.PanicFadeAll();
        if (allFadeWasPaused)
        {
            _playback.StopPausedPads(); // PAUSE中パッドはカットアウト扱い
            _pauseBd ??= PauseAllButton.Template.FindName("PauseBd", PauseAllButton) as System.Windows.Controls.Border;
            if (_pauseBd != null) _pauseBd.BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0xCC, 0x44));
        }
        _movieCtrl.PanicFade(_settings.LongFadeDuration);
    }

    // ------------------------------------------------------------------
    // ALL CUTボタン（即停止）
    // ------------------------------------------------------------------
    private void PanicButton_Click(object sender, RoutedEventArgs e) => ExecutePanic();

    private void ExecutePanic()
    {
        if (!_playback.HasAnyPlaying() && _imageDisplayingPadIndex < 0) return;

        _panicBd ??= PanicButton.Template.FindName("Bd", PanicButton) as System.Windows.Controls.Border;
        if (_panicBd != null)
        {
            _panicBd.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00));
            _panicFlashTimer?.Stop();
            _panicFlashTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _panicFlashTimer.Tick += (_, _) =>
            {
                _panicFlashTimer!.Stop();
                _panicFlashTimer = null;
                if (_panicBd != null) _panicBd.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x44, 0x44));
            };
            _panicFlashTimer.Start();
        }

        // ALL FADE 中の場合、フェードアニメを即終了（完了状態の色に移行）
        if (_fadeAnimTimer != null)
        {
            _fadeAnimTimer.Stop();
            _fadeAnimTimer = null;
            _fadeCutBd ??= FadeCutButton.Template.FindName("FadeCutBd", FadeCutButton) as System.Windows.Controls.Border;
            if (_fadeCutBd != null)
                _fadeCutBd.BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x88, 0xFF));
        }

        bool wasPaused = _isPauseAllActive;
        _isPauseAllActive = false;
        _imageDisplayingPadIndex = -1;
        _imageFadingPadIndex = -1;
        ++_movieLoopSession; _currentMoviePadIndex = -1; // ループ再起動キャンセル
        _playback.PanicStopAll();
        _playback.FlushOutput();
        if (wasPaused)
        {
            _pauseBd ??= PauseAllButton.Template.FindName("PauseBd", PauseAllButton) as System.Windows.Controls.Border;
            if (_pauseBd != null) _pauseBd.BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0xCC, 0x44));
            _movieCtrl.ResumeVideo(); // PAUSE状態のVLCを正常に停止させる
        }
        _movieCtrl.StopVideo(); // DISPウィンドウは閉じず映像のみ停止
    }

    // ------------------------------------------------------------------
    // PAUSEボタン（MOV/BGM 一時停止・再開）
    // ------------------------------------------------------------------
    private void PauseAllButton_Click(object sender, RoutedEventArgs e) => ExecutePauseAll();

    private void ExecutePauseAll()
    {
        // 一時停止対象がなく、かつ再開すべき状態でもない場合は無反応
        if (!_playback.HasMovieBgmPlaying() && !_isPauseAllActive) return;

        if (_isPauseAllActive)
        {
            _isPauseAllActive = false;
            _playback.ResumeAllPaused();
            _movieCtrl.ResumeVideo();
            ResumeMovieWallClock();
        }
        else
        {
            _isPauseAllActive = true;
            _playback.PauseAllMovieBgm();
            _movieCtrl.PauseVideo();
            PauseMovieWallClock();
        }
        UpdateActionButtons(); // 押した直後にボタン状態を即座に反映
    }

    // ------------------------------------------------------------------
    // バンク切り替えリクエスト
    // ------------------------------------------------------------------
    private void RequestBankSwitch(int bankIndex)
    {
        if (bankIndex == _playback.ActiveBank) return;
        if (IsAnyPadActive())
        {
            SetInfo2("再生中のバンク切り替えは出来ません");
            return;
        }
        ++_movieLoopSession; _currentMoviePadIndex = -1; // ループ再起動キャンセル
        _playback.SwitchBank(bankIndex);
        UpdateBankHighlight();
        _pendingBankSwitchMsg = $"Bank {BankNames[bankIndex]} に切り替えました";
    }

    private bool IsAnyPadActive() =>
        Enumerable.Range(0, BankData.PadCount).Any(i => _playback.GetPadState(i) != PadPlayState.Idle);

    // ------------------------------------------------------------------
    // バンクハイライト更新
    // ------------------------------------------------------------------
    private void UpdateBankHighlight()
    {
        int active = _playback.ActiveBank;
        for (int i = 0; i < ProjectData.BankCount; i++)
        {
            bool sel = i == active;
            var bgColor = _project.Banks[i].BankBackgroundColor;
            _bankButtons[i].Background = string.IsNullOrEmpty(bgColor)
                ? BrushBankNormal
                : new SolidColorBrush((Color)ColorConverter.ConvertFromString(bgColor));
            _bankButtons[i].BorderBrush      = sel ? BrushBankBorderS  : BrushBankBorderN;
            _bankButtons[i].BorderThickness  = sel ? new Thickness(2.5) : new Thickness(1.5);

            // ボタン内 TextBlock と badge の色更新（常に白）
            if (_bankButtons[i].Content is Grid g)
            {
                if (g.Children[0] is TextBlock tb)
                    tb.Foreground = new SolidColorBrush(Colors.White);
                if (g.Children[1] is Border badge)
                {
                    badge.BorderBrush = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
                    if (badge.Child is TextBlock badgeTb)
                        badgeTb.Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
                }
            }
        }
    }

    // ------------------------------------------------------------------
    // インフォメーションエリア
    // ------------------------------------------------------------------
    // ------------------------------------------------------------------
    // バンク確認ボタン
    // ------------------------------------------------------------------
    private void BankYesBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingMemOverwrite.HasValue)      ConfirmMemOverwrite();
        else if (_pendingIkpPath != null)       ConfirmIkpLoad();
        else if (_pendingBankClearIndex >= 0)   ExecuteBankClear();
        else if (_pendingOpenConfirm)           ConfirmOpenLoad();
        else if (_pendingNewConfirm)            ConfirmNew();
    }
    private void BankNoBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingMemOverwrite.HasValue)      CancelMemOverwrite();
        else if (_pendingIkpPath != null)       CancelIkpLoad();
        else if (_pendingBankClearIndex >= 0)   { _pendingBankClearIndex = -1; SetInfo2(""); }
        else if (_pendingOpenConfirm)           CancelOpenLoad();
        else if (_pendingNewConfirm)            CancelNew();
    }

    private void ConfirmMemOverwrite()
    {
        if (!_pendingMemOverwrite.HasValue) return;
        PushUndo();
        var (fader, slot, gain) = _pendingMemOverwrite.Value;
        _pendingMemOverwrite = null;
        fader.StoreMemory(slot, gain);
        SetInfo2("");
    }

    private void CancelMemOverwrite()
    {
        _pendingMemOverwrite = null;
        SetInfo2("");
    }

    // ikpファイル D&D 読み込み
    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files &&
            files.Length > 0 &&
            System.IO.Path.GetExtension(files[0]).Equals(".ikp", StringComparison.OrdinalIgnoreCase))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        string? ikp = null;
        foreach (var f in files)
        {
            if (System.IO.Path.GetExtension(f).Equals(".ikp", StringComparison.OrdinalIgnoreCase))
            { ikp = f; break; }
        }
        if (ikp == null) return;

        // 既存の確認が進行中なら ikp D&D を無視
        if (_pendingMemOverwrite.HasValue || _pendingBankClearIndex >= 0
            || _pendingOpenConfirm || _pendingNewConfirm) return;

        // 初期状態（未変更・未保存）なら確認なしで即読み込み
        if (!_projectDirty && _projectFilePath == null)
        {
            await LoadProjectAsync(ikp);
            e.Handled = true;
            return;
        }

        _pendingIkpPath = ikp;
        SetInfo2Warning("現在のプロジェクトを破棄しますか？\n[Y] 確定  /  [N] キャンセル");
        e.Handled = true;
    }

    private async void ConfirmIkpLoad()
    {
        if (_pendingIkpPath == null) return;
        string path = _pendingIkpPath;
        _pendingIkpPath = null;
        SetInfo2("");
        await LoadProjectAsync(path);
    }

    private void CancelIkpLoad()
    {
        _pendingIkpPath = null;
        SetInfo2("");
    }

    private void SetInfo2(string text)
    {
        InfoLine2.Text = text;
        InfoLine2.Foreground = BrushInfoNormal;
        InfoLine2Border.Background = Brushes.Transparent;
        BankConfirmPanel.Visibility = Visibility.Collapsed;
        _infoClearPending = !string.IsNullOrEmpty(text); // 次のアクションで消去
    }

    private void SetInfo2Warning(string text)
    {
        InfoLine2.Text = text;
        InfoLine2.Foreground = BrushInfoWarnText;
        InfoLine2Border.Background = BrushInfoWarnBg;
        BankConfirmPanel.Visibility = Visibility.Visible;
        _infoClearPending = false; // 確認メッセージは自動消去しない
    }

    // ------------------------------------------------------------------
    // メニュー: ファイル
    // ------------------------------------------------------------------
    private void Menu_New(object sender, RoutedEventArgs e)
    {
        if (!_projectDirty && _projectFilePath == null)
        {
            DoNewProject();
            return;
        }
        if (_pendingMemOverwrite.HasValue || _pendingIkpPath != null
            || _pendingBankClearIndex >= 0 || _pendingOpenConfirm || _pendingNewConfirm) return;

        _pendingNewConfirm = true;
        SetInfo2Warning("現在のプロジェクトを破棄しますか？\n[Y] 確定  /  [N] キャンセル");
    }

    private void ConfirmNew()
    {
        if (!_pendingNewConfirm) return;
        _pendingNewConfirm = false;
        SetInfo2("");
        DoNewProject();
    }

    private void CancelNew()
    {
        _pendingNewConfirm = false;
        SetInfo2("");
    }

    private void DoNewProject()
    {
        _project = new ProjectData();
        _projectFilePath = null;
        _projectDirty = false;
        _missingPads = new bool[ProjectData.BankCount, BankData.PadCount];
        _playback.SetProject(_project);
        SyncFadersFromProject();
        UpdateTitle();
        SetInfo2("新規プロジェクトを作成しました。");
    }

    private async void Menu_Open(object sender, RoutedEventArgs e)
    {
        // 初期状態（未変更・未保存）なら確認なしで即ダイアログ
        if (!_projectDirty && _projectFilePath == null)
        {
            await DoOpenFileDialog();
            return;
        }
        // 他の確認が進行中なら無視
        if (_pendingMemOverwrite.HasValue || _pendingIkpPath != null
            || _pendingBankClearIndex >= 0 || _pendingOpenConfirm) return;

        _pendingOpenConfirm = true;
        SetInfo2Warning("現在のプロジェクトを破棄しますか？\n[Y] 確定  /  [N] キャンセル");
    }

    private async void ConfirmOpenLoad()
    {
        if (!_pendingOpenConfirm) return;
        _pendingOpenConfirm = false;
        SetInfo2("");
        await DoOpenFileDialog();
    }

    private void CancelOpenLoad()
    {
        _pendingOpenConfirm = false;
        SetInfo2("");
    }

    private async Task DoOpenFileDialog()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "プロジェクトを開く",
            Filter = "ikePon プロジェクト (*.ikp)|*.ikp|すべてのファイル|*.*",
            DefaultExt = ".ikp"
        };
        if (dlg.ShowDialog() != true) return;
        await LoadProjectAsync(dlg.FileName);
    }

    private void Menu_Save(object sender, RoutedEventArgs e)
    {
        if (_projectFilePath == null) { Menu_SaveAs(sender, e); return; }
        SaveProject(_projectFilePath);
    }

    private void Menu_SaveAs(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "名前を付けて保存",
            Filter = "ikePon プロジェクト (*.ikp)|*.ikp",
            DefaultExt = ".ikp",
            FileName = _project.ProjectName
        };
        if (dlg.ShowDialog() != true) return;
        _projectFilePath = dlg.FileName;
        _project.ProjectName = System.IO.Path.GetFileNameWithoutExtension(dlg.FileName);
        SaveProject(dlg.FileName);
    }

    private void Menu_Exit(object sender, RoutedEventArgs e) => Close();

    // ------------------------------------------------------------------
    // バンク右クリック
    // ------------------------------------------------------------------
    private void BankRightClick(int bankIdx, MouseButtonEventArgs e)
    {
        if (_isLocked) { e.Handled = true; return; }
        var cm = new ContextMenu();

        var detail = new MenuItem { Header = "詳細設定..." };
        detail.Click += (_, _) => OpenBankDetail(bankIdx);
        cm.Items.Add(detail);
        cm.Items.Add(new Separator());

        var copy = new MenuItem { Header = "コピー" };
        copy.Click += (_, _) => { _bankClipboard = _project.Banks[bankIdx].Clone(); };
        cm.Items.Add(copy);

        var paste = new MenuItem { Header = "ペースト", IsEnabled = _bankClipboard != null };
        paste.Click += (_, _) => PasteBankSettings(bankIdx);
        cm.Items.Add(paste);

        var delete = new MenuItem { Header = "削除" };
        delete.Click += (_, _) => RequestBankClear(bankIdx);
        cm.Items.Add(delete);

        cm.IsOpen = true;
        e.Handled = true;
    }

    private void RequestBankClear(int bankIdx)
    {
        if (_pendingMemOverwrite.HasValue || _pendingIkpPath != null
            || _pendingBankClearIndex >= 0 || _pendingOpenConfirm) return;
        _pendingBankClearIndex = bankIdx;
        SetInfo2Warning($"Bank {BankNames[bankIdx]} の内容をすべてクリアしますか？\n[Y] 確定  /  [N] キャンセル");
    }

    private void ExecuteBankClear()
    {
        if (_pendingBankClearIndex < 0) return;
        int bankIdx = _pendingBankClearIndex;
        _pendingBankClearIndex = -1;
        PushUndo();

        var bank = _project.Banks[bankIdx];
        bank.BankLabel = null;
        bank.BankBackgroundColor = null;
        for (int p = 0; p < BankData.PadCount; p++)
        {
            _engine.GetSource(bankIdx, p).Unload();
            var pad = bank.Pads[p];
            pad.FilePath           = null;
            pad.CustomLabel        = null;
            pad.PadGain            = 1.0f;
            pad.PadBackgroundColor = null;
            pad.StartPositionSec   = 0f;
            pad.EndPositionSec     = -1f;
            pad.AfterPlayback      = AfterPlaybackBehavior.Stop;
            pad.TapBehavior        = TapBehavior.FadeOut;
            pad.LoopStartSec       = -1f;
            pad.Category           = p < 8 ? AudioCategory.BGM : AudioCategory.SE;
            _engine.SetPadCategory(bankIdx, p, pad.Category);
        }
        RefreshBankLabel(bankIdx);
        UpdateBankHighlight();
        MarkDirty();
        SetInfo2($"Bank {BankNames[bankIdx]} をクリアしました。");
    }

    private void OpenBankDetail(int bankIdx)
    {
        var bank = _project.Banks[bankIdx];
        string currentLabel = bank.BankLabel ?? $"Bank {BankNames[bankIdx]}";
        var dlg = new ikePon.UI.Dialogs.BankDetailDialog(currentLabel, bank.BankBackgroundColor) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        PushUndo();

        bank.BankLabel = string.IsNullOrWhiteSpace(dlg.ResultLabel) ? null : dlg.ResultLabel;
        bank.BankBackgroundColor = dlg.ResultBgColor;
        RefreshBankLabel(bankIdx);
        UpdateBankHighlight();
        MarkDirty();
    }

    private void RefreshBankLabel(int bankIdx)
    {
        if (_bankButtons[bankIdx].Content is Grid g && g.Children[0] is TextBlock tb)
            tb.Text = _project.Banks[bankIdx].BankLabel ?? $"Bank {BankNames[bankIdx]}";
    }

    private void RefreshAllBankLabels()
    {
        for (int i = 0; i < ProjectData.BankCount; i++)
            RefreshBankLabel(i);
    }

    private void PasteBankSettings(int bankIdx)
    {
        if (_bankClipboard == null) return;
        PushUndo();
        _project.Banks[bankIdx].BankLabel = _bankClipboard.BankLabel;
        _project.Banks[bankIdx].BankBackgroundColor = _bankClipboard.BankBackgroundColor;
        for (int p = 0; p < BankData.PadCount; p++)
        {
            _project.Banks[bankIdx].Pads[p] = _bankClipboard.Pads[p].Clone();
            _engine.SetPadCategory(bankIdx, p, _project.Banks[bankIdx].Pads[p].Category);
        }
        string bankMsg = $"Bank {BankNames[bankIdx]} に設定をペーストしました。";
        _playback.LoadBank(bankIdx, () => SetInfo2(bankMsg));
        RefreshBankLabel(bankIdx);
        UpdateBankHighlight();
        MarkDirty();
    }

    private string? ShowInputDialog(string title, string current)
    {
        var dlg = new Window
        {
            Title = title, Width = 300, SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.ToolWindow, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = this,
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E))
        };
        var sp = new StackPanel { Margin = new Thickness(12) };
        var tb = new TextBox
        {
            Text = current, Margin = new Thickness(0, 0, 0, 10),
            Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x2E)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xEE, 0xEE, 0xEE)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            Padding = new Thickness(4, 3, 4, 3), FontSize = 13
        };
        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok     = new Button { Content = "OK", Width = 64, Margin = new Thickness(0, 0, 8, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x44, 0x6E)),
            Foreground = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x9F, 0xFF)) };
        var cancel = new Button { Content = "キャンセル", Width = 80,
            Background = new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)) };
        btns.Children.Add(ok); btns.Children.Add(cancel);
        sp.Children.Add(tb); sp.Children.Add(btns);
        dlg.Content = sp;

        string? result = null;
        ok.Click     += (_, _) => { result = tb.Text; dlg.DialogResult = true; };
        cancel.Click += (_, _) => { dlg.DialogResult = false; };
        tb.KeyDown   += (_, ke) => { if (ke.Key == Key.Return) { result = tb.Text; dlg.DialogResult = true; } };
        dlg.Loaded   += (_, _) => { tb.Focus(); tb.SelectAll(); };
        dlg.ShowDialog();
        return result;
    }

    private void Menu_Settings(object sender, RoutedEventArgs e)
    {
        var dlg = new ikePon.UI.Dialogs.SettingsDialog(_settings) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _engine.PaSeparate = _settings.PaSeparateMode;
            _movieCtrl.ReloadStandbyImage();
            _midi.SetDevice(_settings.SelectedMidiDeviceName);
            _settings.Save();
            SetInfo2("設定を保存しました。");
        }
    }

    // ------------------------------------------------------------------
    // プロジェクト読み込み（スマートリロケート付き）
    // ------------------------------------------------------------------
    private async Task LoadProjectAsync(string path)
    {
        var loaded = ProjectData.Load(path);
        if (loaded == null)
        {
            SetInfo2Warning("読み込みに失敗しました。");
            return;
        }

        var relocator = new Controller.RelocateController();
        relocator.StatusMessage += msg => Dispatcher.Invoke(() => SetInfo2(msg));

        var stillMissing = await relocator.RelocateAsync(
            loaded,
            path,
            _settings.LastSelectedResourceDirectory,
            async missingPath =>
            {
                string? result = null;
                await Dispatcher.InvokeAsync(() =>
                {
                    var dlg = new Microsoft.Win32.OpenFileDialog
                    {
                        Title = $"ファイルを手動で指定してください：{System.IO.Path.GetFileName(missingPath)}",
                        Filter = "音声・動画・画像ファイル|*.mp3;*.wav;*.flac;*.ogg;*.aac;*.m4a;*.mp4;*.mov;*.mkv;*.avi;*.wmv;*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp;*.tiff;*.tif|すべてのファイル|*.*",
                        FileName = System.IO.Path.GetFileName(missingPath)
                    };
                    if (dlg.ShowDialog() == true) result = dlg.FileName;
                });
                return result;
            });

        if (!string.IsNullOrEmpty(relocator.ManuallySelectedDirectory))
            _settings.LastSelectedResourceDirectory = relocator.ManuallySelectedDirectory;

        _missingPads = new bool[ProjectData.BankCount, BankData.PadCount];
        foreach (var (b, p) in stillMissing)
            _missingPads[b, p] = true;

        _project         = loaded;
        _projectFilePath = path;
        _projectDirty    = false;
        string fileName = System.IO.Path.GetFileName(path);
        Action? onComplete = relocator.AnyMissingFound ? null
            : () => SetInfo2($"プロジェクトを読み込みました: {fileName}");
        _playback.SetProject(_project, onComplete);
        SyncFadersFromProject();
        RefreshAllBankLabels();
        UpdateBankHighlight();
        UpdateTitle();
    }

    private void SaveProject(string path)
    {
        SyncFadersToProject();
        try
        {
            _project.Save(path);
            _projectDirty = false;
            UpdateTitle();
            SetInfo2($"保存しました: {System.IO.Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存に失敗しました:\n{ex.Message}", "ikePon", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SyncFadersFromProject()
    {
        for (int i = 0; i < 4; i++)
            _faders[i].Value = _project.FaderPositions[i];

        for (int f = 0; f < 4; f++)
            for (int m = 0; m < 3; m++) // M1-M3 のみ（M4廃止）
                if (_project.FaderMemories[f][m].HasValue)
                    _faders[f].StoreMemory(m, _project.FaderMemories[f][m]!.Value);

        // フェーダー値・ミュートをエンジンに反映
        _playback.MovieVolume  = _project.FaderPositions[0];
        _playback.BgmVolume    = _project.FaderPositions[1];
        _playback.SeVolume     = _project.FaderPositions[2];
        _playback.MasterVolume = _project.FaderPositions[3];

        for (int i = 0; i < 4; i++)
            _faders[i].SetMuted(_project.FaderMutes[i]);
        _playback.MuteMovie  = _project.FaderMutes[0];
        _playback.MuteBgm    = _project.FaderMutes[1];
        _playback.MuteSe     = _project.FaderMutes[2];
        _playback.MuteMaster = _project.FaderMutes[3];
    }

    private void SyncFadersToProject()
    {
        for (int i = 0; i < 4; i++)
        {
            _project.FaderPositions[i] = (float)_faders[i].Value;
            _project.FaderMutes[i]     = _faders[i].IsMuted;
        }

        for (int f = 0; f < 4; f++)
            for (int m = 0; m < 3; m++) // M1-M3 のみ（M4廃止）
                _project.FaderMemories[f][m] = _faders[f].GetMemory(m);
    }

    private void UpdateTitle()
    {
        if (_authorTitleActive)
        {
            Title = "ikePon v1.1.1 by Ike-san";
            return;
        }
        string dirty = _projectDirty ? " *" : "";
        string fname = _projectFilePath != null
            ? $" — {System.IO.Path.GetFileName(_projectFilePath)}"
            : " — 未保存";
        Title = $"ikePon v1.1.1{fname}{dirty}";
    }

    // ------------------------------------------------------------------
    // クローズ
    // ------------------------------------------------------------------
    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_projectDirty)
        {
            var r = MessageBox.Show("変更が保存されていません。保存しますか？", "ikePon",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) { e.Cancel = true; return; }
            if (r == MessageBoxResult.Yes) Menu_Save(this, new RoutedEventArgs());
        }
        ComponentDispatcher.ThreadPreprocessMessage -= OnGlobalKey;
        _uiTimer.Stop();
        _midi.Dispose();
        _engine.Dispose();
        _movieCtrl.Dispose(); // CloseDisplay + LibVLC解放
        _settings.WindowWidth  = Width;
        _settings.WindowHeight = Height;
        _settings.Save();
        _gainDb.Save();
    }
}
