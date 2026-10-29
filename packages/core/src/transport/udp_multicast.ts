import dgram from 'node:dgram';
import { ITransport, TransportDataCallback, PeerInfo } from './transport.interface.js';

export const DEFAULT_MULTICAST_ADDR = '239.255.77.88';
export const DEFAULT_MULTICAST_PORT = 7788;

/**
 * Local UDP Multicast Transport.
 * Enables automatic, serverless peer discovery and message gossiping across
 * local Wi-Fi, Ethernet LAN, or ad-hoc hotspots without needing any internet connection.
 */
export class UDPMulticastTransport implements ITransport {
  public readonly name = 'Local-UDP-Multicast';
  public readonly type = 'udp-multicast';

  private socket: dgram.Socket | null = null;
  private isRunning: boolean = false;
  private dataCallback: TransportDataCallback | null = null;

  private readonly multicastAddress: string;
  private readonly port: number;

  constructor(address: string = DEFAULT_MULTICAST_ADDR, port: number = DEFAULT_MULTICAST_PORT) {
    this.multicastAddress = address;
    this.port = port;
  }

  public async start(): Promise<void> {
    if (this.isRunning) return;

    return new Promise((resolve, reject) => {
      try {
        const socket = dgram.createSocket({ type: 'udp4', reuseAddr: true });

        socket.on('error', (err) => {
          console.error('[UDPMulticast] Socket error:', err.message);
        });

        socket.on('message', (msg, rinfo) => {
          if (this.dataCallback) {
            const peerInfo: PeerInfo = {
              transportType: 'udp-multicast',
              address: `${rinfo.address}:${rinfo.port}`,
            };
            this.dataCallback(new Uint8Array(msg), peerInfo);
          }
        });

        socket.bind(this.port, () => {
          try {
            socket.addMembership(this.multicastAddress);
            socket.setMulticastTTL(4);
            socket.setMulticastLoopback(false); // Do not echo back our own broadcasts
            this.socket = socket;
            this.isRunning = true;
            resolve();
          } catch (e) {
            reject(e);
          }
        });
      } catch (err) {
        reject(err);
      }
    });
  }

  public async stop(): Promise<void> {
    if (!this.isRunning || !this.socket) return;
    return new Promise((resolve) => {
      try {
        this.socket!.dropMembership(this.multicastAddress);
        this.socket!.close(() => {
          this.isRunning = false;
          this.socket = null;
          resolve();
        });
      } catch {
        this.isRunning = false;
        this.socket = null;
        resolve();
      }
    });
  }

  public async broadcast(packetBytes: Uint8Array): Promise<void> {
    if (!this.isRunning || !this.socket) {
      throw new Error('UDPMulticastTransport is not running');
    }

    return new Promise((resolve, reject) => {
      this.socket!.send(
        Buffer.from(packetBytes),
        0,
        packetBytes.length,
        this.port,
        this.multicastAddress,
        (err) => {
          if (err) reject(err);
          else resolve();
        }
      );
    });
  }

  public onData(callback: TransportDataCallback): void {
    this.dataCallback = callback;
  }
}
