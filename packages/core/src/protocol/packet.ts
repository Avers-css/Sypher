import crypto from 'node:crypto';

export const SYPHER_MAGIC = 0x53595048; // 'SYPH' in ASCII
export const SYPHER_VERSION = 0x01;
export const HEADER_SIZE = 128; // Fixed binary header size

export enum PacketFlags {
  NONE = 0x00,
  EPHEMERAL = 0x01,       // Do not write to offline store-and-forward queue
  ACK_REQUESTED = 0x02,   // Ask recipient to gossip an acknowledgement receipt
  RELAY_ALLOWED = 0x04,   // Intermediate peers may gossip this packet
  DIRECT = 0x08,          // Direct single-hop (LAN / BLE direct)
}

export interface SypherPacket {
  magic: number;
  version: number;
  flags: number;
  ttlHops: number;
  expiresAt: bigint;       // Unix timestamp in ms
  recipientHash: Uint8Array; // 32 bytes
  packetId: Uint8Array;    // 32 bytes
  ephemeralKey: Uint8Array;// 32 bytes
  nonce: Uint8Array;       // 12 bytes
  payloadLength: number;
  payload: Uint8Array;     // Ciphertext + MAC
}

/**
 * Creates a new SypherPacket with generated PacketID and cryptographic defaults
 */
export function createPacket(params: {
  recipientHash: Uint8Array;
  ephemeralKey: Uint8Array;
  nonce: Uint8Array;
  payload: Uint8Array;
  ttlHops?: number;
  expiresInMs?: number;
  flags?: number;
  packetId?: Uint8Array;
}): SypherPacket {
  const ttlHops = params.ttlHops ?? 16;
  const expiresInMs = params.expiresInMs ?? 24 * 60 * 60 * 1000; // default 24h
  const expiresAt = BigInt(Date.now() + expiresInMs);
  const flags = params.flags ?? (PacketFlags.RELAY_ALLOWED | PacketFlags.ACK_REQUESTED);
  const packetId = params.packetId ?? new Uint8Array(crypto.randomBytes(32));

  return {
    magic: SYPHER_MAGIC,
    version: SYPHER_VERSION,
    flags,
    ttlHops,
    expiresAt,
    recipientHash: params.recipientHash,
    packetId,
    ephemeralKey: params.ephemeralKey,
    nonce: params.nonce,
    payloadLength: params.payload.length,
    payload: params.payload,
  };
}

/**
 * Serializes a SypherPacket into an immutable binary buffer
 */
export function serializePacket(packet: SypherPacket): Uint8Array {
  const totalLength = HEADER_SIZE + packet.payload.length;
  const buffer = Buffer.alloc(totalLength);

  // 0..3: Magic
  buffer.writeUInt32BE(packet.magic, 0);

  // 4: Version
  buffer.writeUInt8(packet.version, 4);

  // 5: Flags
  buffer.writeUInt8(packet.flags, 5);

  // 6..7: TTL Hops
  buffer.writeUInt16BE(packet.ttlHops, 6);

  // 8..15: ExpiresAt (64-bit int)
  buffer.writeBigUInt64BE(packet.expiresAt, 8);

  // 16..47: RecipientHash (32 bytes)
  Buffer.from(packet.recipientHash).copy(buffer, 16, 0, 32);

  // 48..79: PacketID (32 bytes)
  Buffer.from(packet.packetId).copy(buffer, 48, 0, 32);

  // 80..111: EphemeralKey (32 bytes)
  Buffer.from(packet.ephemeralKey).copy(buffer, 80, 0, 32);

  // 112..123: Nonce (12 bytes)
  Buffer.from(packet.nonce).copy(buffer, 112, 0, 12);

  // 124..127: PayloadLength (uint32)
  buffer.writeUInt32BE(packet.payload.length, 124);

  // 128..N: Payload
  Buffer.from(packet.payload).copy(buffer, 128);

  return new Uint8Array(buffer);
}

/**
 * Deserializes raw network bytes into a verified SypherPacket
 */
export function deserializePacket(raw: Uint8Array): SypherPacket {
  if (raw.length < HEADER_SIZE) {
    throw new Error(`Packet buffer underflow: minimum header size is ${HEADER_SIZE} bytes, received ${raw.length}`);
  }

  const buffer = Buffer.from(raw.buffer, raw.byteOffset, raw.byteLength);

  const magic = buffer.readUInt32BE(0);
  if (magic !== SYPHER_MAGIC) {
    throw new Error(`Invalid protocol magic: expected 0x${SYPHER_MAGIC.toString(16)}, got 0x${magic.toString(16)}`);
  }

  const version = buffer.readUInt8(4);
  if (version !== SYPHER_VERSION) {
    throw new Error(`Unsupported protocol version: ${version}`);
  }

  const flags = buffer.readUInt8(5);
  const ttlHops = buffer.readUInt16BE(6);
  const expiresAt = buffer.readBigUInt64BE(8);

  const recipientHash = new Uint8Array(buffer.subarray(16, 48));
  const packetId = new Uint8Array(buffer.subarray(48, 80));
  const ephemeralKey = new Uint8Array(buffer.subarray(80, 112));
  const nonce = new Uint8Array(buffer.subarray(112, 124));
  const payloadLength = buffer.readUInt32BE(124);

  if (buffer.length < HEADER_SIZE + payloadLength) {
    throw new Error(`Truncated packet: expected ${HEADER_SIZE + payloadLength} bytes, have ${buffer.length}`);
  }

  const payload = new Uint8Array(buffer.subarray(128, 128 + payloadLength));

  return {
    magic,
    version,
    flags,
    ttlHops,
    expiresAt,
    recipientHash,
    packetId,
    ephemeralKey,
    nonce,
    payloadLength,
    payload,
  };
}
