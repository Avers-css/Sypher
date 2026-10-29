import test from 'node:test';
import assert from 'node:assert';
import {
  generateIdentityKeyPair,
  generateExchangeKeyPair,
  computeSharedSecret,
  computeFingerprint,
} from '../dist/crypto/keys.js';
import {
  encryptChaCha20Poly1305,
  decryptChaCha20Poly1305,
} from '../dist/crypto/cipher.js';
import { zeroize } from '../dist/crypto/zeroize.js';

test('Identity Keypair generation produce valid 32-byte keys', () => {
  const pair = generateIdentityKeyPair();
  assert.strictEqual(pair.publicKey.length, 32);
  assert.strictEqual(pair.privateKey.length, 32);

  const fp = computeFingerprint(pair.publicKey);
  assert.match(fp, /^([0-9A-F]{4}\s){7}[0-9A-F]{4}$/);
});

test('X25519 Diffie-Hellman derives identical shared secrets on both sides', () => {
  const alice = generateExchangeKeyPair();
  const bob = generateExchangeKeyPair();

  const secretAlice = computeSharedSecret(alice.privateKey, bob.publicKey);
  const secretBob = computeSharedSecret(bob.privateKey, alice.publicKey);

  assert.strictEqual(secretAlice.length, 32);
  assert.strictEqual(secretBob.length, 32);
  assert.deepStrictEqual(secretAlice, secretBob);
});

test('ChaCha20-Poly1305 encrypts, pads, and decrypts successfully', () => {
  const key = new Uint8Array(32).fill(0x42);
  const plaintext = new TextEncoder().encode('Hello, Sypher secure world!');
  const aad = new TextEncoder().encode('channel-metadata');

  const encrypted = encryptChaCha20Poly1305(key, plaintext, aad);
  assert.strictEqual(encrypted.nonce.length, 12);
  assert.strictEqual(encrypted.tag.length, 16);

  const decrypted = decryptChaCha20Poly1305(
    key,
    encrypted.ciphertext,
    encrypted.nonce,
    encrypted.tag,
    aad
  );

  assert.strictEqual(new TextDecoder().decode(decrypted), 'Hello, Sypher secure world!');
});

test('ChaCha20-Poly1305 rejects tampered ciphertext or corrupted auth tag', () => {
  const key = new Uint8Array(32).fill(0x99);
  const plaintext = new TextEncoder().encode('Top secret');
  const encrypted = encryptChaCha20Poly1305(key, plaintext);

  // Flip 1 bit in ciphertext
  encrypted.ciphertext[0] ^= 0x01;

  assert.throws(() => {
    decryptChaCha20Poly1305(key, encrypted.ciphertext, encrypted.nonce, encrypted.tag);
  }, /Authentication tag mismatch/);
});

test('Zeroize wipes buffer memory completely', () => {
  const secretBuffer = new Uint8Array([1, 2, 3, 4, 5, 6, 7, 8]);
  zeroize(secretBuffer);
  for (let i = 0; i < secretBuffer.length; i++) {
    assert.strictEqual(secretBuffer[i], 0);
  }
});
