# Authorization Matrix

Roles are generic. Verticals may rename them in UI.

| Action | Owner | Admin | Host | CoHost | Participant | Observer | Auditor |
|---|---:|---:|---:|---:|---:|---:|---:|
| View workspace metadata | yes | yes | yes | yes | limited | limited | yes |
| Manage workspace settings | yes | yes | no | no | no | no | no |
| Manage members | yes | yes | limited | no | no | no | no |
| Create session | yes | yes | yes | yes | no | no | no |
| Start/end session | yes | yes | yes | yes | no | no | no |
| Create scene | yes | yes | yes | yes | no | no | no |
| View host-only content | yes | yes | yes | yes | no | no | audit-only |
| View participant-visible content | yes | yes | yes | yes | if visible | if visible | audit-only |
| Change visibility rule | yes | yes | yes | yes | no | no | no |
| Execute reveal | yes | yes | yes | yes | no | no | no |
| Send private content | yes | yes | yes | yes | no | no | no |
| View own participant feed | no | no | no | no | yes | no | no |
| View observer feed | yes | yes | yes | yes | no | yes | no |
| View audit log | yes | yes | optional | no | no | no | yes |
| Export workspace | yes | yes | optional | no | no | no | optional |
| Delete workspace | yes | optional | no | no | no | no | no |

## Authorization principles

- The participant-visible-feed route (`GET /api/v1/participants/{participantId}/visible-feed`) is authorized to the participant who OWNS the feed ("View own participant feed" above, participant only) OR a Host/CoHost PREVIEWING it (preview-as-participant — `docs/05_MODULE_CONTRACTS.md` gives the Visibility module "preview-as-participant"; `csv/authorization_matrix.csv` grants Host and CoHost `preview` for the visible feed, and `csv/api_routes.csv` lists the route roles as "Participant owner or Host"). Owner/Admin/Observer/Auditor have no access to a participant's feed unless they are its owner or a Host/CoHost of its workspace.
- Role checks are not enough; object-level authorization is required.
- Organization boundary must be checked before workspace boundary.
- Workspace boundary must be checked before resource-level visibility.
- Participant visibility is computed server-side.
- Audit roles may view metadata but should not automatically view sensitive content unless explicitly allowed.
