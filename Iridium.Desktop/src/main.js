const { app, BaseWindow, WebContentsView, Menu, desktopCapturer, ipcMain, session, shell } = require('electron');
const fs = require('node:fs');
const path = require('node:path');
const { createSettingsStore, normalizeNodeUrl } = require('./settings');
const { allowsPermission, isExternalWebUrl, isNodeUrl } = require('./navigation');
const { createUpdater } = require('./updater');

const TITLE_BAR_HEIGHT = 32;
const BAR_COLOR = '#111319';
const ICON = path.join(__dirname, 'titlebar', 'icon.png');

app.setAppUserModelId('net.iridiumonline.desktop');
if (!app.requestSingleInstanceLock()) app.quit();

let window = null;
let titleView = null;
let contentView = null;
let pickerView = null;
let resolvePicker = null;
let settings = null;
let updater = null;

function log(message) {
    try {
        const directory = path.join(app.getPath('userData'), 'logs');
        fs.mkdirSync(directory, { recursive: true });
        fs.appendFileSync(path.join(directory, 'main.log'), `${new Date().toISOString()} ${message}\n`);
    } catch { /* logging must never take the app down */ }
}

// IRIDIUM_NODE_URL lets a dev build point at a local node without touching the saved setting.
function nodeUrl() {
    return normalizeNodeUrl(process.env.IRIDIUM_NODE_URL) ?? settings.get().nodeUrl;
}

function layout() {
    if (!window) return;
    const { width, height } = window.getContentBounds();
    titleView.setBounds({ x: 0, y: 0, width, height: TITLE_BAR_HEIGHT });
    contentView.setBounds({ x: 0, y: TITLE_BAR_HEIGHT, width, height: Math.max(0, height - TITLE_BAR_HEIGHT) });
    pickerView?.setBounds({ x: 0, y: 0, width, height });
}

function createWindow() {
    const saved = settings.get().bounds;
    window = new BaseWindow({
        width: saved?.width ?? 1280, height: saved?.height ?? 800, x: saved?.x, y: saved?.y,
        minWidth: 940, minHeight: 560, show: false, icon: ICON, backgroundColor: BAR_COLOR, title: 'Iridium',
        titleBarStyle: 'hidden',
        titleBarOverlay: { color: BAR_COLOR, symbolColor: '#a8afbd', height: TITLE_BAR_HEIGHT }
    });

    titleView = new WebContentsView({
        webPreferences: { preload: path.join(__dirname, 'titlebar', 'preload.js'), sandbox: true, contextIsolation: true }
    });
    titleView.setBackgroundColor(BAR_COLOR);
    titleView.webContents.loadFile(path.join(__dirname, 'titlebar', 'titlebar.html'));

    contentView = new WebContentsView({
        webPreferences: {
            preload: path.join(__dirname, 'content-preload.js'), sandbox: true, contextIsolation: true,
            // Calls keep running when the window is minimised or covered.
            backgroundThrottling: false, spellcheck: true,
            additionalArguments: [`--iridium-desktop-version=${app.getVersion()}`]
        }
    });
    contentView.setBackgroundColor(BAR_COLOR);
    wireContent(contentView.webContents);

    window.contentView.addChildView(titleView);
    window.contentView.addChildView(contentView);
    layout();
    window.on('resize', layout);
    window.on('close', () => {
        if (!window.isMaximized() && !window.isMinimized()) settings.update({ bounds: window.getBounds() });
    });
    window.on('closed', () => { window = null; });

    contentView.webContents.loadURL(nodeUrl());
    contentView.webContents.once('did-finish-load', () => window?.show());
    // Show the window even if the node is slow or unreachable, so the app never looks like it didn't start.
    setTimeout(() => window?.show(), 4000);
}

