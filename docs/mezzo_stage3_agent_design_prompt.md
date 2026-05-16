You are a principal software architect with deep expertise in:

- .NET 10
- ASP.NET Core Web API Controllers
- ASP.NET Core Identity
- SignalR
- Linux systemd services
- secure machine-to-machine authentication
- PostgreSQL
- EF Core
- Clean Architecture
- Blazor + MudBlazor UX
- Docker / CI/CD / GHCR
- robust distributed worker/agent design
- .NET Native AOT / trimming-safe Linux services
- GitHub Actions artifact deployment over SSH/SCP

Design **Stage 3** of the MezzoRecovery platform.

Stage 1 already implemented:

```text
MezzoRecovery.Api scaffold
contact form endpoint
health endpoint
email infrastructure
CI/CD
```

Stage 2 already implemented:

```text
login
sign-up
email confirmation
ASP.NET Core Identity
PostgreSQL persistence
Admin/User roles
Blazor UI integration
user management
```

Now design Stage 3 only:

```text
MezzoRecovery.Agent skeleton
agent registration/enrollment
agent token authentication
SignalR communication between Agent and API
SignalR/API updates to App
Agents page in Blazor
Linux installation script
systemd service installation
Native AOT Linux agent publishing
robust reconnect behavior
PostgreSQL persistence for agents
```

Do **not** design tape jobs, agents controlling tape devices, file uploads, tape imaging, or recovery workflows yet.

---

## Existing Workspace Context

The current workspace is a multi-repository modular platform under:

```text
C:\Workspace\MezzoRecovery
```

Relevant repositories:

```text
MezzoRecovery.Api      -> standalone API with Clean Architecture
MezzoRecovery          -> Blazor app, project: MezzoRecovery.App
MezzoRecovery.Agent    -> new repository for the Linux agent
MezzoRecovery.Solution -> meta-solution/workspace
```

The existing API repository has this structure:

```text
MezzoRecovery.Domain
MezzoRecovery.Application
MezzoRecovery.Infrastructure
MezzoRecovery.Api
MezzoRecovery.Application.Tests
MezzoRecovery.Api.Tests
```

Preserve dependency direction:

```text
Api -> Application + Infrastructure
Infrastructure -> Application + Domain
Application -> Domain
```

The Blazor app lives in:

```text
MezzoRecovery/src/MezzoRecovery.App
```

The new agent will live in a separate repository:

```text
MezzoRecovery.Agent
```

The Agent repository should have its own GitHub Actions workflow. Its deployment model should mirror the existing `MezzoRecovery.TapeTools` workflows for `mrtc` / `mrmc`: build the Linux artifact and copy the published agent binary plus install/update scripts to the server, where they are publicly reachable under:

```text
https://mezzorecovery.com/agent/
```

Examples:

```text
https://mezzorecovery.com/agent/install
https://mezzorecovery.com/agent/mra-linux-x64
https://mezzorecovery.com/agent/version.json
```

---

## Stage 3 Goal

Design a robust first version of the Linux agent system.

The goal is not tape recovery yet.

The goal is:

```text
Admin/User logs in
opens Agents page
sees existing agents as cards
adds a new agent
gives the agent a name
gets a very short install command
runs curl | sudo bash on Linux machine
agent installs as systemd service
agent enrolls using short one-time code
agent authenticates to API using machine token
agent connects to API via SignalR
API persists agent status
App shows agent online/offline/status updates
```

---

## Core Architecture

Use this communication model:

```text
MezzoRecovery.App
    |
    | HTTPS / authenticated user cookie
    v
MezzoRecovery.Api
    |
    | PostgreSQL
    v
Database

MezzoRecovery.Agent
    |
    | HTTPS + SignalR + agent token
    v
MezzoRecovery.Api
```

The App must **not** talk directly to the Agent.

The API is the central authority.

The Agent connects outbound to the API.

No inbound access to the Linux recovery machine should be required.

---

## Initial Scope

Implement only:

