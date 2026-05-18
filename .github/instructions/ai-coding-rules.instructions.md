# AI coding discipline (exploration and edits)

These rules complement architecture and stack guidance in `.github/copilot-instructions.md`. Use them for **how** to explore and change code, not product architecture.

## Token usage and exploration

- Default to **low-token work**: prefer symbol search, ripgrep-style search, and targeted file opens over scanning whole trees.
- Do **not** read full repositories or enumerate every file unless the user explicitly asks for that.
- Open only the **minimum files** needed to answer the question or implement the change.
- Summarize findings briefly; avoid dumping large excerpts unless requested.

## Repository scope

- Assume **one repository per task** unless integration clearly spans repos.
- Start in the repo that **owns** the behavior under discussion.
- Cross into another repo only for **shared contracts**, package boundaries, APIs, or explicit integration points—and state **why** before doing so.

## Paths and artifacts to skip (unless the user asks)

Do not open, summarize, or suggest edits under these unless explicitly requested:

- Build output and caches: `bin/`, `obj/`, `dist/`, `build/`, `artifacts/`, `packages/`, `node_modules/`, `generated/`
- IDE and VCS internals: `.git/`, `.vs/`, `.idea/`, `.vscode/`
- Test and diagnostics dumps: `coverage/`, `TestResults/`, `logs/`, `snapshots/`, `*.log`, `*.trx`
- Archives and dumps: `*.zip`, `*.tar`, `*.gz`, `*.bak`, `*.dump`, `*.sql`
- Archived docs: `docs/archive/`, `docs/old/`

## Documentation

- Do **not** read every markdown file to “learn the repo.”
- Prefer **`AI_CONTEXT.md`**, **`README.md`**, and **current** architecture or onboarding docs when they exist.
- Ignore **old design prompts**, archived specs, and historical prompts unless the user points you there.

## Before editing

1. List the **smallest set of files** you expect to touch (or read to justify the change).
2. Briefly note **why each file** is necessary.
3. Make **targeted edits only**—no drive-by refactors, unrelated formatting churn, or scope creep unless asked.
