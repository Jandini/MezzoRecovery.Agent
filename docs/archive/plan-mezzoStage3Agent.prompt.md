## Plan: Stage 3 Agent Platform

Build Stage 3 as a cross-repo slice: the API owns persistence, enrollment, auth, and SignalR; the Agent is a Native AOT Linux worker/CLI; the App is the operator surface. I treated “What is not me” as “what is out of scope” and made that explicit below.

**Steps**
1. Prove the risky seams first. Start with a minimal Agent executable that supports `enroll`, `run`, `status`, and `version`, then do an early Native AOT linux-x64 publish to validate SignalR client viability, source-generated JSON, and config binding before building broader structure.

2. Add machine auth beside existing user auth in the API. Extend [Api/src/MezzoRecovery.Api/Program.cs](Api/src/MezzoRecovery.Api/Program.cs) with JWT bearer support for agents, keep cookie auth for users, and add `AgentOnly`, `CanViewAgents`, and `CanManageAgents` policies so human and machine auth stay separate.

3. Add Stage 3 persistence and application services. Extend [Api/src/MezzoRecovery.Infrastructure/Identity/MezzoIdentityDbContext.cs](Api/src/MezzoRecovery.Infrastructure/Identity/MezzoIdentityDbContext.cs) with `Agent`, `AgentEnrollmentToken`, `AgentCredential`, `AgentHeartbeat`, and `AgentEvent` under a new schema, then register agent services in [Api/src/MezzoRecovery.Application/Extensions/ApplicationServiceExtensions.cs](Api/src/MezzoRecovery.Application/Extensions/ApplicationServiceExtensions.cs) and [Api/src/MezzoRecovery.Infrastructure/Extensions/InfrastructureServiceExtensions.cs](Api/src/MezzoRecovery.Infrastructure/Extensions/InfrastructureServiceExtensions.cs). This keeps Stage 3 small and avoids introducing a second DbContext too early.

4. Implement API enrollment and management flows. Mirror the thin-controller style from [Api/src/MezzoRecovery.Api/Controllers/AdminUsersController.cs](Api/src/MezzoRecovery.Api/Controllers/AdminUsersController.cs) for `GET /api/agents`, `POST /api/agents`, lifecycle actions, events, `POST /api/agent/enroll`, `POST /api/agent/token`, and `POST /api/agent/heartbeat`, with hashed short codes, one-time use, expiry, rate limiting, and generic failure responses.

5. Add SignalR presence and status propagation. Introduce `/api/hubs/agent` for agent-only connectivity and `/api/hubs/app` for authenticated UI updates, then add an offline sweeper background service so `Online` and `Offline` state can be driven by connect, disconnect, and stale heartbeat thresholds.

6. Build the real Agent runtime. Replace the placeholder project in [Agent/src/MezzoRecovery.Agent/MezzoRecovery.Agent.csproj](Agent/src/MezzoRecovery.Agent/MezzoRecovery.Agent.csproj) and [Agent/src/MezzoRecovery.Agent/Class1.cs](Agent/src/MezzoRecovery.Agent/Class1.cs) with a single executable-first project that handles local machine identity, credential storage, `/run/mezzorecovery-agent.lock`, enrollment, token refresh, SignalR reconnect with exponential backoff and jitter, and periodic heartbeat reporting.

7. Add Linux install and service packaging. Publish a static installer from the Agent repo, make the default UX `curl -fsSL https://mezzorecovery.com/agent/install | sudo bash -s CODE`, download the correct AOT binary by architecture, refuse duplicate install, write config and state paths, install `mezzorecovery-agent.service`, enable it, and start it. Keep the optional dynamic `/a/{code}` installer out of the first implementation.

8. Add the App operators page and release pipeline. Build `/agents` in the App using the MudBlazor table/dialog/action patterns already present in [App/src/MezzoRecovery.App/Components/Pages/Admin/Users.razor](App/src/MezzoRecovery.App/Components/Pages/Admin/Users.razor), then replace or supplement [Agent/.github/workflows/build.yml](Agent/.github/workflows/build.yml) with an AOT artifact workflow that mirrors [Tape/Tools/.github/workflows/build-aot-scp.yml](Tape/Tools/.github/workflows/build-aot-scp.yml) to publish installer, binaries, checksums, and version metadata to `https://mezzorecovery.com/agent/`.

