#!/usr/bin/env bash
# Sypher Launcher for macOS (Double-Clickable in Finder)
DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" >/dev/null 2>&1 && pwd )"
cd "$DIR"

echo "=============================================="
echo "   Sypher Messenger (macOS)                   "
echo "   Zero-Knowledge P2P Mesh Communication      "
echo "=============================================="

if ! command -v node &> /dev/null; then
    echo "[!] Node.js not detected. Please install Node.js (https://nodejs.org or 'brew install node')."
    read -p "Press enter to exit..."
    exit 1
fi

if [ ! -d "packages/core/dist" ]; then
    npm run build
fi

echo "[*] Launching Sypher on macOS..."
npm run desktop:dev
