---
applyTo: "**/*.sh, **/install.sh"
---

# Linux agent install script (`install.sh`)

## Shell safety

Always start with:
```bash
#!/usr/bin/env bash
set -euo pipefail
```

## Named arguments (no positional-only)

Support explicit named flags:
- `--server <URL>` - required; MezzoRecovery server base URL
- `--token <TOKEN>` - required; short-lived enrollment token
- `--version <VERSION>` - optional; pin a specific release (default: latest stable)
- `--channel <stable|preview>` - optional
- `--force` - re-install/overwrite even if already installed
- `--help`

**Never log or echo the token value.** Mask it in any `set -x` traces.

## Prerequisites validation

Fail clearly (non-zero exit + message) if any of these are not met:
- Linux OS
- Ubuntu 20.04 or higher
- `systemd` present
- Running as root (`$EUID -eq 0`)
- Required tools exist: `curl` (or `wget`), `tar`

Detect architecture (`x86_64` -> `linux-x64`, `aarch64` -> `linux-arm64`). Fail with a clear message on unsupported architectures.

## Download and integrity

- All downloads over **HTTPS** only.
- Resolve the correct versioned package URL from the server (architecture + version/channel).
- **Verify a SHA-256 checksum** after download before installing. If signature validation is not yet implemented, the script must print a visible warning noting it as a future hardening requirement.

## Filesystem layout

| Path | Purpose |
|------|---------|
| `/opt/mezzorecovery-agent/` | Agent binary and bundled assets |
| `/etc/mezzorecovery-agent/` | Configuration (`agent.json` or env file) |
| `/var/lib/mezzorecovery-agent/` | Durable identity and local state |
| `/etc/systemd/system/mezzorecovery-agent.service` | systemd unit |

Do not use temporary directories as final install destinations.

## Configuration file

Write a small, explicit config file (e.g. `agent.json` or environment file) under `/etc/mezzorecovery-agent/` containing:
- MezzoRecovery server URL
- Enrollment token (written once; agent discards it after successful registration)
- Optional: agent name, site/group, logging settings

Do not scatter configuration across multiple hidden locations.

## systemd unit

```ini
[Unit]
Description=MezzoRecovery Agent
After=network-online.target
Wants=network-online.target

[Service]
ExecStart=/opt/mezzorecovery-agent/mezzorecovery-agent
WorkingDirectory=/opt/mezzorecovery-agent
Restart=on-failure
RestartSec=10s
EnvironmentFile=/etc/mezzorecovery-agent/agent.env

[Install]
WantedBy=multi-unit.target
```

After writing the unit: `systemctl daemon-reload`, `systemctl enable mezzorecovery-agent`, `systemctl restart mezzorecovery-agent`.

## Install-or-upgrade behavior

Re-running the script is **safe by default**:
- Detect existing installation.
- Preserve `/etc/mezzorecovery-agent/` config and `/var/lib/mezzorecovery-agent/` durable state unless `--force` is given.
- Replace binaries cleanly.
- Restart service after upgrade.
- Do not re-enroll (discard enrollment token reuse) if durable identity already exists.

## Post-install verification

After starting the service, the script must check:
- Service file exists.
- Service is enabled.
- `systemctl is-active mezzorecovery-agent` returns `active`.
- Print clear instructions for checking logs: `journalctl -u mezzorecovery-agent -f`.

## Failure handling

- `set -euo pipefail` ensures unexpected failures abort the script.
- Print the failing step and an actionable message before exiting.
- On failure, do **not** leave a half-configured system silently. Print cleanup hints or rollback instructions.

## Output style

Print what the script is doing at each step. Example:
```
[MezzoRecovery Agent Installer]
  Detected: Ubuntu 22.04, x86_64
  Downloading: linux-x64 v1.3.0 ...
  Verifying checksum ...
  Installing to /opt/mezzorecovery-agent/ ...
  Writing configuration ...
  Installing systemd service ...
  Starting service ...
  Done. Agent is running.
  Check status: systemctl status mezzorecovery-agent
  View logs:    journalctl -u mezzorecovery-agent -f
```
