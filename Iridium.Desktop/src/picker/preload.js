const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('picker', {
    onSources: callback => ipcRenderer.on('picker:sources', (_event, sources) => callback(sources)),
    choose: choice => ipcRenderer.send('picker:choose', choice)
});
