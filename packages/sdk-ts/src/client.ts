// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (c) 2026 The LiveCore Platform contributors

/**
 * The LiveCore SDK client (CORE-SDK-002): the typed entry point a vertical app
 * uses to call the Core API.
 *
 * It groups the implemented `/api/v1` routes into resource clients that mirror
 * the Core server modules (csv/api_routes.csv) over one shared, authenticated
 * transport. Every method returns the exact `@livecore/contracts` response type
 * for its route, so a vertical consumes a stable, typed Core surface without
 * hand-writing requests. The client is product-neutral: it carries only generic
 * Core vocabulary, and a vertical maps these shapes to its own UI labels
 * (AGENTS.md; docs/04_PRODUCT_BOUNDARIES.md).
 *
 * Authorization is enforced server-side; the client only carries the OIDC bearer
 * token and surfaces a denial as a `LiveCoreApiError`
 * (docs/07_SECURITY_THREAT_MODEL.md).
 */
import { HttpClient } from "./http.js";
import type { LiveCoreClientOptions } from "./options.js";
import { AssetsClient } from "./resources/assets.js";
import { AuditClient } from "./resources/audit.js";
import { ContentClient } from "./resources/content.js";
import { EntitiesClient } from "./resources/entities.js";
import { EntityTypesClient } from "./resources/entity-types.js";
import { EntitlementsClient } from "./resources/entitlements.js";
import { ExportsClient } from "./resources/exports.js";
import { IdentityClient } from "./resources/identity.js";
import { OrganizationsClient } from "./resources/organizations.js";
import { RealtimeClient } from "./resources/realtime.js";
import { RecapsClient } from "./resources/recaps.js";
import { ScenesClient } from "./resources/scenes.js";
import { SessionsClient } from "./resources/sessions.js";
import { StoreClient } from "./resources/store.js";
import { TemplatesClient } from "./resources/templates.js";
import { VisibilityClient } from "./resources/visibility.js";
import { WorkspacesClient } from "./resources/workspaces.js";

export class LiveCoreClient {
  /** The authenticated caller's principal-context read (`GET /api/v1/me`). */
  readonly identity: IdentityClient;
  /** Organization tenant create/list and offboarding/data-protection routes. */
  readonly organizations: OrganizationsClient;
  /** The tenant's append-only audit log read. */
  readonly audit: AuditClient;
  /** Organization-scoped template create, list, read and delete routes. */
  readonly templates: TemplatesClient;
  /** Workspace create/read/update, archive, member and invitation routes. */
  readonly workspaces: WorkspacesClient;
  /** Session list/create/read, lifecycle commands and participant presence. */
  readonly sessions: SessionsClient;
  /** Scene list, create, read, reorder and delete routes. */
  readonly scenes: ScenesClient;
  /** Content block list, read, create and delete routes. */
  readonly content: ContentClient;
  /** Generic entity create, list, read, delete and relationship-delete routes. */
  readonly entities: EntitiesClient;
  /** Generic entity-type define, list and by-id read routes. */
  readonly entityTypes: EntityTypesClient;
  /** Reveal/hide commands and the participant-visible feed. */
  readonly visibility: VisibilityClient;
  /** Reconnect replay of the durable session event stream. */
  readonly realtime: RealtimeClient;
  /** Session recap read (role-projected). */
  readonly recaps: RecapsClient;
  /** Asset upload-intent, signed-download, linking and delete flows. */
  readonly assets: AssetsClient;
  /** Completed workspace export read/download. */
  readonly exports: ExportsClient;
  /** Quota status, effective-entitlements and ad-eligibility reads. */
  readonly entitlements: EntitlementsClient;
  /** Purchase verification submissions. */
  readonly store: StoreClient;

  constructor(options: LiveCoreClientOptions) {
    const http = new HttpClient(options);
    this.identity = new IdentityClient(http);
    this.organizations = new OrganizationsClient(http);
    this.audit = new AuditClient(http);
    this.templates = new TemplatesClient(http);
    this.workspaces = new WorkspacesClient(http);
    this.sessions = new SessionsClient(http);
    this.scenes = new ScenesClient(http);
    this.content = new ContentClient(http);
    this.entities = new EntitiesClient(http);
    this.entityTypes = new EntityTypesClient(http);
    this.visibility = new VisibilityClient(http);
    this.realtime = new RealtimeClient(http, options.hubConnectionFactory);
    this.recaps = new RecapsClient(http);
    this.assets = new AssetsClient(http);
    this.exports = new ExportsClient(http);
    this.entitlements = new EntitlementsClient(http);
    this.store = new StoreClient(http);
  }
}
