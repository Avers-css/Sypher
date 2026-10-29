import test from 'node:test';
import assert from 'node:assert';
import {
  createPacket,
  serializePacket,
  deserializePacket,
  SYPHER_MAGIC,
  PacketFlags,
} from '../dist/protocol/packet.js';
import { chunkPacketForBLE, BLEChunkReassembler } from '../dist/protocol/framing.js';

test('Packet binary serialization and deserialization preserves all fields', () => {
  const recipientHash = new Uint8Array(32).fill(0xaa);
  const ephemeralKey = new Uint8Array(32).fill(0xbb);
  const nonce = new Uint8Array(12).fill(0xcc);
  const payload = new Uint8Array(64).fill(0xdd);

  const packet = createPacket({
    recipientHash,
    ephemeralKey,
    nonce,
    payload,
    ttlHops: 12,
    flags: PacketFlags.RELAY_ALLOWED | PacketFlags.ACK_REQUESTED,
  });

  const serialized = serializePacket(packet);
  assert.strictEqual(serialized.length, 128 + 64);

  const parsed = deserializePacket(serialized);
  assert.strictEqual(parsed.magic, SYPHER_MAGIC);
  assert.strictEqual(parsed.version, 1);
  assert.strictEqual(parsed.flags, PacketFlags.RELAY_ALLOWED | PacketFlags.ACK_REQUESTED);
  assert.strictEqual(parsed.ttlHops, 12);
  assert.deepStrictEqual(parsed.recipientHash, recipientHash);
  assert.deepStrictEqual(parsed.ephemeralKey, ephemeralKey);
  assert.deepStrictEqual(parsed.nonce, nonce);
  assert.deepStrictEqual(parsed.payload, payload);
});

test('Deserializer rejects corrupted magic bytes', () => {
  const packet = createPacket({
    recipientHash: new Uint8Array(32),
    ephemeralKey: new Uint8Array(32),
    nonce: new Uint8Array(12),
    payload: new Uint8Array(16),
  });

  const bytes = serializePacket(packet);
  bytes[0] = 0x00; // Corrupt magic

  assert.throws(() => {
    deserializePacket(bytes);
  }, /Invalid protocol magic/);
});

test('BLE chunking and reassembly reconstructs complete packet', () => {
  const originalBytes = new Uint8Array(500);
  for (let i = 0; i < 500; i++) originalBytes[i] = i % 256;

  const chunks = chunkPacketForBLE(originalBytes, 100);
  assert.ok(chunks.length > 1);

  const reassembler = new BLEChunkReassembler();
  let reassembled = null;

  for (const chunk of chunks) {
    reassembled = reassembler.feedChunk(chunk);
  }

  assert.notStrictEqual(reassembled, null);
  assert.deepStrictEqual(reassembled, originalBytes);
});
