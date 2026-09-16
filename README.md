# Hermes Honyaku

Hermes Agent がローカル LLM (llama-server) で考えている内容 (reasoning / thinking) を、
リアルタイムに抜き出して日本語に翻訳し、ブラウザで眺めるためのツールです。

```
[llmsv: Ubuntu 24.04 / V100]
  Hermes Agent ──> hermes_honyaku.py (中継 :8081) ──> llama-server (:8080, 本体モデル)
                          │
                          ├─ 思考を文単位で切り出し ──(LAN)──> [Windows 機] llama-server Vulkan (:8082, RX 560, 翻訳専用)
                          └─ ブラウザ表示 (:8765)   ← Windows 機のブラウザで http://192.168.1.50:8765/ を開く
```

- 中継サーバーは llama-server が返す `reasoning_content` (および本文中の `<think>` タグ) をストリーミングのまま横取りします。Hermes には何も手を加えず、接続先 URL を変えるだけです。
- 本体モデルの llama-server は router モード・API キー・モデル名などそのままで動きます (中継はヘッダーと `model` をそのまま素通しします)。
- 翻訳は V100 に触らず、Windows 機の RX 560 (2GB) に載せた小型モデル (Qwen3-1.7B) で行います。
- Python 3.10 以上の標準ライブラリだけで動き、pip は不要です。

## 1. Windows 機側 (翻訳サーバー) の準備