```text
agent records
agent enrollment
agent authentication
agent heartbeat/status
agent SignalR connection
agent install script
systemd service install
Agents page in Blazor
basic admin/user authorization
```

Do not implement yet:

```text
tape device discovery
tape jobs
file uploads
job progress
agent command execution
remote shell
tape recovery
SignalR job orchestration
```

However, design the model so those features can be added later.

---

## User Experience

After successful login, add a new page:

```text
Agents
```

The Agents page should use the existing MudBlazor theme and existing app layout.

The UX should be:

```text
professional
simple
clear
operator-friendly
easy for non-technical users
```

Use existing MudBlazor components and do not introduce another UI framework.

Suggested page behavior:

```text
- show existing agents as cards
- each card shows name, status, last seen, hostname, OS, version
- status indicators: Pending Enrollment, Online, Offline, Disabled, Revoked
- button: Add Agent
- Add Agent dialog asks for agent name and optional description
- after creation, show a short install command
- command must be easy to copy and short enough to type manually
```

Example card content:

```text
Recovery Server 01
Status: Online
Hostname: ubuntu-recovery-01
OS: Ubuntu 22.04
Last seen: 25 seconds ago
Agent version: 0.1.0
```

---

## Minimal Install Command UX

The App should generate a very short install command.

Preferred command, using the static installer hosted by the Agent GitHub Actions deployment:

```bash
curl -fsSL https://mezzorecovery.com/agent/install | sudo bash -s MK7P9D
```

Alternative:

```bash
wget -qO- https://mezzorecovery.com/agent/install | sudo bash -s MK7P9D
```

The user should not have to type:

```text
long GUIDs
long tokens
--api
--enrollment-token
many arguments
```

The short code should be:

```text
6 to 10 uppercase characters
easy to read
no confusing characters such as O, 0, I, 1, L
one-time use
short lived
rate limited
revocable
stored hashed in PostgreSQL
```

Example codes:

```text
MK7P9D
TAPE42
R8K3PN
```

The UI should show:

```text
Run this on the Linux recovery machine:
```

```bash
curl -fsSL https://mezzorecovery.com/agent/install | sudo bash -s MK7P9D
```

If the dynamic API script option is retained, the UI may alternatively show:

```bash
curl -fsSL https://io.mezzorecovery.com/a/MK7P9D | sudo bash
```

Also show:

```text
Enrollment code: MK7P9D
Expires in: 15 minutes
```

Warning text:

```text
Run this only on the Linux machine that will run the MezzoRecovery Agent.
This code works once and expires soon.
If an Agent is already installed, the script will stop.
```

---

## Agent Enrollment Flow

Design this flow:

```text
1. Authenticated user opens Agents page.
2. User clicks Add Agent.
3. User enters agent name and optional description.
4. App calls API to create pending agent registration.
5. API creates Agent row with status PendingEnrollment.
6. API creates short one-time enrollment code.
7. API stores only hashed code/token.
8. API returns minimal install command.
9. User runs command on Linux machine.
10. GET /a/{code} returns generated install script.
11. Script downloads Agent binary.
12. Script installs config/directories/systemd service.
13. Script runs agent enroll command.
14. Agent sends short code and machine fingerprint to API.
15. API validates code.
16. API rejects duplicates.
17. API creates agent credential/token.
18. API marks Agent as enrolled.
19. Agent stores credential locally.
20. Agent starts systemd service.
21. Agent connects to AgentHub via SignalR.
22. API marks Agent Online.
23. App Agents page updates.
```

---

## Agent Authentication

Do not authenticate the Agent as a normal user.

Agent must use separate machine authentication.

Use token-based agent authentication.

Design:

```text
agent_id
agent credential / secret
short-lived access token
optional refresh/rotation model
client_type = agent
```

Recommended claims:

```json
{
  "sub": "agent:<agent-id>",
  "agent_id": "<agent-id>",
  "client_type": "agent",
  "scope": "agent.connect agent.heartbeat"
}
```

