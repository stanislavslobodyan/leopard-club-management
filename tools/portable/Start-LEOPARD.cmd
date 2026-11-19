@echo off
setlocal
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-LEOPARD.ps1"
if errorlevel 1 (
  echo.
  echo Не вдалося запустити LEOPARD. Перевір повідомлення вище.
  pause
)
endlocal
