import crypto from 'node:crypto';
import { zeroize, constantTimeEquals } from './zeroize.js';

export interface EncryptedPayload {
  ciphertext: Uint8Array;
  nonce: Uint8Array;
  tag: Uint8Array;
}

/**
 * HKDF-SHA256 Key Derivation Function (RFC 5869)
 */
export function hkdfExtractAndExpand(
  salt: Uint8Array,
  ikm: Uint8Array,
  info: string,
  length: number
): Uint8Array {
  const derived = crypto.hkdfSync(
    'sha256',
    Buffer.from(ikm),
    Buffer.from(salt),
    Buffer.from(info, 'utf-8'),
    length
  );
  return new Uint8Array(derived);
}

/**
 * Pads plaintext to a multiple of blockSize (default 64 bytes)
 * to prevent packet length traffic analysis.
 * Uses standard PKCS#7 / ISO padding.
 */
export function padPlaintext(data: Uint8Array, blockSize: number = 64): Uint8Array {
  const padLength = blockSize - (data.length % blockSize);
  const result = new Uint8Array(data.length + padLength);
  result.set(data, 0);
  result.fill(padLength, data.length);
  return result;
}

/**
 * Strips padding from decrypted plaintext.
 */
export function unpadPlaintext(data: Uint8Array): Uint8Array {
  if (data.length === 0) throw new Error('Cannot unpad empty buffer');
  const padLength = data[data.length - 1];
  if (padLength <= 0 || padLength > data.length) {
    throw new Error('Invalid padding detected');
  }
  for (let i = data.length - padLength; i < data.length; i++) {
    if (data[i] !== padLength) {
      throw new Error('Inconsistent padding bytes');
    }
  }
  return data.subarray(0, data.length - padLength);
}

/**
 * Encrypts a plaintext buffer using ChaCha20-Poly1305 (RFC 8439) with optional AAD
 */
export function encryptChaCha20Poly1305(
  key: Uint8Array,
  plaintext: Uint8Array,
  aad: Uint8Array = new Uint8Array(0)
): EncryptedPayload {
  if (key.length !== 32) {
    throw new Error('Key must be exactly 32 bytes');
  }

  // 12-byte (96-bit) cryptographically random nonce
  const nonce = crypto.randomBytes(12);
  const cipher = crypto.createCipheriv('chacha20-poly1305', Buffer.from(key), nonce, {
    authTagLength: 16,
  });

  if (aad.length > 0) {
    cipher.setAAD(Buffer.from(aad));
  }

  const padded = padPlaintext(plaintext);
  const encrypted = Buffer.concat([cipher.update(Buffer.from(padded)), cipher.final()]);
  const tag = cipher.getAuthTag();

  return {
    ciphertext: new Uint8Array(encrypted),
    nonce: new Uint8Array(nonce),
    tag: new Uint8Array(tag),
  };
}

/**
 * Decrypts a ciphertext buffer using ChaCha20-Poly1305 with tag verification
 */
export function decryptChaCha20Poly1305(
  key: Uint8Array,
  ciphertext: Uint8Array,
  nonce: Uint8Array,
  tag: Uint8Array,
  aad: Uint8Array = new Uint8Array(0)
): Uint8Array {
  if (key.length !== 32) throw new Error('Key must be exactly 32 bytes');
  if (nonce.length !== 12) throw new Error('Nonce must be exactly 12 bytes');
  if (tag.length !== 16) throw new Error('Tag must be exactly 16 bytes');

  const decipher = crypto.createDecipheriv('chacha20-poly1305', Buffer.from(key), Buffer.from(nonce), {
    authTagLength: 16,
  });

  decipher.setAuthTag(Buffer.from(tag));
  if (aad.length > 0) {
    decipher.setAAD(Buffer.from(aad));
  }

  try {
    const decrypted = Buffer.concat([decipher.update(Buffer.from(ciphertext)), decipher.final()]);
    return unpadPlaintext(new Uint8Array(decrypted));
  } catch (err) {
    throw new Error('Decryption failed: Authentication tag mismatch or corrupted ciphertext');
  }
}