The Agent must not:

```text
log in as a human user
use ASP.NET Core Identity cookies
manage users
access admin endpoints
access future jobs not assigned to it
```

Create policy:

```text
AgentOnly
```

Agent endpoints and SignalR hub must require `AgentOnly`.

Future user/browser auth and agent/machine auth must remain separate.

---

## SignalR Design

Add SignalR to the API.

Initial hub:

```text
/api/hubs/agent
```

Used only by agents.

Authorize with:

```text
AgentOnly
```

Initial AgentHub responsibilities:

```text
- agent connects
- API resolves agent from token claims
- API marks agent online
- API stores connection id if needed
- agent sends heartbeat
- agent reports basic runtime info
- API marks agent offline on disconnect
```

Initial messages from Agent to API:

```text
RegisterRuntime
Heartbeat
ReportStatus
```

Initial messages from API to Agent:

```text
Ping
RequestStatus
```

Do not design tape job commands yet.

For the App, decide whether to use:

```text
Option A: App polls /api/agents initially
Option B: App subscribes to /api/hubs/app for live updates
```

Preferred design:

```text
Add /api/hubs/app if simple.
Use it to broadcast AgentStatusChanged to authenticated users.
Keep polling as fallback.
```

AppHub initial message:

```text
AgentStatusChanged
```

---

## Robust Agent Runtime Requirements

The Agent must be robust from the start.

Design the Agent as a .NET worker service.

Requirements:

```text
runs as Linux systemd service
starts on boot
reconnects automatically
uses exponential backoff with jitter
handles API downtime
handles network drop
sends periodic heartbeat
stores credential securely on disk
does not lose local identity
has clear logs
has health/status command
does not run two instances on the same machine
supports upgrade later
```

Agent local paths:

```text
/usr/local/bin/mra
/opt/mezzorecovery-agent/mra
/etc/mezzorecovery-agent/agent.json
/var/lib/mezzorecovery-agent/
/var/lib/mezzorecovery-agent/agent.credential
/var/lib/mezzorecovery-agent/machine.id
/run/mezzorecovery-agent.lock
```

Service name:

```text
mra.service
```

The installer must refuse duplicate install by default.

---

## Prevent Duplicate Agent on Same Machine

Guarantee:

```text
One Linux machine = one active MezzoRecovery Agent installation
```

Use defense in depth:

```text
local install checks
fixed systemd service name
runtime process lock
stable machine id
machine fingerprint
PostgreSQL unique constraint
API duplicate enrollment rejection
```

Installer should check:

```text
/etc/mezzorecovery-agent/agent.json
/var/lib/mezzorecovery-agent/agent.credential
/var/lib/mezzorecovery-agent/machine.id
/etc/systemd/system/mra.service
```

If existing install is found:

```text
MezzoRecovery Agent already appears to be installed on this machine.
Refusing to enroll a second agent.
```

Agent runtime must acquire lock:

```text
/run/mezzorecovery-agent.lock
```

If lock cannot be acquired, exit with clear log:

```text
Another MezzoRecovery Agent instance is already running.
```

API must reject enrollment if an active agent already has the same machine fingerprint.

---

## Persistence Design

Use PostgreSQL.

Add Stage 3 tables/entities only.

Recommended entities:

```text
Agent
AgentEnrollmentToken
AgentCredential
AgentHeartbeat
AgentEvent
```

Do not add tape-job or tape-device tables yet.

### Agent

Fields:

```text
Id
Name
Description
Status
MachineFingerprintHash
Hostname
OsDescription
Architecture
AgentVersion
CreatedByUserId
CreatedAt
EnrolledAt
LastSeenAt
DisabledAt
RevokedAt
IsEnabled
```

Statuses:

```text
PendingEnrollment
Online
Offline
Disabled
Revoked
```

### AgentEnrollmentToken

Fields:

```text
Id
AgentId
ShortCodeHash
TokenHash if needed
CreatedAt
ExpiresAt
UsedAt
RevokedAt
FailedAttemptCount
LockedUntil
CreatedByUserId
```

