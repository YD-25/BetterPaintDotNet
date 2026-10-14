@echo off
chcp 65001 >nul
title LayerChannelLive 一键卸载器
setlocal EnableExtensions

set "PS1=%~dp0uninstall.ps1"
if not exist "%PS1%" (
  echo [错误] 找不到 uninstall.ps1，请确保它与本批处理文件位于同一目录下！
  call :maybe_pause
  exit /b 1
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%PS1%" %*
set "RC=%ERRORLEVEL%"

echo.
if "%RC%"=="0" (
  echo [成功] 卸载完成！
) else (
  echo [失败] 卸载遇到问题，退出码: %RC%
)

call :maybe_pause
exit /b %RC%

:maybe_pause
echo %CMDCMDLINE% | find /i "/c" >nul
if errorlevel 1 pause
exit /b 0