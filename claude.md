・C:\Users\ike09\.claude\claude.md 初回起動時と更新あり時は必ず読むこと。
・tmpフォルダに拡張子mdの仕様書がある。初回起動時と更新あり時は必ず読むこと。
・このmdファイルの内容を書き換える時、元のファイルを必ず「.md.bak」で残すこと。「.md.bak」の上書きは許可する。


●mp4ファイルを再生しても、ディスプレイがスタンバイ画像のまま。
v1.0.39でMediaElement IsMuted=True を追加（WASAPIとの競合対策）。
→ これで直っていない場合はデバッグログ（Debug.WriteLine [MovieWindow] プレフィックス）をVisual StudioのOutputウィンドウで確認すること。
MediaFailed が出ていればエラー内容から原因特定できる。
MediaOpened が出ていれば Visibility の問題。
どちらも出ていなければ Source セット自体の問題。

