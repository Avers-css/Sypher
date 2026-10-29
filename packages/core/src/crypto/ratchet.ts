import crypto from 'node:crypto';
import { KeyPair, generateExchangeKeyPair, computeSharedSecret } from './keys.js';
import { hkdfExtractAndExpand, encryptChaCha20Poly1305, decryptChaCha20Poly1305 } from './cipher.js';
import { zeroize } from './zeroize.js';

export interface RatchetState {
  ourDHKeyPair: KeyPair;
  peerDHPublicKey: Uint8Array | null;

  rootKey: Uint8Array;
  sendingChainKey: Uint8Array | null;
  receivingChainKey: Uint8Array | null;

  sendingMessageNumber: number;
  receivingMessageNumber: number;
  previousCounter: number;

  skippedMessageKeys: Map<string, Uint8Array>;
}

export interface RatchetHeader {
  dhPublicKey: Uint8Array;
  previousCounter: number;
  messageNumber: number;
}

export interface EncryptedRatchetMessage {
  header: RatchetHeader;
  ciphertext: Uint8Array;
  nonce: Uint8Array;
  tag: Uint8Array;
}

const MAX_SKIP = 2000;
const KDF_INFO = 'Sypher-Ratchet-Step';

/**
 * Initializes a Double Ratchet session as the initiator (Alice)
 */
export function initializeAliceSession(
  sharedSecret: Uint8Array,
  bobDHPublicKey: Uint8Array
): RatchetState {
  const aliceDH = generateExchangeKeyPair();
  const dhOutput = computeSharedSecret(aliceDH.privateKey, bobDHPublicKey);

  // KDF_RK(sharedSecret, dhOutput) -> (rootKey, sendingChainKey)
  const derived = hkdfExtractAndExpand(sharedSecret, dhOutput, KDF_INFO, 64);
  const rootKey = new Uint8Array(derived.subarray(0, 32));
  const sendingChainKey = new Uint8Array(derived.subarray(32, 64));

  return {
    ourDHKeyPair: aliceDH,
    peerDHPublicKey: bobDHPublicKey,
    rootKey,
    sendingChainKey,
    receivingChainKey: null,
    sendingMessageNumber: 0,
    receivingMessageNumber: 0,
    previousCounter: 0,
    skippedMessageKeys: new Map(),
  };
}

/**
 * Initializes a Double Ratchet session as the recipient (Bob)
 */
export function initializeBobSession(
  sharedSecret: Uint8Array,
  bobDHKeyPair: KeyPair
): RatchetState {
  return {
    ourDHKeyPair: bobDHKeyPair,
    peerDHPublicKey: null,
    rootKey: new Uint8Array(sharedSecret),
    sendingChainKey: null,
    receivingChainKey: null,
    sendingMessageNumber: 0,
    receivingMessageNumber: 0,
    previousCounter: 0,
    skippedMessageKeys: new Map(),
  };
}

/**
 * Derives a message key from the chain key and advances the chain
 */
function stepChain(chainKey: Uint8Array): { nextChainKey: Uint8Array; messageKey: Uint8Array } {
  const messageKeyHmac = crypto.createHmac('sha256', Buffer.from(chainKey));
  messageKeyHmac.update(Buffer.from([0x01]));
  const messageKey = new Uint8Array(messageKeyHmac.digest());

  const nextChainHmac = crypto.createHmac('sha256', Buffer.from(chainKey));
  nextChainHmac.update(Buffer.from([0x02]));
  const nextChainKey = new Uint8Array(nextChainHmac.digest());

  return { nextChainKey, messageKey };
}

/**
 * Encrypts a message using the Double Ratchet state
 */
