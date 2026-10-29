import crypto from 'node:crypto';
import { SypherIdentity, createAnonymousIdentity, computeFingerprint } from '../crypto/keys.js';

export class IdentityManager {
  private currentIdentity: SypherIdentity | null = null;

  /**
   * Generates a completely new anonymous Sypher identity
   */
  public generate(): SypherIdentity {
    this.currentIdentity = createAnonymousIdentity();
    return this.currentIdentity;
  }

  /**
   * Sets current active identity from saved state
   */
  public load(identity: SypherIdentity): void {
    this.currentIdentity = identity;
  }

  public get identity(): SypherIdentity {
    if (!this.currentIdentity) {
      throw new Error('Identity not initialized. Call generate() or load() first.');
    }
    return this.currentIdentity;
  }

  /**
   * Signs a binary payload with the user's Ed25519 identity key
   */
  public sign(data: Uint8Array): Uint8Array {
    if (!this.currentIdentity) throw new Error('No active identity');

    // Reconstruct private key object
    const privKey = crypto.createPrivateKey({
      key: Buffer.concat([
        Buffer.from('302e020100300506032b657004220420', 'hex'),
        Buffer.from(this.currentIdentity.identityKeyPair.privateKey),
      ]),
      format: 'der',
      type: 'pkcs8',
    });

    const signature = crypto.sign(null, Buffer.from(data), privKey);
    return new Uint8Array(signature);
  }

  /**
   * Verifies an Ed25519 signature from a peer
   */
  public static verifySignature(
    peerPublicKey: Uint8Array,
    data: Uint8Array,
    signature: Uint8Array
  ): boolean {
    try {
      const pubKey = crypto.createPublicKey({
        key: Buffer.concat([
          Buffer.from('302a300506032b6570032100', 'hex'),
          Buffer.from(peerPublicKey),
        ]),
        format: 'der',
        type: 'spki',
      });

      return crypto.verify(null, Buffer.from(data), pubKey, Buffer.from(signature));
    } catch {
      return false;
    }
  }
}
