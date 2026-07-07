using System.Windows.Threading;
using NAudio.Midi;

namespace ikePon.Controller;

/// <summary>
/// MIDI入力デバイスの監視とメッセージのディスパッチを担当する。
/// </summary>
public class MidiController : IDisposable
{
    // ── イベント ──────────────────────────────────────────────
    /// <summary>PADトリガー (padIndex: 0-15)</summary>
    public event Action<int>? PadTriggered;
    /// <summary>ALL CUT</summary>
    public event Action? AllCutTriggered;
    /// <summary>ALL FADE</summary>
    public event Action? AllFadeTriggered;
    /// <summary>PAUSE</summary>
    public event Action? PauseTriggered;
    /// <summary>全画面トグル</summary>
    public event Action? FullScreenTriggered;
    /// <summary>DISPLAY表示切り替え</summary>
    public event Action? DisplayTriggered;
    /// <summary>BANK切り替え (bankIndex: 0-7)</summary>
    public event Action<int>? BankTriggered;
    /// <summary>フェーダーMU (faderIndex: 0=MOVIE/1=BGM/2=SE/3=MASTER)</summary>
    public event Action<int>? MuTriggered;
    /// <summary>フェーダーメモリ (faderIndex, slot: 0-2)</summary>
    public event Action<int, int>? MemTriggered;
    /// <summary>フェーダーCC (faderIndex, value: 0-127)</summary>
    public event Action<int, int>? FaderCCReceived;

    // ── 内部 ─────────────────────────────────────────────────
    private MidiIn? _midiIn;
    private readonly Dispatcher _dispatcher;
    private bool _disposed;

    // ── NOTE マッピング ───────────────────────────────────────
    // PAD: 36-51 → padIndex 0-15
    private const int PadBase   = 36;
    private const int PadEnd    = 51;
    // Global/Bank: 52-67
    private const int NoteAllCut    = 52;
    private const int NoteAllFade   = 53;
    private const int NotePause     = 54;
    private const int NoteFullScr   = 56;
    private const int NoteDisplay   = 57;
    // BANK E/F/G/H: 60-63, BANK A/B/C/D: 64-67
    private const int BankBase  = 60; // 60→E(4), 61→F(5), 62→G(6), 63→H(7), 64→A(0), 65→B(1), 66→C(2), 67→D(3)
    private const int BankEnd   = 67;
    // Mixer MU/Memory: 68-83
    // 68-71: MU (MOVIE/BGM/SE/MASTER)
    // 72-75: M3, 76-79: M2, 80-83: M1
    private const int MixerBase = 68;
    private const int MixerEnd  = 83;
    // CC フェーダー
    private const int CcMovie  = 16;
    private const int CcBgm    = 17;
    private const int CcSe     = 18;
    private const int CcMaster = 19;

    public MidiController(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    // ── デバイス列挙 ─────────────────────────────────────────

    /// <summary>現在接続されているMIDI入力デバイス名の一覧を返す。</summary>
    public static IReadOnlyList<string> GetDeviceNames()
    {
        var list = new List<string>();
        for (int i = 0; i < MidiIn.NumberOfDevices; i++)
            list.Add(MidiIn.DeviceInfo(i).ProductName);
        return list;
    }

    // ── デバイス選択 / 切断 ───────────────────────────────────

    /// <summary>デバイス名でリスニング開始。空文字列または未発見なら無効化。</summary>
    public void SetDevice(string deviceName)
    {
        StopDevice();
        if (string.IsNullOrEmpty(deviceName)) return;

        int index = -1;
        for (int i = 0; i < MidiIn.NumberOfDevices; i++)
        {
            if (MidiIn.DeviceInfo(i).ProductName == deviceName)
            { index = i; break; }
        }
        if (index < 0) return;

        try
        {
            _midiIn = new MidiIn(index);
            _midiIn.MessageReceived += OnMessageReceived;
            _midiIn.ErrorReceived   += OnErrorReceived;
            _midiIn.Start();
        }
        catch
        {
            _midiIn?.Dispose();
            _midiIn = null;
        }
    }

    public void StopDevice()
    {
        if (_midiIn == null) return;
        try
        {
            _midiIn.Stop();
            _midiIn.MessageReceived -= OnMessageReceived;
            _midiIn.ErrorReceived   -= OnErrorReceived;
            _midiIn.Dispose();
        }
        catch { }
        _midiIn = null;
    }

    // ── メッセージ受信 ────────────────────────────────────────

    private void OnMessageReceived(object? sender, MidiInMessageEventArgs e)
    {
        var msg = e.MidiEvent;
        _dispatcher.BeginInvoke(() => Dispatch(msg));
    }

    private static void OnErrorReceived(object? sender, MidiInMessageEventArgs e) { }

    private void Dispatch(MidiEvent evt)
    {
        if (evt is NoteOnEvent noteOn && noteOn.Velocity > 0)
        {
            DispatchNote(noteOn.NoteNumber);
        }
        else if (evt is ControlChangeEvent cc)
        {
            DispatchCC((int)cc.Controller, cc.ControllerValue);
        }
    }

    private void DispatchNote(int note)
    {
        if (note >= PadBase && note <= PadEnd)
        {
            // ノート36=底行、ノート48=上行。上行がPAD0-3なので行を反転する
            int n = note - PadBase;               // 0-15
            int padIndex = (3 - n / 4) * 4 + n % 4;
            PadTriggered?.Invoke(padIndex);
            return;
        }

        switch (note)
        {
            case NoteAllCut:  AllCutTriggered?.Invoke();  return;
            case NoteAllFade: AllFadeTriggered?.Invoke(); return;
            case NotePause:   PauseTriggered?.Invoke();   return;
            case NoteFullScr: FullScreenTriggered?.Invoke(); return;
            case NoteDisplay: DisplayTriggered?.Invoke(); return;
        }

        if (note >= BankBase && note <= BankEnd)
        {
            // 60-63 → bank index 4-7 (E/F/G/H), 64-67 → bank index 0-3 (A/B/C/D)
            int bankIndex = note < 64 ? (note - BankBase + 4) : (note - 64);
            BankTriggered?.Invoke(bankIndex);
            return;
        }

        if (note >= MixerBase && note <= MixerEnd)
        {
            int offset     = note - MixerBase; // 0-15
            int row        = offset / 4;       // 0=MU, 1=M3, 2=M2, 3=M1
            int faderIndex = offset % 4;       // 0=MOVIE, 1=BGM, 2=SE, 3=MASTER
            if (row == 0)
            {
                MuTriggered?.Invoke(faderIndex);
            }
            else
            {
                int slot = 3 - row; // row1→slot2(M3), row2→slot1(M2), row3→slot0(M1)
                MemTriggered?.Invoke(faderIndex, slot);
            }
        }
    }

    private void DispatchCC(int controller, int value)
    {
        int faderIndex = controller switch
        {
            CcMovie  => 0,
            CcBgm    => 1,
            CcSe     => 2,
            CcMaster => 3,
            _        => -1
        };
        if (faderIndex >= 0)
            FaderCCReceived?.Invoke(faderIndex, value);
    }

    // ── IDisposable ───────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopDevice();
    }
}
