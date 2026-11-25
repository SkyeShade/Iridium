// Updates come from the GitHub releases of SkyeShade/Iridium (latest.yml + installer attached to each
// release). They download in the background; the title bar shows a green button once one is ready, and
// pressing it restarts into the new version. Quitting normally installs it too.

const CHECK_INTERVAL_MS = 30 * 60 * 1000;

function createUpdater({ app, log, onState }) {
    let state = { status: 'idle', version: null, percent: null };
    const publish = changes => {
        state = { ...state, ...changes };
        onState({ ...state });
    };

    if (!app.isPackaged) {
        return { getState: () => ({ ...state }), install() {}, start() { log('updates disabled: not packaged'); } };
    }

    const { autoUpdater } = require('electron-updater');
    autoUpdater.autoDownload = true;
    autoUpdater.autoInstallOnAppQuit = true;
    autoUpdater.disableWebInstaller = true;
    // Iridium versions carry an -alpha tag, and electron-updater skips prereleases unless told otherwise.
    autoUpdater.allowPrerelease = true;
    autoUpdater.logger = { info: log, warn: log, error: log, debug() {} };
    // Testing hook: serve a build's dist folder on localhost and point IRIDIUM_UPDATE_URL at it.
    const testFeed = process.env.IRIDIUM_UPDATE_URL;
    if (testFeed && /^http:\/\/(localhost|127\.0\.0\.1)(:\d+)?\//.test(testFeed))
        autoUpdater.setFeedURL({ provider: 'generic', url: testFeed });

    autoUpdater.on('update-available', info => publish({ status: 'downloading', version: info.version, percent: 0 }));
    autoUpdater.on('download-progress', progress => publish({ percent: Math.round(progress.percent) }));
    autoUpdater.on('update-downloaded', info => publish({ status: 'ready', version: info.version, percent: 100 }));
    autoUpdater.on('error', error => {
        log(`update error: ${error?.message ?? error}`);
        // A failed check must never leave a stale "downloading" state; a downloaded update stays installable.
        if (state.status !== 'ready') publish({ status: 'idle', percent: null });
    });

    const check = () => autoUpdater.checkForUpdates().catch(error => log(`update check failed: ${error?.message ?? error}`));

    return {
        getState: () => ({ ...state }),
        start() {
            check();
            setInterval(() => { if (state.status === 'idle') check(); }, CHECK_INTERVAL_MS).unref?.();
        },
        install() {
            if (state.status !== 'ready') return;
            // Silent installer, then relaunch, so one press gets you back into Iridium on the new version.
            setImmediate(() => autoUpdater.quitAndInstall(true, true));
        }
    };
}

module.exports = { createUpdater };
