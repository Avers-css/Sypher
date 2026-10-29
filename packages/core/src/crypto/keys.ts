import crypto from 'node:crypto';
import { zeroize } from './zeroize.js';

export interface KeyPair {
  publicKey: Uint8Array;
  privateKey: Uint8Array;
}

export interface SypherIdentity {
  id: string;                      // syph1... address
  publicKeyHex: string;            // hex representation
  fingerprint: string;             // human-readable verification fingerprint
  identityKeyPair: KeyPair;        // Ed25519 signing pair
  exchangeKeyPair: KeyPair;        // X25519 Diffie-Hellman pair
}

/**
 * Extracts raw 32-byte key from SPKI / PKCS8 DER buffer
 */
function extractRawKey(der: Buffer, isPublic: boolean): Uint8Array {
  // In standard SPKI/PKCS8 for Ed25519 and X25519:
  // SPKI: last 32 bytes are the public key
  // PKCS8: last 32 bytes (or inside octet string) are the private seed
  return new Uint8Array(der.subarray(der.length - 32));
}

/**
 * Generates an Ed25519 Identity Keypair
 */
export function generateIdentityKeyPair(): KeyPair {
  const { publicKey, privateKey } = crypto.generateKeyPairSync('ed25519');
  const pubDer = publicKey.export({ type: 'spki', format: 'der' });
  const privDer = privateKey.export({ type: 'pkcs8', format: 'der' });

  const rawPub = extractRawKey(pubDer, true);
  const rawPriv = extractRawKey(privDer, false);

  return {
    publicKey: rawPub,
    privateKey: rawPriv,
  };
}

/**
 * Generates an ephemeral or long-term X25519 DH Keypair
 */
export function generateExchangeKeyPair(): KeyPair {
  const { publicKey, privateKey } = crypto.generateKeyPairSync('x25519');
  const pubDer = publicKey.export({ type: 'spki', format: 'der' });
  const privDer = privateKey.export({ type: 'pkcs8', format: 'der' });

  return {
    publicKey: extractRawKey(pubDer, true),
    privateKey: extractRawKey(privDer, false),
  };
}

/**
 * Computes a standardized SHA-256 fingerprint formatted into 8 groups of 4 hex chars.
 * Example: "7F2A 9C81 D0E4 11B3 44FE 8812 90AA 3BC7"
 */
export function computeFingerprint(publicKey: Uint8Array): string {
  const hash = crypto.createHash('sha256').update(publicKey).digest('hex').toUpperCase();
  const chunks: string[] = [];
  for (let i = 0; i < 32; i += 4) {
    chunks.push(hash.substring(i, i + 4));
  }
  return chunks.slice(0, 8).join(' ');
}

/**
 * Generates a full Sypher anonymous identity from scratch
 */
export function createAnonymousIdentity(): SypherIdentity {
  const identityPair = generateIdentityKeyPair();
  const exchangePair = generateExchangeKeyPair();

  const hexId = Buffer.from(identityPair.publicKey).toString('hex');
  const id = `syph1${hexId.substring(0, 24)}`;
  const fingerprint = computeFingerprint(identityPair.publicKey);

  return {
    id,
    publicKeyHex: hexId,
    fingerprint,
    identityKeyPair: identityPair,
    exchangeKeyPair: exchangePair,
  };
}

/**
 * Derives a shared secret between our private X25519 key and peer's public X25519 key.
 */
export function computeSharedSecret(ourPrivateKey: Uint8Array, peerPublicKey: Uint8Array): Uint8Array {
  // Reconstruct Node KeyObjects
  const privKeyObject = crypto.createPrivateKey({
    key: Buffer.concat([
      // PKCS8 header for X25519
      Buffer.from('302e020100300506032b656e04220420', 'hex'),
      Buffer.from(ourPrivateKey),
    ]),
    format: 'der',
    type: 'pkcs8',
  });

  const pubKeyObject = crypto.createPublicKey({
    key: Buffer.concat([
      // SPKI header for X25519
      Buffer.from('302a300506032b656e032100', 'hex'),
      Buffer.from(peerPublicKey),
    ]),
    format: 'der',
    type: 'spki',
  });

  const secret = crypto.diffieHellman({
    privateKey: privKeyObject,
    publicKey: pubKeyObject,
  });

  return new Uint8Array(secret);
}
