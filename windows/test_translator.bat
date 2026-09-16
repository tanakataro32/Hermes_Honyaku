@echo off
rem 翻訳サーバーが動いているか、1文だけ投げて確認する
chcp 65001 >nul
curl -s http://127.0.0.1:8082/v1/chat/completions -H "Content-Type: application/json" -d "{\"model\":\"honyaku\",\"messages\":[{\"role\":\"system\",\"content\":\"Translate the user's English into natural Japanese. Output only the translation.\"},{\"role\":\"user\",\"content\":\"The user wants me to check the log file before editing the config.\"}],\"chat_template_kwargs\":{\"enable_thinking\":false},\"max_tokens\":200}"
echo.
pause
