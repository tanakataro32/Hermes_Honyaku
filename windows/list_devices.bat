@echo off
rem Vulkan から見える GPU の一覧を表示する。RX 560 の番号 (VulkanN) を start_translator.bat の DEVICE に書く
chcp 65001 >nul
cd /d "%~dp0"
"%~dp0llama\llama-server.exe" --list-devices
pause
