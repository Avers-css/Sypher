import test from 'node:test';
import assert from 'node:assert';
import { generateExchangeKeyPair } from '../dist/crypto/keys.js';
import {
  initializeAliceSession,
  initializeBobSession,
  ratchetEncrypt,
  ratchetDecrypt,
} from '../dist/crypto/ratchet.js';

test('Double Ratchet full multi-turn conversation and forward secrecy', () => {
  const sharedKey = new Uint8Array(32).fill(0x77);
  const bobDH = generateExchangeKeyPair();

  // 1. Initialize sessions
  const aliceState = initializeAliceSession(sharedKey, bobDH.publicKey);
  const bobState = initializeBobSession(sharedKey, bobDH);

  // 2. Alice sends message 1 to Bob
  const msg1Text = 'Hey Bob, this is Alice!';
  const enc1 = ratchetEncrypt(aliceState, new TextEncoder().encode(msg1Text));
  const dec1 = ratchetDecrypt(bobState, enc1);
  assert.strictEqual(new TextDecoder().decode(dec1), msg1Text);

  // 3. Alice sends message 2 to Bob (in same sending chain)
  const msg2Text = 'Second message before Bob replies';
  const enc2 = ratchetEncrypt(aliceState, new TextEncoder().encode(msg2Text));
  const dec2 = ratchetDecrypt(bobState, enc2);
  assert.strictEqual(new TextDecoder().decode(dec2), msg2Text);

  // 4. Bob replies to Alice (triggers DH ratchet step)
  const reply1Text = 'Hey Alice! I hear you loud and clear on Sypher.';
  const encReply1 = ratchetEncrypt(bobState, new TextEncoder().encode(reply1Text));
  const decReply1 = ratchetDecrypt(aliceState, encReply1);
  assert.strictEqual(new TextDecoder().decode(decReply1), reply1Text);

  // 5. Alice replies back (triggers DH ratchet step on Alice)
  const reply2Text = 'Double Ratchet state verified!';
  const encReply2 = ratchetEncrypt(aliceState, new TextEncoder().encode(reply2Text));
  const decReply2 = ratchetDecrypt(bobState, encReply2);
  assert.strictEqual(new TextDecoder().decode(decReply2), reply2Text);
});

test('Double Ratchet correctly handles out-of-order messages', () => {
  const sharedKey = new Uint8Array(32).fill(0x33);
  const bobDH = generateExchangeKeyPair();

  const aliceState = initializeAliceSession(sharedKey, bobDH.publicKey);
  const bobState = initializeBobSession(sharedKey, bobDH);

  // Alice sends 3 messages
  const enc1 = ratchetEncrypt(aliceState, new TextEncoder().encode('Message #1'));
  const enc2 = ratchetEncrypt(aliceState, new TextEncoder().encode('Message #2'));
  const enc3 = ratchetEncrypt(aliceState, new TextEncoder().encode('Message #3'));

  // Bob receives #3 first (skipped #1 and #2)
  const dec3 = ratchetDecrypt(bobState, enc3);
  assert.strictEqual(new TextDecoder().decode(dec3), 'Message #3');

  // Bob later receives #1
  const dec1 = ratchetDecrypt(bobState, enc1);
  assert.strictEqual(new TextDecoder().decode(dec1), 'Message #1');

  // Bob finally receives #2
  const dec2 = ratchetDecrypt(bobState, enc2);
  assert.strictEqual(new TextDecoder().decode(dec2), 'Message #2');
});
