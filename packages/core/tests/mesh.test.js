import test from 'node:test';
import assert from 'node:assert';
import crypto from 'node:crypto';
import { PacketDeduplicator } from '../dist/mesh/deduplication.js';
import { StoreAndForwardPool } from '../dist/mesh/store_and_forward.js';
import { MeshRouter } from '../dist/mesh/router.js';
import { createPacket, serializePacket, PacketFlags } from '../dist/protocol/packet.js';

test('PacketDeduplicator detects duplicates and prevents loops', () => {
  const dedup = new PacketDeduplicator(100);
  const packetId = new Uint8Array(32).fill(0x55);

  assert.strictEqual(dedup.isDuplicate(packetId), false);
  assert.strictEqual(dedup.isDuplicate(packetId), true);
});

test('StoreAndForwardPool stores and prunes expired packets', () => {
  const pool = new StoreAndForwardPool(10);
  const recipientHash = new Uint8Array(32).fill(0x11);

  const packet = createPacket({
    recipientHash,
    ephemeralKey: new Uint8Array(32),
    nonce: new Uint8Array(12),
    payload: new Uint8Array(10),
    expiresInMs: 50,
  });

  const rawBytes = serializePacket(packet);
  const enqueued = pool.enqueue(packet, rawBytes);
  assert.strictEqual(enqueued, true);
  assert.strictEqual(pool.size, 1);

  return new Promise((resolve) => {
    setTimeout(() => {
      const candidates = pool.getRelayCandidates();
      assert.strictEqual(candidates.length, 0);
      assert.strictEqual(pool.size, 0);
      resolve();
    }, 70);
  });
});

test('MeshRouter delivers packet addressed to us and gossips packets for others', () => {
  const bobKey = new Uint8Array(32).fill(0x02);
  const charlieKey = new Uint8Array(32).fill(0x03);

  const bobRouter = new MeshRouter({ ourPublicKey: bobKey });

  let messageReceivedByBob = false;
  bobRouter.onMessage(() => {
    messageReceivedByBob = true;
  });

  let packetRelayedByBob = false;
  bobRouter.onRelay(() => {
    packetRelayedByBob = true;
  });

  // 1. Packet addressed to Bob
  const toBobPacket = createPacket({
    recipientHash: bobRouter.ourRecipientHash,
    ephemeralKey: new Uint8Array(32),
    nonce: new Uint8Array(12),
    payload: new Uint8Array(16),
  });
  bobRouter.receiveIncoming(serializePacket(toBobPacket));
  assert.strictEqual(messageReceivedByBob, true);

  // 2. Packet addressed to Charlie (Bob acts as relay router)
  const charlieHash = new Uint8Array(crypto.createHash('sha256').update(charlieKey).digest());
  const toCharliePacket = createPacket({
    recipientHash: charlieHash,
    ephemeralKey: new Uint8Array(32),
    nonce: new Uint8Array(12),
    payload: new Uint8Array(16),
    ttlHops: 5,
    flags: PacketFlags.RELAY_ALLOWED,
  });

  bobRouter.receiveIncoming(serializePacket(toCharliePacket));
  assert.strictEqual(packetRelayedByBob, true);
});
