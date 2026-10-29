#!/usr/bin/env bash
# Sypher Launcher for macOS (Double-Clickable in Finder)
DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" >/dev/null 2>&1 && pwd )"
cd "$DIR"

echo "=============================================="
echo "   Sypher Messenger (macOS)                   "
echo "   Zero-Knowledge P2P Mesh Communication      "
echo "=============================================="

if ! command -v node &> /dev/null; then
    echo "[!] Node.js is not detected on your Mac."
    echo "    To install, visit: https://nodejs.org or run 'brew install node'"
    echo ""
    read -p "Press Enter to exit..."
    exit 1
fi

echo "[*] Node.js runtime detected: $(node -v)"
echo "[*] Starting Sypher Mesh Node & Desktop UI..."

node packages/desktop/src/launcher.js
STATUS=$?
if [ $STATUS -ne 0 ]; then
    echo ""
    echo "[!] Sypher stopped with exit code $STATUS."
    read -p "Press Enter to close this window..."
fi
