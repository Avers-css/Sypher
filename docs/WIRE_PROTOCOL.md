# Sypher Wire Protocol Specification (SYPH-Wire v1)

## 1. Frame Layout

All packets exchanged over the network (whether via Bluetooth BLE, local UDP broadcast, or TCP/WebSocket relays) conform to the deterministic binary layout below. Multi-byte integers are encoded in **Big-Endian (Network Byte Order)**.

```
 0                   1                   2                   3
 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                     Magic: 'SYPH' (0x53595048)                |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|  Version (1)  |   Flags (1)   |           TTL_Hops (2)        |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                                                               |
+                       ExpiresAt (8 Bytes)                     +
|                                                               |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                                                               |
+                   RecipientHash (32 Bytes)                    +
|                                                               |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                                                               |
+                      PacketID (32 Bytes)                      +
|                                                               |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                                                               |
+                  EphemeralKey (32 Bytes)                      +
|                                                               |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                                                               |
+                       Nonce (12 Bytes)                        +
|                                                               |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                       PayloadLength (4 Bytes)                 |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
|                     Ciphertext + Poly1305 MAC                 |
|                            (Variable)                         |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
```

## 2. Field Definitions

1. **Magic (4 Bytes):** Fixed signature `0x53 0x59 0x50 0x48`. Packets without this header are instantly dropped.
2. **Version (1 Byte):** Wire protocol version (`0x01`).
3. **Flags (1 Byte):**
   - Bit 0 (`0x01`): `EPHEMERAL` (Do not store in persistent offline queue).
   - Bit 1 (`0x02`): `ACK_REQUESTED` (Recipient should reply with receipt).
   - Bit 2 (`0x04`): `RELAY_ALLOWED` (Peer is permitted to gossip this packet).
   - Bit 3 (`0x08`): `DIRECT` (Packet intended for single-hop neighbor).
4. **TTL_Hops (2 Bytes):** Decremented at each relay hop. When reaching 0, packet is discarded. Maximum: 32.
5. **ExpiresAt (8 Bytes):** 64-bit Unix millisecond timestamp. Once `CurrentTime > ExpiresAt`, packet is pruned.
6. **RecipientHash (32 Bytes):** SHA-256 digest of recipient's public key. For public broadcast announcements, this field is set to all `0xFF`.
7. **PacketID (32 Bytes):** Random 256-bit entropy or hash used by the deduplication engine.
8. **EphemeralKey (32 Bytes):** Sender's ephemeral X25519 public key.
9. **Nonce (12 Bytes):** 96-bit cryptographic nonce for authenticated encryption.
10. **PayloadLength (4 Bytes):** Length of the following ciphertext and MAC.
11. **Payload (Variable):** Authenticated ciphertext.