function wireContent(contents) {
    contents.setWindowOpenHandler(({ url }) => {
        if (isExternalWebUrl(url) && !isNodeUrl(url, nodeUrl())) shell.openExternal(url);
        return { action: 'deny' };
    });
    contents.on('will-navigate', (event, url) => {
        if (isNodeUrl(url, nodeUrl())) return;
        event.preventDefault();
        if (isExternalWebUrl(url)) shell.openExternal(url);
    });
    contents.on('did-fail-load', (_event, code, description, url, isMainFrame) => {
        // -3 is an aborted load (navigation replaced), not an outage.
        if (!isMainFrame || code === -3) return;
        log(`load failed ${code} ${description} ${url}`);
        contents.loadFile(path.join(__dirname, 'offline.html'), { query: { node: nodeUrl() } });
    });
    contents.on('before-input-event', (event, input) => {
        if (input.type !== 'keyDown') return;
        const key = input.key.toLowerCase();
        if (input.control && key === 'r') { contents.reload(); event.preventDefault(); }
        else if (input.control && input.shift && key === 'i') { contents.toggleDevTools(); event.preventDefault(); }
        else if (input.control && (key === '=' || key === '+')) { contents.setZoomLevel(contents.getZoomLevel() + .5); event.preventDefault(); }
        else if (input.control && key === '-') { contents.setZoomLevel(contents.getZoomLevel() - .5); event.preventDefault(); }
        else if (input.control && key === '0') { contents.setZoomLevel(0); event.preventDefault(); }
    });
    contents.on('context-menu', (_event, params) => {
        const items = [];
        for (const suggestion of params.dictionarySuggestions.slice(0, 5))
            items.push({ label: suggestion, click: () => contents.replaceMisspelling(suggestion) });
        if (items.length) items.push({ type: 'separator' });
        if (params.isEditable) items.push({ role: 'cut' }, { role: 'copy' }, { role: 'paste' }, { type: 'separator' }, { role: 'selectAll' });
        else if (params.selectionText) items.push({ role: 'copy' });
        if (params.linkURL && isExternalWebUrl(params.linkURL))
            items.push({ type: 'separator' }, { label: 'Copy Link', click: () => require('electron').clipboard.writeText(params.linkURL) });
        if (items.length) Menu.buildFromTemplate(items).popup({ window });
    });
}

function wireSession() {
    const defaultSession = session.defaultSession;
    defaultSession.setPermissionRequestHandler((_contents, permission, callback, details) =>
        callback(allowsPermission(permission, details.requestingUrl, nodeUrl())));
    defaultSession.setPermissionCheckHandler((_contents, permission, requestingOrigin) =>
        allowsPermission(permission, requestingOrigin, nodeUrl()));
    defaultSession.setDisplayMediaRequestHandler(async (request, callback) => {
        if (!isNodeUrl(request.securityOrigin, nodeUrl()) || pickerView) return callback({});
        try {
            const choice = await pickDisplaySource();
            if (!choice) return callback({});
            callback(choice.audio ? { video: choice.source, audio: 'loopback' } : { video: choice.source });
        } catch (error) {
            log(`screen picker failed: ${error?.message ?? error}`);
            callback({});
        }
    });
}

async function pickDisplaySource() {
    const sources = await desktopCapturer.getSources({
        types: ['window', 'screen'], thumbnailSize: { width: 400, height: 225 }, fetchWindowIcons: true
    });
    const ownWindow = window.getMediaSourceId();
    const listed = sources.filter(source => source.id !== ownWindow && source.name);

    pickerView = new WebContentsView({
        webPreferences: { preload: path.join(__dirname, 'picker', 'preload.js'), sandbox: true, contextIsolation: true }
    });
    pickerView.setBackgroundColor('#00000000');
    window.contentView.addChildView(pickerView);
    layout();
    await pickerView.webContents.loadFile(path.join(__dirname, 'picker', 'picker.html'));
    pickerView.webContents.focus();
    pickerView.webContents.send('picker:sources', listed.map(source => ({
        id: source.id,
        name: source.name,
        kind: source.id.startsWith('screen:') ? 'screen' : 'window',
        thumbnail: source.thumbnail.toDataURL(),
        icon: source.appIcon && !source.appIcon.isEmpty() ? source.appIcon.toDataURL() : null
    })));

    const choice = await new Promise(resolve => { resolvePicker = resolve; });
    resolvePicker = null;
    window?.contentView.removeChildView(pickerView);
    pickerView.webContents.close();
    pickerView = null;
    if (!choice) return null;
    const source = listed.find(value => value.id === choice.sourceId);
    return source ? { source, audio: choice.audio === true } : null;
}

function wireIpc() {
    ipcMain.handle('titlebar:info', () => ({ nodeUrl: nodeUrl(), update: updater.getState() }));
    ipcMain.handle('titlebar:change-node', (_event, address) => {
        const normalized = normalizeNodeUrl(address);
        if (!normalized) return { ok: false, error: 'Not a valid node address' };
        settings.update({ nodeUrl: normalized });
        contentView.webContents.loadURL(normalized);
        return { ok: true, nodeUrl: normalized };
    });
    ipcMain.on('titlebar:install-update', () => updater.install());
    ipcMain.on('picker:choose', (_event, choice) => resolvePicker?.(choice));
}

app.on('second-instance', () => {
    if (!window) return;
    if (window.isMinimized()) window.restore();
    window.focus();
});

app.whenReady().then(() => {
    Menu.setApplicationMenu(null);
    settings = createSettingsStore(app.getPath('userData'));
    updater = createUpdater({
        app, log,
        onState: state => titleView?.webContents.send('titlebar:update-state', state)
    });
    wireSession();
    wireIpc();
    createWindow();
    updater.start();
});

app.on('window-all-closed', () => app.quit());
