# 翻訳モデルの比較スクリプト (compare_models.bat から呼ばれる)
# 各モデルを 8083 番で順に起動し、同じ英文 3 つを同じ指示で翻訳して、訳文と速度を表示する。
param(
    [string[]]$Models = @("qwen3", "tinyswallow", "gemma3", "sarashina")
)
$ErrorActionPreference = "Continue"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
# 画面の出力をすべて記録する (問題の切り分け用)
try { Start-Transcript -Path (Join-Path $root "compare_transcript.txt") -Force | Out-Null } catch {}

# Windows PowerShell 標準の Web コマンドはプロキシ自動検出で数分固まることがあるので、
# プロキシを使わない HttpClient を直接使う
Add-Type -AssemblyName System.Net.Http
$handler = New-Object System.Net.Http.HttpClientHandler
$handler.UseProxy = $false
$client = New-Object System.Net.Http.HttpClient($handler)
$client.Timeout = [TimeSpan]::FromSeconds(180)
$exe = Join-Path $root "llama\llama-server.exe"
$port = 8083
$resultFile = Join-Path $root "compare_result.txt"

# start_translator.bat の DEVICE と同じ値を使う (空欄なら自動)
$device = ""
$bat = Get-Content (Join-Path $root "start_translator.bat") -Encoding UTF8
foreach ($line in $bat) { if ($line -match '^set DEVICE=(\S+)') { $device = $Matches[1] } }

$map = @{
    qwen3       = "unsloth/Qwen3-1.7B-GGUF:Q4_K_M"
    tinyswallow = "bartowski/TinySwallow-1.5B-Instruct-GGUF:Q4_K_M"
    gemma3      = "unsloth/gemma-3-1b-it-GGUF:Q4_K_M"
    sarashina   = "mmnga/sarashina2.2-1b-instruct-v0.1-gguf:Q4_K_M"
}

# 中継サーバー (hermes_honyaku.py) と同じ指示と応答例
$system = "You are a professional English-to-Japanese translator. The input is an AI agent's internal reasoning (its private notes while working). Translate it into natural Japanese written as a first-person monologue (e.g. 「〜しよう」「〜だ」「〜かもしれない」「〜する必要がある」). Output ONLY the Japanese translation. Never reply in English. Never add explanations, notes, or the original text. Keep code, shell commands, file paths, URLs, and identifiers exactly as they are. If the input is already Japanese, output it unchanged."
$fewshot = @(
    @{ role = "user"; content = "Let me check the config first. The file may be large, so I should be careful." },
    @{ role = "assistant"; content = "まず設定を確認しよう。ファイルが大きいかもしれないので注意が必要だ。" },
    @{ role = "user"; content = "The user wants me to check disk usage. I'll run ``df -h`` and then look at /var/log for errors." },
    @{ role = "assistant"; content = "ユーザーはディスク使用量の確認を求めている。``df -h`` を実行してから、/var/log のエラーを見よう。" },
    @{ role = "user"; content = "Since they're independent, I can batch these calls." },
    @{ role = "assistant"; content = "これらは互いに独立しているので、まとめて呼び出せる。" }
)
$sentences = @(
    "The output got truncated partway through. I fetched 100 repositories, but only 6 are shown in the first 4000 characters, so I'll fetch the complete list with a smaller page size.",
    "Regarding recent logs: I need to figure out which logs to check. ""Recent logs"" is ambiguous, but the most standard would be the system logs, so I can check ``journalctl --since ""24 hours ago"" -p err`` or /var/log/syslog.",
    "The user asked me to check the contents of their GitHub repository. I've retrieved the list, and since they are Japanese, I should present it concisely in Japanese with a table."
)

function Invoke-Json($url, $bodyObj) {
    $json = $bodyObj | ConvertTo-Json -Depth 8 -Compress
    $content = New-Object System.Net.Http.StringContent($json, [System.Text.Encoding]::UTF8, "application/json")
    $resp = $client.PostAsync($url, $content).GetAwaiter().GetResult()
    $bytes = $resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
    $text = [System.Text.Encoding]::UTF8.GetString($bytes)
    if (-not $resp.IsSuccessStatusCode) { throw ("HTTP {0}: {1}" -f [int]$resp.StatusCode, $text) }
    return $text | ConvertFrom-Json
}

function Test-Health($url) {
    try {
        $cts = New-Object System.Threading.CancellationTokenSource(3000)
        $resp = $client.GetAsync($url, $cts.Token).GetAwaiter().GetResult()
        return $resp.IsSuccessStatusCode
    } catch { return $false }
}

