#!/usr/bin/env bash
# Sypher Launcher for Linux
set -e
DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" >/dev/null 2>&1 && pwd )"
cd "$DIR"

echo "=============================================="
echo "   Sypher Messenger (Linux)                   "
echo "   Zero-Knowledge P2P Mesh Communication      "
echo "=============================================="

if ! command -v node &> /dev/null; then
    echo "[!] Node.js is not found. Please install Node.js (v20+):"
    echo "    Ubuntu/Debian: sudo apt update && sudo apt install -y nodejs npm"
    echo "    Arch Linux:    sudo pacman -S nodejs npm"
    echo "    Fedora:        sudo dnf install -y nodejs npm"
    exit 1
fi

echo "[*] Checking core build..."
if [ ! -d "packages/core/dist" ]; then
    npm run build
fi

echo "[*] Launching Sypher Desktop UI..."
npm run desktop:dev 2>/dev/null || (
    echo "[*] Desktop GUI fallback: Starting Headless Daemon & CLI..."
    cd packages/cli && node src/index.js daemon
)
