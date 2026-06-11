# livecore-platform

[![CI](https://github.com/Live-Experience-Application/livecore-platform/actions/workflows/ci.yml/badge.svg)](https://github.com/Live-Experience-Application/livecore-platform/actions/workflows/ci.yml)

Generic Core Platform for live, role-aware, scene-based interactive sessions.

This repository must stay product-neutral. It must not contain ArcanOS, Pen-and-Paper, DnD, Enterprise or ScenarioOS domain language in source code.

## Owns

```text
API
Realtime hub
Domain model
Database migrations
Visibility engine
Reveal engine
Session event stream
Asset authorization
Audit log
Generic templates
TypeScript contracts
TypeScript SDK
Generic UI primitives and design tokens
```

## Does not own

```text
Campaigns
Dungeon Masters
Players
NPCs
Quests
Monster stats
Character sheets
Training scenarios
Facilitators
Trainees
Incidents
Debrief reports
```

Those belong to vertical repositories.

## Start here

Read in order:

1. `AGENTS.md`
2. `docs/00_START_HERE.md`
3. `docs/01_PRODUCT_VISION_AND_SCOPE.md`
4. `docs/02_ARCHITECTURE.md`
5. `docs/04_PRODUCT_BOUNDARIES.md`
6. `docs/07_SECURITY_THREAT_MODEL.md`
7. `csv/core_epics_stories.csv`

Do not implement code until the first story is selected.

## Repository layout

```text
LiveCore.slnx            .NET solution (apps + tests)
Directory.Build.props    repository-wide .NET build/lint enforcement
.editorconfig            formatting and C# code-style baseline
.gitattributes           line-ending normalization (LF in the repository)
.github/workflows/ci.yml CI pipeline (build, tests, format/lint, boundary scan)
eslint.config.mjs        ESLint flat config for the TypeScript packages
.prettierrc.json         Prettier configuration (with .prettierignore)
apps/api                 ASP.NET Core API host (LiveCore.Api) - health endpoints only
apps/worker              Background worker host skeleton (LiveCore.Worker)
packages/contracts       @livecore/contracts  - TypeScript contract types (skeleton)
packages/sdk-ts          @livecore/sdk-ts     - TypeScript SDK client (skeleton)
packages/ui-core         @livecore/ui-core    - generic UI primitives (skeleton)
packages/design-tokens   @livecore/design-tokens - design tokens/theme contracts (skeleton)
tests/LiveCore.SmokeTests  xUnit smoke and health endpoint tests for the hosts
scripts/boundary-scan.ps1  forbidden-term boundary scan for Core source
docs/                    architecture and product documentation
csv/                     backlog stories and forbidden term list
```

## Prerequisites

- .NET SDK 10.0 or later
- Node.js 22 or later
- pnpm 10 (pinned via the `packageManager` field; with Corepack run `corepack enable pnpm` once, or prefix pnpm commands with `corepack`)

## Build, format, lint, test and boundary scan

Run all commands from the repository root. CI (`.github/workflows/ci.yml`)
calls these commands verbatim, so a green local run means a green pipeline.

### .NET solution (API, worker, smoke tests)

Build:

```bash
dotnet build LiveCore.slnx
```

Run the smoke tests:

```bash
dotnet test LiveCore.slnx
```

Verify formatting and code style (no files are changed; non-zero exit code on
violations):

```bash
dotnet format LiveCore.slnx --verify-no-changes
```

Apply formatting and code-style fixes:

```bash
dotnet format LiveCore.slnx
```

C# style rules live in `.editorconfig`. `Directory.Build.props` additionally
enforces them at build time (`EnforceCodeStyleInBuild`) and treats warnings as
errors, so `dotnet build` doubles as the .NET lint gate.

### TypeScript packages

Install dependencies:

```bash
pnpm install
```

Build all packages:

```bash
pnpm --recursive run build
```

Lint (ESLint; zero warnings allowed):

```bash
pnpm run lint
```

Verify formatting (Prettier; non-zero exit code on violations):

```bash
pnpm run format:check
```

Apply formatting:

```bash
pnpm run format
```

Run package test scripts (packages define none yet; this exits 0 and picks up
`test` scripts automatically as packages add them):

```bash
pnpm --recursive run test
```

### Boundary scan

Run the boundary scan (fails with a non-zero exit code if any forbidden
vertical term from `csv/forbidden_core_terms.csv` appears in Core source under
`apps/`, `packages/`, `tests/`, `scripts/` or `.github/`):

```powershell
# Windows (Windows PowerShell 5.1 or pwsh)
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/boundary-scan.ps1
```

```bash
# Linux/macOS (PowerShell 7+)
pwsh -NoProfile -File scripts/boundary-scan.ps1
```

## Run the hosts locally

Start the API host (listens on `http://localhost:5062` by default, see
`apps/api/Properties/launchSettings.json`):

```bash
dotnet run --project apps/api
```

Start the background worker host (registers no jobs yet):

```bash
dotnet run --project apps/worker
```

### Health endpoints

The API host exposes two unauthenticated health endpoints:

| Endpoint        | Purpose                                                                                                                              |
| --------------- | ------------------------------------------------------------------------------------------------------------------------------------ |
| `/health/live`  | Liveness: the process is up and serving HTTP. Runs no dependency checks on purpose.                                                  |
| `/health/ready` | Readiness: runs the health checks tagged `ready` (none registered yet; database and other dependencies add theirs in later stories). |

Both return `200 OK` with the minimal JSON body `{"status":"Healthy"}`;
readiness returns `503` with `{"status":"Unhealthy"}` once a registered
readiness check fails. Because the endpoints are reachable without
authentication, the response carries only the overall status: no version
numbers, configuration values, host names or individual check details (see
`docs/07_SECURITY_THREAT_MODEL.md`).

### Structured logging

Both hosts write structured, single-line JSON log entries to stdout using the
JSON console formatter built into `Microsoft.Extensions.Logging` (UTC
timestamps, scopes included); no external logging dependency is used. Log
levels are configured per host in `appsettings.json`. Logs must carry
identifiers and metadata, never sensitive content (threat T7 in
`docs/07_SECURITY_THREAT_MODEL.md`).

## Continuous integration

GitHub Actions runs `.github/workflows/ci.yml` on every push to `main` and on
every pull request. All jobs run on `ubuntu-latest` and execute the commands
documented above verbatim:

| Job               | What it runs                                                                                |
| ----------------- | ------------------------------------------------------------------------------------------- |
| `dotnet`          | `dotnet build`, `dotnet test`, `dotnet format --verify-no-changes` on `LiveCore.slnx`       |
| `typescript`      | `pnpm install --frozen-lockfile`, `lint`, `format:check`, recursive `build` and `test`      |
| `boundary-scan`   | `pwsh -NoProfile -File scripts/boundary-scan.ps1` (forbidden vertical terms fail the build) |
| `powershell-lint` | PSScriptAnalyzer (Error/Warning severity) over `scripts/*.ps1`                              |

Line endings are normalized to LF in the repository via `.gitattributes`, so
the boundary scan and `dotnet format` behave identically on Linux CI and on
Windows working copies.

## License

This project is licensed under the GNU Affero General Public License v3.0 or later.

Commercial dual licensing may be offered in the future for organizations that require proprietary use, embedding, hosting, or distribution without AGPL obligations.

For commercial licensing inquiries, contact: singh.harwinder@outlook.copm
