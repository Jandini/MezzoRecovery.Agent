# MezzoRecovery Agent - architecture and conventions

Single privileged Linux agent (.NET 10, Native AOT) that connects to the MezzoRecovery central system via SignalR and safely orchestrates all tape drives on one machine.

## Non-negotiables

- **One agent per machine** - the agent represents the machine identity, not individual drives.
- **Privileged execution** (root / systemd service). Do NOT use interactive `sudo`. No raw shell execution from server input.
- **Exclusive access per drive** - no concurrent operations on the same device.
- **Native AOT + trim-friendly** - no runtime reflection, no assembly scanning, no `dynamic`, no late-bound invocation.
- **SignalR is the control plane only** - small strongly typed JSON DTOs; not for large data transfer.
- All commands from the server must be **validated, whitelisted, and explicitly typed**. Reject unknown or unsafe commands.

## Internal components

1. **Connection manager** - outbound `HubConnection`, auto-reconnect, registration handshake on connect, token refresh.
2. **Command dispatcher** - validates payloads, routes to known handlers only, enforces concurrency policy.
3. **Job scheduler** - central coordinator; queues and assigns drives; enforces per-drive exclusivity, changer exclusivity, and optional machine-level global limits.
4. **Drive manager** - discovers `/dev/nst*`, maps logical IDs to devices, tracks state (idle / busy / offline / error).
5. **Per-drive controller** - dedicated execution lane, max concurrency = 1, own state machine.
6. **Changer/library controller** - if present: shared exclusive resource, one operation at a time.
7. **Job executor** - typed `JobType` enum, `JobId`, `CorrelationId`, `Timeout`, `CancellationToken`; supports cancellation, timeout, progress reporting.
8. **Heartbeat/telemetry** - periodic: agent version, OS info, connected state, drive states, active job count.

## Job model

Every job must carry: `JobId`, `JobType` (enum), typed parameters, optional `TargetDrive`, `Timeout`, `CorrelationId`. Jobs must be **idempotent** where possible; duplicate messages must not corrupt state.

Job types: `LoadTape`, `UnloadTape`, `ReadTape`, `WriteTape`, `VerifyTape`, `Inventory`, `Rewind`, `Eject`.

## Security

- TLS only; strong auth (token or client cert); no secrets hardcoded.
- Full audit log: received commands, executed actions, results.
- Secrets from environment, mounted files, or secure storage - never in source.

## Deployment and installation

- Published as self-contained Native AOT binaries per RID (`linux-x64`, `linux-arm64`). Target machine needs no .NET SDK.
- The MezzoRecovery app hosts an install endpoint; single-line `curl | bash` install experience.
- File layout: `/opt/mezzorecovery-agent/` (binaries), `/etc/mezzorecovery-agent/` (config), `/var/lib/mezzorecovery-agent/` (durable state).
- systemd unit: `After=network-online.target`, `Restart=on-failure`.
- First-time enrollment uses a **short-lived token** generated in the UI; agent exchanges it for durable identity on first startup.

## C# conventions

- **File-scoped namespaces** everywhere.
- **`sealed` by default**; if not sealed, state why.
- Prefer **primary constructors** and constructor injection; avoid service locators.
- No em dashes (`-`) or en dashes (`-`) in strings or comments. Use ASCII hyphen-minus (`-`) only.
- Never write decorative banner or section-divider comments (`// ===...`, `// ---...`).
- `System.Text.Json` with source-generated serializers for all DTOs.

## Copilot workflow

For exploration discipline (single-repo scope, paths to skip, focused docs), see [.github/instructions/ai-coding-rules.instructions.md](instructions/ai-coding-rules.instructions.md).
