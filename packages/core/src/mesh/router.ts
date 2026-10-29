import crypto from 'node:crypto';
import { SypherPacket, PacketFlags, deserializePacket, serializePacket } from '../protocol/packet.js';
import { PacketDeduplicator } from './deduplication.js';
import { StoreAndForwardPool } from './store_and_forward.js';

export interface MeshRouterConfig {
  ourPublicKey: Uint8Array;
  maxHops?: number;
}

export type MessageHandler = (packet: SypherPacket, rawBytes: Uint8Array) => void;
export type RelayHandler = (packetBytes: Uint8Array) => void;

/**
 * Sypher Epidemic Mesh Router.
 * Manages zero-configuration peer discovery, gossip message propagation,
 * hop-limit decrementing, and packet relaying.
 */
export class MeshRouter {
  public readonly ourPublicKey: Uint8Array;
  public readonly ourRecipientHash: Uint8Array;
  private readonly deduplicator = new PacketDeduplicator();
  private readonly storeAndForward = new StoreAndForwardPool();

  private messageListeners: MessageHandler[] = [];
  private relayListeners: RelayHandler[] = [];

  constructor(config: MeshRouterConfig) {
    this.ourPublicKey = config.ourPublicKey;
    this.ourRecipientHash = new Uint8Array(
      crypto.createHash('sha256').update(this.ourPublicKey).digest()
    );
  }

  /**
   * Registers a listener for messages addressed to this node or broadcast
   */
  public onMessage(handler: MessageHandler): void {
    this.messageListeners.push(handler);
  }

  /**
   * Registers a listener for packets that need to be broadcast/relayed over transports
   */
  public onRelay(handler: RelayHandler): void {
    this.relayListeners.push(handler);
  }

  /**
   * Main entry point when a raw byte stream arrives from ANY transport
   * (UDP multicast, BLE, WebSocket relay, Tor)
   */
  public receiveIncoming(rawBytes: Uint8Array): boolean {
    let packet: SypherPacket;
    try {
      packet = deserializePacket(rawBytes);
    } catch (err) {
      // Malformed or corrupt packet dropped silently
      return false;
    }

    // 1. Anti-Replay / Deduplication
    if (this.deduplicator.isDuplicate(packet.packetId)) {
      return false; // Already seen or processed
    }

    // 2. Check if expired
    if (BigInt(Date.now()) > packet.expiresAt) {
      return false;
    }

    // 3. Check destination
    const isForUs = this.isPacketAddressedToUs(packet.recipientHash);
    const isBroadcast = this.isBroadcastPacket(packet.recipientHash);

    if (isForUs || isBroadcast) {
      for (const listener of this.messageListeners) {
        try {
          listener(packet, rawBytes);
        } catch (e) {
          console.error('[Router] Error in message listener:', e);
        }
      }
    }

    // 4. Mesh Relay Logic
    // If not direct-only, relay allowed, and hops remain, gossip to neighbors
    const canRelay = (packet.flags & PacketFlags.RELAY_ALLOWED) !== 0 && (packet.flags & PacketFlags.DIRECT) === 0;

    if (canRelay && packet.ttlHops > 1) {
      // Decrement hop limit
      packet.ttlHops -= 1;
      const reSerialized = serializePacket(packet);

      // Store in offline store-and-forward pool
      this.storeAndForward.enqueue(packet, reSerialized);

      // Gossip to immediate neighbors via active transports
      for (const relay of this.relayListeners) {
        try {
          relay(reSerialized);
        } catch (e) {
          console.error('[Router] Error in relay handler:', e);
        }
      }
    }

    return true;
  }

  /**
   * Gets pending offline messages to push when a new peer connects
   */
  public getOfflineQueue(limit: number = 30): Uint8Array[] {
    return this.storeAndForward.getRelayCandidates(limit);
  }

  private isPacketAddressedToUs(hash: Uint8Array): boolean {
    if (hash.length !== 32) return false;
    for (let i = 0; i < 32; i++) {
      if (hash[i] !== this.ourRecipientHash[i]) return false;
    }
    return true;
  }

  private isBroadcastPacket(hash: Uint8Array): boolean {
    if (hash.length !== 32) return false;
    for (let i = 0; i < 32; i++) {
      if (hash[i] !== 0xff) return false;
    }
    return true;
  }
}
