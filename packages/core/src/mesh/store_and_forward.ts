import { SypherPacket, PacketFlags } from '../protocol/packet.js';

export interface StoredPacketEntry {
  packet: SypherPacket;
  rawBytes: Uint8Array;
  storedAt: number;
}

/**
 * Store-and-Forward Pool: Holds encrypted packets while in transit through the mesh.
 * Packets are stored in their native encrypted form (the host cannot read them).
 */
export class StoreAndForwardPool {
  private pool = new Map<string, StoredPacketEntry>();
  private readonly maxCapacity: number;

  constructor(maxCapacity: number = 2000) {
    this.maxCapacity = maxCapacity;
  }

  /**
   * Adds a packet to the store-and-forward pool if it is eligible for relaying
   */
  public enqueue(packet: SypherPacket, rawBytes: Uint8Array): boolean {
    // If EPHEMERAL flag is set, do not store offline
    if (packet.flags & PacketFlags.EPHEMERAL) {
      return false;
    }

    const now = Date.now();
    // Do not store if already expired
    if (BigInt(now) > packet.expiresAt) {
      return false;
    }

    const idHex = Buffer.from(packet.packetId).toString('hex');
    if (this.pool.has(idHex)) {
      return false;
    }

    if (this.pool.size >= this.maxCapacity) {
      this.evictExpiredOrOldest();
    }

    this.pool.set(idHex, {
      packet,
      rawBytes,
      storedAt: now,
    });
    return true;
  }

  /**
   * Retrieves all candidate packets that can be relayed to a newly encountered peer
   */
  public getRelayCandidates(limit: number = 50): Uint8Array[] {
    this.pruneExpired();
    const result: Uint8Array[] = [];
    let count = 0;

    for (const entry of this.pool.values()) {
      result.push(entry.rawBytes);
      count++;
      if (count >= limit) break;
    }
    return result;
  }

  /**
   * Removes expired packets from memory
   */
  public pruneExpired(): number {
    const now = BigInt(Date.now());
    let pruned = 0;

    for (const [id, entry] of this.pool.entries()) {
      if (now > entry.packet.expiresAt) {
        this.pool.delete(id);
        pruned++;
      }
    }
    return pruned;
  }

  private evictExpiredOrOldest(): void {
    if (this.pruneExpired() > 0) return;

    // Evict oldest 10%
    const keys = Array.from(this.pool.keys());
    const countToEvict = Math.max(1, Math.floor(this.maxCapacity * 0.1));
    for (let i = 0; i < countToEvict; i++) {
      this.pool.delete(keys[i]);
    }
  }

  public get size(): number {
    return this.pool.size;
  }

  public remove(packetIdHex: string): boolean {
    return this.pool.delete(packetIdHex);
  }
}
