# Desktop app

`Iridium.Desktop/` is an Electron shell for Windows. It loads the web client from a node (default
`https://iridiumonline.net`, changeable from the node chip in the title bar), so UI changes ship with a
node deploy. The app itself only updates for desktop features.

What the shell adds over the browser:

- A screen-share picker with Applications and Screens (Electron has none built in). "Share sound" uses
  Windows process loopback through a small native helper (`native/AppAudio`, built with Native AOT into
  `resources/bin/iridium-app-audio.exe`): an app window sends only that program's sound, a whole screen sends
  everything except Iridium. Unlike Chromium's loopback this doesn't depend on the default output device, so
  GoXLR/Sonar-style setups work. The helper streams 48 kHz float stereo; `Iridium.Web/wwwroot/js/desktopAppAudio.js`
  turns it into the share's audio track. If the helper can't start, the app falls back to system loopback.
- Calls keep running at full speed while the window is minimised (`backgroundThrottling: false`).
- Only the node stays in the window and gets device permissions; other links open in the browser.
- `window.iridiumDesktop` (`platform`, `version`, `takeAppAudio`) lets the web client detect the app and claim per-app sound.

## Updates

The app checks the GitHub releases of SkyeShade/Iridium at launch and every 30 minutes, downloads a newer
version in the background, then shows a green download button at the top right. Pressing it restarts into
the new version; quitting normally installs it too.

Every Iridium release should carry the desktop assets, or the app will see no update for that version:

```powershell
# After creating the GitHub release v0.2.10-alpha with the server archive:
./scripts/build-desktop-release.ps1 -Version 0.2.10-alpha -Upload
```

This uploads `Iridium-Setup-<version>.exe`, its `.blockmap` (for delta updates) and `latest.yml`.

## Development

```powershell
cd Iridium.Desktop
npm ci
npm test
dotnet publish native\AppAudio\AppAudio.csproj -c Release -o native\bin   # needs VS C++ build tools
$env:IRIDIUM_NODE_URL = "http://localhost:5185"   # optional: point at a local node
npm start
```

To try the update flow without publishing, serve another build's `dist` folder on localhost and start a
packaged build with `IRIDIUM_UPDATE_URL=http://localhost:<port>/`. Closing that app installs the test
version for real (install on quit), so uninstall it afterwards.

The installer is unsigned, so Windows SmartScreen warns on first install until the app has a code-signing
certificate.

## Icon

The icon sources are `branding/iridium-icon.svg` (full), `iridium-icon-small.svg` (simplified, for 16 and 32 px) and
`iridium-icon-maskable.svg` (PWA). `node branding/source/gen.cjs` regenerates them from `takes.cjs`. The desktop
installer uses `Iridium.Desktop/build/icon.ico`, which packs the small design at 16/32 and the full one at 48-256.
