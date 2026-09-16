@echo off
rem 翻訳モデルの比較。各モデルを順に起動し、同じ英文を翻訳して結果を compare_result.txt に残す
rem   compare_models.bat                       → qwen3 tinyswallow gemma3 sarashina を順に試す
rem   compare_models.bat tinyswallow gemma3    → 指定したものだけ
rem 先に start_translator.bat を止めておくこと (GPU メモリを取り合うため)
chcp 65001 >nul
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0compare_models.ps1" %*
pause
