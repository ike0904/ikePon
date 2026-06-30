●すべての「即座に停止」「即座に移動」のフェードアウトタイムを、0.5秒から0.2秒に短縮。
→効いてるかどうか不明。少なくとも、MIXERのM1ボタンによる移動はもっと時間がかかっている。

●有効バンクと、MIXERの有効M1～4ボタン。現在は枠が青になっているけど、黄色にして。鳴動中のキーパッドと同じ色。


●白・グレー系の色を以下のようにして。（無指定は現状維持）

・白（現在のPANICの文字色と同じ色）：PANIC、インフォメーションウィンドウ（切り替え警告時は黄のまま）、キーパッドの名前、使用中バンク、フェーダーの枠と模様、M1～M4のボタンと現在のフェーダー位置が同じ時の文字フォント

・明るいグレー（現在の指定外Bankの文字色と同じ色）：ショートカットのフォント、ショートカットの枠、指定外Bankの文字色、KEYPAD,BANK,MIXERの各説明、MIXER内の「MOVIE,BGM,SE,MASTER」、MIXERの目盛り（0も青じゃなくてこの色）


●ショートカットの文字サイズを1.5倍にして。また、その際に「ESC」と「PANIC」の文字とかぶってしまうので、PANICをやや左寄せにして。
　また、すべてのショートカットの色を、状態にかかわらず統一。枠・文字ともに「使われていないBANKのフォント」と同じにして。

●「KEYPAD」「BGM」「SE」「BANK」「MIXER」、ミキサーの目盛り、キーパッド内の「BGM,SE,MOVIE」表記が小さすぎる。 「準備完了」と同じ大きさにして。

●MIXERの最大値を「+10」にして。

●キーパッド内の説明フォント、大きさを「PANIC」と同じにして。

●パッドにWAVを読み込んだとき、インフォメーションに「～.wavを読み込み中…」と出続けている。読み込み完了してる？してるなら書き換えて。


・SHIFTを押したときのパッドの色がわり、仕様変更。
　現在再生されているパッドだけ色を変える。また、現在登録されているミキサーM1～M4も色を変える。
→●CTRLを押したときの色変わりも同様にして。



●パッド右クリック　詳細設定画面
　全体的に文字が小さい。何種類かある大きさを統一して、メイン画面の「Bank A」と同じ大きさにして。


音を再生したら、短い音のループになってしまっている。PANICも効かない。確認・修正して。
→直っていない。PANICとは関係なく、音を鳴らすルーチンに根本的に問題がありそう。おそらく、すべてのファイルで起きる。tmp/err.mp4 にキャプチャ動画あり。
→●●●かわっていない。とにかくこれを直して。直らないと話にならない。

---以下、別のAIの意見

大変失礼いたしました。提示いただいた現在のコードを細部まで再検証したところ、**まだノイズやブザー音、あるいは異常な挙動を引き起こす「致命的なミキシングのバグ」が1箇所残っている**のを発見しました。

原因は、`AudioEngine.Read`（[source: 11]）のループ内における **`_tempBuf` のクリア漏れ**です。

---

## 🛑 原因：`_tempBuf` の過去データ残留による波形汚染

`AudioEngine.Read` の以下のループ処理に問題があります。

```csharp
for (int pad = 0; pad < PadCount; pad++)
{
    var src = _sources[bank, pad];
    if (src.State == PadPlayState.Idle) continue;

    // 👈 ここで _tempBuf がクリアされていない！
    src.Read(_tempBuf, 0, count); 

```

### 何が起きているか

1. `_tempBuf` はクラス内で1つしか用意されていない**共有配列**です。


2. 例えば「パッド0」が音声を再生すると、`_tempBuf` にその波形データが書き込まれ、マスターバッファに加算されます。


3. 次のループで「パッド1」の処理に移った際、**`_tempBuf` の中にはまだ「パッド0」の波形データが丸ごと残っています**。
4. もし「パッド1」がストリーミング再生（`_streamProvider.Read`）のデコーダー warmup などの理由で、要求された `count` よりも少ないサンプル数しか読み込めなかった場合（`totalRead < count`）、`PadAudioSource` 側で部分的に `Array.Clear` は行われますが、NAudioの内部デコーダーやリサンプラー（`WdlResamplingSampleProvider`）の実装によっては、**引数で渡されたバッファの残りの領域にゴミデータ（パッド0の波形）が残っていることで演算が破綻し、バリバリとしたノイズや超高速ループによるブザー音を発生させる直接の原因**になります。



複数のパッドを同時に鳴らしたとき、あるいは同じパッドを連打したときに、過去のバッファの残骸が次の処理に引き継がれてしまうため、オーディオが正常に合成されません。

---

## 🛠️ 修正コード

修正は非常にシンプルです。`AudioEngine.Read` 内のループの先頭で、**毎回必ず `_tempBuf` をゼロクリア**してください。

`AudioEngine.cs` の `Read` メソッドを以下のように差し替えてください。

```csharp
    // ------------------------------------------------------------------
    // ISampleProvider.Read — オーディオスレッドから呼ばれる（GC負荷ゼロ）
    // ------------------------------------------------------------------
    public int Read(float[] buffer, int offset, int count)
    {
        // 出力先バッファをクリア
        Array.Clear(buffer, offset, count);

        if (count > _tempBuf.Length)
            count = _tempBuf.Length;

        int bank = _activeBank;
        float mstr = _masterVol;
        bool separate = _paSeparate;

        for (int pad = 0; pad < PadCount; pad++)
        {
            var src = _sources[bank, pad];
            if (src.State == PadPlayState.Idle) continue;

            var cat = _padCategories[bank, pad];
            float catVol = cat switch
            {
                AudioCategory.BGM   => _bgmVol,
                AudioCategory.SE    => _seVol,
                _                   => _movieVol
            };

            // ⚠️【重要】次のパッドを読み込む前に、共有作業バッファを必ず完全にクリアする
            Array.Clear(_tempBuf, 0, count);

            src.Read(_tempBuf, 0, count);
            float gain = catVol * mstr;

            if (!separate)
            {
                for (int i = 0; i < count; i++)
                    buffer[offset + i] += _tempBuf[i] * gain;
            }
            else
            {
                // PAセパレート: L=BGM+MOVIE, R=SE
                if (cat == AudioCategory.SE)
                {
                    for (int i = 0; i < count - 1; i += 2)
                    {
                        float mono = (_tempBuf[i] + _tempBuf[i + 1]) * 0.5f * gain;
                        buffer[offset + i + 1] += mono;  // R のみ
                    }
                }
                else
                {
                    for (int i = 0; i < count - 1; i += 2)
                    {
                        float mono = (_tempBuf[i] + _tempBuf[i + 1]) * 0.5f * gain;
                        buffer[offset + i] += mono;       // L のみ
                    }
                }
            }
        }

        return count;
    }

```

この各パッドごとの `Array.Clear(_tempBuf, 0, count);` が入ることで、それぞれの音声ソースが完全に独立したクリーンなバッファ上でデコード・計算されるようになり、混線によるグリッチやフリーズが解消されます。一度こちらで試してみてください。
