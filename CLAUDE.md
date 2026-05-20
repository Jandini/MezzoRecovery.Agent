# MezzoRecovery.Agent — CLAUDE.md

> Inherits all rules from [CLAUDE.md](../MezzoRecovery.Solution/CLAUDE.md) in `MezzoRecovery.Solution`. This file adds Agent-specific context only.

## What This Is

The `mra` binary — a **single privileged Linux agent** that runs as a `systemd` service on each customer recovery host. It controls physical tape drives, manages device discovery, and communicates with the API via SignalR.

- **Runtime:** .NET 10, Native AOT (Release only), Worker Service hosting model
- **Assembly name:** `mra`
- **Output:** Native AOT binary deployed to `/opt/mezzorecovery-agent/mra`

---

## Think Before Coding

1. Is this code on the hot path for AOT? No reflection, no `dynamic`, source-gen JSON only.
2. Does this touch the SignalR contract? Changes must be mirrored in `MezzoRecovery.Api`.
3. Is this a new command? Add to `AgentCli.Build()`, not ad-hoc.
4. Does this touch device I/O? Ensure per-drive lock via `TapeDeviceLockManager`.

---

## Project Structure

```
src/MezzoRecovery.Agent/
  AgentMain.cs          ← CLI entry point (System.CommandLine root)
  AgentCli.cs           ← builds command tree
  Program.cs            ← IHost setup, DI registration
  Commands/             ← one folder per CLI command (Enroll, Run, Status, Update, Restart, Version)
  Runtime/
    AgentConnectionLoop.cs  ← outer reconnect loop, process lock
    ProcessLock.cs          ← single-instance enforcement (/var/lock/mra.lock)
  Api/
    AgentApiClient.cs       ← typed HTTP client (enrollment, token refresh)
  Contracts/
    ApiDtos.cs              ← API response/request shapes
    ConfigModels.cs         ← config file shapes
    TapeOperationContracts.cs ← SignalR command/event records
  Devices/
    AgentDeviceStateStore.cs    ← in-memory device state
    DeviceReportPublisher.cs    ← pushes device state to AgentHub
    TapeDeviceDiscoveryService.cs
    TapeDeviceStatusPoller.cs
    TapeGstatLabels.cs
  Identity/               ← JWT credential load/save, enrollment token handling
  Configuration/          ← config file path resolution, loader
  TapeOperations/
    TapeDeviceLockManager.cs    ← per-drive exclusive lock (SemaphoreSlim)
    TapeReadRunner.cs           ← executes a tape read job
    TapeMediaControlService.cs  ← rewind, eject, space operations
    StopOperationHandler.cs     ← cancels in-flight operations
    TapeOperationReporter.cs    ← progress/status reporting back to hub
    TapeOperationStateStore.cs  ← tracks active operations
    TapeProgressMapper.cs
  Extensions/             ← DI extension methods
tests/MezzoRecovery.Agent.Tests/
```

---

## CLI Commands

| Command | Purpose |
|---|---|
| `mra enroll ENROLLMENT_CODE` | Exchange enrollment code for JWT; write to credential file |
| `mra run` | Start the agent service loop (used by systemd) |
| `mra status` | Print current agent/device state |
| `mra update` | Self-update binary from API |
| `mra restart` | Restart the systemd service |
| `mra version` | Print version info |

---

## SignalR Contract (AgentHub)

The agent is **always the client**. Hub path: `/api/hubs/agent` (JWT auth).

### Agent → Hub (sends)
- `RegisterRuntime(hostname, osDescription, architecture, agentVersion)`
- `Heartbeat(hostname?, osDescription?, architecture?, agentVersion?)`
- `ReportDevices(devices[])` — device discovery results
- `ReportOperationProgress(...)` — streaming progress updates
- `ReportOperationCompleted(...)` — final result

### Hub → Agent (receives via `On<T>`)
- `StartTapeRead` → `StartTapeReadCommand`
- `StopTapeOperation` → `StopTapeOperationCommand`
- `ExecuteTapeMediaAction` → `ExecuteTapeMediaActionCommand`
- `RefreshTapeDevice` → `RefreshTapeDeviceCommand`

**Adding a new command:** add a record to `TapeOperationContracts.cs`, register the handler in `AgentConnectionLoop`, and add the corresponding hub method in `MezzoRecovery.Api`.

---

## Concurrency Rules

- `TapeDeviceLockManager` uses one `SemaphoreSlim(1,1)` per device path — acquire before **any** tape I/O
- `ProcessLock` writes `/var/lock/mra.lock` — prevents running two agent instances
- `CancellationToken` from the operation's `StopOperationHandler` entry — never use `CancellationToken.None` for tape I/O

---

## AOT Rules (non-negotiable)

- All JSON: use `[JsonPropertyName]` on records; no `JsonNamingPolicy` at runtime
- No `Type.GetType`, no `Assembly.Load`, no `dynamic`
- AOT only applies in **Release** — Debug uses JIT (acceptable for dev)
- Serilog is suppressed with `IL2104` (informational only, already `NoWarn`'d) — do not add new reflection-heavy logging sinks

---

## Deployment

```
/opt/mezzorecovery-agent/
  mra                ← the binary
  config.json        ← server URL, reconnect policy
  credential.json    ← JWT (written by mra enroll; root-only read)

/etc/systemd/system/mra.service
```

Install: `curl -fsSL https://mezzorecovery.com/agent/install | sudo bash -s ENROLLMENT_CODE`  
Re-enroll: run the same command with a new code on an existing host.

---

## Dual Reference Mode

Within this solution, `TapeDrive` and `Tape` are consumed as `ProjectReference` (sibling paths).  
When built from another solution, they fall back to NuGet `PackageReference` automatically (controlled by `OwnSolutionName` / `SolutionName` MSBuild properties — do not remove this logic).
