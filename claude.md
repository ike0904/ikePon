・C:\Users\ike09\.claude\claude.md 初回起動時と更新あり時は必ず読むこと。
・docsフォルダに拡張子mdの仕様書がある。初回起動時と更新あり時は必ず読むこと。
・mdファイルの内容を書き換える時、元のファイルを必ず「.md.bak」で残すこと。「.md.bak」の上書きは許可する。もちろん、このmdファイルも含む。


・レターボックスが白 → v1.3.16で対処済み（ForegroundWindow._gridにCanvasで黒帯を追加）。
  最大化ON/OFFやウィンドウサイズ変更時に一瞬白が見えるが許容範囲とのこと。
  【根本原因】VideoView.Visibility=Collapsed→Visible の切り替えで D3D11 スワップチェーンが
  新規作成されるたびに未初期化領域（白）が露出する。昔は VideoView が常に Visible だったため
  スワップチェーンが維持され黒のまま安定していた。

　（池田追記）現在、レターボックスはほぼ安定しているが、ウィンドウサイズ変更時に白が少し見えてしまう。
　　現在の方法論だとこれが限界だと思うのと、イベント中にウィンドウサイズを変更することはまずないので許容できる。
　　しかし、VLC PLAYERアプリ本体のレターボックスが黒なのだから、白になってしまうという状況は納得できない。
　　必ず原因があるし、わざわざ上から黒をかぶせないと調整できないということはないはず。

・v1.3.18：VideoView を常時 Visible に固定して白フラッシュを根本解決。
  VideoView.Visibility=Collapsed をコード全体から削除。スタンバイ表示は
  ForegroundWindow の _fgGrid 内に _fgStandbyLayer（Grid, ZIndex=100）を追加して実現。
  ForegroundWindow は Topmost WPF Window のため _fgStandbyLayer が VLC 映像の前面に表示される。
  WPF 側の StandbyLayer は Loaded 前の短時間フォールバック用として残す（Loaded 後は非表示）。

・立ち上げ直後にDISPLAYがOFFになっていると、動画が再生できない → v1.3.15で対処済み。ユーザー確認済み。

・初回起動時にDISPLAYボタンを押してもウィンドウが立ち上がらなかった（2回目は正常）
  → v1.3.17で_pendingOpenをvolatile化＋ダブルチェック追加。再現性低く「2回押せばOK」で許容。