export function ratchetEncrypt(
  state: RatchetState,
  plaintext: Uint8Array,
  associatedData: Uint8Array = new Uint8Array(0)
): EncryptedRatchetMessage {
  // If Bob is replying for the first time or sending key needs initialization
  if (!state.sendingChainKey) {
    if (!state.peerDHPublicKey) {
      throw new Error('Cannot send: peer public key is not yet known');
    }
    state.ourDHKeyPair = generateExchangeKeyPair();
    const dhSend = computeSharedSecret(state.ourDHKeyPair.privateKey, state.peerDHPublicKey);
    const derivedSend = hkdfExtractAndExpand(state.rootKey, dhSend, KDF_INFO, 64);
    state.rootKey = new Uint8Array(derivedSend.subarray(0, 32));
    state.sendingChainKey = new Uint8Array(derivedSend.subarray(32, 64));
    state.previousCounter = state.sendingMessageNumber;
    state.sendingMessageNumber = 0;
  }

  const { nextChainKey, messageKey } = stepChain(state.sendingChainKey);
  state.sendingChainKey = nextChainKey;

  const header: RatchetHeader = {
    dhPublicKey: state.ourDHKeyPair.publicKey,
    previousCounter: state.previousCounter,
    messageNumber: state.sendingMessageNumber,
  };
  state.sendingMessageNumber++;

  const headerBytes = Buffer.concat([
    Buffer.from(header.dhPublicKey),
    Buffer.from(new Uint32Array([header.previousCounter, header.messageNumber]).buffer),
    Buffer.from(associatedData),
  ]);

  const { ciphertext, nonce, tag } = encryptChaCha20Poly1305(messageKey, plaintext, headerBytes);
  zeroize(messageKey);

  return { header, ciphertext, nonce, tag };
}

function skipMessageKeys(state: RatchetState, until: number): void {
  if (!state.receivingChainKey) return;
  if (state.receivingMessageNumber + MAX_SKIP < until) {
    throw new Error('Too many dropped messages: skip threshold exceeded');
  }

  while (state.receivingMessageNumber < until) {
    const { nextChainKey, messageKey } = stepChain(state.receivingChainKey);
    state.receivingChainKey = nextChainKey;

    const cacheKey = `${Buffer.from(state.peerDHPublicKey!).toString('hex')}:${state.receivingMessageNumber}`;
    state.skippedMessageKeys.set(cacheKey, messageKey);
    state.receivingMessageNumber++;
  }
}

/**
 * Decrypts a message using the Double Ratchet state
 */
export function ratchetDecrypt(
  state: RatchetState,
  message: EncryptedRatchetMessage,
  associatedData: Uint8Array = new Uint8Array(0)
): Uint8Array {
  const headerBytes = Buffer.concat([
    Buffer.from(message.header.dhPublicKey),
    Buffer.from(new Uint32Array([message.header.previousCounter, message.header.messageNumber]).buffer),
    Buffer.from(associatedData),
  ]);

  const peerKeyHex = Buffer.from(message.header.dhPublicKey).toString('hex');
  const skippedKeyLookup = `${peerKeyHex}:${message.header.messageNumber}`;

  // 1. Check if key was previously skipped
  if (state.skippedMessageKeys.has(skippedKeyLookup)) {
    const messageKey = state.skippedMessageKeys.get(skippedKeyLookup)!;
    state.skippedMessageKeys.delete(skippedKeyLookup);

    const plaintext = decryptChaCha20Poly1305(
      messageKey,
      message.ciphertext,
      message.nonce,
      message.tag,
      headerBytes
    );
    zeroize(messageKey);
    return plaintext;
  }

  // 2. DH Ratchet step if peer introduced new ephemeral key
  const peerKeyChanged =
    !state.peerDHPublicKey ||
    !Buffer.from(state.peerDHPublicKey).equals(Buffer.from(message.header.dhPublicKey));

  if (peerKeyChanged) {
    skipMessageKeys(state, message.header.previousCounter);

    state.previousCounter = state.sendingMessageNumber;
    state.sendingMessageNumber = 0;
    state.receivingMessageNumber = 0;
    state.peerDHPublicKey = message.header.dhPublicKey;

    const dhReceive = computeSharedSecret(state.ourDHKeyPair.privateKey, state.peerDHPublicKey);
    const derivedRecv = hkdfExtractAndExpand(state.rootKey, dhReceive, KDF_INFO, 64);
    state.rootKey = new Uint8Array(derivedRecv.subarray(0, 32));
    state.receivingChainKey = new Uint8Array(derivedRecv.subarray(32, 64));

    // Invalidate sending chain key so next send generates a fresh DH step
    state.sendingChainKey = null;
  }

  // 3. Skip any missing messages in current receiving chain
  skipMessageKeys(state, message.header.messageNumber);

  // 4. Derive message key
  if (!state.receivingChainKey) {
    throw new Error('Receiving chain not ready');
  }

  const { nextChainKey, messageKey } = stepChain(state.receivingChainKey);
  state.receivingChainKey = nextChainKey;
  state.receivingMessageNumber++;

  const plaintext = decryptChaCha20Poly1305(
    messageKey,
    message.ciphertext,
    message.nonce,
    message.tag,
    headerBytes
  );
  zeroize(messageKey);

  return plaintext;
}
