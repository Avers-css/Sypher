# Sypher Threat Model & Security Posture (STRIDE Analysis)

## 1. Adversary Capabilities

We evaluate Sypher against three classes of adversaries:

| Class | Profile | Capabilities |
|---|---|---|
| **Class I: Passive Network Eavesdropper** | ISPs, Wi-Fi sniffers, rogue cell towers, internet backbones | Observes ciphertext packets in transit, attempts traffic analysis, packet timing correlation. |
| **Class II: Active Malicious Peer / Carrier** | Rogue BLE relay, compromised mesh node, malicious DHT peer | Modifies packets, replays old packets, injects malformed data, attempts MITM. |
| **Class III: Physical Device Seizure** | Law enforcement, border control, lost/stolen hardware | Gains physical access to persistent storage and RAM. |

---

## 2. Mitigations & Defenses

### 2.1 Confidentiality & Integrity (Class I & II)
- **ChaCha20-Poly1305 / AES-256-GCM** ensures ciphertext cannot be read by any intermediate node.
- **Poly1305 / GCM 128-bit MAC** ensures any single-bit tampering causes instant authentication rejection.

### 2.2 Replay & Broadcast Storm Attacks (Class II)
- Every packet has a cryptographic `PacketID`.
- Every node maintains an LRU sliding-window filter of seen packet IDs. Replayed packets are discarded with zero processing overhead.

### 2.3 Traffic Analysis & Padding (Class I)
- Variable-length messages are padded to block boundaries (multiples of 128 or 256 bytes) to conceal exact plaintext length from network observers.

### 2.4 Device Seizure & Data at Rest (Class III)
- Local messages, contacts, and session keys are stored inside an encrypted vault (`EncryptedVault`) protected with PBKDF2 (100,000+ iterations) and AES-256-GCM.
- Optional **"Burn-on-Exit"** and **"Dumb PIN"** (decoy vault) wipe the memory and database upon triggering.
