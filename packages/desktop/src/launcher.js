#!/usr/bin/env node

/**
 * Sypher Universal Desktop Launcher
 * Launches Electron desktop GUI if available, or lightweight local HTTP server
 * with system browser integration across Linux, macOS, and Windows.
 */

import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { exec, spawn } from 'node:child_process';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const rendererDir = path.join(__dirname, 'renderer');

const PORT = process.env.PORT || 7788;
const HOST = '127.0.0.1';

const MIME_TYPES = {
  '.html': 'text/html; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.js': 'application/javascript; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.ico': 'image/x-icon',
};

function tryLaunchElectron() {
  return new Promise((resolve) => {
    try {
      const electronProcess = spawn('npx', ['electron', '.'], {
        cwd: path.join(__dirname, '..'),
        stdio: 'ignore',
        shell: true,
      });

      electronProcess.on('error', () => resolve(false));
      electronProcess.on('spawn', () => resolve(true));

      // Check if process exits immediately (e.g. electron missing)
      setTimeout(() => {
        if (electronProcess.exitCode !== null) {
          resolve(false);
        } else {
          resolve(true);
        }
      }, 1500);
    } catch {
      resolve(false);
    }
  });
}

function startWebServer() {
  const server = http.createServer((req, res) => {
    let reqPath = req.url.split('?')[0];
    if (reqPath === '/' || reqPath === '') {
      reqPath = '/index.html';
    }

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
    } catch (err) {
      res.writeHead(500, { 'Content-Type': 'text/plain; charset=utf-8' });
      res.end('Internal Server Error');
    }
  });

  server.listen(PORT, HOST, () => {
    const url = `http://${HOST}:${PORT}`;
    console.log(`
==============================================================
   SYPHER MESH MESSENGER (Desktop & Web Engine)
   Zero-Knowledge, Anonymous, Serverless P2P Messenger
==============================================================
  [+] Active at:   ${url}
  [+] Transports:  Local UDP Multicast & Bluetooth Mesh
  [+] Cryptography: X25519 ECDH / Signal Double Ratchet / ChaCha20
==============================================================
`);
    openBrowser(url);
  });

  process.on('SIGINT', () => {
    console.log('\n[*] Stopping Sypher node...');
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
    cmd = `xdg-open "${url}" >/dev/null 2>&1 || sensible-browser "${url}" >/dev/null 2>&1`;
  }

  exec(cmd, (err) => {
    if (err) {
      console.log(`[*] Please open your browser at: ${url}`);
    }
  });
}

async function main() {
  const useWebOnly = process.argv.includes('--web') || process.argv.includes('--no-electron');
  if (!useWebOnly) {
    const launched = await tryLaunchElectron();
    if (launched) return;
  }
  startWebServer();
}

main();
