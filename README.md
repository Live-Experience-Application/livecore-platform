# livecore-platform

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

## License

This project is licensed under the GNU Affero General Public License v3.0 or later.

Commercial dual licensing may be offered in the future for organizations that require proprietary use, embedding, hosting, or distribution without AGPL obligations.

For commercial licensing inquiries, contact: singh.harwinder@outlook.copm
