import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { zeroize } from '../crypto/zeroize.js';

export interface VaultMetadata {
  version: number;
  saltHex: string;
  iterations: number;
}

/**
 * Encrypted on-disk key-value storage protected by PBKDF2 + AES-256-GCM.
 * Protects keys, ratchet states, contacts, and chat history against physical device seizure.
 */
export class EncryptedVault {
  private readonly filePath: string;
  private masterKey: Uint8Array | null = null;
  private salt: Buffer | null = null;
  private iterations: number = 100000;
  private data: Record<string, any> = {};
  private isUnlocked: boolean = false;

  constructor(filePath: string) {
    this.filePath = filePath;
  }

  /**
   * Initializes a new vault with a user passphrase
   */
  public async create(passphrase: string): Promise<void> {
    this.salt = crypto.randomBytes(32);
    this.iterations = 100000;
    const key = crypto.pbkdf2Sync(passphrase, this.salt, this.iterations, 32, 'sha256');

    this.masterKey = new Uint8Array(key);
    this.data = {};
    this.isUnlocked = true;

    await this.saveToFile();
  }

  /**
   * Unlocks an existing vault file using user passphrase
   */
  public async unlock(passphrase: string): Promise<boolean> {
    if (!fs.existsSync(this.filePath)) {
      throw new Error(`Vault file does not exist at ${this.filePath}`);
    }

    const raw = fs.readFileSync(this.filePath);
    const metaLen = raw.readUInt32BE(0);
    const metaJson = raw.subarray(4, 4 + metaLen).toString('utf-8');
    const meta: VaultMetadata = JSON.parse(metaJson);

    this.salt = Buffer.from(meta.saltHex, 'hex');
    this.iterations = meta.iterations;
    const key = crypto.pbkdf2Sync(passphrase, this.salt, this.iterations, 32, 'sha256');

    const ivOffset = 4 + metaLen;
    const iv = raw.subarray(ivOffset, ivOffset + 12);
    const tag = raw.subarray(ivOffset + 12, ivOffset + 28);
    const ciphertext = raw.subarray(ivOffset + 28);

    try {
      const decipher = crypto.createDecipheriv('aes-256-gcm', key, iv);
      decipher.setAuthTag(tag);
      const decrypted = Buffer.concat([decipher.update(ciphertext), decipher.final()]);

      this.data = JSON.parse(decrypted.toString('utf-8'));
      this.masterKey = new Uint8Array(key);
      this.isUnlocked = true;
      return true;
    } catch {
      throw new Error('Failed to unlock vault: incorrect passphrase or corrupted vault file');
    }
  }

  public set(key: string, value: any): void {
    this.assertUnlocked();
    this.data[key] = value;
  }

  public get<T>(key: string): T | undefined {
    this.assertUnlocked();
    return this.data[key] as T;
  }

  public async flush(): Promise<void> {
    this.assertUnlocked();
    await this.saveToFile();
  }

  public emergencyBurn(): void {
    if (this.masterKey) {
      zeroize(this.masterKey);
      this.masterKey = null;
    }
    this.data = {};
    this.isUnlocked = false;

    if (fs.existsSync(this.filePath)) {
      const size = fs.statSync(this.filePath).size;
      const noise = crypto.randomBytes(size);
      fs.writeFileSync(this.filePath, noise);
      fs.unlinkSync(this.filePath);
    }
  }

  public lock(): void {
    if (this.masterKey) {
      zeroize(this.masterKey);
      this.masterKey = null;
    }
    this.data = {};
    this.isUnlocked = false;
  }

  private assertUnlocked(): void {
    if (!this.isUnlocked || !this.masterKey) {
      throw new Error('Vault is locked. Unlock with passphrase first.');
    }
  }

  private async saveToFile(): Promise<void> {
    if (!this.salt) throw new Error('Salt not set');

    const meta: VaultMetadata = {
      version: 1,
      saltHex: this.salt.toString('hex'),
      iterations: this.iterations,
    };

    const metaBuffer = Buffer.from(JSON.stringify(meta), 'utf-8');
    const iv = crypto.randomBytes(12);

    const cipher = crypto.createCipheriv('aes-256-gcm', Buffer.from(this.masterKey!), iv);
    const plaintext = Buffer.from(JSON.stringify(this.data), 'utf-8');
    const encrypted = Buffer.concat([cipher.update(plaintext), cipher.final()]);
    const tag = cipher.getAuthTag();

    const output = Buffer.concat([
      (() => {
        const b = Buffer.alloc(4);
        b.writeUInt32BE(metaBuffer.length, 0);
        return b;
      })(),
      metaBuffer,
      iv,
      tag,
      encrypted,
    ]);

    const dir = path.dirname(this.filePath);
    if (!fs.existsSync(dir)) {
      fs.mkdirSync(dir, { recursive: true });
    }

    fs.writeFileSync(this.filePath, output);
  }
}
