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

## Required checks

Every PR must include:

- tests for new behavior
- negative authorization tests where applicable
- boundary scan for forbidden terms
- docs update if contracts, events or schema changed

## Output expected from LLM

1. Summary
2. Changed files
3. Tests added/updated
4. Commands run
5. Boundary/security considerations
6. Risks or follow-up tasks not implemented
