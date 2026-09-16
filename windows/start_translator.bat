@echo off
rem ============================================================
rem  Hermes Honyaku 翻訳サーバー (Windows / RX 560 / Vulkan)
rem  ・llama.cpp の Windows Vulkan ビルドを windows\llama\ に展開しておく
rem  ・初回は Hugging Face から翻訳モデル (約1.1GB) を自動ダウンロードする
rem  ・GPU が複数ある (内蔵GPU + RX 560) 場合は list_devices.bat で番号を確認し、
rem    下の DEVICE を "VulkanN" に書き換える
rem ============================================================
chcp 65001 >nul
cd /d "%~dp0"

set LLAMA_DIR=%~dp0llama
set PORT=8082

rem ---- モデル (Hugging Face から自動取得。手元のファイルを使うなら MODEL_ARGS を -m "パス" に変える)
set MODEL_ARGS=-hf unsloth/Qwen3-1.7B-GGUF:Q4_K_M

rem ---- GPU 指定。空欄なら自動選択。例: set DEVICE=Vulkan0
set DEVICE=

set DEVICE_ARGS=
if not "%DEVICE%"=="" set DEVICE_ARGS=--device %DEVICE%

if not exist "%LLAMA_DIR%\llama-server.exe" (
  echo llama-server.exe が見つかりません: %LLAMA_DIR%
  echo https://github.com/ggml-org/llama.cpp/releases から llama-bXXXX-bin-win-vulkan-x64.zip を
  echo ダウンロードして、このフォルダの llama\ に展開してください。
  pause
  exit /b 1
)

echo 翻訳サーバーを起動します: http://0.0.0.0:%PORT%/v1  (Ctrl+C で終了)
"%LLAMA_DIR%\llama-server.exe" %MODEL_ARGS% %DEVICE_ARGS% ^
  -ngl 99 ^
  -c 4096 --parallel 2 ^
  -fa on -ctk q8_0 -ctv q8_0 ^
  --jinja ^
  --reasoning-budget 0 ^
  --host 0.0.0.0 --port %PORT% ^
  --alias honyaku
pause