Rules:

```text
one-time use
short expiry, e.g. 15 minutes
stored hashed
revocable
rate limited
locked after repeated failures
```

### AgentCredential

Fields:

```text
Id
AgentId
CredentialHash
CreatedAt
ExpiresAt
RevokedAt
LastUsedAt
```

### AgentHeartbeat

Fields:

```text
Id
AgentId
ReceivedAt
Hostname
OsDescription
Architecture
AgentVersion
Status
DataJson optional
```

### AgentEvent

Append-only audit/event log.

Fields:

```text
Id
AgentId
CreatedAt
EventType
Message
DataJson optional
```

Events:

```text
AgentCreated
EnrollmentCodeGenerated
EnrollmentAttemptFailed
AgentEnrolled
AgentConnected
AgentDisconnected
HeartbeatReceived
AgentDisabled
AgentRevoked
```

Recommended indexes:

```text
Agents(Status)
Agents(CreatedAt)
Agents(LastSeenAt)
Agents(CreatedByUserId)
AgentEnrollmentTokens(ShortCodeHash)
AgentCredentials(AgentId)
AgentHeartbeats(AgentId, ReceivedAt)
AgentEvents(AgentId, CreatedAt)
```

Add partial unique index:

```text
unique active MachineFingerprintHash where status not Disabled/Revoked
```

---

## API Endpoint Design

Design controller-based endpoints.

User-facing agent management endpoints:

```text
GET  /api/agents
GET  /api/agents/{agentId}
POST /api/agents
POST /api/agents/{agentId}/disable
POST /api/agents/{agentId}/enable
POST /api/agents/{agentId}/revoke
POST /api/agents/{agentId}/regenerate-enrollment-code
GET  /api/agents/{agentId}/events
```

Installer endpoint:

```text
GET /a/{code}
```

Agent runtime endpoints:

```text
POST /api/agent/enroll
POST /api/agent/token
POST /api/agent/heartbeat
```

SignalR hubs:

```text
/api/hubs/agent
/api/hubs/app
```

Do not add tape-job endpoints yet.

---

## Authorization

Use existing human roles:

```text
Admin
User
```

Recommended authorization:

```text
Admin:
- can create agents
- can disable/revoke agents
- can regenerate enrollment codes
- can view all agents

User:
- can view agents
```

If the system should allow normal User to add agents, explicitly discuss trade-off.

Preferred for now:

```text
Only Admin can add, disable, revoke, or regenerate agent enrollment.
User can view agent status.
```

Agent endpoints must require agent token auth, not user auth.

Policies:

```text
CanViewAgents
CanManageAgents
AgentOnly
```

---

## Installation Script Design

The static URL deployed by the `MezzoRecovery.Agent` GitHub Actions workflow:

```text
https://mezzorecovery.com/agent/install
```

returns a generic Bash installer script. The enrollment code is passed as the first script argument:

```bash
curl -fsSL https://mezzorecovery.com/agent/install | sudo bash -s MK7P9D
```

Optionally, the API may also expose:

```text
GET /a/{code}
```

which returns a generated Bash script with the enrollment code embedded. The design should compare both approaches and recommend the default UX.

The script should:

```text
validate it is running as root
check existing installation
create directories
download latest agent binary
write config
generate/read machine.id
run enrollment command
write systemd unit
enable service
start service
print success/failure clearly
```

Example user-facing command:

```bash
curl -fsSL https://mezzorecovery.com/agent/install | sudo bash -s MK7P9D
```

The installer should download the Native AOT agent binary from the public static agent path, for example:

```text
https://mezzorecovery.com/agent/mra-linux-x64
```

The script should be safe and readable.

It should refuse to overwrite an existing agent unless a future explicit repair mode is designed.

Do not require the user to type long arguments.

---

## Agent Repository Design

Design the new `MezzoRecovery.Agent` repository.

