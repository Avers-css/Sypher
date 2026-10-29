import { app, BrowserWindow, ipcMain, Tray, Menu } from 'electron';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);

let mainWindow = null;
let tray = null;

function createWindow() {
  mainWindow = new BrowserWindow({
    width: 1200,
    height: 800,
    minWidth: 800,
    minHeight: 600,
    frame: true,
    titleBarStyle: 'hiddenInset',
    backgroundColor: '#0a0d14',
    webPreferences: {
      preload: path.join(__dirname, '../preload/preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
    },
  });

  mainWindow.loadFile(path.join(__dirname, '../renderer/index.html'));

  mainWindow.on('closed', () => {
    mainWindow = null;
  });
}

function createTray() {
  // Safe tray initialization
  try {
    const contextMenu = Menu.buildFromTemplate([
      { label: 'Open Sypher', click: () => mainWindow && mainWindow.show() },
      { type: 'separator' },
      {
        label: 'Emergency Burn & Exit',
        click: () => {
          if (mainWindow) {
            mainWindow.webContents.send('sypher:emergency-burn');
          }
          setTimeout(() => app.exit(0), 500);
        },
      },
      { label: 'Quit', click: () => app.quit() },
    ]);
  } catch (err) {
    console.error('Tray init warning:', err);
  }
}

app.whenReady().then(() => {
  createWindow();
  createTray();

  app.on('activate', () => {
    if (BrowserWindow.getAllWindows().length === 0) createWindow();
  });
});

app.on('window-all-closed', () => {
  if (process.platform !== 'darwin') {
    app.quit();
  }
});
