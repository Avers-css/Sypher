export interface PeerInfo {
  transportType: 'udp-multicast' | 'ble-mesh' | 'websocket-relay' | 'tor-onion';
  peerId?: string;
  address?: string;
}

export type TransportDataCallback = (data: Uint8Array, peerInfo?: PeerInfo) => void;

/**
 * Common polymorphic interface for all Sypher physical/virtual transports.
 */
export interface ITransport {
  readonly name: string;
  readonly type: PeerInfo['transportType'];

  start(): Promise<void>;
  stop(): Promise<void>;

  /**
   * Broadcasts packet bytes to all reachable peers on this transport
   */
  broadcast(packetBytes: Uint8Array): Promise<void>;

  /**
   * Sends packet bytes directly to a specific target peer
   */
  sendTo?(targetAddress: string, packetBytes: Uint8Array): Promise<void>;

  /**
   * Registers callback invoked whenever incoming bytes arrive
   */
  onData(callback: TransportDataCallback): void;
}