**Relevant files**
- [Api/src/MezzoRecovery.Api/Program.cs](Api/src/MezzoRecovery.Api/Program.cs) — add agent auth, SignalR registration, policies, and background services
- [Api/src/MezzoRecovery.Infrastructure/Identity/MezzoIdentityDbContext.cs](Api/src/MezzoRecovery.Infrastructure/Identity/MezzoIdentityDbContext.cs) — extend current EF Core context for Stage 3 tables
- [Api/src/MezzoRecovery.Application/Extensions/ApplicationServiceExtensions.cs](Api/src/MezzoRecovery.Application/Extensions/ApplicationServiceExtensions.cs) — register new agent application services
- [Api/src/MezzoRecovery.Infrastructure/Extensions/InfrastructureServiceExtensions.cs](Api/src/MezzoRecovery.Infrastructure/Extensions/InfrastructureServiceExtensions.cs) — register infrastructure dependencies and offline checker
- [Api/src/MezzoRecovery.Api/Controllers/AdminUsersController.cs](Api/src/MezzoRecovery.Api/Controllers/AdminUsersController.cs) — controller template to mirror
- [App/src/MezzoRecovery.App/Components/Pages/Admin/Users.razor](App/src/MezzoRecovery.App/Components/Pages/Admin/Users.razor) — MudBlazor interaction pattern to reuse for `/agents`
- [App/src/MezzoRecovery.App/Services/Auth/AuthorizedApiClient.cs](App/src/MezzoRecovery.App/Services/Auth/AuthorizedApiClient.cs) — existing authenticated API calling pattern
- [Agent/src/MezzoRecovery.Agent/MezzoRecovery.Agent.csproj](Agent/src/MezzoRecovery.Agent/MezzoRecovery.Agent.csproj) — convert to executable Native AOT publish settings
- [Agent/src/MezzoRecovery.Agent/Class1.cs](Agent/src/MezzoRecovery.Agent/Class1.cs) — remove placeholder and wrong namespace
- [Agent/docs/mezzo_stage3_agent_design_prompt.md](Agent/docs/mezzo_stage3_agent_design_prompt.md) — scope guard for Stage 3
- [Agent/.github/workflows/build.yml](Agent/.github/workflows/build.yml) — current workflow to replace or supplement
- [Tape/Tools/.github/workflows/build-aot-scp.yml](Tape/Tools/.github/workflows/build-aot-scp.yml) — deployment model to mirror

**Verification**
1. Validate a minimal Agent console app can publish as Native AOT for linux-x64 before broader runtime work, then repeat for linux-arm64.
2. Add API tests for agent creation, code hashing, expiry, one-time use, duplicate machine rejection, token issuance, `AgentOnly` enforcement, heartbeat updates, and offline detection.
3. Add Agent tests for config loading, machine id persistence, single-instance lock, enrollment flow, token refresh, and reconnect policy.
4. Add App validation for list rendering, role-based action visibility, add-agent dialog flow, install command rendering, and status refresh behavior.
5. Run an end-to-end manual Linux check: create agent in UI, run installer, confirm service startup, confirm API marks Online, stop service, confirm Offline.

**Decisions**
- Default install UX: static public installer plus short code argument; do not depend on `GET /a/{code}` in the first slice.
- Persistence: extend the current EF Core context now; defer a dedicated application DbContext until the non-identity domain grows.
- Agent repo shape: keep one executable project plus tests for Stage 3; avoid splitting into multiple assemblies yet.
- App route: use `/agents`, not an admin-only route, because `User` can view while `Admin` manages.
- Out of scope: tape device discovery, tape jobs, file uploads, recovery workflows, browser-to-agent communication, remote shell, and any agent command orchestration beyond enrollment, auth, heartbeat, and status.

The full plan is saved in session memory as `plan.md`. If you want, I can revise this into either:
1. a strict implementation order for one engineer
2. a parallelized workstream plan split across API, App, Agent, and CI/CD
3. a Stage 3 design document outline mapped to the prompt’s required 24 sections
