・C:\Users\ike09\.claude\claude.md 初回起動時と更新あり時は必ず読むこと。
・docsフォルダに拡張子mdの仕様書がある。初回起動時と更新あり時は必ず読むこと。
・mdファイルの内容を書き換える時、元のファイルを必ず「.md.bak」で残すこと。「.md.bak」の上書きは許可する。もちろん、このmdファイルも含む。

・アプリ立ち上げ直後、DISPLAYをONにすると、画面全体で「白→黒」のフェードアウトが発生する。

・フェードアウト中はキー操作を受け付けないという仕様を撤廃する。先ほどのバージョンアップの仕様と相いれないため。

---

## v1.6.0 リリース済み（2026-07-14）

前回公開バージョン：v1.3.0
今回公開バージョン：v1.6.0（v1.5.x は内部バージョンのため非公開扱い）

### 主な変更内容

- 映像ウィンドウのレターボックス・ピラーボックス白フラッシュを根本修正（VideoView 常時表示化）
- DISPLAY ON 時に映像が白くフラッシュする問題を修正
  - `ShowStandby(immediate: true)` で VLC 初期化中の白を防止
  - FG ウィンドウ初期 Background を Black にして HideFgStandby で Transparent に戻す方式
- ループ再生・音声同期の安定性向上
- DISPLAY OFF 状態で動画パッドをトリガーした際の音声即時再生を修正
- カットアウト実行時は連打防止（インターロック）の対象外に変更
- DISPLAY は毎回 OFF 状態で起動するよう変更（前回状態の引き継ぎを廃止）
- アプリ終了確認をポップアップからインフォメーションエリア表示に変更
- 二重起動防止機能を追加
- バンク詳細設定ダイアログのエラーを修正
- 映像フェードアウト中に DISPLAY / FULL SCR を操作すると映像が止まらなくなる問題を修正
  - `CloseDisplay()` で `_window.StopVideo()` を先に呼ぶ
  - `ToggleFullScreen()` で `IsFading == true` なら `StopVideo()` を先に呼ぶ
- DISPLAY OFF 中にフェードアウト完了 → DISPLAY ON した際に映像が止まらなくなる問題を修正
  - `ResumeMovieIfPlaying()` で `FadingOut` 状態のパッドはスキップするよう変更
- ALL FADE または PAUSE 中にパッドをタップした際、有効状態を即座に解除するよう変更
  - `wasActive` の判定を `Playing || Paused`（`FadingOut` を除外）に統一
  - ALL FADE / PAUSE 両方のハンドラを統合
- DISPLAY ボタンの操作後グレーアウト時間を 3 秒から 1 秒に短縮