The agent must be designed to run as a **Native AOT-published Linux executable** from the start. The design must account for trimming/AOT compatibility, predictable startup, small deployment footprint, and minimal runtime dependencies. Avoid dependencies or reflection-heavy patterns that are problematic with Native AOT unless they are explicitly configured and justified.

Recommended projects:

```text
src/
  MezzoRecovery.Agent/
  MezzoRecovery.Agent.Application/
  MezzoRecovery.Agent.Infrastructure/

tests/
  MezzoRecovery.Agent.Tests/
```

Or keep it simpler initially:

```text
src/
  MezzoRecovery.Agent/

tests/
  MezzoRecovery.Agent.Tests/
```

Recommend the best balance.

Agent executable responsibilities:

```text
enroll <code>
run
status
version
```

CLI examples:

```bash
sudo mra enroll MK7P9D
sudo mra run
mra status
mra version
sudo mra update
```

Systemd service should run:

```bash
/opt/mezzorecovery-agent/mra run --config /etc/mezzorecovery-agent/agent.json
```

---

## Native AOT Requirements

The Agent must work as a Native AOT Linux executable.

Design requirements:

```text
publish as self-contained linux-x64 Native AOT binary
no dependency on installed .NET runtime on target machine
trim/AOT-safe dependency choices
minimal reflection and dynamic loading
source-generated JSON serialization where practical
explicit configuration binding strategy compatible with trimming
AOT-friendly logging and options setup
clear handling of SignalR client compatibility with Native AOT
small, predictable binary suitable for install script download
````

Recommended publish style:

```text
RuntimeIdentifier = linux-x64
PublishAot = true
SelfContained = true
InvariantGlobalization = true if acceptable
StripSymbols = true for release if appropriate
````

Only linux-x64 is supported. linux-arm64 is out of scope.

The design should call out any libraries that may cause Native AOT warnings and propose fixes.

The CI pipeline should treat important AOT/trimming warnings as build issues unless explicitly justified.

The installer should download the AOT binary, not require installing the .NET runtime.

---

## App / MudBlazor UX Design

Add page:

```text
/agents
```

Use existing MudBlazor theme/layout.

Page sections:

```text
Title: Agents
Description: Linux recovery machines connected to MezzoRecovery.
Add Agent button
Agent cards/grid
Pending enrollment section if applicable
```

Agent card should show:

```text
name
status badge
hostname
OS
agent version
last seen
created date
actions depending on role
```

Add Agent dialog:

```text
Agent name
Description optional
Create button
```

After creation, show install command in a copy-friendly box:

```bash
curl -fsSL https://io.mezzorecovery.com/a/MK7P9D | sudo bash
```

Also show a manual typing-friendly code:

```text
Code: MK7P9D
```

Do not create tape-job UI yet.

---

## Security Requirements

Include:

```text
short-lived enrollment codes
hashed enrollment tokens/codes
rate limit enrollment attempts
do not reveal whether a code exists
agent credentials stored hashed in DB
agent credential stored securely on Linux disk
HTTPS only
agent SignalR requires token auth
admin operations require Admin
audit all enrollment attempts
audit agent status changes
do not expose raw credentials in UI
do not expose enrollment code after page is closed unless regenerated
```

Generic failed enrollment response:

```text
Invalid or expired enrollment code.
```

Do not reveal:

```text
code exists but expired
code already used
agent disabled
machine already enrolled
```

except in authenticated admin logs/UI where appropriate.

---

## Reconnect and Offline Detection

Design reconnect behavior.

Agent:

```text
SignalR automatic reconnect
exponential backoff with jitter
heartbeat every 15-30 seconds
send runtime info after reconnect
log connection state transitions
```

API:

```text
mark agent Online on connect/heartbeat
mark Offline on disconnect or missed heartbeat
background service checks stale LastSeenAt
offline threshold e.g. 60-90 seconds
broadcast AgentStatusChanged to App
```

---

## CI/CD and Static Agent Artifact Deployment Requirements

