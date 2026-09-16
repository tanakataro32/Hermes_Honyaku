@echo off
rem ============================================================
rem  Hermes Honyaku 翻訳サーバー (Windows / RX 560 / Vulkan)
rem
rem  使い方:
rem    start_translator.bat              → DEFAULT_MODEL で起動
rem    start_translator.bat tinyswallow  → モデルを指定して起動
rem
rem  モデル名 (2GB の GPU に収まるもの):
rem    qwen3        Qwen3-1.7B (unsloth, Q4_K_M 約1.1GB)              汎用
rem    tinyswallow  TinySwallow-1.5B-Instruct (Sakana AI, 約1.0GB)     日本語特化
rem    gemma3       Gemma 3 1B it (Google, 約0.8GB)                   多言語・指示に忠実
rem    sarashina    Sarashina2.2-1B-instruct (SB Intuitions, 約0.9GB)  日本語ネイティブ
rem  それ以外の文字列は Hugging Face の "リポジトリ:量子化" か、手元の .gguf のパスとして扱う
rem
rem  ・llama.cpp の Windows Vulkan ビルドを windows\llama\ に展開しておく
rem  ・初回はモデルを Hugging Face から自動ダウンロードする
rem  ・GPU が複数ある場合は list_devices.bat で番号を確認し、DEVICE を "VulkanN" にする
rem ============================================================
chcp 65001 >nul
cd /d "%~dp0"

set LLAMA_DIR=%~dp0llama
set PORT=8082

rem ---- 既定のモデル (引数なしで起動したとき)
set DEFAULT_MODEL=qwen3

rem ---- GPU 指定。空欄なら自動選択。例: set DEVICE=Vulkan1
set DEVICE=

rem ------------------------------------------------------------
set MODEL=%~1
if "%MODEL%"=="" set MODEL=%DEFAULT_MODEL%

set HF=
if /i "%MODEL%"=="qwen3"       set HF=unsloth/Qwen3-1.7B-GGUF:Q4_K_M
if /i "%MODEL%"=="tinyswallow" set HF=bartowski/TinySwallow-1.5B-Instruct-GGUF:Q4_K_M
if /i "%MODEL%"=="gemma3"      set HF=unsloth/gemma-3-1b-it-GGUF:Q4_K_M
if /i "%MODEL%"=="sarashina"   set HF=mmnga/sarashina2.2-1b-instruct-v0.1-gguf:Q4_K_M

if defined HF (
  set MODEL_ARGS=-hf %HF%
) else if exist "%MODEL%" (
  set MODEL_ARGS=-m "%MODEL%"
) else (
  set MODEL_ARGS=-hf %MODEL%
)

set DEVICE_ARGS=
if not "%DEVICE%"=="" set DEVICE_ARGS=--device %DEVICE%

if not exist "%LLAMA_DIR%\llama-server.exe" (
  echo llama-server.exe が見つかりません: %LLAMA_DIR%
  echo https://github.com/ggml-org/llama.cpp/releases から llama-bXXXX-bin-win-vulkan-x64.zip を
  echo ダウンロードして、このフォルダの llama\ に展開してください。
  pause
  exit /b 1
)

echo モデル: %MODEL%  (%MODEL_ARGS%)
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
