# Product Boundaries - Core vs Verticals

## Core owns

- generic domain model
- generic authorization
- generic visibility and reveal mechanics
- generic event stream
- generic assets
- generic audit
- generic templates
- generic SDK contracts

## Core does not own

- ArcanOS visual identity
- DnD/Pen-and-Paper domain vocabulary
- enterprise-specific vocabulary
- vertical navigation labels
- vertical-specific reports
- vertical-specific template content

## Dependency direction

```text
livecore-platform -> no vertical dependency
arcanos-app -> livecore-platform
scenarioos-enterprise -> livecore-platform
livecore-deploy -> deploys Core and verticals
```

## Forbidden in Core source code

See `csv/forbidden_core_terms.csv`.

Examples:

```text
Campaign
DM
GM
Player
Party
NPC
Quest
Monster
Facilitator
Trainee
Incident
Debrief
```

## Allowed vertical extension mechanisms

Vertical repositories may provide:

- label maps
- route maps
- themes
- templates
- entity type definitions
- seed data
- vertical-specific screens
- vertical-specific UI wrappers
- vertical-specific reports

## Template boundary

Core may store template definitions, but the template content must be data, not hardcoded source language.

Example:

```json
{
  "templateKey": "arcanos.campaign",
  "entityTypes": ["npc", "location", "quest"]
}
```

The Core can store this as data. The Core source code must not contain logic like `if entityType == npc then ...`.

## Public contract boundary

Core APIs must expose generic resources:

```text
/workspaces
/sessions
/scenes
/entities
/content-blocks
/visibility-rules
/session-events
/assets
```

Vertical apps can map these to domain language in their UI.
