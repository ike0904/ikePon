using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ikePon.Audio;
using ikePon.Controller;
using ikePon.Model;
using ikePon.UI.Controls;

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
    private ProjectData _project;
    private string? _projectFilePath;
    private bool _projectDirty;

    private readonly PadButton[] _padButtons = new PadButton[BankData.PadCount];
    private readonly Button[] _bankButtons = new Button[ProjectData.BankCount];
    private readonly VFaderControl[] _faders = new VFaderControl[4];

    private readonly DispatcherTimer _uiTimer;
    private ModifierState _modifier = ModifierState.None;

    // バンク確認中フラグ
    private bool _pendingBankConfirm;

    private static readonly SolidColorBrush BrushBankNormal   = new(Color.FromRgb(0x30, 0x30, 0x30));
    private static readonly SolidColorBrush BrushBankSelected = new(Color.FromRgb(0x1C, 0x44, 0x6E));
    private static readonly SolidColorBrush BrushBankBorderN  = new(Color.FromRgb(0x55, 0x55, 0x55));
    private static readonly SolidColorBrush BrushBankBorderS  = new(Color.FromRgb(0x3A, 0x9F, 0xFF));
    private static readonly SolidColorBrush BrushInfoWarnBg   = new(Color.FromRgb(0x44, 0x22, 0x00));
    private static readonly SolidColorBrush BrushInfoWarnText = new(Color.FromRgb(0xFF, 0xDD, 0x00));
    private static readonly SolidColorBrush BrushInfoNormal   = new(Color.FromRgb(0x88, 0x88, 0x88));

    public MainWindow()
    {
        _settings = AppSettings.Load();
        _gainDb = FileGainDatabase.Load();
        _engine = new AudioEngine();
        _playback = new PlaybackController(_engine, _settings, _gainDb);
        _bankManager = new BankManager(_playback);
        _keyMapper = new KeyboardMapper();
        _panic = new PanicController(_playback);
        _project = new ProjectData();

        InitializeComponent();
        BuildPadGrid();
        BuildBankGrid();
        BuildMixerGrid();
        WireEvents();

        _playback.SetProject(_project);
        _engine.Start(_settings.WasapiLatencyMs);
        _engine.PaSeparate = _settings.PaSeparateMode;
        MenuPaSeparate.IsChecked = _settings.PaSeparateMode;

        _uiTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _uiTimer.Tick += UiTimer_Tick;
        _uiTimer.Start();

        UpdateBankHighlight();
        UpdateTitle();
        SetInfo2("プロジェクト未読み込み。ファイルメニューから開くか、パッドを右クリックしてファイルを読み込んでください。");
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
            pad.MouseLeftButtonDown += (_, e) =>
            {
                bool shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
                bool ctrl  = Keyboard.IsKeyDown(Key.LeftCtrl)  || Keyboard.IsKeyDown(Key.RightCtrl);
                _playback.TriggerPad(captured, shift, ctrl);
                e.Handled = true;
            };
            pad.MouseRightButtonUp += (_, e) => PadRightClick(captured, e);

            _padButtons[i] = pad;
            PadGrid.Children.Add(pad);
        }
    }

    private static readonly string[] BankNames = ["A", "B", "C", "D", "E", "F", "G", "H"];

    private void BuildBankGrid()
    {
        for (int i = 0; i < ProjectData.BankCount; i++)
        {
            int captured = i;

            // バンクボタン内レイアウト: 中央にバンク名、右下にキーバッジ
            var content = new Grid { Margin = new Thickness(4) };
            content.Children.Add(new TextBlock
            {
                Text = $"Bank {BankNames[i]}",
                FontSize = 12, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA))
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
                    FontSize = 9, FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77))
                }
            };
            content.Children.Add(badge);

            var btn = new Button
            {
                Content = content,
                Margin = new Thickness(2),
                Cursor = Cursors.Hand,
                Background = BrushBankNormal,
                BorderBrush = BrushBankBorderN,
                Tag = new object?[] { content.Children[0] as TextBlock, badge }
            };
            btn.Style = CreateBankButtonStyle();
            btn.Click += (_, _) => _bankManager.RequestSwitch(captured);

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
        bd.SetValue(Border.BorderThicknessProperty, new Thickness(1.5));
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Stretch);
        bd.AppendChild(cp);
        tpl.VisualTree = bd;
        style.Setters.Add(new Setter(Control.BackgroundProperty, BrushBankNormal));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, BrushBankBorderN));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA))));
        style.Setters.Add(new Setter(Control.TemplateProperty, tpl));
        return style;
    }

    private void BuildMixerGrid()
    {
        string[] labels = ["MOVIE", "BGM", "SE", "MASTER"];
        for (int i = 0; i < 4; i++)
        {
            var fader = new VFaderControl { Label = labels[i] };
            fader.Value = 1.0;
            int captured = i;
            fader.VolumeChanged += (_, v) => OnFaderChanged(captured, v);
            fader.MemoryRecall += (_, args) => OnMemoryRecall(captured, args.slot, args.quick);

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
        var settings = _project.Banks[_playback.ActiveBank];
        for (int i = 0; i < BankData.PadCount; i++)
        {
            var state = _playback.GetPadState(i);
            var pos   = _playback.GetPadPosition(i);
            var pad   = _playback.GetPadSettings(i);
            _padButtons[i].UpdateState(state, pos, pad, _modifier);
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

        // パニック
        if (e.Key == Key.Escape)
        {
            _panic.Trigger();
            e.Handled = true;
            return;
        }

        // パッドキー
        var padIdx = _keyMapper.GetPadIndex(e.Key);
        if (padIdx.HasValue)
        {
            bool shift = _modifier == ModifierState.Shift;
            bool ctrl  = _modifier == ModifierState.Ctrl;
            _playback.TriggerPad(padIdx.Value, shift, ctrl);
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
            var fadeOut = new MenuItem { Header = $"フェードアウト ({_settings.LongFadeDuration:F1}秒)" };
            fadeOut.Click += (_, _) => _playback.TriggerPad(padIndex, fadeOut: true);
            var stopNow = new MenuItem { Header = $"即座に停止 ({_settings.ShortFadeDuration:F1}秒)" };
            stopNow.Click += (_, _) => _playback.TriggerPad(padIndex, stopImmediate: true);
            cm.Items.Add(fadeOut);
            cm.Items.Add(stopNow);
        }
        else
        {
            var load = new MenuItem { Header = "ファイルを読み込み..." };
            load.Click += (_, _) => OpenFileForPad(padIndex);
            cm.Items.Add(load);
        }

        cm.IsOpen = true;
        e.Handled = true;
    }

    private void OpenFileForPad(int padIndex)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"パッド {padIndex + 1} のファイルを選択",
            Filter = "音声・動画ファイル|*.mp3;*.wav;*.flac;*.ogg;*.aac;*.mp4;*.mov;*.mkv|すべてのファイル|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        var pad = _project.Banks[_playback.ActiveBank].Pads[padIndex];
        pad.FilePath = dlg.FileName;
        _playback.LoadBank(_playback.ActiveBank);
        MarkDirty();
        SetInfo2($"Pad {padIndex + 1}: {System.IO.Path.GetFileName(dlg.FileName)} を読み込み中...");
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
        // TODO: フェードアニメーション実装（現在は即時移動）
        _faders[faderIndex].Value = mem.Value;
        OnFaderChanged(faderIndex, mem.Value);
    }

    // ------------------------------------------------------------------
    // パニックボタン
    // ------------------------------------------------------------------
    private void PanicButton_Click(object sender, RoutedEventArgs e) => _panic.Trigger();

    // ------------------------------------------------------------------
    // バンクハイライト更新
    // ------------------------------------------------------------------
    private void UpdateBankHighlight()
    {
        int active = _playback.ActiveBank;
        for (int i = 0; i < ProjectData.BankCount; i++)
        {
            bool sel = i == active;
            _bankButtons[i].Background  = sel ? BrushBankSelected : BrushBankNormal;
            _bankButtons[i].BorderBrush = sel ? BrushBankBorderS  : BrushBankBorderN;

            // ボタン内 TextBlock と badge の色更新
            if (_bankButtons[i].Content is Grid g)
            {
                if (g.Children[0] is TextBlock tb)
                    tb.Foreground = new SolidColorBrush(sel ? Colors.White : Color.FromRgb(0xAA, 0xAA, 0xAA));
                if (g.Children[1] is Border badge)
                {
                    badge.BorderBrush = new SolidColorBrush(sel
                        ? Color.FromRgb(0x3A, 0x9F, 0xFF)
                        : Color.FromRgb(0x4A, 0x4A, 0x4A));
                    if (badge.Child is TextBlock badgeTb)
                        badgeTb.Foreground = new SolidColorBrush(sel
                            ? Color.FromRgb(0xAA, 0xCC, 0xFF)
                            : Color.FromRgb(0x77, 0x77, 0x77));
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
    private void BankYesBtn_Click(object sender, RoutedEventArgs e) => _bankManager.Confirm();
    private void BankNoBtn_Click(object sender, RoutedEventArgs e)  => _bankManager.Cancel();

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

    private void Menu_PaSeparate(object sender, RoutedEventArgs e)
    {
        _engine.PaSeparate = MenuPaSeparate.IsChecked;
        _settings.PaSeparateMode = MenuPaSeparate.IsChecked;
        SetInfo2(MenuPaSeparate.IsChecked ? "PAセパレートモード ON (L=MOVIE+BGM / R=SE)" : "PAセパレートモード OFF");
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
        Title = $"ikePon v1.0.0{fname}{dirty}";
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
        _settings.Save();
        _gainDb.Save();
    }
}
