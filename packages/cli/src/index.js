#!/usr/bin/env node

import {
  createAnonymousIdentity,
  UDPMulticastTransport,
  MeshRouter,
  createPacket,
  serializePacket,
  PacketFlags,
} from '../../core/dist/index.js';

const args = process.argv.slice(2);
const command = args[0] || 'help';

console.log(`
   ███████╗██╗   ██╗██████╗ ██╗  ██╗███████╗██████╗ 
   ██╔════╝╚██╗ ██╔╝██╔══██╗██║  ██║██╔════╝██╔══██╗
   ███████╗ ╚████╔╝ ██████╔╝███████║█████╗  ██████╔╝
   ╚════██║  ╚██╔╝  ██╔═══╝ ██╔══██║██╔══╝  ██╔══██╗
   ███████║   ██║   ██║     ██║  ██║███████╗██║  ██║
   ╚══════╝   ╚═╝   ╚═╝     ╚═╝  ╚═╝╚══════╝╚═╝  ╚═╝
   Zero-Knowledge P2P Mesh Communication Engine
`);

switch (command) {
  case 'id':
  case 'identity': {
    console.log('[*] Generating new cryptographic Sypher Identity...');
    const id = createAnonymousIdentity();
    console.log(`\n  Address (ID):   ${id.id}`);
    console.log(`  Public Key:     ${id.publicKeyHex}`);
    console.log(`  Fingerprint:    ${id.fingerprint}`);
    console.log('\n  [!] Keep your private seed safe. Zero servers keep your keys.\n');
    break;
  }

  case 'daemon':
  case 'node': {
    console.log('[*] Initializing Sypher Mesh Daemon on local network...');
    const identity = createAnonymousIdentity();
    const router = new MeshRouter({ ourPublicKey: identity.identityKeyPair.publicKey });
    const transport = new UDPMulticastTransport();

    router.onMessage((pkt) => {
      console.log(`\n[+] Incoming packet received! Payload size: ${pkt.payloadLength} bytes`);
    });

    router.onRelay(async (bytes) => {
      console.log(`[~] Gossiping packet to local mesh peers (${bytes.length} bytes)...`);
      await transport.broadcast(bytes);
    });

    transport.onData((data) => {
      router.receiveIncoming(data);
    });

    transport
      .start()
      .then(() => {
        console.log(`[+] Sypher Mesh Node active!`);
        console.log(`    Node ID:     ${identity.id}`);
        console.log(`    Fingerprint: ${identity.fingerprint}`);
        console.log(`    Transport:   UDP Multicast 239.255.77.88:7788`);
        console.log(`    Listening for local peers... Press Ctrl+C to stop.\n`);
      })
      .catch((err) => {
        console.error('[-] Failed to bind multicast radio:', err.message);
      });
    break;
  }

  case 'broadcast': {
    const message = args.slice(1).join(' ') || 'Hello from Sypher CLI!';
    console.log(`[*] Gossiping broadcast message to mesh: "${message}"`);
    const identity = createAnonymousIdentity();
    const transport = new UDPMulticastTransport();

    transport.start().then(async () => {
      const broadcastHash = new Uint8Array(32).fill(0xff);
      const packet = createPacket({
        recipientHash: broadcastHash,
        ephemeralKey: identity.exchangeKeyPair.publicKey,
        nonce: new Uint8Array(12).fill(0x01),
        payload: new TextEncoder().encode(message),
        flags: PacketFlags.RELAY_ALLOWED,
      });

      const serialized = serializePacket(packet);
      await transport.broadcast(serialized);
      console.log('[+] Broadcast packet transmitted across mesh!');
      setTimeout(() => transport.stop(), 500);
    });
    break;
  }

  default:
    console.log(`
Usage:
  sypher identity            Generate a new anonymous cryptographic identity
  sypher daemon              Launch the headless background P2P mesh relay node
  sypher broadcast "<msg>"   Send an unencrypted mesh broadcast announcement
  sypher help                Display this manual
    `);
    break;
}
