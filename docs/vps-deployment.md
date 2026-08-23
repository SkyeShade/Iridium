# Manual VPS deployment

Iridium uses one combined `iridium-linux-x64.tar.gz` release. It contains a self-contained Linux x64
server under `server/`, the deployable Blazor WebAssembly files under `web/`, and an informational
`release.json`. The VPS does not need a system-wide .NET runtime.

The deployment layout is:

```text
/opt/iridium/
  app/
    server/       # replaced by an update
    web/          # replaced by an update
  data/           # persistent SQLite database and media objects
  config/         # persistent local configuration and secrets
  backups/        # persistent application and database backups
  scripts/
    update-iridium.sh
```

## Build a release on Windows

From the repository root in PowerShell:

```powershell
./scripts/build-linux-release.ps1 -Version 0.1.0
```

The archive is written to `release/iridium-linux-x64.tar.gz`. Upload that file as a GitHub Release
asset. `release/` is ignored by Git.

## First install

The examples assume Debian/Ubuntu, nginx, systemd, and the `iridium.example.com` hostname. Review the
templates before installing them.

```bash
sudo useradd --system --home /opt/iridium --shell /usr/sbin/nologin iridium
sudo mkdir -p /opt/iridium/{app,data,config,backups,scripts}
sudo install -m 0755 scripts/update-iridium.sh /opt/iridium/scripts/update-iridium.sh
sudo install -m 0644 deploy/iridium-server.service /etc/systemd/system/iridium-server.service
sudo install -m 0644 deploy/nginx-iridium.conf /etc/nginx/sites-available/iridium
sudo ln -s /etc/nginx/sites-available/iridium /etc/nginx/sites-enabled/iridium
sudo systemctl daemon-reload
sudo systemctl enable iridium-server.service
sudo nginx -t && sudo systemctl reload nginx
```

Copy `deploy/appsettings.Production.example.json` to
`/opt/iridium/config/appsettings.Production.json` and set the real public hostname. Put sensitive
environment overrides in `/opt/iridium/config/iridium.env`, make it readable only by root and the
`iridium` group, and never commit that file. ASP.NET nested environment settings use double
underscores, for example `Node__AllowRegistrations=false`.

The unit sets:

```text
IRIDIUM_DATA_DIR=/opt/iridium/data
IRIDIUM_CONFIG_DIR=/opt/iridium/config
```

`IRIDIUM_DATA_DIR` places `iridium.db` and `attachments/` outside the replaceable application tree.
Without it, the existing application-relative defaults remain in effect for Rider/Windows
development. `IRIDIUM_CONFIG_DIR` optionally loads `appsettings.json` and the current environment's
`appsettings.<Environment>.json`; environment variables retain highest priority.

Run the first installation with a direct public GitHub Release asset URL:

```bash
sudo /opt/iridium/scripts/update-iridium.sh \
  https://github.com/OWNER/REPO/releases/download/v0.1.0/iridium-linux-x64.tar.gz
```

For a private release, export a short-lived token before using `sudo` and explicitly preserve it:

```bash
sudo --preserve-env=GITHUB_TOKEN /opt/iridium/scripts/update-iridium.sh \
  https://github.com/OWNER/REPO/releases/download/v0.1.0/iridium-linux-x64.tar.gz
```

The token is sent only as the download authorization header and is not stored.

Configure TLS in nginx before exposing the service publicly. nginx serves `/opt/iridium/app/web`
directly and proxies `/api/` and `/hubs/` to Kestrel on `127.0.0.1:5080`; there is no separate web
service.

## Update

```bash
ssh your-vps
sudo /opt/iridium/scripts/update-iridium.sh \
  https://github.com/OWNER/REPO/releases/download/v0.1.1/iridium-linux-x64.tar.gz
```

The updater downloads and validates the archive before stopping the server. It then backs up the
current `app/server` and `app/web`, stops SQLite writes, and archives `iridium.db` together with any
WAL/SHM sidecars. Only the two application directories are swapped. `data/`, `config/`, and
`backups/` are never replaced.

After startup it waits up to 30 seconds for `http://127.0.0.1:5080/health`. A failed start or health
check restores the previous application directories and tests them again. It deliberately does not
restore the database: binary rollback cannot safely reverse an incompatible schema migration. The
pre-update database archive in `/opt/iridium/backups/` is available for a deliberate manual restore.

Useful overrides can be supplied for a nonstandard installation:

```bash
sudo IRIDIUM_ROOT=/srv/iridium SERVICE_USER=iridium SERVICE_GROUP=iridium \
  SERVICE_NAME=iridium-server.service HEALTH_URL=http://127.0.0.1:5080/health \
  /srv/iridium/scripts/update-iridium.sh <release-url>
```

## Status and logs

```bash
systemctl status iridium-server.service
journalctl -u iridium-server.service -f
curl --fail http://127.0.0.1:5080/health
```

Media database rows contain stable media/object keys rather than local absolute paths. Relocating the
local root therefore does not bind message or profile data to `/opt/iridium`, and a later Local-to-R2
storage provider can retain the same references.
