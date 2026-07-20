・C:\Users\ike09\.claude\claude.md 初回起動時と更新あり時は必ず読むこと。


---

●今回も特別に配布用ビルドにして。dllなどを極力まとめて、不必要なファイルは出力しない。

●低遅延ワイヤレスで音がおかしくなる問題、直らなかった。以下、他AIからの指摘。内容の正しさを吟味してから作業して。


# 概要
WASAPI Shared のレイテンシを `0` に変更した後も、一部のゲーミングヘッドセット環境（Logitech G733等の 48kHz 固定デバイス）において「再生終了・ALL CUT・ミュート操作後もブザー音（直前の波形ループ）が鳴り止まない」現象が報告されています。

原因を解析した結果、`AudioEngine.cs` 内でデバイスの `MixFormat.SampleRate`（48000Hz等）とアプリ内部フォーマット（44100Hz）の差分を吸収するために使用している `WdlResamplingSampleProvider` が原因であることが判明しました。
リアルタイムストリーミング処理において、`WdlResamplingSampleProvider` が内部バッファの計算差分（余りサンプル）を正しく破棄できず、循環バッファ上で無限ループ（デッドロック/ブザー音化）を起こしています。

Bluetooth機器や一般的なオーディオ機器で発生しないのは、OS側のサンプリングレートが 44.1kHz で動作しており、このリサンプラーを通過しないためです。

リアルタイムでの WDL リサンプラー通過を廃止することで、ブザー音の根本解決だけでなく「CPU負荷の軽減」および「音質・レスポンスの向上」を図ります。

---

# 改修要件

### 1. `AudioEngine.cs` の修正

- **`WdlResamplingSampleProvider` の完全廃止**
  `Start()` メソッド内にある `WdlResamplingSampleProvider` によるリアルタイムリサンプリング処理を削除してください。

- **`AudioEngine` のフォーマットをデバイスの `MixFormat` に合わせる**
  起動時に `MMDeviceEnumerator` からデフォルト再生デバイスの `MixFormat`（SampleRate）を取得し、`AudioEngine` 自体の再生フォーマット（`_format`）をそのサンプルレート（例: 48000Hz）で生成・初期化するように変更してください。
  これにより、`WasapiOut` とのフォーマット不一致エラーを回避しつつ、再生時のリアルタイムリサンプリング処理を完全に不要にします。

- **代替案（リアルタイム変換が必要な場合）**
  何らかの理由で `AudioEngine` 側のフォーマット変更が困難な場合は、不具合のある `WdlResamplingSampleProvider` ではなく、Windows OS標準で動作が堅牢な `MediaFoundationResampler`（または NAudio の `MediaFoundationResampler` ラッパー）を使用する構成に書き換えてください。

### 2. `PadAudioSource.cs` への影響確認・対応

- `AudioEngine` のフォーマット（`_format`）がデバイスに合わせて動的に決まる場合、音源ファイルのロード時（`Load` メソッド内の `ConvertToFormat` 等）にそのサンプルレートへ一括変換（プリロード）されるように連携してください。
- ロード時の一括リサンプリングであれば処理も安全であり、再生中のリアルタイム負荷やバッファループ不具合は一切発生しません。

---

# 修正対象ファイル
- `AudioEngine.cs`
- `PadAudioSource.cs`（必要に応じて）


---

## 作業記録 (v1.6.1 / 2026-07-19)

### 映像ウィンドウ 16:9 固定（MovieWindow.xaml.cs）
- `WM_SIZING` メッセージを `OnSourceInitialized` で HwndSource フックとして登録
- ドラッグ方向（WMSZ_*）に応じて幅 or 高さを 16:9 比率に補正
- 非クライアント領域サイズ（タイトルバー＋枠）を `Loaded` イベントでピクセル計算しキャッシュ
- 全画面時（`_isFullScreen == true`）はフックをスキップ
- 保存済みウィンドウ幅から高さも 16:9 で再計算（旧設定が非 16:9 でも起動時に修正）

### WASAPIレイテンシ固定化（AudioEngine.cs）
- `Start()` 引数廃止、`WasapiOut(Shared, 0)` に一本化（OS/ドライバデフォルト周期に委任）
- `_latencyMs` フィールド削除、Exclusiveフォールバックは 100ms 固定
- `FlushOutput()` の `Start(_latencyMs)` 呼び出しも `Start()` に変更

### WASAPIレイテンシ設定削除（AppSettings / SettingsDialog / Strings）
- `AppSettings.WasapiLatencyMs` プロパティ削除
- SettingsDialog の TbLatency UI・バリデーション・保存ロジック一式削除
- Strings.ja/en.xaml の `Str_Dlg_Settings_WasapiLatency` 削除

### MainWindow.xaml.cs
- `_engine.Start()` の引数削除
- 設定変更後の再起動判定からレイテンシ変更チェックを除去

### バージョン
v1.6.0 → v1.6.1（Debug ビルド済み、警告 0 / エラー 0）

---

## 作業記録 (v1.6.2 / 2026-07-19)

### インターロックのデフォルト値変更
- `AppSettings.InterLockMs` のデフォルトを 500 → 100 に変更
- `SettingsDialog` のリセット値も "500" → "100" に変更
- Release publish 済み（警告 0 / エラー 0）

---

## 作業記録 (v1.6.3 / 2026-07-20)

### 低遅延ワイヤレスデバイスでのブザー音修正（AudioEngine.cs）

**根本原因**: `Start()` 内の `WdlResamplingSampleProvider` がリアルタイムオーディオスレッドに乗っており、Stop/AllCut 後も resampler の内部バッファが WASAPI から pull され続けブザー音化していた。

**修正内容**:
- コンストラクタで `MMDeviceEnumerator` からデフォルトデバイスの `MixFormat.SampleRate` を取得し、`_format` をデバイスレートで初期化するよう変更
- `Start()` から `WdlResamplingSampleProvider` のリアルタイムリサンプリング処理を完全削除（`this` を直接 `WasapiOut.Init()` に渡す）
- `PadAudioSource.ConvertToFormat()` はロード時に一括リサンプリングするため変更不要（`_format.SampleRate` がデバイスレートになることで自動的に正しいレートでプリロードされる）

**バージョン**: v1.6.2 → v1.6.3（Debug ビルド済み、警告 0 / エラー 0）

