・C:\Users\ike09\.claude\claude.md 初回起動時と更新あり時は必ず読むこと。
・docsフォルダに拡張子mdの仕様書がある。初回起動時と更新あり時は必ず読むこと。
・mdファイルの内容を書き換える時、元のファイルを必ず「.md.bak」で残すこと。「.md.bak」の上書きは許可する。もちろん、このmdファイルも含む。


・v1.5.5：DISPLAY ON 時の白→黒フェード解消。ShowStandby(immediate:true) で初期スタンバイを即時表示。
  フェードアウト中のキー操作ブロックを撤廃。IsFading チェックを削除し、FadingOut 状態のタップは新規再生として扱う。
  （wasActive = Playing || Paused のみ。FadingOut は !wasActive 扱いで isNewMoviePlay=true になる）

・v1.5.6：DISPLAY ON 時の白フラッシュ対策。
  CacheFgWin() で _fgWin.Background = Black に設定（_fgStandbyLayer 追加前の 1 フレーム白が透けるのを防ぐ）。
  HideFgStandby() で _fgWin.Background = Transparent に戻す（動画表示時に Black のままだと映像が隠れるため）。
  【根本原因】v1.3.18 で VideoView 常時 Visible 化以降、VLC D3D11 HWND（初期化前=白）が常に存在するようになった。
  それ以前は VideoView を Collapsed にしていたため HWND が存在せず白は出なかった。

・v1.5.7：映像フェードアウト中に DISPLAY/FULL SCR を操作すると映像が止まらなくなる問題を修正。
  【原因】_fadeOverlay は MovieWindow とは別の Topmost Window のため、DISPLAY を閉じても破棄されない。
  FULL SCR 切り替え時はオーバーレイが旧サイズのまま残り、フルスクリーン映像がオーバーレイ外に露出する。
  【修正】CloseDisplay() で Close 前に StopVideo() を呼び確実にオーバーレイを破棄。
  ToggleFullScreen() でフェード中なら先に StopVideo() してから SetFullScreen を呼ぶ。
  音声は NAudio 管理のため StopVideo() の影響を受けずそのままフェードアウトを継続する。

・v1.5.8：DISP OFF 中に映像フェードアウト→フェード完了前に DISP ON すると映像が再起動する問題を修正。
  【原因】ResumeMovieIfPlaying() が FadingOut 状態のパッドをスキップしていなかった。
  Idle のみスキップしていたため、フェード中の Movie パッドが「再生中」と判定され映像が再起動していた。
  【修正】FadingOut もスキップ対象に追加（Idle || FadingOut → continue）。

