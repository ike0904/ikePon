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
    private readonly BankManager _bankManager;
    private readonly KeyboardMapper _keyMapper;
    private readonly MovieController _movieCtrl;
    private readonly MidiController _midi;
    private ProjectData _project;
    private string? _projectFilePath;
    private bool _projectDirty;
    private bool _initComplete;

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
    private long _imageFadeStartTick;
    private float _imageFadeDuration;

    private readonly DispatcherTimer _uiTimer;

    // FULL/DISP/FADE-CUT/PAUSEボタンのテンプレートパーツ（遅延キャッシュ）
    private System.Windows.Controls.Border? _fullBd;
    private System.Windows.Controls.TextBlock? _fullText;
    private System.Windows.Controls.Border? _dispBd;
    private System.Windows.Controls.TextBlock? _dispText;
    private System.Windows.Controls.Border? _panicBd;
    private System.Windows.Controls.Border? _fadeCutBd;
    private System.Windows.Controls.Border? _pauseBd;

    // ALL CUT 黄色フラッシュ用タイマー
    private DispatcherTimer? _panicFlashTimer;
    // ALL FADE 黄→青アニメーション用タイマー
    private DispatcherTimer? _fadeAnimTimer;
    private long _fadeAnimStartTick;
    // PAUSEボタンの一時停止状態（PAUSE ボタン自身でまとめて一時停止したときのみ true）
    private bool _isPauseAllActive;

    // 確認待ちフラグ（バンク切り替え）
    private bool _pendingBankConfirm;
    // 確認待ち（バンク削除）
    private int _pendingBankClearIndex = -1;
    // 確認待ち（フェーダーメモリ上書き）
    private (VFaderControl fader, int slot, double gain)? _pendingMemOverwrite;
    // 確認待ち（ikpファイルD&D読み込み）
    private string? _pendingIkpPath;
    // 確認待ち（ファイルメニュー「開く」）
    private bool _pendingOpenConfirm;

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
        _bankManager = new BankManager(_playback);
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

        _movieCtrl.DisplayActiveChanged += on => Dispatcher.Invoke(() => UpdateDispButton(on));
        _movieCtrl.FullScreenChanged    += on => Dispatcher.Invoke(() => UpdateFullButton(on));
        _movieCtrl.StatusMessage        += msg => Dispatcher.Invoke(() => SetInfo2(msg));

        WireMidi();
        _midi.SetDevice(_settings.SelectedMidiDeviceName);

        UpdateBankHighlight();
        UpdateTitle();
        UpdateFullButton(_movieCtrl.IsFullScreen);
        UpdateDispButton(_movieCtrl.DisplayActive);
        SetInfo2("準備完了");
        Loaded += (_, _) => _initComplete = true;
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
            pad.SeekRequested += (_, fraction) => SeekPad(captured, fraction);
            pad.PadVolumeChanged += (_, gainInt) =>
            {
                var padSettings = _playback.GetPadSettings(captured);
                if (padSettings == null) return;
                padSettings.PadGain = gainInt / 100.0f;
                _playback.UpdatePadGain(captured, padSettings.PadGain);
                MarkDirty();
            };
            pad.PreviewMouseDown += (_, _) => Keyboard.ClearFocus();
            pad.MouseLeftButtonDown += (_, e) =>
            {
                bool shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
                bool ctrl  = Keyboard.IsKeyDown(Key.LeftCtrl)  || Keyboard.IsKeyDown(Key.RightCtrl);
                TriggerPadWithMovie(captured, shift, ctrl);
                e.Handled = true;
            };
            pad.MouseRightButtonUp += (_, e) => PadRightClick(captured, e);
            pad.AllowDrop = true;
            pad.DragOver  += (_, e) =>
            {
                e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                    ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            };
            pad.Drop += (_, e) => HandlePadDrop(captured, e);

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
            fader.MemoryRecall += (_, args) => OnMemoryRecall(captured, args.slot, args.quick);
            fader.MuteChanged += (_, muted) => OnFaderMute(captured, muted);
            fader.MemoryRegisterRequested += (s, args) =>
            {
                if (_pendingBankConfirm || _pendingMemOverwrite.HasValue || _pendingBankClearIndex >= 0) return;
                var f = (VFaderControl)s!;
                _pendingMemOverwrite = (f, args.slot, args.gain);
                SetInfo2Warning($"{f.Label} M{args.slot + 1} 上書き登録しますか？  [Y] 確定  /  [N] キャンセル");
            };

            _faders[i] = fader;
            MixerGrid.Children.Add(fader);
        }
    }

    private void WireEvents()
    {
        // MovieWindow フォーカス時でもショートカットを有効化（スレッドレベルでキー割り込み）
        ComponentDispatcher.ThreadPreprocessMessage += OnGlobalKey;

        _bankManager.BankSwitchRequested += idx =>
        {
            _pendingBankConfirm = true;
            SetInfo2Warning($"Bank {BankNames[idx]} [{KeyboardMapper.BankLabels[idx]}] に切り替えますか？  [Y] 確定  /  [N] キャンセル");
        };
        _bankManager.BankSwitched += idx =>
        {
            _pendingBankConfirm = false;
            UpdateBankHighlight();
            SetInfo2($"Bank {BankNames[idx]} に切り替えました");
        };
        _bankManager.BankSwitchCancelled += () =>
        {
            _pendingBankConfirm = false;
            SetInfo2("");
        };
    }

    // ------------------------------------------------------------------
    // UI タイマー（30fps でパッド状態を更新）
    // ------------------------------------------------------------------
    private void UiTimer_Tick(object? sender, EventArgs e)
    {
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

        for (int i = 0; i < BankData.PadCount; i++)
        {
            var state    = _playback.GetPadState(i);
            var pos      = _playback.GetPadPosition(i);
            var pad      = _playback.GetPadSettings(i);
            var fadeGain = _playback.GetPadFadeGain(i);
            var totalSec = _playback.GetPadTotalTime(i);
            bool imageDisplaying = _imageDisplayingPadIndex == i;
            float iGain = (fadingIdx == i) ? imageFadeGainForPad : -1f;
            bool isMissing = _missingPads[_playback.ActiveBank, i];
            _padButtons[i].UpdateState(state, pos, pad, fadeGain, totalSec, imageDisplaying, iGain, isMissing);
        }
        UpdateActionButtons();
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
            bool isOurKey = key == Key.Escape || key == Key.D0 || key == Key.OemMinus ||
                            key == Key.Space || key == Key.Return ||
                            key == Key.Y || key == Key.N ||
                            _keyMapper.GetPadIndex(key).HasValue ||
                            _keyMapper.GetBankIndex(key).HasValue;
            if (!isOurKey) return;
        }

        if (HandleKeyDown(key)) handled = true;
    }

    // true を返した場合は e.Handled = true にする
    private bool HandleKeyDown(Key key)
    {
        // バンク確認中は Y/N のみ受け付け
        if (_pendingBankConfirm)
        {
            if (key == Key.Y) { _bankManager.Confirm(); return true; }
            if (key == Key.N) { _bankManager.Cancel();  return true; }
        }

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

        // バンク削除確認中
        if (_pendingBankClearIndex >= 0)
        {
            if (key == Key.Y) { ExecuteBankClear(); return true; }
            if (key == Key.N) { _pendingBankClearIndex = -1; SetInfo2(""); return true; }
        }

        // ファイルメニューショートカット（Ctrl+N/O/S）
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (key == Key.N) { Menu_New(null!, null!);      return true; }
            if (key == Key.O) { Menu_Open(null!, null!);     return true; }
            if (key == Key.S) { Menu_Save(null!, null!);     return true; }
            if (key == Key.E) { Menu_Settings(null!, null!); return true; }
        }

        // パニック
        if (key == Key.Escape)
        {
            ExecutePanic();
            return true;
        }

        // FULL ボタン（[0]キー）
        if (key == Key.D0)
        {
            _movieCtrl.ToggleFullScreen();
            UpdateFullButton(_movieCtrl.IsFullScreen);
            return true;
        }

        // DISPLAY ボタン（[-]キー）
        if (key == Key.OemMinus)
        {
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
            // 動画バッファリング中はMOVIEカテゴリパッドの操作を無視
            var padCat = _playback.GetPadSettings(padIdx.Value)?.Category;
            if (padCat == AudioCategory.Movie && _movieCtrl.IsBuffering)
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
        var pad0 = _playback.GetPadSettings(padIndex);
        bool isImagePad = pad0 != null && IsImageFile(pad0.FilePath);
        var cm = new ContextMenu();

        if (state != PadPlayState.Idle || imageActive)
        {
            var fadeOut = new MenuItem { Header = "フェードアウト" };
            fadeOut.Click += (_, _) => TriggerPadWithMovie(padIndex, fadeOut: true);
            var cutOut = new MenuItem { Header = "カットアウト" };
            cutOut.Click += (_, _) => TriggerPadWithMovie(padIndex, stopImmediate: true);
            bool pauseEnabled = !isImagePad && pad0?.Category != AudioCategory.SE;
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

        cm.IsOpen = true;
        e.Handled = true;
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
                // 新規表示（DISPLAY OFF中でも index を記録しておき、ON時に再表示）
                _movieCtrl.PlayVideo(pad!.FilePath!, pad.StartPositionSec, pad.AfterPlayback);
                _imageDisplayingPadIndex = padIndex;
            }
            return;
        }

        // 音声パッド（動画含む）: 再生前の状態を記録（同パッド再押し判定）
        var stateBefore = isMoviePad ? _playback.GetPadState(padIndex) : PadPlayState.Idle;
        bool wasActive  = stateBefore != PadPlayState.Idle;

        _playback.TriggerPad(padIndex, fadeOut, stopImmediate);

        if (!isMoviePad) return;

        if (stopImmediate)
        {
            _movieCtrl.StopVideo();
            _imageDisplayingPadIndex = -1;
        }
        else if (fadeOut)
        {
            _movieCtrl.FadeVideo(_settings.LongFadeDuration);
            _imageDisplayingPadIndex = -1;
        }
        else if (wasActive && pad?.TapBehavior == TapBehavior.PauseResume)
        {
            // 一時停止／再開: 音声は TriggerPad 内で処理済み。映像をそれに同期
            var stateAfter = _playback.GetPadState(padIndex);
            if (stateAfter == PadPlayState.Paused)
                _movieCtrl.PauseVideo();
            else if (stateAfter == PadPlayState.Playing)
                _movieCtrl.ResumeVideo();
        }
        else if (wasActive)
        {
            // 同パッド再押し → 音声停止済み → 映像も同様に停止（TapBehavior準拠）
            bool cutOut = pad?.TapBehavior == TapBehavior.CutOut;
            if (cutOut)
                _movieCtrl.StopVideo();
            else
                _movieCtrl.FadeVideo(_settings.LongFadeDuration);
            _imageDisplayingPadIndex = -1;
        }
        else if (!string.IsNullOrEmpty(pad!.FilePath))
        {
            _movieCtrl.PlayVideo(pad.FilePath, pad.StartPositionSec, pad.AfterPlayback);
            _imageDisplayingPadIndex = -1;
        }
    }

    private void PastePadSettings(int padIndex)
    {
        if (_clipboardPad == null) return;
        if (_project == null) return;
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
        _playback.LoadBank(_playback.ActiveBank);
        MarkDirty();
        SetInfo2($"Pad {padIndex + 1} に設定をペーストしました。");
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
            if (newState == PadPlayState.Paused)  _movieCtrl.PauseVideo();
            else if (newState == PadPlayState.Playing) _movieCtrl.ResumeVideo();
        }
    }

    private void SeekPad(int padIndex, float fraction)
    {
        _engine.GetSource(_playback.ActiveBank, padIndex).SeekToFraction(fraction);
        var pad = _playback.GetPadSettings(padIndex);
        if (pad?.Category == AudioCategory.Movie)
            _movieCtrl.SeekVideo(fraction);
    }

    private void CyclePadCategory(int padIndex)
    {
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

        bool shouldLoop = pad.AfterPlayback == AfterPlaybackBehavior.Loop;
        _engine.GetSource(_playback.ActiveBank, padIndex).SetLoop(shouldLoop);
        _engine.SetPadCategory(_playback.ActiveBank, padIndex, pad.Category);
        MarkDirty();
    }

    private void ClearPad(int padIndex)
    {
        if (_project == null) return;
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
        if (dlg.ShowDialog() != true) return;

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
            _playback.LoadBank(_playback.ActiveBank);
        }
        else
            _playback.UpdatePadGain(padIndex, pad.PadGain);

        MarkDirty();
        SetInfo2($"Pad {padIndex + 1}: 詳細設定を更新しました。");
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
        var pad = _project.Banks[_playback.ActiveBank].Pads[padIndex];
        pad.FilePath = filePath;
        // 動画・画像ファイルは自動的に MOV カテゴリに設定
        if (VideoImageExtensions.Contains(System.IO.Path.GetExtension(filePath)))
            pad.Category = AudioCategory.Movie;
        _missingPads[_playback.ActiveBank, padIndex] = false;
        string? resDir = System.IO.Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(resDir)) _settings.LastSelectedResourceDirectory = resDir;
        string fname = System.IO.Path.GetFileName(filePath);
        SetInfo2($"Pad {padIndex + 1}: {fname} を読み込み中...");
        _playback.LoadBank(_playback.ActiveBank,
            () => SetInfo2($"Pad {padIndex + 1}: {fname} を読み込みました"));
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

    private void OnMemoryRecall(int faderIndex, int slot, bool quick)
    {
        var mem = _faders[faderIndex].GetMemory(slot);
        if (!mem.HasValue) return;
        double duration = quick ? 0.0 : _settings.LongFadeDuration;
        _faders[faderIndex].SmoothMoveTo(mem.Value, duration);
    }

    private void OnFaderMute(int faderIndex, bool muted)
    {
        switch (faderIndex)
        {
            case 0: _playback.MuteMovie  = muted; break;
            case 1: _playback.MuteBgm    = muted; break;
            case 2: _playback.MuteSe     = muted; break;
            case 3: _playback.MuteMaster = muted; break;
        }
    }

    // ------------------------------------------------------------------
    // MIDIワイヤリング
    // ------------------------------------------------------------------
    private void WireMidi()
    {
        _midi.PadTriggered += idx =>
        {
            var padCat = _playback.GetPadSettings(idx)?.Category;
            if (padCat == AudioCategory.Movie && _movieCtrl.IsBuffering)
            { SetInfo2("バッファリング中..."); return; }
            TriggerPadWithMovie(idx);
        };
        _midi.AllCutTriggered    += ExecutePanic;
        _midi.AllFadeTriggered   += ExecuteAllFade;
        _midi.PauseTriggered     += ExecutePauseAll;
        _midi.FullScreenTriggered += () =>
        {
            _movieCtrl.ToggleFullScreen();
            UpdateFullButton(_movieCtrl.IsFullScreen);
        };
        _midi.DisplayTriggered += () =>
        {
            bool was = _movieCtrl.DisplayActive;
            _movieCtrl.ToggleDisplay();
            if (_movieCtrl.DisplayActive && !was) ResumeMovieIfPlaying();
        };
        _midi.BankTriggered    += idx => RequestBankSwitch(idx);
        _midi.MuTriggered      += idx => _faders[idx].ToggleMute();
        _midi.MemTriggered     += (idx, slot) => OnMemoryRecall(idx, slot, quick: false);
        _midi.FaderCCReceived  += (idx, val) => _faders[idx].Value = VFaderControl.FaderMax * val / 127.0;
    }

    // ------------------------------------------------------------------
    // FULL / DISPLAY ボタン
    // ------------------------------------------------------------------
    private void FullButton_Click(object sender, RoutedEventArgs e)
    {
        _movieCtrl.ToggleFullScreen();
        UpdateFullButton(_movieCtrl.IsFullScreen);
    }

    private void DispButton_Click(object sender, RoutedEventArgs e)
    {
        bool wasActive = _movieCtrl.DisplayActive;
        _movieCtrl.ToggleDisplay();
        if (_movieCtrl.DisplayActive && !wasActive)
            ResumeMovieIfPlaying();
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
                _movieCtrl.PlayVideo(imgPad.FilePath, imgPad.StartPositionSec, imgPad.AfterPlayback);
                return;
            }
        }

        // 音声付き Movie パッドが再生中なら映像を再開
        for (int i = 0; i < BankData.PadCount; i++)
        {
            var state = _playback.GetPadState(i);
            if (state == PadPlayState.Idle) continue;
            var pad = _playback.GetPadSettings(i);
            if (pad?.Category != AudioCategory.Movie) continue;
            if (string.IsNullOrEmpty(pad.FilePath)) continue;
            float pos      = _playback.GetPadPosition(i);
            float totalSec = _playback.GetPadTotalTime(i);
            _movieCtrl.PlayVideo(pad.FilePath, pos * totalSec, pad.AfterPlayback);
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
            _fullBd.Background      = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30));
            _fullBd.BorderBrush     = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
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
            _dispBd.Background      = new SolidColorBrush(Color.FromRgb(0x1C, 0x3D, 0x1C));
            _dispBd.BorderBrush     = new SolidColorBrush(Color.FromRgb(0xFF, 0xD7, 0x00));
            _dispBd.BorderThickness = new Thickness(2.5);
            _dispText.Foreground    = new SolidColorBrush(Colors.White);
        }
        else
        {
            _dispBd.Background      = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30));
            _dispBd.BorderBrush     = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
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

        _isPauseAllActive = false;
        if (_imageDisplayingPadIndex >= 0)
        {
            _imageFadingPadIndex = _imageDisplayingPadIndex;
            _imageFadeStartTick  = Environment.TickCount64;
            _imageFadeDuration   = _settings.LongFadeDuration;
        }
        _imageDisplayingPadIndex = -1;
        _playback.PanicFadeAll();
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

        _isPauseAllActive = false;
        _imageDisplayingPadIndex = -1;
        _imageFadingPadIndex = -1;
        _playback.PanicStopAll();
        _playback.FlushOutput();
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
        }
        else
        {
            _isPauseAllActive = true;
            _playback.PauseAllMovieBgm();
            _movieCtrl.PauseVideo();
        }
    }

    // ------------------------------------------------------------------
    // バンク切り替えリクエスト
    // ------------------------------------------------------------------
    private void RequestBankSwitch(int bankIndex)
    {
        // 同じバンクへの切り替えかつ確認待ちでなければ無視
        if (bankIndex == _playback.ActiveBank && !_bankManager.IsPendingConfirmation) return;
        // 確認待ち中、またはアクティブパッドがある場合は確認ダイアログへ
        if (_bankManager.IsPendingConfirmation || IsAnyPadActive())
        {
            _bankManager.RequestSwitch(bankIndex);
        }
        else
        {
            // 全パッドがアイドル → 即座に切り替え（確認なし）
            _playback.SwitchBank(bankIndex);
            UpdateBankHighlight();
            SetInfo2($"Bank {BankNames[bankIndex]} に切り替えました");
        }
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
        if (_pendingBankConfirm)                   _bankManager.Confirm();
        else if (_pendingMemOverwrite.HasValue)     ConfirmMemOverwrite();
        else if (_pendingIkpPath != null)           ConfirmIkpLoad();
        else if (_pendingBankClearIndex >= 0)       ExecuteBankClear();
        else if (_pendingOpenConfirm)               ConfirmOpenLoad();
    }
    private void BankNoBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingBankConfirm)                   _bankManager.Cancel();
        else if (_pendingMemOverwrite.HasValue)     CancelMemOverwrite();
        else if (_pendingIkpPath != null)           CancelIkpLoad();
        else if (_pendingBankClearIndex >= 0)       { _pendingBankClearIndex = -1; SetInfo2(""); }
        else if (_pendingOpenConfirm)               CancelOpenLoad();
    }

    private void ConfirmMemOverwrite()
    {
        if (!_pendingMemOverwrite.HasValue) return;
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
        if (_pendingBankConfirm || _pendingMemOverwrite.HasValue || _pendingBankClearIndex >= 0) return;

        // 初期状態（未変更・未保存）なら確認なしで即読み込み
        if (!_projectDirty && _projectFilePath == null)
        {
            await LoadProjectAsync(ikp);
            e.Handled = true;
            return;
        }

        _pendingIkpPath = ikp;
        SetInfo2Warning("プロジェクトを読み込みますか？  [Y] 確定  /  [N] キャンセル");
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
    }

    private void SetInfo2Warning(string text)
    {
        InfoLine2.Text = text;
        InfoLine2.Foreground = BrushInfoWarnText;
        InfoLine2Border.Background = BrushInfoWarnBg;
        BankConfirmPanel.Visibility = Visibility.Visible;
    }

    // ------------------------------------------------------------------
    // メニュー: ファイル
    // ------------------------------------------------------------------
    private void Menu_New(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
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
        if (_pendingBankConfirm || _pendingMemOverwrite.HasValue || _pendingIkpPath != null
            || _pendingBankClearIndex >= 0 || _pendingOpenConfirm) return;

        _pendingOpenConfirm = true;
        SetInfo2Warning("プロジェクトを読み込みますか？  [Y] 確定  /  [N] キャンセル");
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
        if (_pendingBankConfirm || _pendingMemOverwrite.HasValue || _pendingIkpPath != null
            || _pendingBankClearIndex >= 0 || _pendingOpenConfirm) return;
        _pendingBankClearIndex = bankIdx;
        SetInfo2Warning($"Bank {BankNames[bankIdx]} の内容をすべてクリアしますか？  [Y] 確定  /  [N] キャンセル");
    }

    private void ExecuteBankClear()
    {
        if (_pendingBankClearIndex < 0) return;
        int bankIdx = _pendingBankClearIndex;
        _pendingBankClearIndex = -1;

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
        _project.Banks[bankIdx].BankLabel = _bankClipboard.BankLabel;
        _project.Banks[bankIdx].BankBackgroundColor = _bankClipboard.BankBackgroundColor;
        for (int p = 0; p < BankData.PadCount; p++)
        {
            _project.Banks[bankIdx].Pads[p] = _bankClipboard.Pads[p].Clone();
            _engine.SetPadCategory(bankIdx, p, _project.Banks[bankIdx].Pads[p].Category);
        }
        _playback.LoadBank(bankIdx);
        RefreshBankLabel(bankIdx);
        UpdateBankHighlight();
        MarkDirty();
        SetInfo2($"Bank {BankNames[bankIdx]} に設定をペーストしました。");
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
        _playback.SetProject(_project);
        SyncFadersFromProject();
        RefreshAllBankLabels();
        UpdateBankHighlight();
        UpdateTitle();

        if (!relocator.AnyMissingFound)
            SetInfo2($"プロジェクトを読み込みました: {System.IO.Path.GetFileName(path)}");
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

    private bool ConfirmDiscard()
    {
        if (!_projectDirty) return true;
        var r = MessageBox.Show("変更が保存されていません。続けますか？", "ikePon",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        return r == MessageBoxResult.Yes;
    }

    private void SyncFadersFromProject()
    {
        for (int i = 0; i < 4; i++)
            _faders[i].Value = _project.FaderPositions[i];

        for (int f = 0; f < 4; f++)
            for (int m = 0; m < 3; m++) // M1-M3 のみ（M4廃止）
                if (_project.FaderMemories[f][m].HasValue)
                    _faders[f].StoreMemory(m, _project.FaderMemories[f][m]!.Value);

        // フェーダー値をエンジンに反映
        _playback.MovieVolume  = _project.FaderPositions[0];
        _playback.BgmVolume    = _project.FaderPositions[1];
        _playback.SeVolume     = _project.FaderPositions[2];
        _playback.MasterVolume = _project.FaderPositions[3];
    }

    private void SyncFadersToProject()
    {
        for (int i = 0; i < 4; i++)
            _project.FaderPositions[i] = (float)_faders[i].Value;

        for (int f = 0; f < 4; f++)
            for (int m = 0; m < 3; m++) // M1-M3 のみ（M4廃止）
                _project.FaderMemories[f][m] = _faders[f].GetMemory(m);
    }

    private void UpdateTitle()
    {
        string dirty = _projectDirty ? " *" : "";
        string fname = _projectFilePath != null
            ? $" — {System.IO.Path.GetFileName(_projectFilePath)}"
            : " — 未保存";
        Title = $"ikePon v1.0.73{fname}{dirty}";
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
