@echo off
rem このファイルを local_settings.bat という名前でコピーして、自分の環境の値を書く
rem (local_settings.bat は git 管理外なので、更新で上書きされない)

rem GPU 番号 (list_devices.bat で確認)
set DEVICE=Vulkan1

rem 引数なしで起動したときのモデル
set DEFAULT_MODEL=tinyswallow
