#!/usr/bin/env bash
set -euo pipefail

IRIDIUM_ROOT="${IRIDIUM_ROOT:-/opt/iridium}"
SERVICE_USER="${SERVICE_USER:-iridium}"
SERVICE_GROUP="${SERVICE_GROUP:-iridium}"
SERVICE_NAME="${SERVICE_NAME:-iridium-server.service}"
HEALTH_URL="${HEALTH_URL:-http://127.0.0.1:5080/health}"
HEALTH_ATTEMPTS="${HEALTH_ATTEMPTS:-30}"
SQLITE_DB="${SQLITE_DB:-$IRIDIUM_ROOT/data/iridium.db}"

APP_DIR="$IRIDIUM_ROOT/app"
DATA_DIR="$IRIDIUM_ROOT/data"
CONFIG_DIR="$IRIDIUM_ROOT/config"
BACKUP_DIR="$IRIDIUM_ROOT/backups"
TIMESTAMP="$(date -u +%Y%m%d-%H%M%S)"
TMP_DIR=""
SERVER_NEW="$APP_DIR/.server.new.$$"
WEB_NEW="$APP_DIR/.web.new.$$"
SERVER_OLD="$APP_DIR/.server.old.$$"
WEB_OLD="$APP_DIR/.web.old.$$"
SWAP_STARTED=0
SERVICE_STOPPED=0
ROLLBACK_RUNNING=0

log() { printf '[Iridium] %s\n' "$*"; }
error() { printf '[Iridium] ERROR: %s\n' "$*" >&2; }

cleanup() {
    [[ -n "$TMP_DIR" && -d "$TMP_DIR" ]] && rm -rf -- "$TMP_DIR"
    [[ -d "$SERVER_NEW" ]] && rm -rf -- "$SERVER_NEW"
    [[ -d "$WEB_NEW" ]] && rm -rf -- "$WEB_NEW"
}

show_journal() {
    error "Recent service log follows:"
    journalctl -u "$SERVICE_NAME" -n 50 --no-pager >&2 || true
}

wait_for_health() {
    local attempt
    for ((attempt = 1; attempt <= HEALTH_ATTEMPTS; attempt++)); do
        if systemctl is-active --quiet "$SERVICE_NAME" && curl -fsS --max-time 2 "$HEALTH_URL" >/dev/null; then
            return 0
        fi
        sleep 1
    done
    return 1
}

rollback() {
    ROLLBACK_RUNNING=1
    trap - ERR
    set +e

    error "The new release failed. Attempting application rollback; the database will not be changed."
    systemctl stop "$SERVICE_NAME"

    local restored=0
    local failed_suffix=".failed.$TIMESTAMP"
    if [[ -d "$SERVER_OLD" ]]; then
        [[ -d "$APP_DIR/server" ]] && mv "$APP_DIR/server" "$APP_DIR/server$failed_suffix"
        mv "$SERVER_OLD" "$APP_DIR/server"
        restored=1
    fi
    if [[ -d "$WEB_OLD" ]]; then
        [[ -d "$APP_DIR/web" ]] && mv "$APP_DIR/web" "$APP_DIR/web$failed_suffix"
        mv "$WEB_OLD" "$APP_DIR/web"
        restored=1
    fi

    if [[ "$restored" -eq 0 ]]; then
        error "This was a first install, so there is no previous application to restore."
        error "The staged application remains under $APP_DIR for diagnosis."
        return 1
    fi

    systemctl start "$SERVICE_NAME"
    if wait_for_health; then
        error "Update failed; previous version restored."
        error "If the release changed the schema incompatibly, restore the database backup manually."
        return 0
    fi

    error "ROLLBACK FAILED. The previous application was restored but did not become healthy."
    show_journal
    return 1
}

on_error() {
    local exit_code=$?
    local line_number="$1"
    trap - ERR
    set +e
    error "Update aborted at line $line_number (exit $exit_code)."
    show_journal
    if [[ "$SWAP_STARTED" -eq 1 && "$ROLLBACK_RUNNING" -eq 0 ]]; then
        rollback || true
    elif [[ "$SERVICE_STOPPED" -eq 1 ]]; then
        error "Restarting the unchanged application after the pre-install failure."
        if systemctl start "$SERVICE_NAME" && wait_for_health; then
            error "The unchanged application is healthy again."
        else
            error "The unchanged application did not become healthy after restart."
            show_journal
        fi
    fi
    exit "$exit_code"
}

trap 'on_error $LINENO' ERR
trap cleanup EXIT

