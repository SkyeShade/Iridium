const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('titlebar', {
    getInfo: () => ipcRenderer.invoke('titlebar:info'),
    changeNode: address => ipcRenderer.invoke('titlebar:change-node', address),
    installUpdate: () => ipcRenderer.send('titlebar:install-update'),
    onUpdateState: callback => ipcRenderer.on('titlebar:update-state', (_event, state) => callback(state))
});
