@echo off
chcp 65001 >nul
title 更好的Paint.NET 一键安装器
setlocal EnableExtensions

set "PS1=%~dp0install.ps1"
if not exist "%PS1%" (
  echo [错误] 找不到 install.ps1，请确保它与本批处理文件位于同一目录下！
  call :maybe_pause
  exit /b 1
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%PS1%" %*
set "RC=%ERRORLEVEL%"

echo.
if "%RC%"=="0" (
  echo [成功] 安装流程完成！
) else (
  echo [失败] 安装遇到问题，退出码: %RC%
  echo 日志文件: %TEMP%\LayerChannelLive-install.log
)

call :maybe_pause
exit /b %RC%

:maybe_pause
echo %CMDCMDLINE% | find /i "/c" >nul
if errorlevel 1 pause
exit /b 0