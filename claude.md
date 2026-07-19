・C:\Users\ike09\.claude\claude.md 初回起動時と更新あり時は必ず読むこと。


●映像表示ウィンドウの縦横比を、拡大縮小しても常に16:9固定にして。（全画面の時は、もちろん画面の縦横比に従う）

---

●以下、他AIからの指摘。内容の正しさを吟味してから作業して。


# 概要
C# + NAudioで開発中のポン出しアプリ「ikePon」において、特定のワイヤレスヘッドセット（Logitech G733等）環境の使用者から「ブザーのような音しか鳴らない」「再生終了やALL CUT、ミュートをしても音が鳴り止まない（アプリ終了またはOSミキサーでのミュートでのみ停止する）」という不具合報告を受けました。

原因は、WASAPI Shared（共有モード）の初期化時に明示的なレイテンシ（ミリ秒）を指定していることで、G HUB等の独自APO（サラウンド等のエフェクト処理）を持つドライバの内部バッファと同期ズレを起こし、ドライバ側でバッファの循環ハングアップ（デッドロック）が発生しているためです。

排他モードは不要であり、ポン出しとしての十分な低遅延と環境依存トラブルの根絶を両立させるため、WASAPI Sharedの初期化時にレイテンシ指定を「0（OS/ドライバのデフォルト周期に任せる）」に一本化する改修を行います。

あわせて、不要になった全体設定の「WASAPIレイテンシ」に関連するUI項目やロジックの削除・整理をお願いします。

# 改修の要件

1. **AudioEngine.cs の修正**
   - `Start(int latencyMs = 30)` メソッド内、またはWASAPI Sharedの初期化処理において、第2引数に `latencyMs` や `Math.Max(30, latencyMs)` を渡すのをやめ、固定で `0` を指定するように書き換えてください。
   - `_wasapiOut = new WasapiOut(AudioClientShareMode.Shared, 0);` に一本化します。
   - 共有モードでの不要なレイテンシ計算ロジックや、不要になったフィールド等があれば整理してください。

2. **全体設定 UI・ロジックの削除とクリーンアップ**
   - 画面（設定画面等）にある「WASAPIレイテンシ」の変更項目（下限1ms等の数値指定UI）を削除または非表示にしてください。
   - アプリの設定保存データ（ProjectDataやConfig類）から、WASAPIレイテンシに関する不要なプロパティや読み込み・保存処理、初期値設定などを適切にクリーンアップしてください。

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

