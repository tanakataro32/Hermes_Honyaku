@echo off
rem Windows ファイアウォールで翻訳サーバーの受信ポート 8082 を許可する (管理者として実行)
netsh advfirewall firewall add rule name="Hermes Honyaku translator (llama-server 8082)" dir=in action=allow protocol=TCP localport=8082 profile=private
pause