Design initial CI/CD for `MezzoRecovery.Agent`.

The Agent GitHub Actions workflow must follow the same deployment style as the existing `MezzoRecovery.TapeTools` workflows for `mrtc` and `mrmc`:

```text
build agent artifact
build/copy install script
copy artifacts to the server using SSH/SCP or the same mechanism used by TapeTools
make them publicly reachable from mezzorecovery.com
```

The published files must be reachable as:

```text
https://mezzorecovery.com/agent/{install script, agent code}
```

Recommended public artifact layout:

```text
https://mezzorecovery.com/agent/install
https://mezzorecovery.com/agent/mra-linux-x64
https://mezzorecovery.com/agent/mra-linux-x64.sha256
https://mezzorecovery.com/agent/version.json
```

Agent pipeline should:

```text
restore
build
test
publish Linux x64 Native AOT binary
fail or flag important AOT/trimming warnings
produce a self-contained executable
produce install script
produce checksum file
produce version metadata
copy installer, binary, checksum, and version file to server
verify artifact URLs after upload
ensure installer downloads the Native AOT binary, not framework-dependent output
```

The installer should download from:

```text
https://mezzorecovery.com/agent/mra-linux-x64
```

The install command shown in the App should use the static installer plus short enrollment code:

```bash
curl -fsSL https://mezzorecovery.com/agent/install | sudo bash -s MK7P9D
```

The API is still responsible for generating, validating, expiring, and revoking short enrollment codes. The static installer is only responsible for installing the binary and calling the API enrollment endpoint with the provided code.

Recommend whether to keep the optional dynamic API installer endpoint:

```text
GET /a/{code}
```

If retained, explain its relationship to the static `https://mezzorecovery.com/agent/install` script.

Existing API CI/CD must remain working.

Existing App CI/CD must remain working.

---

## Testing Requirements

Design tests for:

```text
Admin can create pending agent
User cannot create agent if only Admin allowed
short code generated and hashed
enrollment code expires
enrollment code is one-time use
invalid code rejected generically
duplicate machine fingerprint rejected
agent credential created on successful enrollment
agent can authenticate with token
agent cannot access user/admin endpoints
AgentHub requires AgentOnly
agent heartbeat updates LastSeenAt
offline detection works
App can list agents
existing login/contact/health still work
```

Agent-side tests:

```text
config loading
single-instance lock
machine.id generation
enrollment command
reconnect policy
heartbeat sender
```

---

## Things To Avoid

Avoid:

```text
browser-to-agent direct communication
using user Identity cookies for Agent
using Admin/User roles for Agent auth
long install commands
requiring inbound access to customer Linux machine
storing raw enrollment code in database
storing raw agent credential in database
allowing same machine to enroll twice
adding tape jobs too early
adding file uploads too early
adding remote command execution
breaking existing login/user management/contact endpoints
overengineering with Kafka/RabbitMQ/event bus
framework-dependent agent deployment if Native AOT is viable
reflection-heavy libraries that break trimming/AOT without explicit handling
```

---

## Required Output

Produce a concise Markdown design document for Stage 3 only.

Include:

```text
1. Executive summary
2. Scope
3. Fit within current MezzoRecovery workspace
4. Agent repository design
5. API persistence model
6. Agent enrollment flow
7. Minimal install command design
8. Linux install script design
9. systemd service design
10. Agent authentication/token design
11. SignalR hub design
12. App-to-API update design
13. Agents page MudBlazor UX
14. API endpoint design
15. Authorization policies
16. Reconnect/offline detection design
17. Duplicate-machine prevention
18. Security requirements
19. Testing strategy
20. Native AOT publishing strategy
21. CI/CD and static agent artifact publishing strategy
22. Migration strategy
23. Things to avoid
24. Final recommendation
```

Keep the design focused only on:

```text
agent skeleton
enrollment
authentication
SignalR connection
status/heartbeat
Linux service install
Native AOT Linux executable
Agents page UX
PostgreSQL persistence
```
