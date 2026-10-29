/**
 * Constant-time and deterministic memory wiper for sensitive cryptographic buffers.
 * Prevents key material from remaining in heap memory after use.
 */
export function zeroize(buffer: Uint8Array | ArrayBuffer | Buffer): void {
  if (!buffer) return;
  const view = buffer instanceof Uint8Array ? buffer : new Uint8Array(buffer);
  for (let i = 0; i < view.length; i++) {
    view[i] = 0;
  }
}

/**
 * Constant-time byte array comparison to prevent timing attacks.
 */
export function constantTimeEquals(a: Uint8Array, b: Uint8Array): boolean {
  if (a.length !== b.length) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) {
    diff |= a[i] ^ b[i];
  }
  return diff === 0;
}
