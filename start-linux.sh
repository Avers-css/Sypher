#!/usr/bin/env bash
# Sypher Launcher for Linux
set -e
DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" >/dev/null 2>&1 && pwd )"
cd "$DIR"

echo "=============================================="
echo "   Sypher Messenger (Linux)                   "
echo "   Zero-Knowledge P2P Mesh Communication      "
echo "=============================================="

# Detect node or nodejs executable
NODE_BIN=""
if command -v node &> /dev/null; then
    NODE_BIN="node"
elif command -v nodejs &> /dev/null; then
    NODE_BIN="nodejs"
else
    echo "[!] Node.js is not found. Please install Node.js (v20+):"
    echo "    Ubuntu/Debian: sudo apt update && sudo apt install -y nodejs npm"
    echo "    Arch Linux:    sudo pacman -S nodejs npm"
    echo "    Fedora:        sudo dnf install -y nodejs npm"
    exit 1
fi

echo "[*] Node.js runtime detected: $($NODE_BIN -v)"
echo "[*] Starting Sypher Universal Mesh Node..."
$NODE_BIN packages/desktop/src/launcher.js
