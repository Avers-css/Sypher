import test from 'node:test';
import assert from 'node:assert';
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { EncryptedVault } from '../dist/storage/encrypted_vault.js';

test('EncryptedVault lifecycle: create, lock, unlock, persist and emergency burn', async () => {
  const tmpVaultPath = path.join(os.tmpdir(), `sypher_test_${Date.now()}.vault`);

  try {
    const vault = new EncryptedVault(tmpVaultPath);
    await vault.create('master_secure_passphrase_42');

    vault.set('userId', 'syph19827364');
    vault.set('privateKeyHex', 'deadbeef00112233');
    await vault.flush();

    // Lock vault
    vault.lock();
    assert.throws(() => vault.get('userId'), /Vault is locked/);

    // Attempt unlock with WRONG passphrase
    const wrongVault = new EncryptedVault(tmpVaultPath);
    await assert.rejects(async () => {
      await wrongVault.unlock('wrong_passphrase');
    }, /Failed to unlock vault/);

    // Unlock with CORRECT passphrase
    const successVault = new EncryptedVault(tmpVaultPath);
    const unlocked = await successVault.unlock('master_secure_passphrase_42');
    assert.strictEqual(unlocked, true);
    assert.strictEqual(successVault.get('userId'), 'syph19827364');
    assert.strictEqual(successVault.get('privateKeyHex'), 'deadbeef00112233');

    // Emergency Burn
    successVault.emergencyBurn();
    assert.strictEqual(fs.existsSync(tmpVaultPath), false);
  } finally {
    if (fs.existsSync(tmpVaultPath)) {
      fs.unlinkSync(tmpVaultPath);
    }
  }
});
