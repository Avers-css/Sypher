#!/usr/bin/env node

/**
 * Sypher Universal Desktop Launcher & Local Mesh Transport Daemon
 * Provides static HTTP hosting with smart port fallback (7788..7808),
 * Server-Sent Events (SSE) bridge, dual UDP Multicast (239.255.77.88:7788)
 * and Subnet Broadcast (255.255.255.255:7788) for Linux, macOS, and Windows.
 */

import http from 'node:http';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import dgram from 'node:dgram';
import { fileURLToPath } from 'node:url';
import { exec } from 'node:child_process';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const rendererDir = path.join(__dirname, 'renderer');

const BASE_HTTP_PORT = parseInt(process.env.PORT, 10) || 7788;
const HTTP_HOST = '0.0.0.0';

const MULTICAST_ADDR = '239.255.77.88';
const BROADCAST_ADDR = '255.255.255.255';
const MULTICAST_PORT = 7788;

const sseClients = new Set();
const knownPeers = new Set();
let udpSocket = null;

const MIME_TYPES = {
  '.html': 'text/html; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.js': 'application/javascript; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.ico': 'image/x-icon',
};

function getLocalIp() {
  const ifaces = os.networkInterfaces();
  for (const name of Object.keys(ifaces)) {
    for (const iface of ifaces[name]) {
      if (iface.family === 'IPv4' && !iface.internal) {
        return iface.address;
      }
    }
  }
  return '127.0.0.1';
}

function getBroadcastAddresses() {
  const addresses = new Set([BROADCAST_ADDR]);
  try {
    const ifaces = os.networkInterfaces();
    for (const name of Object.keys(ifaces)) {
      for (const iface of ifaces[name]) {
        if (iface.family === 'IPv4' && !iface.internal) {
          const ipParts = iface.address.split('.').map(Number);
          const maskParts = (iface.netmask || '255.255.255.0').split('.').map(Number);
          const bcastParts = ipParts.map((b, i) => (b | (~maskParts[i] & 255)));
          addresses.add(bcastParts.join('.'));
        }
      }
    }
  } catch {}
  return Array.from(addresses);
}

function initUdpMulticast() {
  try {
    const socket = dgram.createSocket({ type: 'udp4', reuseAddr: true });

    socket.on('error', (err) => {
      console.warn('[UDP Multicast] Socket notice:', err.message);
    });

    socket.on('message', (msg, rinfo) => {
      const raw = msg.toString('utf8');
      if (rinfo && rinfo.address && rinfo.address !== '127.0.0.1' && rinfo.address !== getLocalIp()) {
        knownPeers.add(rinfo.address);
      }

      for (const client of sseClients) {
        try {
          client.write(`data: ${raw}\n\n`);
        } catch {
          sseClients.delete(client);
        }
      }
    });

    socket.bind(MULTICAST_PORT, () => {
      try {
        socket.addMembership(MULTICAST_ADDR);
      } catch (e) {
        console.warn('[!] Multicast membership notice:', e.message);
      }
      try {
        socket.setBroadcast(true);
      } catch {}
      try {
        socket.setMulticastTTL(4);
        socket.setMulticastLoopback(true);
      } catch {}
      udpSocket = socket;
      console.log(`[+] UDP Mesh radio active on port ${MULTICAST_PORT} (Multicast & Broadcast)`);
    });
  } catch (err) {
    console.warn('[!] Could not initialize UDP socket:', err.message);
  }
}

function broadcastUdp(rawString) {
  if (!udpSocket) return;
  const buffer = Buffer.from(rawString, 'utf8');

  // 1. Multicast send
  try {
    udpSocket.send(buffer, 0, buffer.length, MULTICAST_PORT, MULTICAST_ADDR, () => {});
  } catch {}

  // 2. Subnet Broadcast send across all active network adapters
  const bcasts = getBroadcastAddresses();
  for (const baddr of bcasts) {
    try {
      udpSocket.send(buffer, 0, buffer.length, MULTICAST_PORT, baddr, () => {});
    } catch {}
  }

  // 3. Direct Unicast UDP to known peer IPs
  for (const peerIp of knownPeers) {
    try {
      udpSocket.send(buffer, 0, buffer.length, MULTICAST_PORT, peerIp, () => {});
    } catch {}

    // 4. HTTP POST TCP Fallback directly to peer (Android 7789 or Desktop 7788)
    try {
      [7789, 7788].forEach((port) => {
        const req = http.request({
          hostname: peerIp,
          port: port,
          path: '/api/mesh/send',
          method: 'POST',
          headers: {
            'Content-Type': 'application/json',
            'Content-Length': buffer.length,
          },
          timeout: 1000,
        }, () => {});
        req.on('error', () => {});
        req.write(buffer);
        req.end();
      });
    } catch {}
  }
}

