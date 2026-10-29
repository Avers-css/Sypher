#!/usr/bin/env node

/**
 * Sypher Universal Desktop Launcher & Local Mesh Transport Daemon
 * Provides static HTTP hosting with smart port fallback (7788..7808),
 * Server-Sent Events (SSE) bridge, and native UDP Multicast (239.255.77.88:7788)
 * for Linux, macOS, and Windows.
 */

import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import dgram from 'node:dgram';
import { fileURLToPath } from 'node:url';
import { exec } from 'node:child_process';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const rendererDir = path.join(__dirname, 'renderer');

const BASE_HTTP_PORT = parseInt(process.env.PORT, 10) || 7788;
const HTTP_HOST = '127.0.0.1';

const MULTICAST_ADDR = '239.255.77.88';
const MULTICAST_PORT = 7788;

const sseClients = new Set();
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

// 1. Инициализация локального радиосокета UDP Multicast
function initUdpMulticast() {
  try {
    const socket = dgram.createSocket({ type: 'udp4', reuseAddr: true });

    socket.on('error', (err) => {
      console.warn('[UDP Multicast] Socket notice:', err.message);
    });

    socket.on('message', (msg) => {
      const raw = msg.toString('utf8');
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
        socket.setMulticastTTL(4);
        socket.setMulticastLoopback(true);
      } catch {}
      udpSocket = socket;
      console.log(`[+] UDP Multicast radio active on ${MULTICAST_ADDR}:${MULTICAST_PORT}`);
    });
  } catch (err) {
    console.warn('[!] Could not initialize UDP socket:', err.message);
  }
}

function broadcastUdp(rawString) {
  if (!udpSocket) return;
  const buffer = Buffer.from(rawString, 'utf8');
  udpSocket.send(buffer, 0, buffer.length, MULTICAST_PORT, MULTICAST_ADDR, (err) => {
    if (err) console.error('[UDP] Send error:', err.message);
  });
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
      tester.listen(port, HTTP_HOST);
    }
    test();
  });
}

async function startWebServer() {
  initUdpMulticast();

  const activePort = await findAvailablePort(BASE_HTTP_PORT);

  const server = http.createServer((req, res) => {
    const parsedUrl = new URL(req.url, `http://${req.headers.host}`);
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
    const url = `http://${HTTP_HOST}:${activePort}`;
    console.log(`
==============================================================
   SYPHER MESH MESSENGER (Production Desktop Engine)
   Zero-Knowledge, Anonymous, Serverless P2P Messenger
==============================================================
  [+] Active at:   ${url}
  [+] Radio Mesh:  UDP Multicast ${MULTICAST_ADDR}:${MULTICAST_PORT}
  [+] Routing:     Ad-hoc Peer-to-Peer / Local Wi-Fi / Hotspot
==============================================================
`);
    openBrowser(url);
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
      console.log(`[*] Please open your browser at: ${url}`);
    }
  });
}

startWebServer();
