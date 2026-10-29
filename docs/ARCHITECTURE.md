# Sypher System Architecture

## 1. High-Level Overview

Sypher is an asymmetric, zero-knowledge, peer-to-peer (P2P) secure communications platform designed to operate with equal reliability across:
- **Global Public IP Networks** (via DHT, WebSocket relays, and Tor v3 Onion hidden services).
- **Ad-Hoc Local Mesh Networks** (via UDP multicast, Wi-Fi Direct, and Bluetooth Low Energy Mesh).

The system enforces end-to-end encryption (E2EE) at the protocol core: **no central server or intermediary node ever possesses private key material, message keys, or unencrypted plaintext.**

```
+-------------------------------------------------------------------------+
|                        SYPHER CORE ENGINE (@sypher/core)                |
|                                                                         |
|  +--------------------+  +----------------------+  +-----------------+  |
|  | Cryptographic Core |  | SYPH Wire Protocol   |  | Mesh & Routing  |  |
|  | - Ed25519 / X25519 |  | - Binary Framing     |  | - Gossip Router |  |
|  | - ChaCha20-Poly1305|  | - Magic (0x53595048) |  | - LRU Deduplic. |  |
|  | - Double Ratchet   |  | - Anti-Replay Guard  |  | - Offline Queue |  |
|  +--------------------+  +----------------------+  +-----------------+  |
|                                                                         |
|  +-------------------------------------------------------------------+  |
|  | Transport Abstraction Layer (ITransport)                          |  |
|  |   - Local Multicast (UDP) | BLE Mesh Adapter | WebSocket Bridge   |  |
|  +-------------------------------------------------------------------+  |
|                                                                         |
|  +-------------------------------------------------------------------+  |
|  | Encrypted Vault Layer (PBKDF2 + AES-256-GCM Session/State Store)  |  |
|  +-------------------------------------------------------------------+  |
+------------------------------------+------------------------------------+
                                     |
              +----------------------+----------------------+
              |                                             |
              v                                             v
+-------------------------------+             +-------------------------------+
| @sypher/desktop               |             | @sypher/android               |
| - Windows (x64 / ARM64)       |             | - Native AndroidManifest      |
| - Linux (AppImage / DEB)      |             | - BLE Background Radio Worker |
| - macOS (Intel / Apple Silicon|             | - Foreground Mesh Service     |
| - Telegram-Grade UI & Glass   |             | - Biometric Local Vault Auth  |
+-------------------------------+             +-------------------------------+
```

---

## 2. Component Breakdown

### 2.1 `@sypher/core` (Protocol Engine)
- **Zero UI Dependency:** Completely decoupled from visual frameworks. Runs in any modern ECMAScript / Node / Native environment.
- **Constant-Time Crypto:** Protects against side-channel and timing attacks.
- **Double Ratchet State Machine:** Manages DH ratchet steps and symmetric chain progressions, ensuring **Perfect Forward Secrecy (PFS)** and **Post-Compromise Security (PCS)**.
- **Binary Wire Protocol (`SYPH`):** Compact, non-JSON wire framing to optimize bandwidth over BLE and high-latency links.

### 2.2 `@sypher/desktop` (Desktop Application)
- **Targets:** Windows 10/11 (`.exe`), Linux (`.AppImage`, `.deb`), macOS (`.dmg`).
- **User Interface:** Telegram-class dark aesthetics, smooth 60fps animations, glassmorphic layout, conversation bubbles, message delivery status (Sent / Relayed / Delivered / Decrypted).
- **Security Isolation:** Electron context-isolated bridge (`contextIsolation: true`, `nodeIntegration: false`, strict Content Security Policy).

### 2.3 `@sypher/android` (Mobile Mesh Daemon)
- **BLE Mesh Integration:** Uses Android Bluetooth Low Energy Advertising and Scanning with low-duty cycling (<1% battery per 24 hours).
- **Foreground Mesh Service:** Ensures persistent relay capability even when screen is locked.

### 2.4 `@sypher/cli` (Headless Daemon / TUI)
- Command-line utility for running headless nodes on servers, single-board computers (Raspberry Pi), or remote Linux environments.
