/**
 * High-performance sliding-window LRU deduplication filter.
 * Prevents mesh broadcast storms, infinite routing loops, and replay attacks.
 */
export class PacketDeduplicator {
  private readonly capacity: number;
  private readonly seen = new Map<string, number>();

  constructor(capacity: number = 10000) {
    this.capacity = capacity;
  }

  /**
   * Checks if a packet has already been processed.
   * If not seen, records it and returns false.
   * If already seen, returns true (duplicate).
   */
  public isDuplicate(packetId: Uint8Array): boolean {
    const key = Buffer.from(packetId).toString('hex');
    const now = Date.now();

    if (this.seen.has(key)) {
      return true;
    }

    if (this.seen.size >= this.capacity) {
      // Evict oldest 10%
      const keysToDelete: string[] = [];
      let count = 0;
      const target = Math.floor(this.capacity * 0.1);
      for (const k of this.seen.keys()) {
        keysToDelete.push(k);
        count++;
        if (count >= target) break;
      }
      for (const k of keysToDelete) {
        this.seen.delete(k);
      }
    }

    this.seen.set(key, now);
    return false;
  }

  /**
   * Prunes entries older than maxAgeMs
   */
  public prune(maxAgeMs: number = 24 * 60 * 60 * 1000): void {
    const now = Date.now();
    for (const [key, timestamp] of this.seen.entries()) {
      if (now - timestamp > maxAgeMs) {
        this.seen.delete(key);
      }
    }
  }

  public get size(): number {
    return this.seen.size;
  }
}
