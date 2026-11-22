#!/usr/bin/env bash
# HeatBridge Agent installer and updater.
#
# Run as root from an extracted release package:
#   sudo ./install.sh
#
# What it does (idempotent):
#   - creates the unprivileged system user "heatbridge" (no login, no home) if missing
#   - installs the release to /opt/heatbridge/agent/releases/<version> (root-owned, read-only for the service)
#   - installs /usr/local/sbin/heatbridge-agentctl and the systemd unit heatbridge-agent.service
#   - creates /etc/heatbridge/agent.json from the example if it does not exist (never overwrites it)
#   - validates the configuration with the new release, then atomically switches
#     /opt/heatbridge/agent/current and restarts the service (a few seconds of downtime)
#   - keeps the previous release for rollback and prunes older ones
#
# It does not touch other services, does not open ports and does not change firewall or SSH settings.
set -euo pipefail

readonly SERVICE=heatbridge-agent
readonly SERVICE_USER=heatbridge
readonly INSTALL_ROOT=/opt/heatbridge/agent
readonly RELEASES="$INSTALL_ROOT/releases"
readonly CONFIG_DIR=/etc/heatbridge
readonly UNIT_PATH=/etc/systemd/system/$SERVICE.service
readonly CTL_PATH=/usr/local/sbin/heatbridge-agentctl
readonly KEEP_RELEASES=3

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

log() { printf '==> %s\n' "$*"; }
die() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

[[ $EUID -eq 0 ]] || die "Run as root: sudo $0"
[[ "$(uname -m)" == "aarch64" ]] || die "This package is for linux-arm64 (aarch64), but this machine is $(uname -m)."
command -v systemctl >/dev/null || die "systemd is required."
[[ -f "$here/VERSION" && -f "$here/app/HeatBridge.Agent" ]] || die "Run this script from an extracted HeatBridge Agent release package."

version="$(tr -d '[:space:]' < "$here/VERSION")"
[[ "$version" =~ ^[A-Za-z0-9._+-]+$ ]] || die "Invalid version '$version' in VERSION."
target="$RELEASES/$version"
current="$(readlink -f "$INSTALL_ROOT/current" 2>/dev/null || true)"

[[ "$current" != "$target" ]] || die "Release $version is already active. Build a new release to update (publish.ps1 creates unique versions)."

# 1. Service account.
if ! id -u "$SERVICE_USER" >/dev/null 2>&1; then
    log "Creating system user '$SERVICE_USER' (no login shell, no home directory)"
    useradd --system --user-group --no-create-home --home-dir /nonexistent --shell /usr/sbin/nologin "$SERVICE_USER"
fi

# 2. Application files.
log "Installing release $version"
install -d -m 0755 -o root -g root /opt/heatbridge "$INSTALL_ROOT" "$RELEASES"
rm -rf "$target"
staging="$(mktemp -d "$RELEASES/.staging.XXXXXX")"
trap 'rm -rf "$staging"' EXIT
cp -a "$here/app/." "$staging/"
chown -R root:root "$staging"
find "$staging" -type d -exec chmod 0755 {} +
find "$staging" -type f -exec chmod 0644 {} +
chmod 0755 "$staging/HeatBridge.Agent"
mv "$staging" "$target"
trap - EXIT

# 3. Management tool and systemd unit.
install -m 0755 -o root -g root "$here/heatbridge-agentctl" "$CTL_PATH"
install -m 0644 -o root -g root "$here/heatbridge-agent.service" "$UNIT_PATH"
systemctl daemon-reload

# 4. Configuration (never overwritten).
install -d -m 0750 -o root -g "$SERVICE_USER" "$CONFIG_DIR"
install -m 0640 -o root -g "$SERVICE_USER" "$here/agent.example.json" "$CONFIG_DIR/agent.example.json"
install -m 0600 -o root -g root "$here/agent.env.example" "$CONFIG_DIR/agent.env.example"
if [[ ! -f "$CONFIG_DIR/agent.json" ]]; then
    install -m 0640 -o root -g "$SERVICE_USER" "$here/agent.example.json" "$CONFIG_DIR/agent.json"
    log "Created $CONFIG_DIR/agent.json from the example - review it before starting the service"
fi

# 5. Activate, if the configuration is complete and valid for this release.
if [[ ! -s "$CONFIG_DIR/agent-secret" ]]; then
    ln -sfn "$target" "$INSTALL_ROOT/current.new" && mv -Tf "$INSTALL_ROOT/current.new" "$INSTALL_ROOT/current"
    log "Installed $version, but the service was not started: no agent secret yet."
    cat <<EOF

Next steps:
  1. Review the configuration:      sudo nano $CONFIG_DIR/agent.json
  2. Create the agent secret:       sudo heatbridge-agentctl new-secret
     and register the printed hash on the HeatBridge server.
  3. Development tunnel only:       sudo install -m 600 $CONFIG_DIR/agent.env.example $CONFIG_DIR/agent.env
  4. Check and start:               sudo heatbridge-agentctl check-config && sudo heatbridge-agentctl enable
EOF
    exit 0
fi

log "Validating configuration with release $version"
if ! "$CTL_PATH" check-config "$target"; then
    die "Configuration is not valid for release $version; the running version was left unchanged. Fix $CONFIG_DIR/agent.json and run install.sh again."
fi

ln -sfn "$target" "$INSTALL_ROOT/current.new" && mv -Tf "$INSTALL_ROOT/current.new" "$INSTALL_ROOT/current"
if [[ -n "$current" && -d "$current" ]]; then
    ln -sfn "$current" "$INSTALL_ROOT/previous.new" && mv -Tf "$INSTALL_ROOT/previous.new" "$INSTALL_ROOT/previous"
fi

systemctl enable "$SERVICE" >/dev/null 2>&1
systemctl restart "$SERVICE"
log "Active release: $version (previous: ${current:+$(basename "$current")}${current:-none})"
sleep 2
systemctl --no-pager --lines=10 status "$SERVICE" || true

# 6. Prune old releases, always keeping current and previous.
keep_current="$(readlink -f "$INSTALL_ROOT/current" 2>/dev/null || true)"
keep_previous="$(readlink -f "$INSTALL_ROOT/previous" 2>/dev/null || true)"
mapfile -t releases < <(find "$RELEASES" -mindepth 1 -maxdepth 1 -type d ! -name '.*' -printf '%T@ %p\n' | sort -rn | cut -d' ' -f2-)
for ((i = KEEP_RELEASES; i < ${#releases[@]}; i++)); do
    dir="${releases[$i]}"
    if [[ "$dir" != "$keep_current" && "$dir" != "$keep_previous" ]]; then
        log "Removing old release $(basename "$dir")"
        rm -rf "$dir"
    fi
done