if [[ $# -ne 1 ]]; then
    error "Usage: $0 <direct-release-archive-url>"
    exit 2
fi
RELEASE_URL="$1"

if [[ "$EUID" -ne 0 ]]; then
    error "Run this script as root (normally with sudo)."
    exit 2
fi
if [[ -z "$IRIDIUM_ROOT" || "$IRIDIUM_ROOT" != /* || "$IRIDIUM_ROOT" == "/" ]]; then
    error "IRIDIUM_ROOT must be a specific absolute directory, not '/'."
    exit 2
fi
for command_name in curl grep tar systemctl journalctl; do
    command -v "$command_name" >/dev/null || { error "Required command is missing: $command_name"; exit 2; }
done

TMP_DIR="$(mktemp -d)"
ARCHIVE="$TMP_DIR/iridium-release.tar.gz"
EXTRACTED="$TMP_DIR/extracted"
mkdir -p "$EXTRACTED"

log "Downloading release..."
curl_arguments=(-fL --retry 3 --retry-delay 1 --connect-timeout 15)
if [[ -n "${GITHUB_TOKEN:-}" ]]; then
    curl_arguments+=(-H "Authorization: Bearer $GITHUB_TOKEN")
fi
curl "${curl_arguments[@]}" "$RELEASE_URL" -o "$ARCHIVE"

log "Validating archive..."
if tar -tzf "$ARCHIVE" | grep -Eq '(^/|(^|/)\.\.(/|$))'; then
    error "Archive contains an unsafe absolute or parent path."
    exit 1
fi
tar -xzf "$ARCHIVE" -C "$EXTRACTED"
[[ -f "$EXTRACTED/server/Iridium.Server" ]] || { error "Archive is missing server/Iridium.Server."; exit 1; }
[[ -f "$EXTRACTED/web/index.html" ]] || { error "Archive is missing web/index.html."; exit 1; }

mkdir -p "$APP_DIR" "$DATA_DIR" "$CONFIG_DIR" "$BACKUP_DIR" "$IRIDIUM_ROOT/scripts"
rm -rf -- "$SERVER_NEW" "$WEB_NEW"
cp -a "$EXTRACTED/server" "$SERVER_NEW"
cp -a "$EXTRACTED/web" "$WEB_NEW"
chmod +x "$SERVER_NEW/Iridium.Server"
chown -R "$SERVICE_USER:$SERVICE_GROUP" "$SERVER_NEW" "$WEB_NEW"
chown "$SERVICE_USER:$SERVICE_GROUP" "$DATA_DIR" "$CONFIG_DIR"

log "Stopping server..."
systemctl stop "$SERVICE_NAME"
SERVICE_STOPPED=1

backup_items=()
[[ -d "$APP_DIR/server" ]] && backup_items+=(server)
[[ -d "$APP_DIR/web" ]] && backup_items+=(web)
if [[ ${#backup_items[@]} -gt 0 ]]; then
    log "Backing up current application..."
    tar -czf "$BACKUP_DIR/app-$TIMESTAMP.tar.gz" -C "$APP_DIR" "${backup_items[@]}"
else
    log "No current application found; performing first install."
fi

if [[ -f "$SQLITE_DB" ]]; then
    log "Backing up stopped SQLite database..."
    sqlite_directory="$(dirname "$SQLITE_DB")"
    sqlite_name="$(basename "$SQLITE_DB")"
    sqlite_files=("$sqlite_name")
    [[ -f "$SQLITE_DB-wal" ]] && sqlite_files+=("$sqlite_name-wal")
    [[ -f "$SQLITE_DB-shm" ]] && sqlite_files+=("$sqlite_name-shm")
    tar -czf "$BACKUP_DIR/iridium-db-$TIMESTAMP.tar.gz" -C "$sqlite_directory" "${sqlite_files[@]}"
fi

log "Installing new release..."
SWAP_STARTED=1
[[ -d "$APP_DIR/server" ]] && mv "$APP_DIR/server" "$SERVER_OLD"
[[ -d "$APP_DIR/web" ]] && mv "$APP_DIR/web" "$WEB_OLD"
mv "$SERVER_NEW" "$APP_DIR/server"
mv "$WEB_NEW" "$APP_DIR/web"

log "Starting server..."
systemctl start "$SERVICE_NAME"
log "Waiting for health check..."
wait_for_health

rm -rf -- "$SERVER_OLD" "$WEB_OLD"
SWAP_STARTED=0
SERVICE_STOPPED=0
log "Health check passed."
log "Update complete."