if (-not (Test-Path $exe)) { Write-Host "llama-server.exe が見つかりません: $exe"; exit 1 }
"===== 翻訳モデル比較  $(Get-Date -Format 'yyyy-MM-dd HH:mm') =====" | Out-File $resultFile -Encoding utf8

foreach ($m in $Models) {
    $hf = $map[$m]; if (-not $hf) { $hf = $m }
    $srvArgs = @("-hf", $hf, "-ngl", "99", "-c", "4096", "--parallel", "1", "-fa", "on", "-ctk", "q8_0", "-ctv", "q8_0",
              "--jinja", "--reasoning-budget", "0", "--host", "127.0.0.1", "--port", $port, "--alias", "honyaku")
    if ($device) { $srvArgs += @("--device", $device) }
    $log = Join-Path $root "compare_$m.log"
    $logOut = Join-Path $root "compare_$m.out.log"
    Write-Host ""
    Write-Host "================ $m  ($hf) ================" -ForegroundColor Cyan
    Write-Host "起動中... (初回はダウンロードに数分かかります。ログ: $log)"
    $t0 = Get-Date
    $p = Start-Process -FilePath $exe -ArgumentList $srvArgs -PassThru -NoNewWindow -RedirectStandardError $log -RedirectStandardOutput $logOut
    $ok = $false
    for ($i = 0; $i -lt 600; $i++) {
        Start-Sleep -Seconds 2
        if ($p.HasExited) { break }
        if (Test-Health "http://127.0.0.1:$port/health") { $ok = $true; break }
    }
    if (-not $ok) {
        Write-Host "起動に失敗しました。$log の末尾:" -ForegroundColor Red
        Get-Content $log -Tail 15
        "[$m] 起動失敗 ($hf)" | Out-File $resultFile -Append -Encoding utf8
        if (-not $p.HasExited) { Stop-Process -Id $p.Id -Force }
        continue
    }
    $load = [math]::Round(((Get-Date) - $t0).TotalSeconds, 1)
    Write-Host "起動完了 ($load 秒)"
    "[$m] $hf  起動 $load 秒" | Out-File $resultFile -Append -Encoding utf8
    # 1 回目はウォームアップ
    try { $null = Invoke-Json "http://127.0.0.1:$port/v1/chat/completions" @{ model = "honyaku"; messages = @(@{ role = "user"; content = "Hello." }); max_tokens = 8; chat_template_kwargs = @{ enable_thinking = $false } } } catch {}
    $n = 0
    foreach ($s in $sentences) {
        $n++
        $msgs = @(@{ role = "system"; content = $system }) + $fewshot + @(@{ role = "user"; content = $s })
        $body = @{ model = "honyaku"; messages = $msgs; temperature = 0.2; max_tokens = 300; stream = $false; chat_template_kwargs = @{ enable_thinking = $false } }
        $t1 = Get-Date
        try {
            $r = Invoke-Json "http://127.0.0.1:$port/v1/chat/completions" $body
            $sec = [math]::Round(((Get-Date) - $t1).TotalSeconds, 1)
            $out = ($r.choices[0].message.content -replace '(?s)<think>.*?</think>\s*', '').Trim()
            $tps = ""
            if ($r.timings) { $tps = "  " + [math]::Round($r.timings.predicted_per_second, 1) + " tok/s" }
            Write-Host ("[{0}] 英: {1}" -f $n, $s) -ForegroundColor DarkGray
            Write-Host ("     日: {0}" -f $out) -ForegroundColor White
            Write-Host ("     {0} 秒{1}" -f $sec, $tps) -ForegroundColor DarkGray
            "  [$n] 英: $s" | Out-File $resultFile -Append -Encoding utf8
            "      日: $out" | Out-File $resultFile -Append -Encoding utf8
            "      $sec 秒$tps" | Out-File $resultFile -Append -Encoding utf8
        } catch {
            Write-Host ("[{0}] エラー: {1}" -f $n, $_.Exception.Message) -ForegroundColor Red
            "  [$n] エラー: $($_.Exception.Message)" | Out-File $resultFile -Append -Encoding utf8
        }
    }
    Stop-Process -Id $p.Id -Force
    Start-Sleep -Seconds 2
}
Write-Host ""
Write-Host "結果を $resultFile に保存しました。" -ForegroundColor Green
try { Stop-Transcript | Out-Null } catch {}
