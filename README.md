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
[![License](https://img.shields.io/badge/License-MIT-green?style=for-the-badge)](LICENSE)

[ 🇷🇺 **Документация на русском языке (README.ru.md)** ](README.ru.md) • [ **Download Binaries (.exe, .apk)** ](https://github.com/Avers-css/Sypher/releases)

</div>

---

## 🇷🇺 Краткое описание на русском

**Sypher (Сайфер)** — полностью анонимный P2P-мессенджер со сквозным шифрованием (Double Ratchet) и работой в ячеистых Mesh-сетях.
- **Работает без интернета:** сообщения могут передаваться локально по Wi-Fi Direct и через прохожих по Bluetooth (BLE).
- **Без серверов и без номеров телефонов:** 100% анонимность, $0 затрат на хостинг.
- **Интерфейс уровня Telegram:** аппаратный темный Glassmorphism UI.
- Полная документация на русском: **[README.ru.md](README.ru.md)**.
- Готовые сборки для установки (.exe, .apk): вкладка **[Releases](https://github.com/Avers-css/Sypher/releases)**.

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

## 📦 Where to download binaries (.exe, .apk)?

Compiled release binaries are published in the **[GitHub Releases](https://github.com/Avers-css/Sypher/releases)** tab:
* **Windows:** `Sypher-Setup-1.0.0.exe` / `Sypher-Portable.exe`
* **Linux:** `Sypher-1.0.0.AppImage` / `.deb`
* **macOS:** `Sypher-1.0.0.dmg`
* **Android:** `Sypher-1.0.0.apk`

---

## 🚀 Quickstart & Development

```bash
git clone https://github.com/Avers-css/Sypher.git
cd Sypher
npm install
npm run build
npm test
npm run desktop:dev
```

---

## 📄 License

Licensed under the **Apache License, Version 2.0**. See the [LICENSE](LICENSE) file for terms and conditions.
