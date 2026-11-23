#!/usr/bin/env bash
# Read-only checks: may be streamed through SSH before uploading the release.
set -euo pipefail
# Non-interactive SSH sessions for an ordinary user often omit administrative tool directories.
export PATH="${PATH:-/usr/local/bin:/usr/bin:/bin}:/usr/local/sbin:/usr/sbin:/sbin"
fail() { printf 'ERROR: %s\n' "$*" >&2; exit 1; }
[[ "$(uname -m)" == aarch64 ]] || fail 'This package requires a 64-bit ARM Linux OS (aarch64).'
for tool in systemctl python3 curl ss awk df getconf flock runuser; do
    command -v "$tool" >/dev/null || fail "Required tool is missing: $tool"
done
[[ -d /run/systemd/system ]] || fail 'A running systemd installation is required.'
printf 'Host: %s\n' "$(hostname)"
cat /etc/os-release
getconf GNU_LIBC_VERSION
free -m
df -h /opt /var /tmp
ss -ltn
printf 'Agent: '; systemctl is-active heatbridge-agent || true
printf 'Agent boot state: '; systemctl is-enabled heatbridge-agent || true
printf 'Existing web server: '; systemctl is-active heatbridge-web || true
# A low-memory preflight never stops a working service to make room.
available_kb="$(awk '/^MemAvailable:/ { print $2 }' /proc/meminfo)"
(( available_kb >= 393216 )) || fail 'Need at least 384 MiB available RAM before installation; existing services were left running.'
# Release payloads are staged on /opt, not in the Pi's RAM-backed /tmp.
available_kb="$(df -Pk /opt | awk 'NR==2 { print $4 }')"
(( available_kb >= 524288 )) || fail 'Need at least 512 MiB free on /opt.'
# /tmp holds only the small configuration import and management launcher.
available_kb="$(df -Pk /tmp | awk 'NR==2 { print $4 }')"
(( available_kb >= 16384 )) || fail 'Need at least 16 MiB free on /tmp for temporary configuration files.'
available_kb="$(df -Pk /var | awk 'NR==2 { print $4 }')"
(( available_kb >= 65536 )) || fail 'Need at least 64 MiB free on /var.'
listeners="$(ss -H -ltn 'sport = :5171')"
if [[ -n "$listeners" ]] && ! systemctl is-active --quiet heatbridge-web; then
    fail 'Port 5171 is in use by another service.'
fi
if [[ $EUID -eq 0 ]]; then
    [[ -f /etc/heatbridge-web/web.json || -f /etc/heatbridge/agent.json ]] || fail 'Neither existing web configuration nor agent device configuration was found.'
fi
echo 'Preflight passed. No services changed.'
