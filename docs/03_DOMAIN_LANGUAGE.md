# Domain Language - Core

The Core uses generic names only.

| Core term | Meaning |
|---|---|
| Organization | Tenant or owner boundary |
| Workspace | Container for a live experience |
| WorkspaceMember | Authenticated user membership in a workspace |
| Participant | Session-facing person or role, may be linked to a user |
| Session | Live or prepared run of a workspace |
| Scene | Segment of a session |
| ContentBlock | Text/media/data unit shown or hidden by visibility rules |
| Entity | Generic domain object |
| EntityType | Template-defined type for an entity |
| VisibilityRule | Rule determining audience visibility |
| Reveal | Command/action that changes visibility or sends content |
| SessionEvent | Append-only event in a live session |
| Asset | Stored file or media object |
| Template | Configurable vertical/domain template |
| AuditLog | Security and compliance audit record |
| Recap | Session summary or structured continuation output |

## Vertical mappings

Core does not store these names, but verticals may map them:

| Core | ArcanOS | ScenarioOS |
|---|---|---|
| Workspace | Campaign | Scenario Program |
| Host | Dungeon Master / Game Master | Facilitator |
| Participant | Player | Trainee / Participant |
| Scene | Scene / Encounter | Module / Exercise |
| Entity | NPC / Location / Quest / Item | Role / Stakeholder / Incident |
| Reveal | Reveal / Whisper | Release / Brief |
| Recap | Session Recap | Debrief Report |

## Naming rule

Core persistence, APIs, DTOs, events and source code must use Core terms only.