1. RX 560 を取り付け、AMD の通常ドライバー (Adrenalin) が当たっていることを確認します。
2. [llama.cpp の Releases](https://github.com/ggml-org/llama.cpp/releases) から
   `llama-bXXXX-bin-win-vulkan-x64.zip` をダウンロードし、このリポジトリの `windows\llama\` に展開します
   (`windows\llama\llama-server.exe` ができる形)。
3. `windows\list_devices.bat` を実行し、`Vulkan0` / `Vulkan1` のどちらが RX 560 かを確認します。
   内蔵 GPU の側が選ばれるのを防ぐため、`windows\start_translator.bat` の `set DEVICE=` に RX 560 の番号 (例 `Vulkan0`) を書きます。
4. `windows\firewall_allow.bat` を「管理者として実行」し、受信ポート 8082 を許可します。
5. `windows\start_translator.bat` を実行します。初回は Hugging Face から翻訳モデル (Qwen3-1.7B Q4_K_M、約 1.1GB) を自動ダウンロードします。
   `server is listening on http://0.0.0.0:8082` が出れば起動完了です。このウィンドウは開いたままにします。
6. `windows\test_translator.bat` で 1 文翻訳して、日本語が返ることを確認します。
7. ルーターの DHCP 予約で Windows 機の IP を `192.168.1.8` に固定します (変わると llmsv 側の設定と食い違うため)。

VRAM の内訳の目安: モデル本体 約 1.1GB + KV キャッシュ (4096 トークン・q8_0) 約 0.25GB + 作業領域。2GB にぎりぎり収まる構成です。
起動時にメモリ確保で落ちる場合は `start_translator.bat` の `-c 4096` を `-c 3072` に下げてください。

## 2. llmsv 側 (中継サーバー) の準備

```bash
cd ~
git clone https://github.com/tanakataro32/Hermes_Honyaku.git hermes_honyaku
cd hermes_honyaku
nano config.ini          # [translator] url の IP が Windows 機と合っているか確認
python3 hermes_honyaku.py   # まず手動で起動して動作確認
```

起動ログに `proxy : http://127.0.0.1:8081/v1 -> http://127.0.0.1:8080` と `ui : http://0.0.0.0:8765/` が出ます。

ブラウザ表示を LAN から開くために ufw を 1 ポート開けます (04章で ufw を有効にしている場合)。

```bash
sudo ufw allow 8765/tcp    # Hermes Honyaku 表示画面
```

Windows 機のブラウザで http://192.168.1.50:8765/ を開き、ヘッダーの「翻訳」のランプが緑ならば Windows 機の翻訳サーバーにも届いています
(赤の場合は Windows 側の起動・ファイアウォール・IP を確認)。Tailscale 経由なら `http://<llmsv の Tailscale IP>:8765/` でも開けます。

常駐化する場合:

```bash
sudo cp hermes-honyaku.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now hermes-honyaku
journalctl -u hermes-honyaku -f
```

## 3. Hermes Agent の接続先を切り替える

`~/.hermes/config.yaml` の `model.base_url` を llama-server 直結から中継サーバーに変えます。API キーとモデル名はそのままです。

```yaml
model:
  default: qwen3.8-27b            # そのまま
  provider: custom
  base_url: http://127.0.0.1:8081/v1   # ← 8080 から 8081 に変更
  api_key: "(llama-server の API キー。そのまま)"
```

`hermes model` の対話設定や `hermes config set model.base_url http://127.0.0.1:8081/v1` でも構いません。
元に戻したいときは 8080 に戻すだけです。中継サーバーを止めている間に 8081 のままだと Hermes が接続エラーになるので注意してください。

## 4. 画面の見方

- ターンごとに 1 枠。見出しは「通し番号 / 時刻 / モデル名 / 直前のメッセージの要約 (user: … や tool(名前): …)」。
  Hermes はツールを呼ぶたびに LLM に問い合わせるので、1 つの指示で複数ターンになります。
- 左列 **Thinking (原文)**: モデルの思考がトークン単位でそのまま流れます。
- 右列 **日本語**: 文の区切りごとに翻訳結果が並びます。翻訳待ちの間は黄色のバーで原文が出て、訳文が届くと緑に変わります。
  翻訳が追いつかないときも左列は止まらず、右列が順に追いつきます。
- そのターンで Hermes が呼んだツール (実行したコマンドなど) は、右列に 🔧 付きで 1 行ずつ出ます。
- Hermes の最終回答は右列の一番下に「回答」として Markdown で描画されます (表・箇条書き・コードブロックなど)。回答は元から日本語なので翻訳は通しません。不要なら「回答も表示」のチェックを外します。
- モデルが日本語で考えている文は翻訳せずそのまま右列に出します。翻訳モデルが英語のまま返してきた文は黄色のまま原文を表示します。
- 上にスクロールすると自動スクロールが止まり、右下の「最新へ」で再開します。「新しいターンを上に」を付けると最新のターンが常に一番上に来ます。

## 4b. Open WebUI も同じ画面で見る

Open WebUI (Docker) の接続先も中継サーバーに向ければ、同じ画面に Open WebUI のターンも流れます。
ターンの見出しに発信元 (Hermes / Open WebUI) のラベルが付き、ヘッダーの「すべての発信元」で絞り込めます。

1. **中継サーバーを Docker からも届く口で待ち受ける**。`config.ini` の `[proxy]` を `listen_host = 0.0.0.0` に変えて再起動します。

   ```bash
   nano ~/hermes_honyaku/config.ini      # listen_host = 0.0.0.0
   sudo systemctl restart hermes-honyaku
   ```

2. **ufw で Docker からの 8081 を許可**します (LAN 全体には開けず、Docker のブリッジだけ)。

   ```bash
   sudo ufw allow in on docker0 to any port 8081 proto tcp
   ```

3. **Open WebUI の接続先を変更**します。ブラウザで Open WebUI → 管理者パネル → 設定 → 接続 → OpenAI API の URL を
   `http://host.docker.internal:8080/v1` から `http://host.docker.internal:8081/v1` に変えて保存します。API キーはそのままです。
   右側の「確認」ボタンで接続テストが通り、モデル一覧に `qwen3.8-27b` などが出れば完了です。

4. Open WebUI で何か聞くと、画面に「Open WebUI」ラベル付きのターンが流れます。

Open WebUI はチャットのたびにタイトル生成・タグ生成・フォローアップ提案などの小さな要求もモデルに送ります。
これらは「背景タスク」として検出し、既定では隠しています (ヘッダーの「背景タスクを隠す」で切り替え)。
Hermes が会話タイトルを付けるための要求も同じ扱いです。

元に戻すときは Open WebUI の URL を 8080 に戻すだけです。

## 5. 設定 (config.ini)

| セクション | 項目 | 意味 |
|---|---|---|
| proxy | listen_host / listen_port | Hermes がつなぐ先。同じマシンなら 127.0.0.1:8081 のまま |
| proxy | upstream | 本体の llama-server |
| ui | listen_port | ブラウザ表示のポート (既定 8765) |
| translator | engine | `openai` (Windows 機の llama-server など OpenAI 互換) / `deepl` / `none` |
| translator | url / model | 翻訳サーバーの URL とモデル名 (`honyaku` は start_translator.bat の `--alias`) |
| translator | workers | 同時に翻訳する文の数。翻訳側の `--parallel` と同じ値にする |
| segment | max_chars / min_chars | 文の最大長・区切らない最小長 |
| segment | idle_flush_sec | 思考がこの秒数止まったら、区切りが無くても溜まった分を翻訳に回す |
| log | dir | 原文と訳文を JSONL で残すフォルダ (日付ごと 1 ファイル)。空欄で無効 |
| sources | (IP の前方一致) | 接続元 IP からターンの発信元ラベルを決める。`127.0.0.1 = Hermes`、`172. = Open WebUI` など |

DeepL を使う場合は `engine = deepl` にして `deepl_key` を設定します (Free プランは月 50 万文字まで。思考ログは量が多いので上限に注意)。

## 6. 困ったとき

| 症状 | 見るところ |
|---|---|
| 画面に何も出ない | Hermes の `base_url` が 8081 になっているか。`journalctl -u hermes-honyaku` に `POST /v1/chat/completions` が出ているか |
| 思考が出ず回答だけ出る | 本体 llama-server が `--reasoning-format none` になっていないか (既定 auto のままなら `reasoning_content` で届く)。Hermes 側で思考を切っていないか |
| 翻訳ランプが赤 | Windows 機で start_translator.bat が動いているか、firewall_allow.bat を実行したか、IP が 192.168.1.8 か。llmsv から `curl http://192.168.1.8:8082/health` で疎通確認 |
| 訳文が遅れて溜まる | `workers` と翻訳側 `--parallel` を増やす。またはモデルを Qwen3-0.6B に落とす (`-hf unsloth/Qwen3-0.6B-GGUF:Q4_K_M`) |
| Hermes がエラーになる | 中継を止めているときは base_url を 8080 に戻す。中継のログに `upstream error` が出ていれば本体 llama-server 側の問題 |

## 7. 動作確認 (開発用)

本体 llama-server なしでも、OpenAI 互換の SSE を返すモックを `upstream` に指定すれば動きます。
中継は Hermes が `stream: true` でも `false` でも動くように、上流には常にストリーミングで要求し、
非ストリーミングで来た場合は完全な応答 (tool_calls・usage を含む) に組み立て直して返します。
