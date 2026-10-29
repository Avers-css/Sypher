# Sypher Cryptographic Specification (CryptoSpec v1.0)

## 1. Mathematical Foundations & Primitives

Sypher strictly relies on audited, modern cryptographic primitives. Custom or unproven algorithms are forbidden.

| Purpose | Algorithm / Standard | Key / Nonce Size |
|---|---|---|
| **Identity & Authentication** | Ed25519 (Edwards-curve Digital Signature) | 256-bit Public Key, 512-bit Signature |
| **Key Agreement (Diffie-Hellman)** | X25519 (Montgomery curve ECDH) | 256-bit Scalar |
| **Symmetric Encryption & MAC** | ChaCha20-Poly1305 (RFC 8439) / AES-256-GCM | 256-bit Key, 96-bit Nonce, 128-bit MAC |
| **Key Derivation Function (KDF)** | HKDF-SHA256 (RFC 5869) | Salt: 256-bit, Info: Domain string |
| **Passphrase KDF (Vault)** | PBKDF2-HMAC-SHA256 / Argon2id | 100,000+ iterations |
| **Fingerprint Generation** | SHA-256 (RFC 6234) | 256-bit Digest (Formatted hex/Bech32) |

---

## 2. Key Derivation & Ratchet Architecture

### 2.1 Double Ratchet State Machine
The core conversation state between any two parties ($A$ and $B$) is modeled as a Double Ratchet:

1. **Root KDF Chain:**
   $$\text{RootKey}_{i+1}, \text{ChainKey}_{i+1} = \text{HKDF-SHA256}(\text{RootKey}_i, \text{DH}(\text{PrivKey}, \text{PubKey}))$$
2. **Sending & Receiving Symmetric Chains:**
   $$\text{MessageKey}_n = \text{HMAC-SHA256}(\text{ChainKey}_n, \text{0x01})$$
   $$\text{ChainKey}_{n+1} = \text{HMAC-SHA256}(\text{ChainKey}_n, \text{0x02})$$

### 2.2 Perfect Forward Secrecy (PFS)
As soon as $\text{MessageKey}_n$ encrypts or decrypts a message, the key is overwritten with zeros (`zeroize()`) in volatile memory. Compromise of long-term identity keys or future ratchet states cannot recover past plaintexts.

### 2.3 Post-Compromise Security (PCS)
If an adversary temporarily gains access to memory at step $k$, the next Diffie-Hellman step generated with fresh entropy will heal the ratchet and lock the adversary out of all subsequent messages.

---

## 3. Secure Memory Zeroization
Sensitive key material is encapsulated in typed memory buffers (`Uint8Array`) that are explicitly cleared using constant-time memory wipes when garbage collected or discarded:
```typescript
function zeroize(buffer: Uint8Array): void {
  for (let i = 0; i < buffer.length; i++) {
    buffer[i] = 0;
  }
}
```
