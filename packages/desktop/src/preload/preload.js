import { contextBridge, ipcRenderer } from 'electron';

contextBridge.exposeInMainWorld('sypherBridge', {
  sendPacket: (channel, data) => ipcRenderer.invoke('sypher:send', { channel, data }),
  onIncomingPacket: (callback) => {
    const subscription = (_event, value) => callback(value);
    ipcRenderer.on('sypher:incoming', subscription);
    return () => ipcRenderer.removeListener('sypher:incoming', subscription);
  },
  onEmergencyBurn: (callback) => {
    ipcRenderer.on('sypher:emergency-burn', () => callback());
  },
  getSystemInfo: () => ({
    platform: process.platform,
    version: '1.0.0',
    meshActive: true,
  }),
});
