using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ikePon.Audio;
using ikePon.Controller;
using ikePon.Model;
using ikePon.UI.Controls;
using ikePon.UI.Dialogs;
using ikePon.UI.Windows;

namespace ikePon;

public partial class MainWindow : Window
{
    private readonly AudioEngine _engine;
    private readonly AppSettings _settings;
    private readonly FileGainDatabase _gainDb;
    private readonly PlaybackController _playback;
    private readonly BankManager _bankManager;
    private readonly KeyboardMapper _keyMapper;
    private readonly PanicController _panic;
    private readonly MovieController _movieCtrl;
    private ProjectData _project;
    private string? _projectFilePath;
    private bool _projectDirty;

    private readonly PadButton[] _padButtons = new PadButton[BankData.PadCount];
    private readonly Button[] _bankButtons = new Button[ProjectData.BankCount];
    private readonly VFaderControl[] _faders = new VFaderControl[4];

    private PadSettings? _clipboardPad;
    private BankData? _bankClipboard;

    private static readonly HashSet<string> AudioVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".wav", ".flac", ".ogg", ".aac", ".m4a", ".mp4", ".mov", ".mkv", ".avi", ".wmv" };

    private readonly DispatcherTimer _uiTimer;
    private ModifierState _modifier = ModifierState.None;

    // PANICボタンのテンプレートパーツ（遅延キャッシュ）
    private System.Windows.Controls.Border? _panicBd;
    private System.Windows.Controls.TextBlock? _panicText;
    private bool _prevPanicFading;

    // FULL/DISPボタンのテンプレートパーツ（遅延キャッシュ）
    private System.Windows.Controls.Border? _fullBd;
    private System.Windows.Controls.TextBlock? _fullText;
    private System.Windows.Controls.Border? _dispBd;
    private System.Windows.Controls.TextBlock? _dispText;

    // 確認待ちフラグ（バンク切り替え）
    private bool _pendingBankConfirm;
    // 確認待ち（フェーダーメモリ上書き）
    private (VFaderControl fader, int slot, double gain)? _pendingMemOverwrite;

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
        _panic     = new PanicController(_playback);
        _movieCtrl = new MovieController(_settings);
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

        UpdateBankHighlight();
        UpdateTitle();
        UpdateFullButton(_movieCtrl.IsFullScreen);
        UpdateDispButton(_movieCtrl.DisplayActive);
        SetInfo2("準備完了");
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
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x4A)),
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
                Tag = new object?[] { content.Children[0] as TextBlock, badge }
            };
            btn.Style = CreateBankButtonStyle();
            btn.Click += (_, _) => _bankManager.RequestSwitch(captured);
            btn.MouseRightButtonUp += (_, e) => BankRightClick(captured, e);

            _bankButtons[i] = btn;
            BankGrid.Children.Add(btn);
        }
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
            fader.MemoryRegisterRequested += (s, args) =>
            {
                if (_pendingBankConfirm || _pendingMemOverwrite.HasValue) return;
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
        float panicMaxGain = 0f;
        bool panicAnyActive = false;

        for (int i = 0; i < BankData.PadCount; i++)
        {
            var state    = _playback.GetPadState(i);
            var pos      = _playback.GetPadPosition(i);
            var pad      = _playback.GetPadSettings(i);
            var fadeGain = _playback.GetPadFadeGain(i);
            var totalSec = _playback.GetPadTotalTime(i);
            _padButtons[i].UpdateState(state, pos, pad, _modifier, fadeGain, totalSec);

            if (state != PadPlayState.Idle)
            {
                panicAnyActive = true;
                if (fadeGain > panicMaxGain) panicMaxGain = fadeGain;
            }
        }

        for (int i = 0; i < _faders.Length; i++)
            _faders[i].UpdateModifierState(_modifier);

        // PANICボタン色更新（フェード中は黄色→通常色にアニメーション）
        bool isFading = _panic.IsFading;
        if (isFading)
        {
            if (!panicAnyActive) { _panic.ClearFadeState(); UpdatePanicButtonColor(-1f); }
            else UpdatePanicButtonColor(panicMaxGain);
        }
        else if (_prevPanicFading)
        {
            UpdatePanicButtonColor(-1f);
        }
        _prevPanicFading = isFading;
    }

    private void UpdatePanicButtonColor(float gain)
    {
        _panicBd   ??= PanicButton.Template.FindName("Bd",        PanicButton) as System.Windows.Controls.Border;
        _panicText ??= PanicButton.Template.FindName("PanicText", PanicButton) as System.Windows.Controls.TextBlock;
        if (_panicBd == null || _panicText == null) return;

        if (gain < 0f)
        {
            _panicBd.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x44, 0x44));
            _panicText.Foreground = new SolidColorBrush(Colors.White);
        }
        else
        {
            float g = Math.Clamp(gain, 0f, 1f);
            // ボーダー: g=1→黄(FFD700), g=0→赤(FF4444)
            byte bg = (byte)(0x44 + (0xD7 - 0x44) * g);
            byte bb = (byte)(0x44 * (1f - g));
            _panicBd.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, bg, bb));
            _panicText.Foreground = new SolidColorBrush(Colors.White);
        }
    }

    // ------------------------------------------------------------------
    // キーボードイベント
    // ------------------------------------------------------------------
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.IsRepeat) return;

        // 修飾キー状態更新
        if (e.Key == Key.LeftShift || e.Key == Key.RightShift)
        { _modifier = ModifierState.Shift; return; }
        if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl)
        { _modifier = ModifierState.Ctrl; return; }

        // バンク確認中は Y/N のみ受け付け（他のキー操作は通常通り）
        if (_pendingBankConfirm)
        {
            if (e.Key == Key.Y) { _bankManager.Confirm(); e.Handled = true; return; }
            if (e.Key == Key.N) { _bankManager.Cancel();  e.Handled = true; return; }
        }

        // メモリ上書き確認中
        if (_pendingMemOverwrite.HasValue)
        {
            if (e.Key == Key.Y) { ConfirmMemOverwrite(); e.Handled = true; return; }
            if (e.Key == Key.N) { CancelMemOverwrite();  e.Handled = true; return; }
        }

        // パニック
        if (e.Key == Key.Escape)
        {
            ExecutePanic();
            e.Handled = true;
            return;
        }

        // FULL ボタン（[0]キー）
        if (e.Key == Key.D0)
        {
            _movieCtrl.ToggleFullScreen();
            UpdateFullButton(_movieCtrl.IsFullScreen);
            e.Handled = true;
            return;
        }

        // DISPLAY ボタン（[-]キー）
        if (e.Key == Key.OemMinus)
        {
            _movieCtrl.ToggleDisplay();
            e.Handled = true;
            return;
        }

        // パッドキー
        var padIdx = _keyMapper.GetPadIndex(e.Key);
        if (padIdx.HasValue)
        {
            bool shift = _modifier == ModifierState.Shift;
            bool ctrl  = _modifier == ModifierState.Ctrl;
            // 通常再生トリガーの場合、PANICフェードを中断して即座停止
            if (!shift && !ctrl && _panic.IsFading)
            {
                _playback.PanicStopAll();
                _panic.ClearFadeState();
                UpdatePanicButtonColor(-1f);
                _movieCtrl.StopVideo();
            }
            TriggerPadWithMovie(padIdx.Value, shift, ctrl);
            e.Handled = true;
            return;
        }

        // バンクキー
        var bankIdx = _keyMapper.GetBankIndex(e.Key);
        if (bankIdx.HasValue)
        {
            _bankManager.RequestSwitch(bankIdx.Value);
            e.Handled = true;
        }
    }

    private void Window_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.LeftShift || e.Key == Key.RightShift ||
            e.Key == Key.LeftCtrl  || e.Key == Key.RightCtrl)
        {
            _modifier = ModifierState.None;
        }
    }

    // ------------------------------------------------------------------
    // パッド右クリック
    // ------------------------------------------------------------------
    private void PadRightClick(int padIndex, MouseButtonEventArgs e)
    {
        var state = _playback.GetPadState(padIndex);
        var cm = new ContextMenu();

        if (state != PadPlayState.Idle)
        {
            var fadeOut = new MenuItem { Header = "フェードアウト" };
            fadeOut.Click += (_, _) => TriggerPadWithMovie(padIndex, fadeOut: true);
            var stopNow = new MenuItem { Header = "即座に停止" };
            stopNow.Click += (_, _) => TriggerPadWithMovie(padIndex, stopImmediate: true);
            cm.Items.Add(fadeOut);
            cm.Items.Add(stopNow);
        }
        else
        {
            var pad = _playback.GetPadSettings(padIndex);

            var load = new MenuItem { Header = "ファイルを読み込み..." };
            load.Click += (_, _) => OpenFileForPad(padIndex);
            cm.Items.Add(load);

            if (pad != null)
            {
                cm.Items.Add(new Separator());

                var copyItem = new MenuItem { Header = "設定をコピー", IsEnabled = !string.IsNullOrEmpty(pad.FilePath) };
                copyItem.Click += (_, _) => { _clipboardPad = pad.Clone(); };
                cm.Items.Add(copyItem);

                var pasteItem = new MenuItem { Header = "設定をペースト", IsEnabled = _clipboardPad != null };
                pasteItem.Click += (_, _) => PastePadSettings(padIndex);
                cm.Items.Add(pasteItem);

                if (!string.IsNullOrEmpty(pad.FilePath))
                {
                    cm.Items.Add(new Separator());
                    var clear = new MenuItem { Header = "ファイルの割り当てをクリア" };
                    clear.Click += (_, _) => ClearPad(padIndex);
                    cm.Items.Add(clear);
                }

                cm.Items.Add(new Separator());
                var detail = new MenuItem { Header = "詳細設定..." };
                detail.Click += (_, _) => OpenPadDetail(padIndex);
                cm.Items.Add(detail);
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
        _playback.TriggerPad(padIndex, fadeOut, stopImmediate);

        var pad = _playback.GetPadSettings(padIndex);
        if (pad?.Category != AudioCategory.Movie) return;

        if (!fadeOut && !stopImmediate)
        {
            if (!string.IsNullOrEmpty(pad.FilePath))
                _movieCtrl.PlayVideo(pad.FilePath, pad.StartPositionSec, pad.AfterPlayback);
        }
        else if (stopImmediate)
        {
            _movieCtrl.StopVideo();
        }
        else
        {
            _movieCtrl.FadeVideo(_settings.ShortFadeDuration);
        }
    }

    private void PastePadSettings(int padIndex)
    {
        if (_clipboardPad == null) return;
        if (_project == null) return;
        var dest = _project.Banks[_playback.ActiveBank].Pads[padIndex];
        dest.FilePath    = _clipboardPad.FilePath;
        dest.Category    = _clipboardPad.Category;
        dest.PadGain     = _clipboardPad.PadGain;
        dest.CustomLabel = _clipboardPad.CustomLabel;
        _engine.SetPadCategory(_playback.ActiveBank, padIndex, dest.Category);
        _playback.LoadBank(_playback.ActiveBank);
        MarkDirty();
        SetInfo2($"Pad {padIndex + 1} に設定をペーストしました。");
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
        _engine.GetSource(_playback.ActiveBank, padIndex).Unload();
        pad.FilePath = null;
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

        _engine.SetPadCategory(_playback.ActiveBank, padIndex, pad.Category);

        if (fileChanged)
            _playback.LoadBank(_playback.ActiveBank);
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
            Filter = "音声・動画ファイル|*.mp3;*.wav;*.flac;*.ogg;*.aac;*.m4a;*.mp4;*.mov;*.mkv;*.avi;*.wmv|すべてのファイル|*.*"
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
        if (_projectDirty) return;
        _projectDirty = true;
        UpdateTitle();
    }

    private void OnMemoryRecall(int faderIndex, int slot, bool quick)
    {
        var mem = _faders[faderIndex].GetMemory(slot);
        if (!mem.HasValue) return;
        double duration = quick ? _settings.ShortFadeDuration : _settings.LongFadeDuration;
        _faders[faderIndex].SmoothMoveTo(mem.Value, duration);
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
        _movieCtrl.ToggleDisplay();
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
            _fullBd.Background      = new SolidColorBrush(Color.FromRgb(0x1C, 0x3D, 0x1C));
            _fullBd.BorderBrush     = new SolidColorBrush(Color.FromRgb(0x33, 0x66, 0x33));
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
            _dispBd.Background      = new SolidColorBrush(Color.FromRgb(0x1C, 0x3D, 0x1C));
            _dispBd.BorderBrush     = new SolidColorBrush(Color.FromRgb(0x33, 0x66, 0x33));
            _dispBd.BorderThickness = new Thickness(2);
            _dispText.Foreground    = new SolidColorBrush(Colors.White);
        }
    }

    // ------------------------------------------------------------------
    // パニックボタン
    // ------------------------------------------------------------------
    private void PanicButton_Click(object sender, RoutedEventArgs e) => ExecutePanic();

    private void ExecutePanic()
    {
        bool wasImmediateStop = _panic.Trigger();
        if (wasImmediateStop)
            _movieCtrl.PanicStop();
        else
            _movieCtrl.PanicFade(_settings.LongFadeDuration);
    }

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
                        badgeTb.Foreground = new SolidColorBrush(Colors.White);
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
        if (_pendingBankConfirm) _bankManager.Confirm();
        else if (_pendingMemOverwrite.HasValue) ConfirmMemOverwrite();
    }
    private void BankNoBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingBankConfirm) _bankManager.Cancel();
        else if (_pendingMemOverwrite.HasValue) CancelMemOverwrite();
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
        _playback.SetProject(_project);
        SyncFadersFromProject();
        UpdateTitle();
        SetInfo2("新規プロジェクトを作成しました。");
    }

    private void Menu_Open(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "プロジェクトを開く",
            Filter = "ikePon プロジェクト (*.ikp)|*.ikp|すべてのファイル|*.*",
            DefaultExt = ".ikp"
        };
        if (dlg.ShowDialog() != true) return;
        var loaded = ProjectData.Load(dlg.FileName);
        if (loaded == null) { MessageBox.Show("読み込みに失敗しました。", "ikePon", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        _project = loaded;
        _projectFilePath = dlg.FileName;
        _projectDirty = false;
        _playback.SetProject(_project);
        SyncFadersFromProject();
        RefreshAllBankLabels();
        UpdateBankHighlight();
        UpdateTitle();
        SetInfo2($"プロジェクトを読み込みました: {System.IO.Path.GetFileName(dlg.FileName)}");
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

        var copy = new MenuItem { Header = "設定をコピー（全16パッド）" };
        copy.Click += (_, _) => { _bankClipboard = _project.Banks[bankIdx].Clone(); };
        cm.Items.Add(copy);

        var paste = new MenuItem { Header = "設定をペースト（全16パッド）", IsEnabled = _bankClipboard != null };
        paste.Click += (_, _) => PasteBankSettings(bankIdx);
        cm.Items.Add(paste);

        cm.IsOpen = true;
        e.Handled = true;
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
        for (int p = 0; p < BankData.PadCount; p++)
        {
            _project.Banks[bankIdx].Pads[p] = _bankClipboard.Pads[p].Clone();
            _engine.SetPadCategory(bankIdx, p, _project.Banks[bankIdx].Pads[p].Category);
        }
        _playback.LoadBank(bankIdx);
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
            _settings.Save();
            SetInfo2("設定を保存しました。");
        }
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
            for (int m = 0; m < 4; m++)
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
            for (int m = 0; m < 4; m++)
                _project.FaderMemories[f][m] = _faders[f].GetMemory(m);
    }

    private void UpdateTitle()
    {
        string dirty = _projectDirty ? " *" : "";
        string fname = _projectFilePath != null
            ? $" — {System.IO.Path.GetFileName(_projectFilePath)}"
            : " — 未保存";
        Title = $"ikePon v1.0.43{fname}{dirty}";
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
        _uiTimer.Stop();
        _engine.Dispose();
        _movieCtrl.CloseDisplay(); // MovieWindowのポジションを_settingsに保存してから
        _settings.WindowWidth  = Width;
        _settings.WindowHeight = Height;
        _settings.Save();
        _gainDb.Save();
    }
}
