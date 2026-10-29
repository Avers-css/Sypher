<div align="center">

```
   ███████╗██╗   ██╗██████╗ ██╗  ██╗███████╗██████╗ 
   ██╔════╝╚██╗ ██╔╝██╔══██╗██║  ██║██╔════╝██╔══██╗
   ███████╗ ╚████╔╝ ██████╔╝███████║█████╗  ██████╔╝
   ╚════██║  ╚██╔╝  ██╔═══╝ ██╔══██║██╔══╝  ██╔══██╗
   ███████║   ██║   ██║     ██║  ██║███████╗██║  ██║
   ╚══════╝   ╚═╝   ╚═╝     ╚═╝  ╚═╝╚══════╝╚═╝  ╚═╝
```

# Sypher

### Zero-Knowledge • Peer-to-Peer • Mesh-Capable • Serverless Messenger
**Target Platforms: Windows • Linux • macOS • Android**

[![Build Status](https://img.shields.io/badge/build-passing-brightgreen?style=for-the-badge&logo=github-actions)](https://github.com)
[![Cryptography](https://img.shields.io/badge/E2EE-Double%20Ratchet-blue?style=for-the-badge)](docs/CRYPTO_SPEC.md)
[![Ciphers](https://img.shields.io/badge/Cipher-ChaCha20--Poly1305-orange?style=for-the-badge)](docs/CRYPTO_SPEC.md)
[![Zero Servers](https://img.shields.io/badge/Hosting%20Cost-%240.00%20(Serverless)-purple?style=for-the-badge)](docs/ARCHITECTURE.md)
[![License](https://img.shields.io/badge/License-Apache%202.0-blueviolet?style=for-the-badge)](LICENSE)

</div>

---

## 🌟 Overview

**Sypher** is a decentralized, sovereign communication network built from the ground up to eradicate all reliance on centralized servers, phone numbers, and cloud hosting infrastructure.

It bridges the best of two worlds:
1. **Under the Hood:** An untraceable, mathematically proven cryptographic fortress implementing **Signal-grade Double Ratchet (X25519 + ChaCha20-Poly1305)**, **Epidemic Gossip Mesh Routing (DTN)**, and **Local Sneakernet Delivery (BLE / UDP Multicast)**.
2. **On the Surface:** A refined, hardware-accelerated **Telegram-class dark glassmorphic interface** designed for effortless everyday use.

---

## 🛡️ Core Pillars

- **Zero Central Servers ($0 Hosting Cost):** Operates natively across local ad-hoc Wi-Fi/Ethernet (`239.255.77.88:7788`), Bluetooth Low Energy Mesh, or distributed P2P relays.
- **Pure Anonymity:** No SIM card, phone number, email, or personal identifiers. Your identity is a 256-bit cryptographic public key (`syph1...`).
- **Military-Grade Cryptography:**
  - **Ed25519:** Digital signatures and non-repudiation.
  - **X25519 (ECDH):** Ephemeral Diffie-Hellman key agreement.
  - **Double Ratchet:** Per-message ratcheting providing **Perfect Forward Secrecy (PFS)** and **Post-Compromise Security (PCS)**.
  - **ChaCha20-Poly1305:** Authenticated symmetric encryption with 128-bit MAC verification.
- **Offline Mesh Routing (Sneakernet / Epidemic Gossip):** Messages for out-of-range contacts are safely ferried in encrypted form through the pockets of passing nodes and delivered automatically upon proximity.
- **Encrypted Vault & Emergency Burn:** All local chat history, keys, and ratchet states are guarded with PBKDF2 (100,000 iterations) + AES-256-GCM. An Emergency Burn trigger instantly overwrites local memory and disk with random noise.

---

## 📐 Architecture

Sypher adopts the enterprise **Protocol Core + Thin Platform Targets** architecture used by Telegram (`tdlib`), Signal (`libsignal`), and Matrix:

```
┌────────────────────────────────────────────────────────────────────────┐
│                        SYPHER PROTOCOL ENGINE                          │
│                                                                        │
│  - Cryptography Core (X25519, Ed25519, ChaCha20-Poly1305, Ratchet)     │
│  - SYPH Binary Wire Protocol (Header, Flags, TTL, Anti-Replay)         │
│  - Mesh Engine (Epidemic Router, LRU Deduplicator, Store-and-Forward)  │
│  - Local Encrypted Vault (PBKDF2 + AES-256-GCM + Emergency Burn)       │
└──────────────────────────────────┬─────────────────────────────────────┘
                                   │
               ┌───────────────────┼───────────────────┐
               ▼                   ▼                   ▼
    ┌──────────────────────┐ ┌──────────┐ ┌──────────────────────┐
    │ @sypher/desktop      │ │ @sypher/ │ │ @sypher/android      │
    │ - Windows (.exe)     │ │   cli    │ │ - Foreground Service │
    │ - Linux (AppImage)   │ │ - Server │ │ - BLE Mesh Radio     │
    │ - macOS (.dmg)       │ │   Daemon │ │ - Biometric Auth     │
    └──────────────────────┘ └──────────┘ └──────────────────────┘
```

Detailed architectural blueprints and mathematical specifications are documented in:
* [Architecture Blueprint](docs/ARCHITECTURE.md)
* [Cryptographic Specification](docs/CRYPTO_SPEC.md)
* [Wire Protocol (SYPH-v1)](docs/WIRE_PROTOCOL.md)
* [Threat Model (STRIDE)](docs/THREAT_MODEL.md)

---

## 📂 Repository Structure

```
Sypher/
├── .github/workflows/          # CI/CD workflows for testing and multi-platform releases
├── docs/                       # Architecture, Cryptography, Wire Protocol, Threat Model
├── packages/
│   ├── core/                   # @sypher/core: Headless cryptographic & mesh routing engine
│   │   ├── src/
│   │   │   ├── crypto/         # Keys, X25519, ChaCha20-Poly1305, Double Ratchet, Zeroize
│   │   │   ├── protocol/       # Binary serializer/deserializer for SYPH packets, BLE framing
│   │   │   ├── mesh/           # Epidemic router, deduplication cache, store-and-forward pool
│   │   │   ├── storage/        # Encrypted Vault (PBKDF2 + AES-256-GCM)
│   │   │   └── transport/      # UDP Multicast & transport abstractions
│   │   └── tests/              # 14 automated unit & integration tests
│   ├── desktop/                # @sypher/desktop: Telegram-class desktop client
│   │   ├── src/main/           # Electron main process, tray, hardware acceleration
│   │   ├── src/preload/        # Context-isolated security bridge
│   │   └── src/renderer/       # Glassmorphism UI (HTML5, Modern CSS, ES Modules)
│   ├── android/                # @sypher/android: Native Android BLE Foreground Service
│   └── cli/                    # @sypher/cli: Terminal client & headless Linux node runner
├── package.json                # Root workspace configuration
├── LICENSE                     # Apache 2.0 License
└── README.md                   # This documentation
```

---

## 🚀 Quickstart & Development

### 1. Requirements
* **Node.js**: v22.x or v24.x (LTS recommended)
* **npm**: v10+

### 2. Install & Compile Core
```bash
# Clone the repository
git clone https://github.com/your-username/Sypher.git
cd Sypher

# Install dependencies across all workspaces
npm install

# Compile the TypeScript core
npm run build
```

### 3. Run Cryptographic & Protocol Tests
```bash
npm test
```
*Executes all 14 unit and integration tests covering ECDH, Double Ratchet, packet framing, and encrypted vault operations.*

### 4. Launch Desktop Client (Windows, Linux, macOS)
```bash
npm run desktop:dev
```

### 5. Launch Headless Mesh Daemon (CLI)
```bash
npm run cli:start -- daemon
```

---

## 📦 Building Platform Binaries

### Windows (x64 / ARM64)
```bash
npm run desktop:build -- --win
```
*Generates standalone installer (`.exe`) and portable executable in `packages/desktop/release/`.*

### Linux (AppImage / DEB)
```bash
npm run desktop:build -- --linux
```
*Generates `.AppImage` and Debian `.deb` packages.*

### macOS (Universal DMG)
```bash
npm run desktop:build -- --mac
```
*Generates Apple Silicon and Intel `.dmg` disk image.*

### Android (APK)
```bash
cd packages/android/android
./gradlew assembleRelease
```
*Generates signed Android APK in `packages/android/android/app/build/outputs/apk/release/`.*

---

## 🔒 Security Audit & Responsible Disclosure

Sypher was built strictly under a **Zero-Trust** philosophy. If you discover a cryptographic defect or security vulnerability, please do not file a public GitHub issue. Send an encrypted notification to the security team or open a confidential security advisory on GitHub.

---

## 📄 License

Licensed under the **Apache License, Version 2.0**. See the [LICENSE](LICENSE) file for terms and conditions.
