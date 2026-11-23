#!/usr/bin/env bash
# Interactive, root-only setup. Credentials are never command-line arguments or environment values.
set -euo pipefail
[[ "${EUID}" == 0 ]] || { echo 'Run with sudo.' >&2; exit 1; }
umask 077
read -r -p 'Verified Mailjet sender email: ' sender
[[ "$sender" =~ ^[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}$ ]] || { echo 'Invalid sender email.' >&2; exit 1; }
read -r -s -p 'Mailjet API key: ' api_key; printf '\n'
read -r -s -p 'Mailjet secret key: ' secret_key; printf '\n'
[[ -n "$api_key" && -n "$secret_key" ]] || { echo 'Both keys are required.' >&2; exit 1; }
directory=/etc/heatbridge-web
[[ -f "$directory/web.json" ]] || { echo 'Install HeatBridge.Web first.' >&2; exit 1; }
temporary="$(mktemp -d "$directory/mailjet-setup.XXXXXX")"
trap 'rm -f "$temporary/api-key" "$temporary/secret-key" "$temporary/mailjet.env" "$temporary/mailjet.conf"; rmdir "$temporary"' EXIT
printf '%s' "$api_key" > "$temporary/api-key"
printf '%s' "$secret_key" > "$temporary/secret-key"
unset api_key secret_key
printf 'HeatBridge__Mailjet__FromEmail=%s\n' "$sender" > "$temporary/mailjet.env"
cat > "$temporary/mailjet.conf" <<'UNIT'
[Service]
EnvironmentFile=/etc/heatbridge-web/mailjet.env
LoadCredential=mailjet-api-key:/etc/heatbridge-web/mailjet-api-key
LoadCredential=mailjet-secret-key:/etc/heatbridge-web/mailjet-secret-key
UNIT
install -m 0600 -o root -g root "$temporary/api-key" "$directory/mailjet-api-key"
install -m 0600 -o root -g root "$temporary/secret-key" "$directory/mailjet-secret-key"
install -m 0600 -o root -g root "$temporary/mailjet.env" "$directory/mailjet.env"
install -d -m 0755 /etc/systemd/system/heatbridge-web.service.d
install -m 0644 -o root -g root "$temporary/mailjet.conf" /etc/systemd/system/heatbridge-web.service.d/mailjet.conf
systemctl daemon-reload
echo 'Credentials installed. Run: sudo heatbridge-webctl restart'
echo 'Then open E-post och larm, save recipients, send a test, and enable email.'
