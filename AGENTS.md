# AGENTS.md - livecore-platform

These instructions are mandatory for all LLM coding agents.

## Non-negotiable rule

The Core Platform is product-neutral.

Do not write source code containing ArcanOS, DnD, Pen-and-Paper, Enterprise or ScenarioOS domain language.

Allowed source terms include:

```text
Organization
Workspace
WorkspaceMember
Session
Participant
Scene
ContentBlock
Entity
EntityType
Asset
VisibilityRule
Reveal
SessionEvent
Template
AuditLog
Recap
```

Forbidden source terms include:

```text
Campaign
Dungeon Master
DM
GM
Player
Party
NPC
Quest
Monster
Spell
Character Sheet
Facilitator
Trainee
Training
Incident
Debrief
Department
```

Forbidden terms may appear only in documentation explaining what is forbidden.

## Required reading before each task

- `README.md`
- `docs/00_START_HERE.md`
- `docs/04_PRODUCT_BOUNDARIES.md`
- `docs/05_MODULE_CONTRACTS.md`
- `docs/06_AUTHORIZATION_MATRIX.md`
- `docs/07_SECURITY_THREAT_MODEL.md`
- story row from `csv/core_epics_stories.csv`

## Implementation discipline

- one story per PR
- production-ready implementation only
- no temporary MVP shortcuts
- no custom password auth
- no public asset access
- no UI-only security
- no new dependencies without explicit justification
- no unrelated refactoring

## Contributor IP policy and source headers (CORE-LIC-004)

The Core is AGPL-3.0-or-later and dual-licensed (CORE-LIC-002), so contribution
provenance must stay clean and license context must travel with the source. The
full policy is in `CONTRIBUTING.md`; the rules an agent must follow:

- **Sign off every commit (DCO).** Each commit needs a `Signed-off-by: Name
<email>` trailer whose email matches the commit author (`git commit -s`). It
  certifies the Developer Certificate of Origin (`DEVELOPER_CERTIFICATE_OF_ORIGIN`)
  and, under `CONTRIBUTING.md`, the dual-license grant. CI fails an unsigned commit
  (`scripts/lint-dco-signoff.ps1`).
- **Add the SPDX header to new shipped source.** Every first-party `.cs`, `.ts`
  and `.tsx` file that builds into an image or a published package must start with:

    ```text
    // SPDX-License-Identifier: AGPL-3.0-or-later
    // Copyright (c) <year> The LiveCore Platform contributors
    ```

    Generated source (the EF migrations under `apps/api/Persistence/Migrations` and
    the generated `packages/contracts/src/openapi.ts`), build output, `*.d.ts` and
    non-shipped tooling (`scripts/`, `.mjs`) are out of scope. Run
    `pwsh -NoProfile -File scripts/lint-license-headers.ps1 -Fix` to add any missing
    headers; CI fails a headerless in-scope file.

## Required checks

Every PR must include:

- tests for new behavior
- negative authorization tests where applicable
- boundary scan for forbidden terms
- DCO sign-off on every commit and SPDX headers on new shipped source (CORE-LIC-004)
- docs update if contracts, events or schema changed

## Output expected from LLM

1. Summary
2. Changed files
3. Tests added/updated
4. Commands run
5. Boundary/security considerations
6. Risks or follow-up tasks not implemented