function findAvailablePort(startPort, maxAttempts = 20) {
  return new Promise((resolve) => {
    let port = startPort;
    function test() {
      const tester = http.createServer();
      tester.once('error', (err) => {
        if (err.code === 'EADDRINUSE') {
          port++;
          if (port < startPort + maxAttempts) test();
          else resolve(startPort);
        } else {
          resolve(port);
        }
      });
      tester.once('listening', () => {
        tester.close(() => resolve(port));
      });
      tester.listen(port, '127.0.0.1');
    }
    test();
  });
}

async function startWebServer() {
  initUdpMulticast();

  const activePort = await findAvailablePort(BASE_HTTP_PORT);
  const myIp = getLocalIp();

  const server = http.createServer((req, res) => {
    const parsedUrl = new URL(req.url, `http://${req.headers.host || '127.0.0.1'}`);
    const pathname = parsedUrl.pathname;

    // API: Server-Sent Events для моментальной доставки входящих пакетов
    if (pathname === '/api/mesh/events') {
      res.writeHead(200, {
        'Content-Type': 'text/event-stream',
        'Cache-Control': 'no-cache',
        'Connection': 'keep-alive',
        'Access-Control-Allow-Origin': '*',
      });
      res.write(': ping\n\n');
      sseClients.add(res);

      req.on('close', () => {
        sseClients.delete(res);
      });
      return;
    }

    // API: Определение локального IP
    if (pathname === '/api/mesh/ip') {
      res.writeHead(200, {
        'Content-Type': 'application/json',
        'Access-Control-Allow-Origin': '*',
      });
      res.end(JSON.stringify({ ip: getLocalIp() }));
      return;
    }

    // API: Отправка пакета в локальную Mesh-сеть
    if (pathname === '/api/mesh/send' && req.method === 'POST') {
      let body = '';
      req.on('data', (chunk) => {
        body += chunk;
      });
      req.on('end', () => {
        if (body) {
          broadcastUdp(body);
        }
        res.writeHead(200, {
          'Content-Type': 'application/json',
          'Access-Control-Allow-Origin': '*',
        });
        res.end(JSON.stringify({ status: 'ok' }));
      });
      return;
    }

    // Раздача статических файлов
    let reqPath = pathname === '/' || pathname === '' ? '/index.html' : pathname;
    const safePath = path.normalize(reqPath).replace(/^(\.\.[\/\\])+/, '');
    const filePath = path.join(rendererDir, safePath);

    if (!fs.existsSync(filePath) || fs.statSync(filePath).isDirectory()) {
      res.writeHead(404, { 'Content-Type': 'text/plain; charset=utf-8' });
      res.end('404 Not Found');
      return;
    }

    const ext = path.extname(filePath).toLowerCase();
    const contentType = MIME_TYPES[ext] || 'application/octet-stream';

    try {
      const content = fs.readFileSync(filePath);
      res.writeHead(200, {
        'Content-Type': contentType,
        'Content-Length': content.length,
        'Cache-Control': 'no-cache',
      });
      res.end(content);
    } catch {
      res.writeHead(500, { 'Content-Type': 'text/plain; charset=utf-8' });
      res.end('Internal Server Error');
    }
  });

  server.listen(activePort, HTTP_HOST, () => {
    const localUrl = `http://127.0.0.1:${activePort}`;
    const lanUrl = `http://${myIp}:${activePort}`;
    console.log(`
==============================================================
   SYPHER MESH MESSENGER (Production Desktop Engine)
   Zero-Knowledge, Anonymous, Serverless P2P Messenger
==============================================================
  [+] Local Web:   ${localUrl}
  [+] Network IP:  ${lanUrl}
  [+] Radio Mesh:  UDP Multicast ${MULTICAST_ADDR}:${MULTICAST_PORT} & Subnet Broadcast
==============================================================
`);
    openBrowser(localUrl);
  });

  process.on('SIGINT', () => {
    console.log('\n[*] Stopping Sypher node...');
    if (udpSocket) {
      try { udpSocket.close(); } catch {}
    }
    server.close(() => process.exit(0));
  });
}

function openBrowser(url) {
  const platform = process.platform;
  let cmd = '';

  if (platform === 'darwin') {
    cmd = `open "${url}"`;
  } else if (platform === 'win32') {
    cmd = `start "" "${url}"`;
  } else {
    cmd = `xdg-open "${url}" >/dev/null 2>&1 || sensible-browser "${url}" >/dev/null 2>&1 || gio open "${url}" >/dev/null 2>&1 || firefox "${url}" >/dev/null 2>&1 || google-chrome "${url}" >/dev/null 2>&1 || chromium "${url}" >/dev/null 2>&1 || brave-browser "${url}" >/dev/null 2>&1`;
  }

  exec(cmd, (err) => {
    if (err) {
      console.log(`[*] Please navigate to ${url} in your browser.`);
    }
  });
}

startWebServer();
