#!/usr/bin/env bash
# MezzoRecovery Agent — bootstrap installer (Ubuntu + systemd).
# Usage: curl -fsSL https://mezzorecovery.com/agent/install | sudo bash -s ENROLLMENT_CODE
set -euo pipefail

MEZZO_CODE="${1:-}"
if [ -z "${MEZZO_CODE}" ]; then
  echo "Usage: sudo bash install.sh ENROLLMENT_CODE" >&2
  exit 1
fi

if [ "${EUID:-}" -ne 0 ]; then
  echo "This installer must run as root (use sudo)." >&2
  exit 1
fi

if ! command -v systemctl >/dev/null 2>&1; then
  echo "systemd is required." >&2
  exit 1
fi

BIN_DIR="/opt/mezzorecovery-agent"
CFG_DIR="/etc/mezzorecovery-agent"
VAR_DIR="/var/lib/mezzorecovery-agent"
DOWNLOAD_BASE="${MEZZO_DOWNLOAD_BASE:-https://mezzorecovery.com/agent}"
API_BASE="${MEZZO_API_BASE:-https://io.mezzorecovery.com}"

for marker in "${CFG_DIR}/agent.json" "${VAR_DIR}/agent.credential" "${VAR_DIR}/machine.id"; do
  if [ -e "${marker}" ]; then
    echo "MezzoRecovery Agent already appears to be installed on this machine (${marker} exists)." >&2
    echo "Refusing to enroll a second agent." >&2
    exit 2
  fi
done

ARCH="$(uname -m)"
case "${ARCH}" in
  x86_64) RID="linux-x64" ;;
  aarch64|arm64) RID="linux-arm64" ;;
  *)
    echo "Unsupported architecture: ${ARCH}" >&2
    exit 3
    ;;
esac

BIN_NAME="mezzorecovery-agent-${RID}"
TMP_BIN="$(mktemp)"
trap 'rm -f "${TMP_BIN}"' EXIT

echo "Downloading agent binary…"
curl -fsSL "${DOWNLOAD_BASE}/${BIN_NAME}" -o "${TMP_BIN}"
chmod +x "${TMP_BIN}"

if curl -fsSL -o /tmp/mezzo-agent.sha256 "${DOWNLOAD_BASE}/${BIN_NAME}.sha256" 2>/dev/null; then
  echo "Verifying checksum…"
  EXPECTED="$(awk '{print $1}' /tmp/mezzo-agent.sha256)"
  ACTUAL="$(sha256sum "${TMP_BIN}" | awk '{print $1}')"
  if [ "${EXPECTED}" != "${ACTUAL}" ]; then
    echo "Checksum mismatch." >&2
    exit 4
  fi
fi

install -d -m 0755 "${BIN_DIR}" "${CFG_DIR}" "${VAR_DIR}"

install -m 0755 "${TMP_BIN}" "${BIN_DIR}/mezzorecovery-agent"

cat > "${CFG_DIR}/agent.json" <<EOF
{"apiBaseUrl":"${API_BASE}"}
EOF
chmod 0644 "${CFG_DIR}/agent.json"

"${BIN_DIR}/mezzorecovery-agent" enroll "${MEZZO_CODE}" --config "${CFG_DIR}/agent.json" --credential "${VAR_DIR}/agent.credential" --machine-id "${VAR_DIR}/machine.id"

cat > /etc/systemd/system/mezzorecovery-agent.service <<'UNIT'
[Unit]
Description=MezzoRecovery Linux agent
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
ExecStart=/opt/mezzorecovery-agent/mezzorecovery-agent run --config /etc/mezzorecovery-agent/agent.json --credential /var/lib/mezzorecovery-agent/agent.credential
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
UNIT

systemctl daemon-reload
systemctl enable mezzorecovery-agent.service
systemctl restart mezzorecovery-agent.service

echo "MezzoRecovery Agent installed and started."
echo "Check status: systemctl status mezzorecovery-agent"
echo "Logs: journalctl -u mezzorecovery-agent -f"
