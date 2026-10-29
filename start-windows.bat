@echo off
title Sypher Messenger (Windows)
cd /d "%~dp0"

echo ==============================================
echo    Sypher Messenger (Windows)
echo    Zero-Knowledge P2P Mesh Communication
echo ==============================================

if exist "release_artifacts\Sypher.exe" (
    echo [*] Starting native standalone Sypher.exe...
    start "" "release_artifacts\Sypher.exe"
    exit /b
)

if exist "Sypher.exe" (
    echo [*] Starting native standalone Sypher.exe...
    start "" "Sypher.exe"
    exit /b
)

echo [*] Starting via Node.js Desktop Launcher...
node packages\desktop\src\launcher.js
pause
