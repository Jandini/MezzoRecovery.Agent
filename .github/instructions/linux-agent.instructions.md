---
applyTo: "**/*.cs"
---

# Agent C# implementation rules

## Native AOT / trimming (mandatory)

- **No runtime reflection**: no `Type.GetType`, no `Assembly.GetTypes`, no attribute-scanning plugins.
- **No `dynamic`**, no late-bound invocation.
- **`System.Text.Json` only** for serialization; use **source-generated serializers** (`[JsonSerializable]`) for all hub DTOs and config models.
- All DTOs must be **explicit, stable, and declared types** - no anonymous objects or `JsonDocument` gymnastics in hot paths.
- Any third-party library with uncertain AOT support must be flagged with a comment: `// Requires AOT validation`.

## SignalR hub client

- Wrap `HubConnection` in a **single connection manager service** with clear lifetime, auto-reconnect loop, and disposal.
- Register on connect (send machine identity + capabilities) before accepting commands.
- All hub method handlers must be **idempotent**: duplicate messages must not corrupt drive state or double-execute jobs.
- Hub callbacks translate to state updates or scheduler commands - no domain rules inline in lambdas.
- Keep messages small; use hub only as control plane.

## Drive and job exclusivity

- **Per-drive concurrency = 1** enforced by the drive controller. No exceptions.
- The central `JobScheduler` owns drive assignment; drive controllers never self-assign.
- Changer/library is a shared exclusive resource - one operation at a time.
- Jobs carry: `JobId`, `JobType` (enum), typed parameters, `TargetDrive?`, `Timeout`, `CorrelationId`, `CancellationToken`.
- All job execution must respect cancellation and timeout. Report progress via the connection manager, not directly on `HubConnection`.

## Security

- **Validate every server message** before acting. Reject unknown `JobType` values; never fall through to default execution.
- No raw shell execution from server-provided strings. Allowed tools (`mt`, `tar`, device paths) are **statically whitelisted** in the dispatcher.
- Secrets (auth tokens, enrollment tokens) are never hardcoded. Load from environment variables or mounted config files at startup.
- Audit log every received command, executed action, and result at `Information` or above.

## Service lifetime and shutdown

- Use `IHostedService` / `BackgroundService` for the connection loop and heartbeat.
- On graceful shutdown: stop accepting new jobs, drain/cancel active jobs per policy, flush logs before exit.
- `systemd` shutdown signal (`SIGTERM`) must be handled cleanly via `IHostApplicationLifetime`.

## C# conventions

- **File-scoped namespaces** everywhere.
- **`sealed` by default**; if not sealed, state why.
- Prefer **primary constructors** (C# 12+) and constructor injection.
- No em dashes or en dashes in strings or comments. ASCII hyphen-minus only.
- Never write decorative section-divider comments (`// ===...`, `// ---...`, `// *** Section ***`).
