/**
 * Wire framing utilities for chunking across BLE MTU limits (e.g., 23-512 bytes)
 * and stream demarcation over TCP/WebSockets.
 */

export interface ChunkHeader {
  packetIdHex: string;
  chunkIndex: number;
  totalChunks: number;
}

/**
 * Splits a packet buffer into MTU-safe chunks for Bluetooth Low Energy transmission
 */
export function chunkPacketForBLE(packetBytes: Uint8Array, mtu: number = 240): Uint8Array[] {
  // 32-byte packetId + 2-byte chunkIndex + 2-byte totalChunks = 36-byte chunk overhead
  const CHUNK_OVERHEAD = 36;
  const maxPayloadPerChunk = Math.max(16, mtu - CHUNK_OVERHEAD);

  const totalChunks = Math.ceil(packetBytes.length / maxPayloadPerChunk);
  const chunks: Uint8Array[] = [];

  // Extract packet ID from bytes 48..80
  const packetId = packetBytes.subarray(48, 80);

  for (let i = 0; i < totalChunks; i++) {
    const start = i * maxPayloadPerChunk;
    const end = Math.min(packetBytes.length, start + maxPayloadPerChunk);
    const slice = packetBytes.subarray(start, end);

    const chunkBuffer = Buffer.alloc(CHUNK_OVERHEAD + slice.length);
    Buffer.from(packetId).copy(chunkBuffer, 0, 0, 32);
    chunkBuffer.writeUInt16BE(i, 32);
    chunkBuffer.writeUInt16BE(totalChunks, 34);
    Buffer.from(slice).copy(chunkBuffer, 36);

    chunks.push(new Uint8Array(chunkBuffer));
  }

  return chunks;
}

/**
 * Reassembles incoming BLE chunks into a single packet
 */
export class BLEChunkReassembler {
  private chunks = new Map<string, { total: number; parts: Map<number, Uint8Array>; receivedAt: number }>();
  private readonly timeoutMs: number;

  constructor(timeoutMs: number = 30000) {
    this.timeoutMs = timeoutMs;
  }

  public feedChunk(chunkBytes: Uint8Array): Uint8Array | null {
    if (chunkBytes.length < 36) return null;

    const buffer = Buffer.from(chunkBytes.buffer, chunkBytes.byteOffset, chunkBytes.byteLength);
    const packetIdHex = buffer.subarray(0, 32).toString('hex');
    const chunkIndex = buffer.readUInt16BE(32);
    const totalChunks = buffer.readUInt16BE(34);
    const payload = chunkBytes.subarray(36);

    this.pruneOld();

    let entry = this.chunks.get(packetIdHex);
    if (!entry) {
      entry = { total: totalChunks, parts: new Map(), receivedAt: Date.now() };
      this.chunks.set(packetIdHex, entry);
    }

    entry.parts.set(chunkIndex, payload);

    if (entry.parts.size === entry.total) {
      // Reassemble
      const orderedParts: Uint8Array[] = [];
      let totalLength = 0;
      for (let i = 0; i < entry.total; i++) {
        const part = entry.parts.get(i);
        if (!part) return null; // Missing chunk
        orderedParts.push(part);
        totalLength += part.length;
      }

      const result = new Uint8Array(totalLength);
      let offset = 0;
      for (const part of orderedParts) {
        result.set(part, offset);
        offset += part.length;
      }

      this.chunks.delete(packetIdHex);
      return result;
    }

    return null;
  }

  private pruneOld(): void {
    const now = Date.now();
    for (const [id, entry] of this.chunks.entries()) {
      if (now - entry.receivedAt > this.timeoutMs) {
        this.chunks.delete(id);
      }
    }
  }
}
