・C:\Users\ike09\.claude\claude.md 初回起動時と更新あり時は必ず読むこと。


・このclaude.md内の作業記録は、mdファイルの一番後ろにつけること。
作業記録は、上に行くほど新しく、下に行くほど古くすること。（目視しやすさを重視）
また、作業記録は、バージョンの中間値インクリメントもしくは左値インクリメントで消去すること。これは全プロジェクト共通なので、共通claude.mdにメモすること。

---

●以下、検証中。今回はいじらない。

低遅延ワイヤレスで音がおかしくなる問題、直らないどころか私の環境でもおかしくなった。
一旦v1.6.2に戻して。そして、以下の他AIからの指摘を、内容の正しさを吟味してから作業して。


# 概要
WASAPI Shared 環境において、パッドのトリガー（再生開始）直後に「ブザー音（直前の波形データが高サイクルで無限ループする現象）」が発生する不具合を修正します。

# 原因分析
1. **トリガー直後の読み取りバッファ未填満**
   `PadAudioSource.cs` の `Trigger()` 実行直後、オーディオスレッドの `ReadSource()` 呼び出しタイミングとの競合や `_readPos` の境界計算により、実際に読み込めたサンプル数（`written`）が要求数（`count`）を満たせないケースが発生していました。

2. **バッファの残置と返り値の不整合（ブザー音の真因）**
   `ReadSource()` 内で `written < count`（データ不足）の際、バッファを完全に無音クリア処理しきれずに `count`（要求サンプル数）を返していました。
   結果として、オーディオカードの出力バッファに未更新（前回再生時などの残留波形）のままデータが残り、それが数ミリ秒周期で繰り返し再生されて「ブザー音」となっていました。

# 改修要件

### `PadAudioSource.cs` の修正

1. **`Read` および `ReadSource` メソッドにおける無音埋めの徹底**
   - `written < count` の場合、未書き込み領域（`offset + written` から `count - written` 分）を確実に `Array.Clear`（または `0f`）でクリアしてください。
   - トリガー直後で `_preloaded` データが 1 サンプルも読み込めなかった場合やエラー発生時は、呼び出し側バッファ全体を即座に `0f`（完全無音）で敷き詰め、安全に無音を出力するようにガードを強化してください。

2. **スレッド境界（`Trigger` 時の `_readPos` 初期化）の保護**
   - `Trigger()` 呼び出し時に `_readPos` や各種フラグを更新する際、`Read()` スレッド側で不正なインデックス参照や配列外参照が起きないよう、範囲チェック（`Math.Clamp`）およびアトミック/ロック保護を再点検・強化してください。

### `AudioEngine.cs` の確認

- `Read()` メソッド冒頭の `Array.Clear(buffer, offset, count)` が確実に実行されていること、および `_tempBuf` が未読み込み時にクリアされているか再確認してください。

# 修正対象ファイル
- `PadAudioSource.cs`
- `AudioEngine.cs`


---

## 作業記録 (v1.7.0 / 2026-07-24)

### ALL CUT が映像フェードを止めない不具合修正（MainWindow.xaml.cs）

**根本原因**: `ExecutePanic()` の早期リターン条件に `!_movieCtrl.IsFading` が含まれていなかった。
一時停止中 MOV パッドへの右クリック FadeOut → 音声は v1.6.12 修正で `Paused→Idle` 即移行、各インデックスも -1 となり ALL CUT を押しても早期リターンしてしまっていた。

**修正内容**:
- `ExecutePanic()` 早期リターン条件に `&& !_movieCtrl.IsFading` を追加（MainWindow.xaml.cs）

### フェード中に最終フレーム到達時の音声継続修正（MovieWindow.xaml.cs / MovieController.cs / MainWindow.xaml.cs）

**Bug 2a – FreezeLastFrame 中**: フェード開始時に `_currentMoviePadIndex = -1` がセットされるため、`OnVideoFreezeAtEnd()` が早期リターンし音声がカットされなかった。
**Bug 2b – Stop/Loop 中**: `OnMediaEnded()` default case にフェード終端イベントが存在せず音声継続した。

**修正内容**:
- `MovieWindow` に `VideoEndedWhileFading` イベントを追加。`OnMediaEnded` default case で `_fadeTimer != null` の場合に発火。
- `MovieController` でイベントをフォワード（購読/解除も OpenDisplay/CloseDisplay に組み込み）。
- `MainWindow` で `OnVideoFreezeAtEnd()` を修正: `_currentMoviePadIndex < 0` のとき `FindFadingMoviePad()` でフォールバック検索。
- `MainWindow` に `OnVideoEndedWhileFading()` ハンドラを追加: FadingOut の Movie パッドを `StopImmediate()`。
- `FindFadingMoviePad()` ヘルパーを追加。

### マニュアル更新（v1.6.0→v1.7.0 差分）・PDF 生成・Release publish

- 表紙・各セクションのバージョン表記を v1.7.0 に更新
- 映像ウィンドウ 16:9 自動維持の記述を追加（JA/EN）
- 連打防止時間デフォルトを 500ms → 100ms に更新
- WASAPI レイテンシ設定行を削除（設定画面テーブル）
- FreezeLastFrame の一時停止/再開挙動をマニュアルに反映（右クリックメニュー）
- v1.7.0 更新履歴を追記（JA/EN）
- `python gen_pdf.py` で PDF 生成 → dist/ に自動コピー
- `dotnet publish -c Release` でシングルファイル Release 済み（警告 0 / エラー 0）

**バージョン**: v1.6.13 → v1.7.0（Release publish 済み）
