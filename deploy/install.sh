#!/usr/bin/env bash
# MezzoRecovery Agent (mra) - bootstrap installer (x86_64 Linux + systemd).
# Usage: curl -fsSL https://mezzorecovery.com/agent/install | sudo bash -s ENROLLMENT_CODE
# Re-run with a new enrollment code on an existing host to re-enroll after revoke/replace.
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

ARCH="$(uname -m)"
if [ "${ARCH}" != "x86_64" ]; then
  echo "Unsupported architecture: ${ARCH}. Only x86_64 is supported." >&2
  exit 3
fi

BIN_DIR="/opt/mezzorecovery-agent"
CFG_DIR="/etc/mezzorecovery-agent"
VAR_DIR="/var/lib/mezzorecovery-agent"
SYMLINK="/usr/local/bin/mra"
DOWNLOAD_BASE="${MEZZO_DOWNLOAD_BASE:-https://mezzorecovery.com/agent}"
API_BASE="${MEZZO_API_BASE:-https://io.mezzorecovery.com}"

for marker in "${CFG_DIR}/agent.json" "${VAR_DIR}/agent.credential" "${VAR_DIR}/machine.id"; do
  if [ -e "${marker}" ]; then
    echo "Existing MezzoRecovery Agent installation detected. Cleaning up and re-enrolling."
    if systemctl is-active --quiet mra.service 2>/dev/null; then
      systemctl stop mra.service
    fi
    rm -f "${CFG_DIR}/agent.json" "${VAR_DIR}/agent.credential"
    break
  fi
done

BIN_NAME="mra-linux-x64"
TMP_BIN="$(mktemp)"
TMP_CHECKSUM="$(mktemp)"
trap 'rm -f "${TMP_BIN}" "${TMP_CHECKSUM}"' EXIT

echo "Downloading mra binary..."
curl -fsSL "${DOWNLOAD_BASE}/${BIN_NAME}" -o "${TMP_BIN}"

echo "Verifying checksum..."
curl -fsSL "${DOWNLOAD_BASE}/${BIN_NAME}.sha256" -o "${TMP_CHECKSUM}"
EXPECTED="$(awk '{print $1}' "${TMP_CHECKSUM}")"
ACTUAL="$(sha256sum "${TMP_BIN}" | awk '{print $1}')"
if [ "${EXPECTED}" != "${ACTUAL}" ]; then
  echo "Checksum mismatch. Expected: ${EXPECTED}  Got: ${ACTUAL}" >&2
  exit 4
fi

install -d -m 0755 "${BIN_DIR}" "${CFG_DIR}" "${VAR_DIR}"

install -m 0755 "${TMP_BIN}" "${BIN_DIR}/mra"

ln -sf "${BIN_DIR}/mra" "${SYMLINK}"

cat > "${CFG_DIR}/agent.json" <<EOF
{"apiBaseUrl":"${API_BASE}"}
EOF
chmod 0644 "${CFG_DIR}/agent.json"

"${BIN_DIR}/mra" enroll "${MEZZO_CODE}" --config "${CFG_DIR}/agent.json" --credential "${VAR_DIR}/agent.credential" --machine-id "${VAR_DIR}/machine.id"

if [ ! -f /etc/systemd/system/mra.service ]; then
  cat > /etc/systemd/system/mra.service <<'UNIT'
[Unit]
Description=MezzoRecovery Linux agent (mra)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
ExecStart=/opt/mezzorecovery-agent/mra run --config /etc/mezzorecovery-agent/agent.json --credential /var/lib/mezzorecovery-agent/agent.credential
Restart=on-failure
RestartSec=5

[Install]
WantedBy=multi-user.target
UNIT
fi

systemctl daemon-reload
systemctl enable mra.service
systemctl restart mra.service

echo "MezzoRecovery Agent installed and started."
echo "Check status: systemctl status mra"
echo "Logs:         journalctl -u mra -f"
echo "Update:       sudo mra update"
